# Tienda pública por tenant — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new tenant module `store` that publishes an anonymous, server-to-server catalog API for each tenant's storefront, stores visitor order requests, and lets an advisor take a request, which converts it into a `Store` order in the backoffice.

**Architecture:** New module `src/Modules/Store/*` with the four layers that `Loans` uses. The domain is pure: `OnlineStore` (settings, slug, publish rules), `StoreDomain` (hostname → tenant), and `StoreOrderRequest` (frozen lines, `New → Converted | Discarded`, plus a conversion lease). Catalog, Quotations, Customers, Geography, Tenancy and Storage come in through ports declared in `Modules.Store.Application`, with adapters in `Bootstrapper`. A request carries a **billing** block and an optional **shipping** block (owner, 2026-11-03); «Tomar» finds or creates the customer from the billing block, adds the missing addresses to the customer's address book, and turns the blocks into the order's `Billing` and `Shipping` parties. Pricing is never duplicated. Quotations gets one new public service, `IStoreOrderGateway`, built on `OrderDraftBuilder`. The cart and the order go through the same pricing path, `allowGrouping` included. The public routes take a shared secret from the Astro server, rate-limit by the visitor IP that server forwards, check Turnstile, and are exempt from CSRF.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql (`postgres:18-alpine`), `WebApplicationFactory<Program>`, `System.Threading.RateLimiting`.

**Spec:** `qep-frontend-worktrees/tienda-publica/docs/superpowers/specs/2026-11-03-tienda-publica-design.md`. Task 1 copies it into this repo as `docs/superpowers/specs/2026-11-03-tienda-publica-design.md`. The spec is the authority, and its **[decisión del diseñador]** marks are approved. Where the code contradicts the spec, this plan follows the code and records the contradiction in § Verified facts.

**Out of scope:** the backoffice SPA and the Astro app, each with its own plan written only from § HTTP contract; DNS and Vercel; the «Escribir por WhatsApp» button, which uses the **existing** `POST /api/v1/tenants/{tenantId}/messaging/conversations` with the request's `customerId` (`MessagingEndpoints.cs:83`, `:424`; owner, 2026-11-03: no new messaging endpoint). The backend only exposes `customerId` (§ C, § D).

---

## Before you start

- Commands handed to the developer are **PowerShell**: no `&&`, one command per line, `$env:VAR = "value"` on its own line.
- Stop `Api.exe` before `dotnet build`, `dotnet test` or `dotnet ef`: `Stop-Process -Name Api -ErrorAction SilentlyContinue`.
- Integration tests need Docker. Run `$env:Seed__Enabled = "false"` first.
- Each task runs only the classes it touches, with `--filter`. The full suites run once, in Task 13.
- Worktree `qep-backend-worktrees/tienda-publica`, branch `feature/tienda-publica`. Run `git branch --show-current` right before each commit, and stop if it says `main`.
- Conventional commits in English, **without** `Co-Authored-By` or any AI attribution, even if the harness suggests one.
- Every new project brings its own `packages.lock.json`. After touching references, run `dotnet restore Backend.slnx` and commit **every** changed lock. The `Dockerfile` restores with `--locked-mode`.
- Migrations: `dotnet ef migrations add <Name> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
- Never print secrets (`Store:Storefront:SharedSecret`, `Store:Turnstile:SecretKey`). To check that one exists, count it, e.g. `dotnet user-secrets list | Select-String -Pattern "Store:" | Measure-Object`.
- Line numbers below are from 2026-11-03. Re-anchor with `rg` if they have drifted.

## Verified facts (read before Task 2)

These were checked in the code. They shape the design, and some of them contradict the spec.

1. **«Tomar» cannot reuse `POST /orders` as is.** `CreateOrderHandler` authorizes with the session (`CreateOrder.cs:133`), resolves the advisor from it (`:142`), and passes `AllowIncompleteCustomer: false` (`:170`). `OrderCustomerEligibility.cs:27-39` then rejects incomplete customers and customers without a CUC. `Order.CreateManual` hard-codes `OrderSource.Manual` (`Order.cs:388`). The reusable unit is `OrderDraftBuilder` (`OrderDraftBuilder.cs:47-90`), which already holds the whole pricing path: the scale per line from `QuotationProductPricingResolver.PriceFor` (`:128-167`), then `OrderPricingRecalculation.ApplyAsync`, which handles group discounts, the global floor and the minimum purchase (`OrderPricingRecalculation.cs:14-81`). The store path reuses `OrderDraftBuilder.PriceAsync` and `OrderPricingRecalculation`, nothing else.
2. **Spec §5.2 says a single transaction across modules. There is no such mechanism.** Every module has its own `DbContext` and unit of work. `UseTransaction` and `TransactionScope` appear nowhere, and `ExternalOrderService` opens a Quotations-only transaction (`IExternalOrderWriter.cs:180`). Shopify gets atomicity in practice from an idempotent order (`ExternalKey`, unique `IX_orders_tenant_external_key`, `QuotationsDbContext.cs:643`) plus marking its mirror afterwards. «Tomar» follows that pattern:
   - a short conversion lease on the request (one taker at a time, 409 for the others);
   - the order created with `ExternalKey = "store-request:{requestId}"`;
   - the request marked `Converted`.

   If the process crashes between the second and third steps, the next take finds the existing order and only marks the request.
3. **What a complete customer requires (re-verified 2026-11-03; the earlier «address, city and classification» was wrong).** `CustomerWriteRules` (the POST rules, which `CustomerProvisioning.CreateCompleteAsync` runs at `CustomerProvisioning.cs:133-134`):
   - **Required:** name (`CustomerWriteRules.cs:65`), identification number (`:68`), identification type, one of `NIT|CC|CE|PASAPORTE` (`:87-92`, `IdentificationType.cs:27-30`), **phone** (`:73-75`), country, ISO-3166-1 alpha-2 (`:111-117`), and for `CO` a DIVIPOLA `cityId` (`:127-130`), otherwise a `cityName` (`:131-134`).
   - **Optional:** address (`:80-81`), email (`:141-144`), person type (`:97-101`) and **classification** (`:103-106`, owner decision 2026-10-19: without it the CUC has no prefix).
   - The billing block of the store (spec §4.1) has every required field. So **«Tomar» can create a complete customer, with CUC, and the dialog has nothing it must ask**: it only *offers* the optional classification. Retention and VAT surplus are born `false` (`CustomerProvisioning.cs:340-356`).
   - Contradiction in code: `CompleteCustomerData.ClassificationId` is a non-nullable `Guid` (`CustomerProvisioning.cs:26`) although the rules accept `null`. Task 12 makes it `Guid?`; `ResolveForCustomerAsync` already takes the nullable id that `CreateCustomerHandler` passes (`CreateCustomer.cs:65`).
   - `CreateCompleteAsync` seeds the first address-book entry from the same data (name, address, city, phone; `CustomerProvisioning.cs:376-385`), and on a lost document race returns the winner with `Created: false` instead of failing (`:156-167`).
   - **An incomplete customer is completed at «Tomar»** (owner, 2026-11-03, spec §5.2 «Cliente incompleto»). There is no partial edit of an incomplete customer (`Customer.cs:700-707`); the only path is `Customer.Complete` (`Customer.cs:432-480`: CUC, name, identification, contact, commercial, validated before assigning, rejects merged and inactive through `EnsureNotMerged`/`EnsureActive`, `:447-448`). Its callers are the `PUT` (`UpdateCustomer.cs:103`) and `CompleteExternalSaleAsync` (`CustomerProvisioning.cs:176-240`), which runs the POST rules, `PrepareAsync` (classification and city before the CUC) and `Complete`. Task 12 adds a sibling, `CompleteFromStoreAsync`, that first merges: **an existing value always wins, the billing block only fills what is blank** (as the frontend's `seedCustomerDocument` does). After it the customer is complete, so its address book accepts the request's addresses. The order engine still runs with `allowIncomplete: true` (`OrderCustomerEligibility.cs:27`) only for the race where completion is not possible (below).
4. **Each store currency carries its own billing account, in that currency.** An order's currency is its billing account's currency (`Order.cs:96`, `Currency = BillingAccount?.Currency ?? header.Currency`), and the engine has no currency override. So the store is a **list** of `{ currency, billingAccount }` entries plus a `primaryCurrency` (owner, 2026-11-03, spec §4.2), never one currency with one account. An entry whose account is in another currency would quote in one currency and sell in another: saving rejects it with `store.settings.currency_mismatch`. A request is priced in the currency the visitor chose, and «Tomar» charges it to **that** currency's entry (spec §5.2). The offered currencies come from `ICurrenciesInUse` (`Modules.Catalog.Application/ICurrenciesInUse.cs:8`), the source of `GET /catalog/currencies-in-use` (`ProductEndpoints.cs:19`). Prices are never converted between currencies: each currency is the price list the variant already has.
5. **Rounding.** `OrderPricingRecalculation.cs:26` applies the tenant's rounding rule only to `Quotation` and `Manual` orders. Every other source uses `PriceRoundingDefaults`. `Store` is priced by the normal engine, so it joins the tenant-rule branch. Otherwise cart and order would differ.
6. **External sources.** `Order.IsExternal` is defined as "not `Quotation` and not `Manual`" (`Order.cs:1190`). `Store` must count as **internal**: an advisor who changes the payment status still has to attach proofs (`Order.cs:1175`). `CreateExternal` rejects `Store` too (`Order.cs:439`).
7. **The `source` CHECK constraint is `source IN ('Quotation','Manual','Shopify')`** (`QuotationsDbContext.cs:493`). The orders list parses `source` with `Enum.TryParse` (`OrderListing.cs:77`), so `source=Store` works once the enum value exists. Reporting re-declares the enum, and its order is contract: new values go at the end, after `Pos` (`ReportFilterEnums.cs:22-31`). It is mapped in `OrdersReportSource.cs:585-591`.
8. **Modules, permissions and rate limits.**
   - Module keys: `TenantModuleKeys.All`, `Dependencies` and `DefaultForNewTenants` (`TenantModuleKey.cs:79-101`), plus the CHECK `CK_tenant_modules_module_key` (`TenancyDbContext.cs:121-122`).
   - Handlers are registered by hand (`QepServiceCollectionExtensions.cs:403-446` for Loans). Roles start at `:1457` (`admin`), and `PermissionDefinition` around `:2231`.
   - `CompositionRootTests.PermissionDiscoveryFindsTheSeventyThreeConstants` asserts 73 (`CompositionRootTests.cs:184-198`).
   - Rate-limit policies live in `Program.cs:81-157`, with their names in `RateLimiterPolicies.cs`.
   - Anonymous endpoints use `.AllowAnonymous().RequireRateLimiting(...)`, as in `InvitationEndpoints.cs:24-25` and `MapShopifyInstallEndpoints` (`Program.cs:252`).
   - CSRF exemptions are explicit prefixes (`RequireCsrfHeaderMiddleware.cs:51-63`, `ExternalApiPaths.cs`).
9. **Client IP.** `UseForwardedHeaders` rewrites `RemoteIpAddress` from `X-Real-IP`, trusted only from `ForwardedHeaders:KnownNetworks` (`ForwardedHeadersServiceCollectionExtensions.cs:31-45`). For storefront calls that address is the Vercel server, not the visitor. The visitor IP therefore travels in a header that is trusted only with a valid shared secret (Task 9).
10. **Customer matching.**
    - Documents are unique per tenant among non-merged customers (`IX_customers_tenant_identification`, `CustomersDbContext.cs:240-248`), so a document match yields at most one customer.
    - Phones are not unique (`IX_customers_tenant_phone_e164`, `:123`). The repository only has `FindOldestByPhoneE164Async` (`ICustomerRepository.cs:171`), so Task 12 adds a method that lists every match.
    - Phones are normalized with `IPhoneNumberNormalizer.ToE164(phone, country)` (`IPhoneNumberNormalizer.cs:11`), the same way `ServiceContractsPhoneNormalizer` does it.
11. **Images and logo are public.** Product images resolve to `IPublicObjectStorage` URLs (`ProductImageLookup.cs:57-61`, `CatalogVariantReads.ImageUrlOf`). `TenantLogoStorage` publishes the tenant logo to the public bucket. A banner is any tenant image file published through Storage's existing `PUT /files/{fileId}/publication`.
12. **Delivery and parties (owner, 2026-11-03: «no recogida en tienda»).** `QuotationPartiesRequest.IsStorePickup` defaults to `false` (`QuotationsDtos.cs:239`); store orders keep `false` and send a `Shipping` party. What the order engine demands of parties:
    - A party is complete only with name, phone, **email**, address, department and city (`OrderParty.cs:45-51`). `EnsureCreatable` (`Order.cs:503-519`), which `CreateManual` runs (`Order.cs:365`), rejects an incomplete billing (`order.billing.party_incomplete`) or shipping party. The store's billing email is **required** (owner, 2026-11-03, spec §4.1) and its shipping block has none, so both parties take the billing email and `CreateFromStore` runs the full `EnsureCreatable`, no relaxation.
    - A billing party with its own data needs `BillingWithRetention` and `BillingVatSurplus` non-null (`Order.cs:546-551`, `order.billing.tax_profile_required`), and with a billing party the order's retention comes from those flags, not from the customer (`Order.cs:290-293`). The take passes **the chosen customer's** `WithRetention` and `VatSurplus` (a new customer: `false`/`false`). That is how «esas reglas recién aplican al convertir, con el cliente real» (spec §5.2) holds.
    - A billing party with its own name needs identification type and number (`Order.cs:537-543`). The store requires both.
    - Party identification types are the same four as the customer's (`PartyFiscalTypes.cs:18-24`).
13. **The order detail HTTP shape** is `OrderDetailResponse` (`OrdersDtos.cs:299-308`), built by `OrderResponseComposer` (`OrderResponseComposer.cs:13-63`). The new `storeRequest` goes there, not into `OrderDetailDto`.
14. **Error mapping goes by exception type** (`ApiExceptionHandler.cs:163-193`):
    - `DomainException` → 422
    - `ResourceConflictException` → 409
    - `RequestConcurrencyException` → 412
    - `ResourceNotFoundException` → 404
    - `RequestUnauthorizedException` → 401
    - `RequestRateLimitedException` → 429
    - `ServiceUnavailableException` → 503

    The new module uses those types, so `ApiExceptionHandler.cs` needs no new branch. Task 13 pins the mapping with a test.
15. **The address book is `CustomerAddress`** (`Customers.Domain/CustomerAddress.cs`), child entity of `Customer`, the backend side of the frontend's `QuoteClientAddress`/`CustomerAddressDto`: `Name` (whose address, ≤ 120), `Address` (≤ 200), `Phone?` (≤ 32), `CityId`, `IsPrincipal`. The department is **not stored**: it is the city's. Only `Customer.AddAddress` creates one (`Customer.cs:695-713`), the first becomes principal, and **it throws `customers.customer.incomplete` on an incomplete customer** (`:702-708`). There is no lookup by content today; Task 12 adds one in the domain. **`CityId` is a required `Guid`** (`CustomerAddress.cs:60`, `CustomerAddressDetails.CityId` `required` and rejected when empty, `:118`, `:136-138`): an address outside Colombia cannot be stored, and `CreateCompleteAsync` seeds no address for a foreign customer (`CustomerProvisioning.cs:376`, «un cliente de afuera … nace[n] sin ella», `Customer.cs:145-146`). The customer **master** already has both lanes: `Country` + `CityId` for `CO`, `Country` + free-text `CityName` (≤ 120, `CustomerContactInfo.cs:53,68`) otherwise (`CustomerWriteRules.cs:127-134`). Task 1A gives the address book the same two lanes.
    - Order and quotation parties have the same gap: `DepartmentId`/`CityId` only (`OrderParty.cs:43-51`, `QuotationParty.cs:76-82`, `QuotationPartyRequest`, `QuotationsDtos.cs:210-222`), and completeness requires both ids. Task 1B adds `Country` and `CityName` to them.
16. **Geography has no country.** It is DIVIPOLA only: `Department`, `City` (with `DepartmentId`), no country field (`Modules.Geography.Domain`). Its endpoints are `GET /api/v1/departments` and `GET /api/v1/cities?departmentId=`, both `RequireAuthorization()` (`GeographyEndpoints.cs:20-28`), so the storefront needs its own anonymous routes. Reads exist as `IDepartmentRepository.ListAllAsync`/`FindAsync` and `ICityRepository.ListByDepartmentAsync`/`FindAsync` (`IDepartmentRepository.cs:7,14`, `ICityRepository.cs:11,20`).
17. **Country is an ISO-3166-1 alpha-2 code** everywhere it is stored: `Customer.Country` (`CustomerContactInfo.CountryLength = 2`, `CustomerContactInfo.cs:64`; rule `CustomerWriteRules.cs:111-117`), with `Customer.ColombiaCountryCode = "CO"` (`Customer.cs:45`), and the same `"CO"` literal in Quotations (`BillingPartyFiscalRules.cs:11`). **The backend has no country catalogue**: `CustomerContactInfo` only checks 2 ASCII letters (`CustomerContactInfo.cs:103-107`); the list of names is the frontend's (`features/customers/utils/countries.ts`, spec §4.2). The only backend notion of «a real country» is libphonenumber's `IPhoneNumberNormalizer.IsKnownRegion` (`IPhoneNumberNormalizer.cs:15`), which ServiceContracts already uses for this (`ServiceContractsPhoneNormalizer.cs:16`). Sales countries are validated with it (owner, 2026-11-03: any country of the customer catalogue, not only `CO`). DIVIPOLA exists only for `CO`: Colombia uses department + city ids, any other country a free-text city, like the customer master.

## Global Constraints

- Module key `store`, last in `TenantModuleKeys.All`, `Dependencies[Store] = [Orders]`, not in `DefaultForNewTenants`. Spec §7: «el backoffice no muestra la sección sin el módulo `store`».
- One new permission: `store.settings.manage`, with `RequiredModules: [ModuleKeys.Store]`, granted only to `admin`. The inbox uses the existing `quotations.order.manage` (spec §4.4), and its handlers also require the `store` module (403 `tenancy.module_not_enabled`).
- Backoffice routes live under `/api/v1/tenants/{tenantId:guid}/store`. Handlers re-check tenant and permission, and another tenant gets **403, never 404**.
- Public routes live under `/api/v1/public/stores/{host}`. «X-Qep-Client y CSRF no aplican a las rutas públicas». They never read the session cookie, and they rate-limit by visitor IP.
- `Modules.Store.Application` references only `BuildingBlocks.*` and `Modules.Tenancy.Application`. No EF Core and no Npgsql (`StoreLayerTests`).
- Schema `store`. Money is `numeric(18,2)` plus a 3-letter currency. Enums are stored as text `varchar(16)`. `version` is a `bigint` concurrency token.
- Slug: lowercase `^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$` (3 to 40 characters), and never one of these reserved words: `www`, `app`, `api`, `admin`, `mail`, `smtp`, `ftp`, `static`, `cdn`, `docs`, `status`, `blog`, `help`, `soporte`, `tienda`, `shop`, `qep`, `qep-api`, `tunnel`.
- Hostname for a slug: `{slug}.{Store:RootDomain}`, with `Store:RootDomain = "lulocrm.com"`. The MVP only inserts that row (spec §5.3).
- Multi-currency from the MVP (owner, 2026-11-03): the store holds 1–10 `{ currency, billingAccount }` entries and one `primaryCurrency` among them. Every public read and write names a `currency`, which must be one of the store's. «Un producto sin precio en la moneda elegida no se muestra.» No exchange rates, and money of different currencies is never added up.
- «Sólo se publican variantes activas de productos activos.» «Visible en la tienda», **off by default**.
- Pricing mode: «Sólo detal» → `isRetail: true`. «Con escalas por cantidad» → `isRetail: false` and `globalScaleFloor: null`.
- The browser never computes a price, a scale or a total. Every total comes from Quotations' engine.
- Public DTOs are their own records. Never expose cost, floors, the global scale, billing account ids, variant `code` or `barcode`, or `tenantId`.
- «Nunca se crean clientes desde el endpoint público.» Customers are created only in «Tomar», **complete** and from the request's billing block (§ Verified facts 3). An existing customer's master data is **never** overwritten: «Tomar» only adds missing address-book entries to it.
- Sales countries (owner, 2026-11-03, spec §4.2 «Países de venta»): a list of 1–N ISO-3166-1 alpha-2 codes, any country the customer catalogue offers, validated in the backend with `IsKnownRegion` (§ Verified facts 17). The public billing and shipping blocks only accept those countries; another → 422 `store.order_request.country_not_sold`.
- City lanes (owner, 2026-11-03, spec §4.2 «Ciudad fuera de Colombia», option B): `CO` → `departmentId` + `cityId` from DIVIPOLA, no `cityName`; any other country → free-text `cityName` (≤ 120), no ids. The same rule in the store request, the address book (Task 1A) and the order parties (Task 1B). Public geography routes serve Colombia only.
- The billing block is fully required, `email` and the document included (owner, 2026-11-03). The shipping block is optional; when present, address, country, city (per lane) and phone are required and the recipient name falls back to the billing name.
- Address-book matching: same entry = same country **and** same normalized address (trimmed, case-insensitive, repeated whitespace collapsed) **and** same city — `cityId` in Colombia, the normalized `cityName` text elsewhere — **and** same phone in E.164 (both absent also matches). Never by `name`.
- Tasks 1A and 1B change existing modules. For `CO` and for every existing row, behaviour, HTTP shape and screens stay the same: new columns are nullable or default to `CO`, new request fields are optional and trailing.
- Store orders are delivered, never picked up: `isStorePickup = false`, `Billing` party from the billing block, `Shipping` party from the shipping block or, with «Usar los mismos datos», from the billing block.
- Cross-module work goes only through Store ports with Bootstrapper adapters: Store never references `Modules.Customers.*`, `Modules.Geography.*` or `Modules.Quotations.*` (`StoreLayerTests`).
- «Descartar sólo aplica a `New`.» Discard reasons are `Spam | Duplicate | Other`, the spec's «spam, duplicado, otro».
- Consent stores the date, the visitor IP, the policy URL in force, and the date that URL was set (spec §4.2).
- Audit happens in the handler, in the same transaction, through `IStoreAuditPublisher` with source `store`. Public submissions are audited as an integration, without an actor.
- Code comments explain **why**, in Spanish, using «tú». Identifiers are in English.

## Review Focus

1. **A cart that has gone stale in `localStorage`.** A variant is hidden, deactivated or lost its price in the chosen currency after it was added, or the visitor switched to a currency the variant has no price in. `cart/quote` must price the rest and report the stale line, never answer 422 for the whole cart. `order-requests` must reject it with a code the Astro app can act on. Tests: Task 10 `AStaleLineIsReportedAndTheRestIsPriced`, Task 10 `ASubmissionWithAStaleLineIs422`.
2. **Two advisors take the same request at the same moment.** Exactly one order exists and the other gets 409, with no second customer created. Tests: Task 12 `TwoConcurrentTakesCreateOneOrder`, Task 12 `TheLoserOfATakeCreatesNoCustomer`.
3. **A crash between creating the order and marking the request.** A retake must not create a second order, and the request ends `Converted` under the first taker. Test: Task 12 `ARetakeAfterACrashMarksTheExistingOrder`.
4. **Grouped scales across lines.** Two variants of the same product, whose quantities only reach a tier together, must get the same discount in the cart as in the order. Tests: Task 3 `TheQuoteAppliesGroupDiscountsAcrossLines`, Task 12 `TheTakenOrderTotalEqualsTheQuotedTotal`.
5. **A spoofed visitor IP.** A caller without the shared secret sends `X-Qep-Visitor-Ip` to escape its rate-limit bucket. Without a valid key the header is ignored and the request is 401. Test: Task 9 `TheVisitorIpHeaderIsIgnoredWithoutTheKey`.
6. **The wrong currency's account or prices.** A EUR request must become a EUR order charged to the EUR entry, never to the primary's account. A currency removed from the store between submission and «Tomar» must give 422 and leave the request `New`, with no lease, order or customer. A currency the catalog uses but the store does not offer must never leak products through `?currency=`. Tests: Task 12 `TakingAEurRequestUsesTheEurAccount`, Task 12 `TakingARequestWhoseCurrencyWasRemovedIs422AndKeepsItNew`, Task 10 `ACurrencyInUseButNotInTheStoreIs422`.
7. **The address book after «Tomar».** An existing customer's name, document, phone, email, address and city must stay unchanged. An address that is already there (other case, extra spaces, phone `300 123 4567` vs `+573001234567`) must not be added twice, and neither must a retake after a crash add it twice. An existing **incomplete** customer is completed first, filling only blank fields, and then receives the addresses like any other. Tests: Task 12 `TakingWithAnExistingCustomerNeverTouchesItsData`, `AnAddressAlreadyInTheBookIsReused`, `ADifferentShippingAddressIsAdded`, `ARetakeDoesNotAddTheAddressTwice`, `AnIncompleteCustomerIsCompletedOnlyInItsBlankFields`, `CompletingNeverOverwritesAnExistingValue`, `AddressesAreAddedAfterCompletion`, `AMergedOrInactiveCustomerIsStillRejected`.
8. **A country the store does not sell to, or a city outside its department.** The public submit must reject both before Turnstile, and a city id must belong to the department id sent with it. Tests: Task 11 `ABillingCountryTheStoreDoesNotSellToIs422`, `AShippingCountryTheStoreDoesNotSellToIs422`, `ACityOfAnotherDepartmentIs422`, `AForeignBlockWithDivipolaIdsIs422`.
10. **Existing Colombian data and screens after Tasks 1A and 1B.** Every existing address and party reads back exactly as before (country `CO`, same ids, same names), the customer form, the quotation editor, the order editor, the PDF and Shopify's city correction keep working, and a request without the new fields behaves as today. Tests: Task 1A `ExistingAddressesReadAsColombian`, `ACustomerPutWithoutCountryKeepsColombianAddresses`, Task 1B `AQuotationSavedWithoutCountryIsColombian`, `ConvertingAForeignQuotationCopiesItsCityText`, `ShopifyCityCorrectionIgnoresForeignAddresses`.
9. **The parties of the order.** Billing goes to `Billing`, shipping to `Shipping`; with «Usar los mismos datos» the shipping party repeats the billing data; `isStorePickup` is `false`; the billing tax profile is the chosen customer's; both parties carry the billing email. Tests: Task 12 `TheOrderCarriesTheBillingAndShippingParties`, `WithTheSameDataTheShippingPartyRepeatsTheBilling`, `TheBillingTaxProfileIsTheCustomers`.

---

## HTTP contract

This section is complete and self-contained. The backoffice and Astro plans are written from it alone.

**Conventions:**
- `datetime` is ISO-8601 UTC.
- `money` is a JSON number with up to 2 decimals.
- `T?` is sent as `null`, never omitted.
- Enums travel by name.
- Errors are RFC 7807 ProblemDetails with `code`. Validation 422s carry `code: "validation.failed"` and an `errors` map.

**Enums:**
- `StorePricingMode` = `RetailOnly | QuantityScales`. On screen: «Sólo detal» and «Con escalas por cantidad».
- `hexColor` = a string `#RRGGBB` (case-insensitive, answered uppercase). Malformed → 422 `store.settings.color_invalid`.
- `StoreColors` (resolved) = `{ primary: hexColor, onPrimary: hexColor, accentText: hexColor }`.
- `StoreColorChecks` = `{ onPrimaryOnPrimary: number, accentTextOnWhite: number, onPrimaryLowContrast: bool, accentTextLowContrast: bool }`. Ratios are WCAG 2.x contrast ratios rounded to 2 decimals; `lowContrast` = ratio < 4.5. Overrides with low contrast are **allowed** (owner, 2026-11-03): the backoffice warns, the backend never rejects them.
- `StorePublishBlocker` = `SlugRequired | DataPolicyRequired | CurrencyRequired | SalesCountryRequired`. `CurrencyRequired` = the currencies list is empty. `SalesCountryRequired` = the sales countries list is empty. A billing account is part of each entry and a mismatch is rejected on save, so neither can be a blocker of a saved store.
- `StoreAddressOutcome` = `Reused | Added`.
- `StoreOrderRequestStatus` = `New | Converted | Discarded`.
- `StoreInboxView` = `Untaken | TakenByMe | All`. On screen: «Sin tomar», «Tomados por mí» and «Todos».
- `StoreDiscardReason` = `Spam | Duplicate | Other`.
- `OrderSource` gains `Store`, labelled «Tienda» by the frontend.

### A. Backoffice: store settings

Base: `/api/v1/tenants/{tenantId}/store`. The tenant goes in the route **and** in `X-Tenant-Id`. Writes carry `X-Qep-Client: web`. Permission: `store.settings.manage`, and module `store` must be enabled.

**`StoreSettings`**

| Field | Type | Note |
| --- | --- | --- |
| `slug` | string? | |
| `storeUrl` | string? | `https://{slug}.lulocrm.com`, built by the backend; `null` without a slug |
| `isPublished` | bool | |
| `publishedAt` | datetime? | |
| `name` | string? | Override of the store name. `null` = use the tenant name |
| `effectiveName` | string | `name` or the tenant's display name |
| `logoUrl` | string? | The tenant's public logo, read-only («no se vuelve a subir») |
| `primaryColor` | hexColor? | Required on `PUT`; `null` only while never saved |
| `onPrimaryColor` | hexColor? | The override as saved; `null` = automatic |
| `accentTextColor` | hexColor? | The override as saved; `null` = automatic |
| `resolvedColors` | `StoreColors?` | What the storefront paints (overrides or automatic values). `null` while `primaryColor` is `null` |
| `colorChecks` | `StoreColorChecks?` | Contrast of `resolvedColors`; `null` with them |
| `banner` | `{ imageFileId: uuid?, imageUrl: string?, text: string? }` | `imageUrl` is `null` if the file is not public |
| `contact` | `{ whatsApp: string?, address: string?, schedule: string?, instagramUrl: string?, facebookUrl: string?, tiktokUrl: string? }` | |
| `dataPolicyUrl` | string? | |
| `dataPolicySetAt` | datetime? | Changes only when the URL changes |
| `currencies` | `StoreCurrency[]` | In the saved order. `[]` while none is set |
| `primaryCurrency` | string? | One of `currencies[].currency`; `null` iff `currencies` is empty |
| `pricingMode` | `StorePricingMode` | Default `RetailOnly`. One for the whole store, not per currency |
| `salesCountries` | string[] | ISO-3166-1 alpha-2, uppercase, in the saved order. `[]` while none is set. The admin picks them from the customer country catalogue (`features/customers/utils/countries.ts`) |
| `featuredCategories` | `{ id: uuid, name: string }[]` | In the saved order; deleted categories are dropped |
| `publishBlockers` | `StorePublishBlocker[]` | Empty = publishable. Computed by the backend |
| `version` | int | `0` while never saved. Sent back in `If-Match` |
| `updatedAt` | datetime? | |

**`StoreCurrency`** = `{ currency: string, billingAccount: { companyId: uuid, bankName: string, accountNumber: string, currency: string } }`. `billingAccount.currency` always equals `currency`.

| Method and route | Body | OK |
| --- | --- | --- |
| `GET /store/settings` | | 200 `StoreSettings` + `ETag` |
| `PUT /store/settings` + `If-Match` | `{ slug?, name?, primaryColor, onPrimaryColor?, accentTextColor?, bannerFileId?, bannerText?, whatsApp?, address?, schedule?, instagramUrl?, facebookUrl?, tiktokUrl?, dataPolicyUrl?, pricingMode, currencies: { currency: string, billingAccount: { companyId: uuid, bankName: string, accountNumber: string, currency: string } }[], primaryCurrency?, salesCountries: string[], featuredCategoryIds: uuid[] }` | 200 `StoreSettings` + `ETag` |
| `POST /store/settings/publish` + `If-Match` | | 200 `StoreSettings` |
| `POST /store/settings/unpublish` + `If-Match` | | 200 `StoreSettings` |
| `POST /store/settings/colors/preview` | `{ primaryColor: hexColor, onPrimaryColor: hexColor?, accentTextColor: hexColor? }` | 200 `{ resolvedColors: StoreColors, colorChecks: StoreColorChecks }` |
| `GET /store/settings/billing-accounts?currency` | | 200 `{ items: { companyId: uuid, companyName: string, bankName: string, accountNumber: string, currency: string }[] }` |

`billing-accounts` lists every bank account of the tenant's **active** companies (`CompanyBankAccount.Currency`, `CompanyBankAccount.cs:26`), ordered by company name then bank, filtered by `currency` when given (case-insensitive; malformed → 422 `store.settings.currency_invalid`). It needs `store.settings.manage`, not the companies permission, so the store admin can fill the selector. A currency with an empty list cannot be added (spec §4.2); the `PUT` still rechecks every account.

**Color preview.** `colors/preview` writes nothing and takes no `If-Match` (like the loans `POST …/preview`). It runs the same `StoreColorScheme.Resolve` as the saved settings and the public store, so what the backoffice previews is exactly what the store paints (spec §4.2). A missing or malformed color → 422 `store.settings.color_invalid`; low contrast is flagged, never rejected. Permission `store.settings.manage`, module `store`.

**Banner upload.** The banner is uploaded through Storage's existing pipeline (`POST /files`, presigned `PUT`, complete) with `ownerType: "Store"` and `ownerId` = the tenant id, then published with `PUT /files/{fileId}/publication`. `Store` is a new `FileOwnerType` (Task 8): owner types are an explicit enum persisted by name (`FileResourceEnums.cs:15-38`, `Product` for product images, `Tenant` for the logo), parsed strictly at upload (`StorageEndpoints.cs:88-95`), and each maps to its module in `FileOwnerModules.ByOwnerType` (`FileOwnerModules.cs:18-29`, a test requires every enum value there), so a `Store` file is hidden with the module off.

`currencies` is the whole desired list, not a delta: an entry left out is removed. `primaryCurrency` is required when `currencies` is not empty and must be one of them; with `[]` it must be `null`. Saving a published store re-checks the blockers, so a published store cannot drop its last currency (`store.settings.currency_required`). Removing a currency that has `New` requests is allowed; taking those requests gives 422 until the currency is back (§ D).

Currency checks run in this order, each entry in list order, and the first failure wins: format → `currency_invalid`; repeated → `currency_duplicate`; `billingAccount.currency ≠ currency` → `currency_mismatch`; primary not in the list → `primary_currency_invalid`; currency not in `currencies-in-use` → `currency_invalid`; then the account through the orders resolver (`quotation.billing.*`).

Settings limits:

| Field | Limit |
| --- | --- |
| `name` | 1–80 |
| `bannerText` | ≤ 200 |
| `whatsApp` | ≤ 32 |
| `address` | ≤ 200 |
| `schedule` | ≤ 200 |
| URL fields | ≤ 500, `https://` only |
| `featuredCategoryIds` | ≤ 12, distinct, tenant's categories |
| `currencies` | 0–10 entries, distinct by `currency` |
| `currencies[].currency` | 3 uppercase letters, one the catalog uses (`GET /catalog/currencies-in-use`) |
| `salesCountries` | 0–50 entries, 2 letters each (matched case-insensitively, stored uppercase), distinct, each a known region (`IsKnownRegion`) |

Settings errors:

| Status | `code` | When |
| --- | --- | --- |
| 403 | `authorization.denied` / `tenancy.module_not_enabled` | |
| 409 | `store.settings.slug_taken` | Another tenant has the slug |
| 412 | `concurrency.conflict` | Stale `If-Match` |
| 428 | `precondition.required` | No `If-Match` |
| 422 | `store.settings.slug_invalid` | Format |
| 422 | `store.settings.slug_reserved` | |
| 422 | `store.settings.color_invalid` | `primaryColor` missing or any color not `#RRGGBB`. Never for low contrast |
| 422 | `store.settings.url_invalid` | Any URL field |
| 422 | `store.settings.currency_invalid` | An entry's currency is malformed or has no priced product |
| 422 | `store.settings.currency_duplicate` | Two entries with the same currency |
| 422 | `store.settings.currency_mismatch` | An entry's account currency ≠ the entry's currency |
| 422 | `store.settings.primary_currency_invalid` | `primaryCurrency` missing with entries, present without them, or not one of them |
| 422 | `store.settings.category_not_found` | |
| 422 | `store.settings.banner_invalid` | File not of the tenant, not owner type `Store`, not an image, or not public |
| 422 | `quotation.billing.*` / 404 `quotation.billing.company_not_found` | Each entry's account, checked with the same resolver as orders |
| 422 | `store.settings.sales_country_invalid` | A sales country is malformed or not a known region |
| 422 | `store.settings.sales_country_duplicate` | The same country twice |
| 422 | `store.settings.slug_required`, `store.settings.data_policy_required`, `store.settings.currency_required`, `store.settings.sales_country_required` | Publishing (or saving while published) with that blocker, checked in this order |

### B. Backoffice: catalog visibility

Base: `/api/v1/tenants/{tenantId}/catalog`. Permissions are the existing ones.

- `ProductDto` and each item of `GET /catalog/products` gain `visibleInStore: bool`.
- `GET /catalog/products?visibleInStore=true|false` filters on it. Any other value is 422 `validation.failed`.
- `POST /catalog/products` and `PUT /catalog/products/{id}` accept `visibleInStore: bool?`. On `POST`, `null` means `false`. On `PUT`, `null` keeps the current value.
- `POST /catalog/products/store-visibility` (`catalog.product.manage`):
  - Body: `{ productIds: uuid[], visible: bool }`, with `productIds` distinct and 1–200 long.
  - Response: 200 `{ items: { id: uuid, visibleInStore: bool, version: int }[] }`.
  - All-or-nothing: one id that is unknown or belongs to another tenant → 404 `catalog.product.not_found`, and nothing changes.
  - Each changed product's `version` goes up, so an editor that is open gets 412 and reloads. Unchanged products keep their version.

### C. Backoffice: order detail and list

- `GET /orders/{orderId}` and every route that returns the order detail gain `storeRequest`. Its shape is `{ id: uuid, number: string, customerId: uuid, customerName: string, phone: string, phoneE164: string, email: string, displayedTotal: money, currency: string, totalDifference: money? }`, and it is `null` for sources other than `Store`. `totalDifference` = order total at conversion − `displayedTotal`, and it is `null` when they are equal. `customerName`, `phone`, `phoneE164` and `email` are the **billing** block's. «Escribir por WhatsApp» in the detail sends `storeRequest.customerId` to the existing `POST …/messaging/conversations`; `phoneE164` is for copying when that is not available.
- `GET /orders?source=Store` filters store orders.

### C2. Existing contracts that grow (Tasks 1A and 1B)

Additive only. A client that neither sends nor reads the new fields sees today's behaviour, because every existing row is Colombian.

- **Customer address book** (`/customers/{id}/addresses`, and `addresses` in `CustomerResponse`):
  - `CustomerAddressRequest` gains `country: string?` (default `CO`) and `cityName: string?`. `CO` (or absent): `cityId` required, `cityName` must be absent. Other: `cityName` required (≤ 120), `cityId` must be absent. Mismatch → 422 `validation.failed` with the field (`CityId` or `CityName`), like the customer master.
  - `CustomerAddressDto` gains `country: string`. For a foreign address `cityId`, `departmentId` and `departmentName` are `null` and `cityName` is the typed text; for `CO` they are as today.
- **Quotation and order parties** (`parties.billing` / `parties.shipping` in requests and responses of quotations, orders and order edits):
  - `QuotationPartyRequest` gains trailing `country: string?` and `cityName: string?`. `null` country = `CO`, today's behaviour.
  - Responses gain `country: string` (`CO` for every existing row) and `cityName: string?`.
  - A party is complete with name, phone, email and address plus, for `CO`, department and city ids, or, for another country, `cityName`. A lane mismatch → 422 `quotations.party.city_lane_invalid`.

### D. Backoffice: «Desde la tienda» inbox

Base: `/api/v1/tenants/{tenantId}/store/order-requests`. Permission: `quotations.order.manage`, and module `store` must be enabled.

**`StoreOrderRequestRow`**

| Field | Type | Note |
| --- | --- | --- |
| `id` | uuid | |
| `number` | string | `T-0001`, per tenant |
| `receivedAt` | datetime | |
| `status` | `StoreOrderRequestStatus` | |
| `billing` | `StoreRequestBilling` | As the visitor wrote it, plus the normalized phone and the resolved place names |
| `shipping` | `StoreRequestShipping?` | `null` = «Usar los mismos datos para el envío» was ticked |
| `customerId` | uuid? | The order's customer once `Converted`; `null` otherwise. «Escribir por WhatsApp» sends it to `POST …/messaging/conversations` |
| `isKnownCustomer` | bool | `true` if a tenant customer matches the billing phone or document («ya es cliente» / «cliente nuevo») |
| `lineCount` | int | |
| `unitCount` | number | |
| `hasScaleDiscount` | bool | «con escala aplicada» |
| `total` | money | Frozen at submission, in `currency` |
| `currency` | string | The currency the visitor chose. Each row has its own; the UI never adds totals of different rows |
| `note` | string? | |
| `convertedBy` | `{ memberId: uuid, displayName: string }?` | |
| `convertedAt` | datetime? | |
| `orderId` | uuid? | |
| `orderNumber` | string? | |
| `totalDifference` | money? | As in C |
| `discardedBy` | `{ memberId: uuid, displayName: string }?` | |
| `discardedAt` | datetime? | |
| `discardReason` | `StoreDiscardReason?` | |
| `discardNote` | string? | |
| `isBeingTaken` | bool | Someone holds the conversion lease right now; the UI disables «Tomar» |

**`StoreRequestBilling`** = `{ name: string, identificationType: "NIT"|"CC"|"CE"|"PASAPORTE", identificationNumber: string, phone: string, phoneE164: string, email: string, address: string, country: string, departmentId: uuid?, departmentName: string?, cityId: uuid?, cityName: string }`.

**`StoreRequestShipping`** = `{ recipientName: string?, effectiveRecipientName: string, phone: string, phoneE164: string, address: string, country: string, departmentId: uuid?, departmentName: string?, cityId: uuid?, cityName: string }`. `effectiveRecipientName` = `recipientName` or the billing name.
- `CO`: `departmentId`, `departmentName`, `cityId` set; `cityName` is the DIVIPOLA name. Other countries: the three are `null` and `cityName` is what the visitor typed. Place names are frozen at submission.

`StoreOrderRequestDetail` = `StoreOrderRequestRow` + `lines: { variantId: uuid, productId: uuid, name: string, quantity: number, unitPrice: money, discountPercentage: number, discountedUnitPrice: money, lineTotal: money }[]`.

| Method and route | OK |
| --- | --- |
| `GET /store/order-requests?view&page&pageSize` | 200 `{ items: StoreOrderRequestRow[], total: int, page: int, pageSize: int }` |
| `GET /store/order-requests/summary` | 200 `{ untaken: int, takenByMe: int, receivedToday: int }` |
| `GET /store/order-requests/{id}` | 200 `StoreOrderRequestDetail` |
| `GET /store/order-requests/{id}/customer-candidates` | 200 `CustomerCandidates` |
| `POST /store/order-requests/{id}/take` | 200 `{ request: StoreOrderRequestRow, orderId: uuid, orderNumber: string, customerId: uuid, customerCreated: bool, customerCompleted: bool, addresses: { billing: StoreAddressOutcome, shipping: StoreAddressOutcome? } }` |
| `POST /store/order-requests/{id}/discard` | 200 `StoreOrderRequestRow` |

List parameters:
- `view` defaults to `Untaken`.
- `Untaken` is `New`.
- `TakenByMe` is `Converted` by the caller.
- `All` is every status.
- `page` defaults to 1. `pageSize` defaults to 25, max 100.
- Ordered by `receivedAt` descending.

Summary:
- `untaken` feeds the counter on the «Desde la tienda» link.
- `receivedToday` counts by the tenant's calendar (`ITenantClock.Today`), every status.

**`CustomerCandidates`**: `{ candidates: { id: uuid, name: string, phoneE164: string?, identificationType: string?, identificationNumber: string?, isComplete: bool, matchedBy: ("Phone"|"Document")[] }[], suggestedCustomerId: uuid?, newCustomer: { name: string, identificationType: string, identificationNumber: string, phone: string, email: string, address: string, country: string, cityName: string, departmentName: string? } }`.
- `candidates` covers active, non-merged customers only.
- `suggestedCustomerId` is set **only** when exactly one distinct customer matched. Several customers, or the phone and the document pointing at different customers, gives `null`, so «nunca se elige solo en un caso ambiguo».
- `newCustomer` is a read-only preview of the customer «Tomar» would create from the billing block. The dialog does not edit it: it only offers the optional classification (§ Verified facts 3).

**Take body:** `{ customerId: uuid?, newCustomer: { classificationId: uuid? }? }`, with exactly one of the two present. No `If-Match`: the request's state is the concurrency check.
- `customerId`: an existing customer. A complete one is never changed; only missing addresses are added. An **incomplete** one is completed with the billing block, **filling only its blank fields** (owner, 2026-11-03, spec §5.2 «Cliente incompleto»): a name, document, phone, email, country or city it already has is kept. Then the addresses go to its book. The response's `customerCompleted: bool` says it happened.
- `newCustomer`: create a complete customer from the billing block (name, document, phone, email, address, country, city), with `classificationId` if given. If the document was taken by another customer meanwhile, that customer is used instead (`customerCreated: false`), exactly as with `customerId`.
- Then each address of the request (billing; shipping when present) is looked up in that customer's book (§ Global Constraints «Address-book matching»): `Reused` if found, `Added` if not (never principal unless it is the book's first). An incomplete customer is completed first (below), so it never skips. A new customer's billing address is the entry `CreateCompleteAsync` seeds, so it reads `Reused`.

**Discard body:** `{ reason: StoreDiscardReason, note: string? }`. `note` is required for `Other`, ≤ 500.

Inbox errors:

| Status | `code` | When |
| --- | --- | --- |
| 403 | `authorization.denied` / `tenancy.module_not_enabled` | |
| 404 | `store.order_request.not_found` | |
| 409 | `store.order_request.not_new` | Already converted or discarded; the UI re-reads |
| 409 | `store.order_request.being_taken` | Another advisor holds the lease |
| 409 | `store.order_request.conflict` | Lost a write race |
| 422 | `validation.failed` | Both or neither of `customerId`/`newCustomer`; note missing for `Other` |
| 422 | `store.order_request.customer_not_found` | `customerId` unknown, of another tenant, inactive or merged |
| 422 | `store.order_request.currency_not_in_store` | The request's currency is no longer one of the store's; the advisor adds it back in settings. Checked before the lease, so nothing is held or created |
| 422 | `customers.customer.*`, `customers.address.*`, `validation.failed` | From creating the new customer or adding an address, e.g. `customers.customer.classification_not_found`, `customers.customer.city_not_found` |
| 409 | `store.order_request.conflict` | The customer changed while its addresses were being added; retake |
| 409 | `store.order_request.customer_document_taken` | Completing an incomplete customer with the billing document, which another customer already has |
| 422 | `quotation.item.product_inactive` | Pricing errors of the order engine |
| 422 | `quotation.item.product_price_unavailable` | Pricing errors of the order engine |

The order it creates:

| Field | Value |
| --- | --- |
| `source` | `Store` |
| `advisorId` | The taker |
| `externalReference` | The request `number` |
| Billing account | The store's entry for the request's `currency`, as configured at take time |
| Currency | The request's (it follows from that account, `Order.cs:96`) |
| `isStorePickup` | `false` (owner, 2026-11-03) |
| `Billing` party | The billing block: name, identification type and number, phone, email, address, country and city per lane (Task 1B) |
| `Shipping` party | The shipping block (name = `effectiveRecipientName`) with the **billing email**, or the billing block when `shipping` is `null` |
| Billing tax profile | `billingWithRetention` / `billingVatSurplus` = the chosen customer's `withRetention` / `vatSurplus` (`false`/`false` for a new one) |
| `paymentStatus` | `PaymentPending` |
| `notes` | The visitor's note |
| `isRetail` / `globalScaleFloor` | From the pricing mode |
| Lines | The request's variants and quantities, re-priced by the engine |

### E. Public storefront API (Astro server → backend)

Base: `/api/v1/public/stores/{host}`, where `host` is the request hostname, e.g. `acme.lulocrm.com`, lowercased by the backend.

Required headers:
- `X-Qep-Storefront-Key: <shared secret>` on every route. Missing or wrong → 401 `store.public.client_unauthorized`.
- `X-Qep-Visitor-Ip: <visitor IP>`, required on `POST /order-requests`, recommended elsewhere. It is trusted only with a valid key.

No cookie, no `X-Qep-Client`, no CORS.

Unknown host, unpublished store, `store` module off, or inactive tenant → **404 `store.public.not_found`**, identical in every case («sin revelar si el tenant existe»).

`GET` routes answer `Cache-Control: public, max-age=60, stale-while-revalidate=300`, except the geography routes (below). `POST` routes answer `Cache-Control: no-store`. The currency travels in the query string, so each currency is a distinct URL and the Astro server caches one copy per currency.

**`currency`** is required on `GET /categories`, `GET /products`, `GET /products/{productId}` (query) and on `POST /cart/quote`, `POST /order-requests` (body). It is matched case-insensitively and answered uppercase. Missing, malformed, or not one of the store's currencies → 422 `store.public.currency_invalid`, also when the catalog has prices in it. `GET /` takes no currency.

Rate limits are fixed windows per visitor IP:

| Routes | Limit |
| --- | --- |
| `GET`s and `cart/quote` | 300/min |
| `order-requests` | 5 per 10 min |

Over the limit → 429. Request body ≤ 64 KB, otherwise 413.

| Method and route | OK |
| --- | --- |
| `GET /` | 200 `PublicStore` |
| `GET /categories?currency` | 200 `{ items: PublicCategory[] }` |
| `GET /products?currency&category&q&page&pageSize` | 200 `PublicProductPage` |
| `GET /products/{productId}?currency` | 200 `PublicProductDetail` |
| `POST /cart/quote` | 200 `PublicCartQuote` |
| `GET /geography/departments` | 200 `{ items: { id: uuid, name: string }[] }` |
| `GET /geography/departments/{departmentId}/cities` | 200 `{ items: { id: uuid, name: string }[] }` |
| `POST /order-requests` | 201 `PublicOrderRequestReceipt` |

**Geography routes** (spec §6, owner 2026-11-03): read-only, over the Geography module through a Store port, for the billing and shipping selects.
- They resolve the store like every public route (404 `store.public.not_found`), so an unpublished store leaks nothing.
- They serve **Colombia only** (DIVIPOLA): the Astro app calls them only when the chosen country is `CO`; for any other country the form shows a city text input (spec §4.2). No country parameter.
- Ordered by name. No DIVIPOLA code, no ids beyond `id` (DTOs of their own, § Global Constraints).
- An unknown `departmentId` → 404 `store.public.department_not_found`.
- `Cache-Control: public, max-age=3600, stale-while-revalidate=86400`: DIVIPOLA changes by migration, not by a tenant.
- Rate-limited with the read policy.

**`PublicStore`**: `{ name: string, logoUrl: string?, colors: StoreColors, regionalFormat: { timeZone: string, dateFormat: string, numberFormat: string, defaultCurrency: string, country: string }, banner: { imageUrl: string?, text: string? }, contact: { whatsApp: string?, whatsAppUrl: string?, address: string?, schedule: string?, instagramUrl: string?, facebookUrl: string?, tiktokUrl: string? }, dataPolicyUrl: string, currencies: string[], primaryCurrency: string, salesCountries: string[], pricingMode: StorePricingMode, featuredCategories: { id: uuid, name: string }[] }`.
- `salesCountries` holds 1 or more ISO-3166-1 alpha-2 codes in the saved order (`["CO"]` today). With one, the Astro app shows no country selector (spec §4.2).
- `colors` are the resolved ones only; overrides, ratios and flags stay in the backoffice.
- `regionalFormat` comes from `ITenantDirectory.GetRegionalSettingsAsync` (`ITenantDirectory.cs:35-47`), the same source and the same values `/auth/me` publishes per tenant (`timeZone`, `dateFormat`, `numberFormat`, `defaultCurrency`, `country`; `AuthSessionEndpoints.cs:172-183`, `SessionEstablisher.cs:95-127`). The storefront formats with it and never writes `'es-CO'`. A tenant without a regional row is not publishable in practice; if it happens, the backend answers the Colombian defaults the SPA uses.
- `whatsAppUrl` = `https://wa.me/{digits of the E.164}`.
- `currencies` holds 1 or more codes in the saved order; `primaryCurrency` is one of them. With one currency the Astro app shows no selector (spec §4.1). Billing accounts never leave the backend.
- `featuredCategories` keeps only categories that still have visible products in **at least one** store currency. Per-currency emptiness is the Astro app's to read from `GET /categories?currency`.

**`PublicCategory`**: `{ id: uuid, name: string, parentId: uuid?, productCount: int }`.
- `productCount` counts visible products with a price in the requested `currency`, the category's own plus all its descendants', accumulated by the backend.
- Categories with 0 are omitted. Their visible children keep their `parentId` only if the parent is also present; otherwise `parentId` is `null`.

**`PublicProductPage`**: `{ items: { id: uuid, name: string, imageUrl: string?, priceFrom: money, priceTo: money, hasVariants: bool }[], total: int, page: int, pageSize: int }`.
- `category` (uuid) includes descendants. An unknown or empty category gives an empty page, never 404.
- `q` is ≤ 100 characters, matched like the POS search.
- `pageSize` defaults to 24, max 48. Ordered by name.
- Only products with at least one sellable variant priced in `currency` are listed. `priceFrom` and `priceTo` are the min and max base price, in that currency, of those variants.

**`PublicProductDetail`**:
- `{ id: uuid, name: string, description: string?, images: string[], options: { name: string, values: { id: uuid, name: string }[] }[], variants: PublicVariant[] }`.
- `PublicVariant` = `{ id: uuid, name: string, optionValueIds: uuid[], price: money, imageUrl: string?, packagingUnits: int[], scales: { fromUnit: int, toUnit: int, discount: number, final: money, restriction: ("Multiple"|"PackagingUnit")?, multiple: int?, allowGrouping: bool }[] }`.
- `variants` holds only sellable variants priced in `currency`; `price` and `final` are in that currency.
- `scales` is `[]` in `RetailOnly`. `final` is `PriceScale.FinalFor(price, discount)`, the same value the catalog shows.
- A hidden or inactive product, or one with no sellable variant priced in `currency` → 404 `store.public.product_not_found`.

**`POST /cart/quote`** body: `{ currency: string, lines: { variantId: uuid, quantity: int }[] }`. `lines` has 1–100 entries; `quantity` is 1–100000. A repeated `variantId` is summed into one line.

**`PublicCartQuote`**:
- `{ currency: string, lines: PublicQuotedLine[], unavailable: { variantId: uuid, reason: "NotAvailable" }[], subtotal: money, discountAmount: money, taxAmount: money, total: money }`.
- `PublicQuotedLine` = `{ variantId: uuid, productId: uuid, name: string, imageUrl: string?, quantity: int, unitPrice: money, discountPercentage: number, discountedUnitPrice: money, lineTotal: money, scaleApplied: bool }`.
- `currency` echoes the requested one; every amount is in it.
- `unavailable` lists lines whose variant is no longer sellable or visible, or has no price in `currency` (the visitor switched currency). They are left out of the totals and the Astro app marks them, never deletes them (spec §4.1).
- With every line unavailable, `lines` is `[]` and the totals are 0.

**`POST /order-requests`** body:
- `{ clientRequestId: uuid, turnstileToken: string, currency: string, billing: PublicBillingInput, shipping: PublicShippingInput?, note: string?, acceptsDataPolicy: bool, lines: { variantId: uuid, quantity: int }[] }`.
- `PublicBillingInput` = `{ name: string, identificationType: string, identificationNumber: string, phone: string, email: string, address: string, country: string, departmentId: uuid?, cityId: uuid?, cityName: string? }`. Everything is required, the city per lane.
- `PublicShippingInput` = `{ recipientName: string?, phone: string, address: string, country: string, departmentId: uuid?, cityId: uuid?, cityName: string? }`. Send `null` when «Usar los mismos datos para el envío» is ticked (the default). `recipientName` empty = the billing name.
- `country` is matched case-insensitively and must be one of the store's `salesCountries`. City lane: with `CO`, `departmentId` and `cityId` are required, `cityId` must be a city of `departmentId`, and `cityName` must be absent; with any other country, `cityName` is required and both ids must be absent. The backend freezes the department and city names.
- The backend checks Turnstile, re-prices the cart in `currency` and freezes the result with that currency.
- `clientRequestId` makes it idempotent: the same id from the same store returns the first receipt, with 201.

Order-request limits:

| Field | Limit |
| --- | --- |
| `billing.name` | 1–150 |
| `billing.identificationType` | Required, one of `NIT`, `CC`, `CE`, `PASAPORTE` (`IdentificationType.cs:27-30`) |
| `billing.identificationNumber` | Required, 1–32 |
| `billing.phone`, `shipping.phone` | Required, ≤ 32; must contain the country code («con indicativo», leading `+`) |
| `billing.email` | Required, ≤ 254, valid shape |
| `billing.address`, `shipping.address` | Required, 1–200 (the address-book limit, `CustomerAddress.AddressMaxLength`) |
| `shipping.recipientName` | Optional, ≤ 120 (`CustomerAddress.NameMaxLength`) |
| `*.country` | Required, 2 letters |
| `*.departmentId`, `*.cityId` | `CO` only: required, non-empty |
| `*.cityName` | Other countries only: required, 1–120 (`CustomerContactInfo.CityNameMaxLength`) |
| `note` | ≤ 1000 |
| `lines` | Same as `cart/quote` |

**`PublicOrderRequestReceipt`**: `{ id: uuid, number: string, total: money, currency: string }`. The Astro app shows «Recibimos tu pedido. Te escribiremos por WhatsApp.» with `number`.

Public errors:

| Status | `code` | When |
| --- | --- | --- |
| 401 | `store.public.client_unauthorized` | |
| 404 | `store.public.not_found` | |
| 404 | `store.public.product_not_found` | |
| 413 | | Body too large |
| 422 | `validation.failed` | Field rules above |
| 422 | `store.public.currency_invalid` | `currency` missing, malformed or not one of the store's |
| 422 | `store.order_request.consent_required` | `acceptsDataPolicy` is not `true` |
| 422 | `store.order_request.captcha_invalid` | Turnstile says no, or its `hostname` ≠ `host` |
| 404 | `store.public.department_not_found` | `GET /geography/departments/{id}/cities` with an unknown department |
| 422 | `store.order_request.country_not_sold` | A billing or shipping `country` not in the store's `salesCountries` |
| 422 | `store.order_request.city_invalid` | `CO`: `cityId` unknown or not a city of `departmentId`. Any lane mismatch: ids sent for another country, or `cityName` sent for `CO`. Field shape errors stay `validation.failed` |
| 422 | `store.order_request.phone_invalid` | A billing or shipping phone does not normalize to E.164 in its block's country |
| 422 | `store.order_request.line_unavailable` | Any line unavailable; the visitor re-quotes |
| 429 | | Rate limit |
| 429 | `store.order_request.daily_cap` | More than 500 requests received by this store in the last 24 h |
| 503 | `store.captcha.unavailable` | Cloudflare unreachable or 5xx |

---

## File structure

```
src/Modules/Store/
  Modules.Store.Domain/        OnlineStore.cs, StoreSlug.cs, StoreColor.cs, StoreColorScheme.cs, StorePricingMode.cs, StorePublishBlocker.cs,
                               StoreBillingAccount.cs, StoreCurrency.cs, StoreContact.cs, StoreDomain.cs, StoreOrderRequest.cs,
                               StoreOrderRequestLine.cs, StoreOrderRequestStatus.cs, StoreDiscardReason.cs,
                               StoreConsent.cs, StoreRequestBilling.cs, StoreRequestShipping.cs, StoreCountry.cs,
                               StoreDomainException.cs, StoreErrorCodes.cs
  Modules.Store.Application/   ports (IStore*.cs), StorePermissions.cs, StoreAuthorization.cs, StoreAuditActions.cs,
                               StoreDtos.cs, PublicStoreDtos.cs, settings handlers, public handlers, inbox handlers,
                               StoreOptions.cs
  Modules.Store.Infrastructure/ Persistence/ (StoreDbContext, repositories, StoreUnitOfWork, StoreAuditPublisher,
                               RequestNumberGenerator, Migrations/), Captcha/CloudflareTurnstileVerifier.cs,
                               StoreInfrastructureExtensions.cs, StoreDatabaseInitializer.cs, StoreOptionsValidator.cs
  Modules.Store.Api/           StoreEndpoints.cs (backoffice), PublicStoreEndpoints.cs (storefront)
src/Bootstrapper/              StoreCatalogReader.cs, StoreOrdersAdapter.cs, StoreCustomerDirectory.cs,
                               StoreMemberDirectory.cs, StoreFileLookup.cs, StoreTenantLookup.cs,
                               StorePhoneNormalizer.cs, StoreGeography.cs, OrderStoreRequestLookup.cs, StorefrontPaths.cs,
                               StorefrontClient.cs
tests/Modules/Store/           Modules.Store.UnitTests/, Modules.Store.IntegrationTests/ (StoreApiHarness.cs + one class per task)
tests/ArchitectureTests/ArchitectureTests/StoreLayerTests.cs
```

---

### Task 1: Module key `store` in Tenancy

**Files:**
- Create: `docs/superpowers/specs/2026-11-03-tienda-publica-design.md`, a verbatim copy of the spec, and this plan, both in this commit.
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs:76-101`, `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:122`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<timestamp>_AddStoreModuleKey.cs` (+ Designer)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`, `OperatorReadHandlerTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs`, `OperatorConsoleApiTests.cs`, `SeedStartupTests.cs`. These are the same files the `loans` key touched; use `rg -n '"loans"' tests/Modules/Tenancy` to find each list.

**Interfaces:**
- Produces: `TenantModuleKeys.Store` (`"store"`), `DependenciesOf(Store) == [Orders]`.

- [ ] **Step 1: Write the failing tests.** Append `"store"` to every contract-ordered list after `"loans"`, and to the alphabetical lists of `SeedStartupTests` between `"shopify"` and `"suppliers"`. Add it to the "off" module sets. Rename `LoansIsLastDependsOnCustomersAndIsOffForNewTenants` to `LoansDependsOnCustomersAndIsOffForNewTenants` and drop its `All[^1]` assertion.

```csharp
[Fact]
public void StoreIsLastDependsOnOrdersAndIsOffForNewTenants()
{
    Assert.Equal("store", TenantModuleKeys.All[^1].Value);
    Assert.Equal([TenantModuleKeys.Orders], TenantModuleKeys.DependenciesOf(TenantModuleKeys.Store));
    Assert.DoesNotContain(TenantModuleKeys.Store, TenantModuleKeys.DefaultForNewTenants);
}
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter FullyQualifiedName~TenantModuleKeysTests` → FAIL: `TenantModuleKeys` has no `Store`.
- [ ] **Step 3: Implement.**
  - Add the key with a `<summary>` citing spec §4.2, last in `All`, with `[Store] = [Orders]` and the comment «una solicitud se convierte en pedido».
  - Add `'store'` to the CHECK.
  - Generate `AddStoreModuleKey`, shaped like the `AddLoansModuleKey` migration. Its `Down` deletes `WHERE module_key = 'store'` before restoring the CHECK. Check that the migration touches only the CHECK.
- [ ] **Step 4: Run to verify GREEN.**
  - `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~TenantModuleKeysTests|FullyQualifiedName~OperatorReadHandlerTests"` → PASS.
  - Then `$env:Seed__Enabled = "false"` and `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~SeedStartupTests"` → PASS.
- [ ] **Step 5: Commit.** `feat(tenancy): add store module key`

---

### Task 1A: Foreign addresses in the customer address book (Customers)

Spec §4.2 «Ciudad fuera de Colombia» (owner, 2026-11-03, option B). Existing module; for Colombian data nothing changes (§ Global Constraints, Review Focus 10). Named 1A, not renumbered, so the task numbers of § HTTP contract and Review Focus stay valid.

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerAddress.cs` (`Country`, `CityId` → `Guid?`, `CityName`; `CustomerAddressDetails` gains `string Country { get; init; } = Customer.ColombiaCountryCode` and `string? CityName`, and `Normalized()` enforces the lane), `Customer.cs:504` (`CorrectCityAsync`'s principal-address branch: only a Colombian address), `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs:30` (`CustomerAddressRequest` + `CustomerAddressDto`), `ManageCustomerAddresses.cs` (`CustomerAddressWriteRules`, shared by `AddCustomerAddressCommand` **and** `UpdateCustomerAddressCommand` (`:18`, `:27`), the same two-lane rules as `CustomerWriteRules.cs:127-134`; `UpdateCustomerAddressHandler` passes country and city text to `Customer.UpdateAddress`, which goes through the same `Apply`/`Normalized()`), `CustomerMapping.cs` (DTO: `null` city/department for a foreign address; city ids batch-resolved only for Colombian ones), `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs:274-310`
- Modify, consumers of `CustomerAddress.CityId` that must accept `null`: `src/Bootstrapper/QuotationCustomerLookup.cs` (carries `Country`/`CityName` to the party prefill, Task 1B), `CustomerReportSource.cs`, `ShopifyCustomerDirectory.cs`, `CustomerRepository.cs` (city/department filters skip foreign rows), `ExportCustomers.cs`
- Create: `.../Customers.Infrastructure/Persistence/Migrations/<timestamp>_AddForeignCustomerAddresses.cs`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerAddressTests.cs`, `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerAddressesApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs`

**Interfaces:**
- Produces:
  - `CustomerAddress.Country` (2, uppercase), `CustomerAddress.CityId: Guid?`, `CustomerAddress.CityName: string?` (≤ `CustomerContactInfo.CityNameMaxLength`, 120).
  - Lane rule in `CustomerAddressDetails.Normalized()`: `CO` → `CityId` non-empty and `CityName` null (`customers.address.city_required`, the existing code); other → `CityName` non-blank (`customers.address.city_required`) and `CityId` null (`customers.address.city_lane_invalid`). Country: 2 ASCII letters (`customers.address.country_invalid`).
  - `CustomerAddressRequest(…, string? Country = null, string? CityName = null)`, `CityId` becomes `Guid?`; the same body serves `POST` and `PUT …/addresses/{addressId}`, so a foreign address is editable, not only creatable (owner, 2026-11-03). `CustomerAddressDto` gains `Country`; `CityId`, `DepartmentId`, `DepartmentName` become nullable.
- Not changed: `CreateCompleteAsync` and the `POST /customers` seeding still create no address for a foreign customer (`CustomerProvisioning.cs:376`); Task 12 adds the store's foreign addresses explicitly. Fewer behaviours change that way.

Migration `AddForeignCustomerAddresses`: `country varchar(2) NOT NULL DEFAULT 'CO'` (backfills every row), `city_id` drops `NOT NULL`, `city_name varchar(120) NULL`, `CK_customer_addresses_city_lane` = `(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)`. `IX_customer_addresses_city` stays (Postgres indexes nulls). `Down` fails loudly (`RAISE EXCEPTION`) if a foreign row exists.

- [ ] **Step 1: Write the failing tests.**

```csharp
// Unit
[Fact] public void AColombianAddressNeedsACityIdAndNoCityName();
[Fact] public void AForeignAddressNeedsACityNameAndNoCityId();      // ES + CityId → customers.address.city_lane_invalid
[Fact] public void AnAddressWithoutCountryIsColombian();             // details without Country → Country "CO"
// Api
[Fact] public async Task ExistingAddressesReadAsColombian();          // row inserted before the migration shape → country "CO", same ids and names
[Fact] public async Task AForeignAddressRoundTrips();                 // POST { country: "ES", cityName: "Madrid" } → cityId null, departmentId null, cityName "Madrid"
[Fact] public async Task AColombianAddressRequestWithoutCountryIsUnchanged(); // today's body → 201, same DTO plus country "CO"
[Fact] public async Task ALaneMismatchIs422WithItsField();            // ES + cityId → validation.failed errors["CityId"]
[Fact] public async Task AForeignAddressCanBeEdited();                    // PUT address ES "Madrid" → "Barcelona", other street → 200, cityName "Barcelona"
[Fact] public async Task AnAddressCanMoveBetweenLanes();                  // PUT CO→ES with cityName and no cityId → 200; ES→CO with cityId → 200
[Fact] public async Task EditingWithALaneMismatchIs422();                 // PUT ES + cityId → validation.failed errors["CityId"]
[Fact] public async Task ACustomerPutWithoutCountryKeepsColombianAddresses();
[Fact] public async Task CityFiltersIgnoreForeignAddresses();         // GET /customers?cityIds=… unchanged
// Bootstrapper
[Fact] public async Task ShopifyCityCorrectionIgnoresForeignAddresses();
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter FullyQualifiedName~CustomerAddressTests` → FAIL: `Country` does not exist.
- [ ] **Step 3: Implement.** Domain and rules first, then mapping, then the migration. Generate it with `dotnet ef migrations add AddForeignCustomerAddresses --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations`, and check it touches only `customer_addresses`.
- [ ] **Step 4: Run to verify GREEN.** The Step 2 filter; then `$env:Seed__Enabled = "false"` and `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerAddresses|FullyQualifiedName~CustomerProvisioning|FullyQualifiedName~ListCustomers"`; then `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~Shopify` → PASS.
- [ ] **Step 5: Commit.** `feat(customers): store foreign addresses with a free-text city`

---

### Task 1B: Foreign places in quotation and order parties (Quotations)

Same rule for the `Billing` and `Shipping` parties. Existing module; for Colombian parties nothing changes.

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPartyDetails.cs` (`string? Country`, `string? CityName`; `null` country = `CO`), `QuotationParty.cs:76-82` and `OrderParty.cs:43-51` (`Country`, `CityName`, lane-aware `IsComplete`; `ToDetails` copies both, so quotation → order conversion carries them), `Order.cs` (`EnsureCreatable` and `SetBilling`/shipping edits use the lane-aware completeness), `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:210-222` (`QuotationPartyRequest` trailing `string? Country = null, string? CityName = null`; party response gains both), `QuotationMapping.cs:161`, `OrderMapping.cs`, `QuotationResponseComposer.cs`, `OrderResponseComposer.cs` (department/city names resolved only for Colombian parties), `QuotationChangeSummary.cs`, `OrdersExportProcessor.cs` (city column = `cityName` for foreign parties), the PDF renderer (place line `{cityName}, {country}` for foreign parties), `IQuotationCustomerLookup.cs` + `src/Bootstrapper/QuotationCustomerLookup.cs` (address prefill carries `Country`/`CityName`), `QuotationsDbContext.cs:311-359, 720-763`
- Not changed: `ExternalShippingParty` and every Shopify path (Shopify parties stay Colombian).
- Create: `.../Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddForeignPartyPlaces.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPartyTests.cs`, `OrderPartyTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationPartiesApiTests.cs`, `OrderEditsApiTests.cs`, `QuotationPdfTests.cs` (or the existing PDF test class)

**Interfaces:**
- Produces: `QuotationPartyDetails.Country`/`CityName`; `OrderParty.Country`/`CityName`; `OrderParty.IsComplete` = name, phone, email, address, and (`CO`: `DepartmentId` and `CityId`; other: `CityName`). Lane mismatch → `QuotationsDomainException("quotations.party.city_lane_invalid")` with the field in `errors` (`Parties.Billing.CityId` style, like `BillingPartyFiscalRules.BillingIdentificationTypeKey`).

Migration `AddForeignPartyPlaces`: on `quotation_parties` and `order_parties`, `country varchar(2) NULL` and `city_name varchar(120) NULL`. No backfill: `null` reads as `CO`. No CHECK on the lane (parties can be half-filled while a quotation is a draft; completeness is a domain rule).

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact] public void AColombianPartyIsCompleteOnlyWithItsIds();          // today's rule, null country
[Fact] public void AForeignPartyIsCompleteWithACityName();             // ES, CityName "Madrid", no ids → IsComplete
[Fact] public void AForeignPartyWithDivipolaIdsIsRejected();           // quotations.party.city_lane_invalid
[Fact] public async Task AQuotationSavedWithoutCountryIsColombian();   // today's body → response country "CO", cityName null
[Fact] public async Task AForeignBillingPartyRoundTrips();             // country "ES", cityName → same back, departmentName null
[Fact] public async Task ConvertingAForeignQuotationCopiesItsCityText(); // order parties country "ES", cityName kept
[Fact] public async Task TheOrderEditorAcceptsAForeignShippingParty();
[Fact] public async Task ThePdfPrintsTheForeignCity();
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationPartyTests|FullyQualifiedName~OrderPartyTests"` → FAIL.
- [ ] **Step 3: Implement.** Domain first, then mapping and composers, then the migration (check it touches only the two party tables).
- [ ] **Step 4: Run to verify GREEN.** The Step 2 filter; then `$env:Seed__Enabled = "false"` and `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~Parties|FullyQualifiedName~OrderEdits|FullyQualifiedName~Pdf|FullyQualifiedName~ConvertQuotation"` → PASS.
- [ ] **Step 5: Commit.** `feat(quotations): allow foreign places in billing and shipping parties`

---

### Task 2: `OrderSource.Store` and the store order factories

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/OrderSource.cs`, `Order.cs:371-463` (factories), `:503-519` (`EnsureCreatable`) and `:1190` (`IsExternal`), `src/Modules/Quotations/Modules.Quotations.Application/OrderPricingRecalculation.cs:26`, `OrderChangeSummary.cs:12-14`, `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs:493`
- Modify: `src/Modules/Reporting/Modules.Reporting.Domain/ReportFilterEnums.cs:25-31`, `src/Bootstrapper/OrdersReportSource.cs:585-591`, and `CompleteBySource` (`:595`) if it enumerates a fixed count
- Create: `.../Quotations.Infrastructure/Persistence/Migrations/<timestamp>_AddStoreOrderSource.cs`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderStoreFactoryTests.cs`, `OrderPricingRecalculationTests.cs`, `tests/Modules/Reporting/Modules.Reporting.UnitTests/ReportFilterParserTests.cs`, `OrdersReportSummaryHandlerTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/ExternalOrderServiceTests.cs`

**Interfaces:**
- Produces:
  - `OrderSource.Store`, appended last.
  - `OrderSourceFilter.Store`, appended after `Pos`.
  - `public static Order Order.CreateFromStore(OrderId id, Guid tenantId, string orderNumber, OrderHeader header, QuotationParties parties, QuotationBillingAccount billingAccount, IReadOnlyCollection<OrderLineInput> lines, string? notes, MemberId createdBy, DateTimeOffset occurredAt, string requestNumber, string externalKey)`: `Source = Store`, `PaymentPending`, no proofs, `ExternalReference = requestNumber`, `ExternalKey = externalKey`. It runs `EnsureCreatable()` unchanged: items, billing identification, tax profile and complete parties, email included (§ Verified facts 12).
  - `public static Order Order.QuoteForStore(Guid tenantId, string currency, bool isRetail, IReadOnlyCollection<OrderLineInput> lines, DateTimeOffset occurredAt)`: never persisted. It has no client, no parties, the customer flags off and no billing account, so no retention or VAT surplus (spec §5.2). It skips the `order.order.client_required` check through a private constructor flag. This is the only path where `ClientId` may be empty, and `QuoteForStore` is the only caller.
  - `OrderChangeSummary.CreatedFromStore(string requestNumber)` → `$"Creado desde la tienda (solicitud {requestNumber})."`.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact] public void CreateFromStoreSetsSourceReferenceAndKey();      // Source == Store, ExternalReference "T-0001", ExternalKey "store-request:<id>"
[Fact] public void AStoreOrderIsNotExternal();                       // AttachExternalShipping throws order.order.source_invalid
[Fact] public void CreateExternalRejectsTheStoreSource();            // ExternalOrderErrors.SourceInvalid
[Fact] public void QuoteForStoreHasNoClientAndIsNeverAManualOrder(); // ClientId == Guid.Empty, Source == Store, BillingAccount null
[Fact] public void AStoreOrderIsDeliveredWithBothParties();         // Billing and Shipping with the billing email → created, IsStorePickup false
[Fact] public void AStoreOrderWithoutAPartyEmailIsRejected();       // Shipping Email null → order shipping party incomplete code
[Fact] public void AStoreOrderStillRequiresTheBillingTaxProfile();  // BillingWithRetention null → order.billing.tax_profile_required
[Fact] public void AStoreOrderStillRequiresTheShippingAddress();    // Shipping without CityId → order.shipping party incomplete code
// OrderPricingRecalculationTests
[Fact] public async Task AStoreOrderUsesTheTenantRoundingRule();     // fake ITenantPriceRounding returns a non-default rule; order.PriceRounding equals it
// ReportFilterParserTests
[Fact] public void StoreIsTheLastSourceAfterPos();                   // Enum.GetValues<OrderSourceFilter>()[^1] == Store, [^2] == Pos
```

Also add `OrderSource.Store` to the "unknown source" negative cases in `ExternalOrderServiceTests`.

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderStoreFactoryTests|FullyQualifiedName~OrderPricingRecalculationTests"` → FAIL: `Store` is not defined.
- [ ] **Step 3: Implement.**
  - `IsExternal => Source is OrderSource.Shopify`. Spell it as a positive list with a comment: Store is priced and paid like an internal order (§ Verified facts 6).
  - `CreateExternal` rejects `Quotation or Manual or Store`.
  - The rounding branch becomes `order.Source is OrderSource.Quotation or OrderSource.Manual or OrderSource.Store`.
  - CHECK `source IN ('Quotation','Manual','Shopify','Store')`. Migration `AddStoreOrderSource` touches only that CHECK, and its `Down` fails loudly if store orders exist (`RAISE EXCEPTION`), like other CHECK narrowings in the repo.
  - Map `OrderSourceFilter.Store => OrderSource.Store` in `OrdersReportSource.MapSource`.
- [ ] **Step 4: Run to verify GREEN.** The Step 2 filter, plus `dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests` → PASS.
- [ ] **Step 5: Commit.** `feat(quotations): add the Store order source`

---

### Task 3: `IStoreOrderGateway`: quote and create with the order engine

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IStoreOrderGateway.cs` (records + interface + `StoreOrderGateway`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrderDraftBuilder.cs`. Extract the body of `PriceAsync` (`:116-154`) into `internal Task<List<OrderLineInput>> PriceLinesAsync(Guid tenantId, IReadOnlyList<OrderItemAddition> items, string currency, bool isRetail, CancellationToken ct)`. `PriceAsync` calls it, and nothing else changes for the manual order.
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:783`, adding `services.AddScoped<IStoreOrderGateway, StoreOrderGateway>();`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/StoreOrderGatewayTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/StoreOrderGatewayTests.cs`

**Interfaces:**
- Produces, all `public` in `Modules.Quotations.Application`:
  - `sealed record StoreQuoteLine(Guid VariantId, decimal Quantity);`
  - `sealed record StoreQuoteRequest(Guid TenantId, string Currency, bool IsRetail, IReadOnlyList<StoreQuoteLine> Lines);`
  - `sealed record StoreQuotedLine(Guid VariantId, Guid ProductId, string Name, decimal Quantity, decimal UnitPrice, decimal DiscountPercentage, string DiscountOrigin, decimal DiscountedUnitPrice, decimal LineTotal);`. `LineTotal` = the item's `Subtotal + TaxAmount`. The item price is VAT-included, so this is the amount the line adds to `Total`.
  - `sealed record StoreQuote(string Currency, IReadOnlyList<StoreQuotedLine> Lines, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount, decimal Total);`
  - `sealed record StoreOrderParty(string Name, string Phone, string Email, string Address, string Country, Guid? DepartmentId, Guid? CityId, string? CityName, string? IdentificationType, string? IdentificationNumber);` Identification only on billing; `Email` is the billing email on both parties. City per lane (Task 1B).
  - `sealed record StoreOrderDraft(Guid TenantId, Guid RequestId, string RequestNumber, Guid ClientId, Guid AdvisorMembershipId, bool IsRetail, QuotationBillingAccountRequest BillingAccount, StoreOrderParty Billing, StoreOrderParty Shipping, bool BillingWithRetention, bool BillingVatSurplus, IReadOnlyList<StoreQuoteLine> Lines, string? Notes);` `Shipping` is never null: Store passes the billing data when the visitor ticked «Usar los mismos datos».
  - `sealed record StoreOrderResult(Guid OrderId, string OrderNumber, decimal Total, string Currency, Guid AdvisorMembershipId, bool AlreadyExisted);`
  - `interface IStoreOrderGateway { Task<StoreQuote> QuoteAsync(StoreQuoteRequest request, CancellationToken ct); Task<StoreOrderResult> CreateAsync(StoreOrderDraft draft, CancellationToken ct); }`
  - `StoreOrderGateway.ExternalKeyFor(Guid requestId) => $"store-request:{requestId}"`, a public static.

- [ ] **Step 1: Write the failing tests.** Unit tests use the fakes of `OrderPricingRecalculationTests`. Integration tests run against Postgres with a seeded catalog, customer and company, using `OrderShippingApiTests`' harness.

```csharp
// Unit
[Fact] public async Task TheQuoteAppliesGroupDiscountsAcrossLines();
// two variants, same product, scale 12–999 at 10% with AllowGrouping, quantities 6 + 6 → both lines DiscountPercentage 10
[Fact] public async Task ARetailQuoteHasNoDiscount();                 // IsRetail true → every DiscountPercentage 0, origin "Own"
[Fact] public async Task TheQuoteHonoursTheMinimumPurchaseSweep();   // settings minimum not met → discounts back to 0
[Fact] public async Task TheLineTotalsAddUpToTheTotal();             // Lines.Sum(LineTotal) == Total for a fixture without retention
[Fact] public async Task AQuoteWithAnInactiveVariantThrowsTheEngineCode(); // QuotationsDomainException "quotation.item.product_inactive"
// Integration
[Fact] public async Task CreateMakesAPendingDeliveredStoreOrderForAnIncompleteCustomer();
// Source "Store", IsStorePickup false, PaymentPending, AdvisorId == draft advisor, history "Creado desde la tienda (solicitud T-0001)."
[Fact] public async Task CreateStoresTheBillingAndShippingParties();   // Billing = draft.Billing incl. CC + number; Shipping = draft.Shipping; PartyWithRetention/PartyVatSurplus = draft flags
[Fact] public async Task CreateStoresAForeignShippingParty();          // Shipping ES + CityName → order Shipping country "ES", cityName kept
[Fact] public async Task ABillingPartyWithRetentionAppliesRetention(); // BillingWithRetention true on a COP order → AppliesRetention, total differs from QuoteAsync
[Fact] public async Task CreateIsIdempotentByRequest();               // second call: AlreadyExisted, same OrderId, one row, one number consumed
[Fact] public async Task TheCreatedOrderTotalEqualsTheQuote();        // same lines, same customer flags off → Total equal
[Fact] public async Task CreatePublishesOrderCreatedAndAudits();      // outbox row + audit "quotation.order.created"
[Fact] public async Task TheCreatedOrderIsInItsAccountsCurrency();    // variant priced COP 100000 and EUR 25; EUR account → order.Currency "EUR", item UnitPrice 25
// Unit
[Fact] public async Task AQuoteInASecondCurrencyUsesThatPriceList();  // same fixture, Currency "EUR" → UnitPrice 25, Currency "EUR"
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter FullyQualifiedName~StoreOrderGatewayTests` → FAIL: the type does not exist.
- [ ] **Step 3: Implement `QuoteAsync`.**
  - Merge repeated variants first by summing their quantities. The engine rejects duplicates (`CreateOrder.cs:77-80`).
  - Call `drafts.PriceLinesAsync`, then `Order.QuoteForStore`, then `OrderPricingRecalculation.ApplyAsync`.
  - Map the items to `StoreQuotedLine`. `Name` comes from the line's product snapshot, falling back to `IQuotationProductLookup`.
  - Nothing is saved.
- [ ] **Step 4: Implement `CreateAsync`.** Mirror `ExternalOrderService.CreateAsync` (`IExternalOrderWriter.cs:137-227`) for the key check, the numbering, the transaction and the external-key race catch.
  - Validate the customer with `OrderCustomerEligibility.Ensure(customer, …, allowIncomplete: true)` (§ Verified facts 3).
  - Resolve the account with `QuotationBillingAccountResolver`. The draft's account is the store's entry for the request currency (Task 12), so the order's currency follows from it (`Order.cs:96`). Price the lines in `draft.BillingAccount.Currency`; never take a separate currency argument that could disagree with the account.
  - Price with `PriceLinesAsync`. Parties are `new QuotationPartiesRequest(Billing: ToParty(draft.Billing), Shipping: ToParty(draft.Shipping), BillingUsesBusinessName: false, IsStorePickup: false, BillsToFinalConsumer: false, BillingWithRetention: draft.BillingWithRetention, BillingVatSurplus: draft.BillingVatSurplus).ToDomain()`, where `ToParty` builds a `QuotationPartyRequest` (`QuotationsDtos.cs:210-222`). Run `BillingPartyFiscalRules.EnsureConsistent` like `OrderDraftBuilder.cs:57`.
  - Build the order with `Order.CreateFromStore`, then `OrderPricingRecalculation.ApplyAsync`.
  - Write history with `CreatedFromStore`. Audit with `auditPublisher.Publish(tenantId, advisorSubjectId?, ...)`. The taker is a person, so pass the membership's user. If `IQuotationAuditPublisher` only has `Publish(tenantId, subjectId, …)`, add `subjectId` to `StoreOrderDraft` as `Guid TakerUserId`.
  - Publish `PublishOrderCreated`.
- [ ] **Step 5: Run to verify GREEN.** The Step 2 filter, then `$env:Seed__Enabled = "false"` and `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter FullyQualifiedName~StoreOrderGatewayTests` → PASS. Re-run `--filter "FullyQualifiedName~OrderDraftBuilder|FullyQualifiedName~CreateOrder"` to prove the extraction changed nothing.
- [ ] **Step 6: Commit.** `feat(quotations): price and create store orders with the order engine`

---

### Task 4: Catalog: «Visible en la tienda»

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/Product.cs`. Add the `VisibleInStore` property and `bool SetStoreVisibility(bool visible, DateTimeOffset now)`, which returns `false` when unchanged and otherwise bumps `Version` and `UpdatedAt`.
- Modify: `CreateProduct.cs`, `UpdateProduct.cs` (`bool? VisibleInStore = null`), `CatalogDtos.cs` (`ProductDto`, `ProductSummary`), `ProductMapping.cs`, `IProductRepository.cs`, `.../Infrastructure/Persistence/CatalogDbContext.cs`, `ProductRepository.cs`, `src/Modules/Catalog/Modules.Catalog.Api/ProductEndpoints.cs`, and the Catalog audit actions
- Create: `src/Modules/Catalog/Modules.Catalog.Application/SetProductsStoreVisibility.cs`, migration `<timestamp>_AddProductVisibleInStore.cs`
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/ProductStoreVisibilityTests.cs`, `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/ProductStoreVisibilityApiTests.cs`, `ProductStoreReadsTests.cs`

**Interfaces:**
- Produces the HTTP behavior of § HTTP contract B, plus these read methods on `IProductRepository`, for Task 8's adapter:
  - `Task<(IReadOnlyList<Product> Items, int Total)> SearchStoreVisibleAsync(Guid tenantId, string currency, string? search, IReadOnlyCollection<Guid>? categoryIds, int page, int pageSize, CancellationToken ct)`. Returns products that are active, `visible_in_store`, and have ≥ 1 active variant with a price in `currency`. Ordered by name. Same `search` semantics as `SearchAsync`.
  - `Task<IReadOnlyDictionary<ProductCategoryId, int>> CountStoreVisibleByCategoryAsync(Guid tenantId, string currency, CancellationToken ct)`. Direct counts, same predicate.
  - `Task<Product?> FindStoreVisibleAsync(Guid tenantId, ProductId id, string currency, CancellationToken ct)`. The aggregate with variants, prices, scales and gallery, or `null`.
  - `SetProductsStoreVisibilityCommand(Guid TenantId, IReadOnlyList<Guid> ProductIds, bool Visible) : ICommand<ProductStoreVisibilityResult>`, with `ProductStoreVisibilityResult(IReadOnlyList<ProductStoreVisibilityItem> Items)` and `ProductStoreVisibilityItem(Guid Id, bool VisibleInStore, long Version)`.
  - Audit action `catalog.product.store_visibility_changed`, one entry per changed product.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact] public void ANewProductIsNotVisibleInTheStore();
[Fact] public void SettingTheSameVisibilityDoesNotBumpTheVersion();
// Api
[Fact] public async Task PutWithoutVisibleInStoreKeepsIt();
[Fact] public async Task TheBatchFlipsEveryProductAndBumpsTheirVersions();
[Fact] public async Task TheBatchIsAllOrNothingWithAnotherTenantsProduct(); // 404 catalog.product.not_found, no row changed
[Fact] public async Task TheBatchRejectsMoreThan200Ids();                    // 422 validation.failed
[Fact] public async Task TheListFiltersByVisibleInStore();
[Fact] public async Task AnEditorWithTheOldVersionGets412AfterTheBatch();
// Reads
[Fact] public async Task StoreSearchSkipsHiddenInactiveAndUnpricedProducts(); // product with a COP-priced variant visible; USD-only not
[Fact] public async Task StoreSearchSkipsAProductWhoseOnlyPricedVariantIsInactive();
[Fact] public async Task StoreCategoryCountsUseTheSamePredicate();
[Fact] public async Task StoreReadsArePerCurrency();                         // A priced COP+EUR, B COP only: EUR search → [A], EUR counts exclude B, FindStoreVisibleAsync(B, "EUR") null
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter FullyQualifiedName~ProductStoreVisibilityTests` → FAIL.
- [ ] **Step 3: Implement.**
  - Column `visible_in_store boolean NOT NULL DEFAULT false`. Index `IX_products_tenant_store_visible (tenant_id) WHERE visible_in_store AND is_active`.
  - The migration adds only that column and that index.
  - The batch handler loads with `FindVariantsByIdsForUpdateAsync`-style tracking (a new `FindManyForUpdateAsync(tenantId, ids)` if no such method exists), checks that the count matches, applies the change, audits and saves once.
  - Register the handler next to the Catalog handlers in `QepServiceCollectionExtensions.cs`.
- [ ] **Step 4: Run to verify GREEN.** The Step 2 filter, then `$env:Seed__Enabled = "false"` and `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~ProductStoreVisibility|FullyQualifiedName~ProductStoreReads"` → PASS.
- [ ] **Step 5: Commit.** `feat(catalog): add the per-product store visibility flag`

---

### Task 5: Store domain

**Files:**
- Create: `src/Modules/Store/Modules.Store.Domain/*` (the files listed in § File structure) and `Modules.Store.Domain.csproj`, copied from Loans.
- Create: `tests/Modules/Store/Modules.Store.UnitTests/Modules.Store.UnitTests.csproj`, `OnlineStoreTests.cs`, `StoreSlugTests.cs`, `StoreOrderRequestTests.cs`
- Modify: `Backend.slnx`, adding the `/src/Modules/Store/` and `/tests/Modules/Store/` folders.

**Interfaces:**
- Produces:
  - `StoreSlug.TryNormalize(string? raw, out string slug, out string? errorCode)`. The error codes are `store.settings.slug_invalid` and `store.settings.slug_reserved`. `StoreSlug.Reserved` holds the 19 words of § Global Constraints.
  - `OnlineStore`:
    - `static OnlineStore CreateEmpty(Guid tenantId, DateTimeOffset now)`, with version 0.
    - `void Configure(StoreSettingsInput input, Guid updatedBy, DateTimeOffset now)`. It bumps `Version`, and moves `DataPolicySetAt` only if the URL changed.
    - `IReadOnlyList<StorePublishBlocker> PublishBlockers()`, in the contract order.
    - `void Publish(Guid by, DateTimeOffset now)`, which throws `StoreDomainException` with the first blocker's code.
    - `void Unpublish(Guid by, DateTimeOffset now)`.
    - Configuring while published re-checks the blockers.
    - `IReadOnlyList<StoreCurrency> Currencies` (saved order) and `string? PrimaryCurrency`.
    - `static void ValidateCurrencies(IReadOnlyList<StoreCurrency> entries, string? primary)`. Pure; throws `StoreDomainException` with `currency_invalid` (format), `currency_duplicate`, `currency_mismatch` or `primary_currency_invalid`, in that order, first failure wins. `Configure` calls it too, so the aggregate can never hold a bad list. Application calls it **before** any I/O (Task 8).
    - `bool Sells(string currency)` (case-insensitive) and `StoreBillingAccount? BillingAccountFor(string currency)`, `null` when the currency is not in the list.
    - `IReadOnlyList<string> CurrenciesForVisitors()`: the codes in saved order (the primary is not moved; spec §4.1 only says it is the one shown first, which `primaryCurrency` tells the Astro app).
    - `IReadOnlyList<string> SalesCountries` (saved order, uppercase) and `bool SellsTo(string country)` (case-insensitive).
    - `static void ValidateSalesCountries(IReadOnlyList<string> countries, Func<string, bool> isKnownCountry)`. Pure; throws `sales_country_invalid` (not 2 ASCII letters, or `isKnownCountry` says no) or `sales_country_duplicate`, first failure wins. `Configure` receives the same predicate and calls it.
  - `StorePlace(string Country, Guid? DepartmentId, string? DepartmentName, Guid? CityId, string CityName)`, a value object with the lane rule (§ Global Constraints «City lanes»): `CO` needs both ids and both names; any other country, no ids and the typed `CityName`. `StoreErrorCodes.CityInvalid` on a mismatch. `bool IsColombian`.
  - `StoreRequestBilling(string Name, string IdentificationType, string IdentificationNumber, string Phone, string PhoneE164, string Email, string Address, StorePlace Place)` and `StoreRequestShipping(string? RecipientName, string Phone, string PhoneE164, string Address, StorePlace Place)`, value objects, trimmed, with the § E limits. `StoreRequestShipping.EffectiveRecipientName(StoreRequestBilling billing)`.
  - `StoreCurrency(string Currency, StoreBillingAccount BillingAccount)`, a value object; `StoreBillingAccount(Guid CompanyId, string BankName, string AccountNumber, string Currency)`.
  - `StoreSettingsInput`, a record with the PUT fields (`Currencies` as `IReadOnlyList<StoreCurrency>`, `PrimaryCurrency`, `SalesCountries` as `IReadOnlyList<string>`), already shape-validated by FluentValidation in Application.
  - `StoreOrderRequest`:
    - `static Create(Guid id, Guid tenantId, long number, Guid clientRequestId, StoreRequestBilling billing, StoreRequestShipping? shipping, string? note, string currency, StorePricingMode mode, IReadOnlyList<StoreOrderRequestLineInput> lines, decimal subtotal, decimal discountAmount, decimal taxAmount, decimal total, StoreConsent consent, DateTimeOffset receivedAt)`.
    - `string DisplayNumber => $"T-{Number:D4}"`.
    - `void BeginConversion(Guid memberId, DateTimeOffset now, TimeSpan lease)`. Throws `store.order_request.not_new` if `Status != New`, and `store.order_request.being_taken` if another member's lease is still running. A lease of the same member is renewed.
    - `void ReleaseConversion()`.
    - `void MarkConverted(Guid orderId, Guid customerId, string orderNumber, decimal orderTotal, Guid convertedBy, DateTimeOffset now)`. Requires `New`, sets `TotalDifference` (`null` when 0), and clears the lease.
    - `void Discard(Guid memberId, StoreDiscardReason reason, string? note, DateTimeOffset now)`. Requires `New` and no running lease of another member, and a note for `Other`.
    - `bool IsBeingTaken(DateTimeOffset now)`.
    - `bool HasScaleDiscount`.
  - `StoreErrorCodes`, with every `store.*` code of § HTTP contract as a constant.
  - Color module, pure, no I/O (`StoreColor.cs`, `StoreColorScheme.cs`):
    - `StoreColor.TryParse(string? raw, out StoreColor color)` (`#RRGGBB`, case-insensitive), `ToHex()` uppercase.
    - `double RelativeLuminance()` (WCAG 2.x: sRGB channel → linear with the 0.04045 threshold, `0.2126 R + 0.7152 G + 0.0722 B`).
    - `static double Contrast(StoreColor a, StoreColor b)` = `(L1 + 0.05) / (L2 + 0.05)`, lighter over darker.
    - `StoreColor Darken(double amount)`: same hue and saturation in HSL, lower lightness.
    - `StoreColorScheme.Resolve(StoreColor primary, StoreColor? onPrimary, StoreColor? accentText) → (StoreColor Primary, StoreColor OnPrimary, StoreColor AccentText, StoreColorChecks Checks)`. Automatic on-primary = `#FFFFFF` or `#111111`, whichever contrasts more with the primary. Automatic accent text = the primary darkened in steps of 1 % lightness until its contrast on `#FFFFFF` is ≥ 4.5 (the primary itself if it already is; black as the floor). Overrides are taken as they are.
  - `StorePricingModes.IsRetail(StorePricingMode mode) => mode == StorePricingMode.RetailOnly`. This is the only mapping from mode to `isRetail`. `globalScaleFloor` is always `null`.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Theory, InlineData("Tienda"), InlineData("qep-api"), InlineData("www")] public void AReservedSlugIsRejected(string raw);
// "Tienda" normalizes to "tienda" first, then is reserved
[Theory, InlineData("ab"), InlineData("-acme"), InlineData("acme-"), InlineData("ac_me")] public void AMalformedSlugIsRejected(string raw);
[Theory, InlineData("#1f4fd8"), InlineData("#ABCDEF")] public void AHexColorParsesAndAnswersUppercase(string raw);
[Theory, InlineData("1F4FD8"), InlineData("#1F4FD"), InlineData("#GGGGGG"), InlineData("blue")] public void AMalformedHexIsRejected(string raw);
[Fact] public void LuminanceMatchesWcagReferenceValues();            // #FFFFFF 1.0, #000000 0.0, #777777 ≈ 0.1845
[Fact] public void ContrastOfBlackOnWhiteIs21();
[Fact] public void ContrastIsSymmetric();
[Fact] public void AutoOnPrimaryPicksTheHigherContrast();           // #FFD400 → #111111; #1F4FD8 → #FFFFFF
[Fact] public void AutoAccentTextReachesFourAndAHalfOnWhiteKeepingTheHue(); // #FFD400 → contrast ≥ 4.5, same hue ± 1°
[Fact] public void AnAccentPrimaryThatAlreadyContrastsIsKeptAsIs();  // #1F4FD8 → accentText == primary
[Fact] public void ALowContrastOverrideIsKeptAndFlagged();          // onPrimary #FFFFFF on #FFD400 → kept, onPrimaryLowContrast true
[Fact] public void AnEmptyStoreReportsEveryBlockerInOrder(); // [SlugRequired, DataPolicyRequired, CurrencyRequired, SalesCountryRequired]
[Fact] public void AnUnknownSalesCountryIsRejected();                // ["XX"] with a predicate that knows CO and ES → store.settings.sales_country_invalid
[Fact] public void AForeignSalesCountryIsAccepted();                 // ["CO", "ES"] → SellsTo("es")
[Fact] public void AColombianPlaceNeedsItsIdsAndAForeignOneItsText(); // CO without CityId → city_invalid; ES with CityId → city_invalid
[Fact] public void ARepeatedSalesCountryIsRejected();                // ["CO", "co"] → store.settings.sales_country_duplicate
[Fact] public void SalesCountriesAreStoredUppercaseAndMatchedIgnoringCase(); // ["co"] → SalesCountries ["CO"], SellsTo("Co")
[Fact] public void APublishedStoreCannotDropItsLastSalesCountry();   // Configure with [] → store.settings.sales_country_required
[Fact] public void ShippingWithoutARecipientUsesTheBillingName();
[Fact] public void ARequestWithoutShippingKeepsItNull();             // «Usar los mismos datos» → Shipping null
[Fact] public void AnEntryWhoseAccountIsInAnotherCurrencyIsRejected();  // { EUR, account COP } → store.settings.currency_mismatch
[Fact] public void ARepeatedCurrencyIsRejected();                       // [COP, cop] → store.settings.currency_duplicate
[Theory, InlineData("USD"), InlineData(null)] public void APrimaryOutsideTheListIsRejected(string? primary); // entries [COP, EUR] → primary_currency_invalid
[Fact] public void APrimaryWithoutEntriesIsRejected();                  // [] + "COP" → primary_currency_invalid
[Fact] public void AMalformedCurrencyIsRejected();                      // "CO" → store.settings.currency_invalid
[Fact] public void EachCurrencyResolvesItsOwnAccount();                 // [COP→A, EUR→B]: BillingAccountFor("eur") == B, BillingAccountFor("USD") null
[Fact] public void ASingleCurrencyStoreIsAListOfOne();                  // [COP] + "COP" → no CurrencyRequired, Sells("COP"), !Sells("EUR")
[Fact] public void ChangingThePolicyUrlMovesItsDateAndKeepingItDoesNot();
[Fact] public void APublishedStoreCannotBeSavedWithoutItsPolicy();      // store.settings.data_policy_required
[Fact] public void APublishedStoreCannotDropItsLastCurrency();          // Configure with [] → store.settings.currency_required
[Fact] public void OnlyANewRequestCanBeConvertedOrDiscarded();
[Fact] public void AnotherMembersRunningLeaseBlocksTakeAndDiscard();   // being_taken
[Fact] public void AnExpiredLeaseCanBeTakenByAnotherMember();
[Fact] public void DiscardingWithOtherRequiresANote();
[Fact] public void TheTotalDifferenceIsNullWhenTheTotalsMatch();
[Fact] public void TheDisplayNumberIsPaddedToFourDigits();              // 7 → "T-0007", 12345 → "T-12345"
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Store/Modules.Store.UnitTests` → FAIL: does not compile.
- [ ] **Step 3: Implement.** Keep the domain pure: no I/O, and no references outside `BuildingBlocks.Domain`. `StoreDomainException : DomainException`. The lease length is passed in, not hard-coded. Application passes `StoreOptions.ConversionLease`, default 2 minutes.
- [ ] **Step 4: Run to verify GREEN.** Same command → PASS.
- [ ] **Step 5: Commit.** `feat(store): add the store and order request domain`

---

### Task 6: Store persistence

**Files:**
- Create: `src/Modules/Store/Modules.Store.Application/Modules.Store.Application.csproj`, `IOnlineStoreRepository.cs`, `IStoreDomainRepository.cs`, `IStoreOrderRequestRepository.cs`, `IStoreRequestNumberGenerator.cs`, `IStoreUnitOfWork.cs`, `IStoreAuditPublisher.cs` (same signature as `ILoansAuditPublisher`, plus `PublishAnonymous(Guid tenantId, string action, string resourceType, string resourceId, DateTimeOffset at)`), `StoreReadModels.cs`
- Create: `src/Modules/Store/Modules.Store.Infrastructure/*`. Under `Persistence/`: `StoreDbContext`, `StoreDbContextFactory`, the repositories, `StoreUnitOfWork`, `StoreAuditPublisher`, `StoreRequestNumberGenerator`, `UniqueViolation.cs` (a copy), `Migrations/<timestamp>_InitialStore.cs`. Also `StoreDatabaseInitializer.cs` and `StoreInfrastructureExtensions.cs`.
- Create: `tests/Modules/Store/Modules.Store.IntegrationTests/Modules.Store.IntegrationTests.csproj`, `StoreApiHarness.cs` (copied from `LoansApiHarness`, plus `ShopifyApiHarness`' catalog/company seeding helpers), `StorePersistenceTests.cs`
- Modify: `Backend.slnx`, `src/Api/Api.csproj`, `src/Bootstrapper/Bootstrapper.csproj`, `src/Api/Program.cs:348-351` (initialize after Loans and before ApiAccess, with its comment), `src/Bootstrapper/QepServiceCollectionExtensions.cs:1079` (`services.AddStoreInfrastructure(configuration);`)

**Interfaces:**
- Produces:
  - `IOnlineStoreRepository`: `Task<OnlineStore?> FindAsync(Guid tenantId, CancellationToken ct)` (tracked); `void Add(OnlineStore store)`; `Task<bool> SlugTakenByAnotherAsync(Guid tenantId, string slug, CancellationToken ct)`.
  - `IStoreDomainRepository`: `Task<StoreDomain?> FindByHostAsync(string hostname, CancellationToken ct)` (untracked); `Task ReplaceSubdomainAsync(Guid tenantId, string? hostname, CancellationToken ct)`. That method deletes the tenant's `Subdomain` row and inserts the new one in the same unit of work.
  - `IStoreOrderRequestRepository`:
    - `void Add(StoreOrderRequest r)`
    - `Task<StoreOrderRequest?> FindAsync(Guid tenantId, Guid id, CancellationToken ct)` (tracked, with lines)
    - `Task<StoreOrderRequest?> FindByClientRequestIdAsync(Guid tenantId, Guid clientRequestId, CancellationToken ct)`
    - `Task<(IReadOnlyList<StoreOrderRequest> Items, int Total)> ListAsync(StoreInboxFilter filter, CancellationToken ct)`
    - `Task<StoreInboxCounts> CountAsync(Guid tenantId, Guid callerMemberId, DateTimeOffset todayStartUtc, DateTimeOffset todayEndUtc, CancellationToken ct)`
    - `Task<int> CountReceivedSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken ct)`
    - `Task<StoreOrderRequest?> FindByOrderAsync(Guid tenantId, Guid orderId, CancellationToken ct)`
  - `StoreReadModels.cs`: `StoreInboxFilter(Guid TenantId, StoreInboxView View, Guid CallerMemberId, int Page, int PageSize)`; `StoreInboxCounts(int Untaken, int TakenByMe, int ReceivedToday)`.
  - `IStoreRequestNumberGenerator.NextAsync(Guid tenantId, CancellationToken ct) → Task<long>`. Same `INSERT … ON CONFLICT DO NOTHING` + `UPDATE … RETURNING` as `OrderNumberGenerator.cs`, on `store.request_counters`.
  - `IStoreUnitOfWork`: `SaveChangesAsync`, `BeginTransactionAsync`.
  - `services.AddStoreInfrastructure(configuration)`, `InitializeStoreDatabaseAsync(...)`. `StoreDbContext` is public.

Tables (schema `store`, `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`, history in `store.__ef_migrations_history`):

| Table | Columns | Constraints |
| --- | --- | --- |
| `stores` | `tenant_id` PK, `slug` (40)?, `is_published`, `published_at`, `name` (80)?, `primary_color` (7)?, `on_primary_color` (7)?, `accent_text_color` (7)?, `banner_file_id`?, `banner_text` (200)?, `whatsapp` (32)?, `address` (200)?, `schedule` (200)?, `instagram_url`, `facebook_url`, `tiktok_url` (500)?, `data_policy_url` (500)?, `data_policy_set_at`?, `primary_currency` (3)?, `pricing_mode` (16), `sales_countries varchar(2)[]` (default `{}`), `featured_category_ids uuid[]`, `version`, `updated_at`, `updated_by`? | Unique `IX_stores_slug` on `slug` where not null; `CK_stores_pricing_mode`, `CK_stores_colors` (each color null or `^#[0-9A-F]{6}$`) |
| `store_currencies` | `tenant_id` FK → `stores` cascade, `currency` (3), `position` int, `billing_company_id`, `billing_bank_name` (120), `billing_account_number` (32), `billing_currency` (3) | PK `(tenant_id, currency)`; `CK_store_currencies_account_currency` (`billing_currency = currency`). Owned collection of `OnlineStore`, mapped with `OwnsMany` like `SuppliersDbContext.cs:126` |
| `store_domains` | `hostname` (253) PK, `tenant_id`, `kind` (16: `Subdomain`/`Custom`), `created_at` | `IX_store_domains_tenant` |
| `order_requests` | `id`, `tenant_id`, `number` bigint, `client_request_id`, `status`, `billing_name` (150), `billing_identification_type` (16), `billing_identification_number` (32), `billing_phone` (32), `billing_phone_e164` (20), `billing_email` (254), `billing_address` (200), `billing_country` (2), `billing_department_id`?, `billing_department_name` (120)?, `billing_city_id`?, `billing_city_name` (120), `has_shipping` bool, `shipping_recipient_name` (120)?, `shipping_phone` (32)?, `shipping_phone_e164` (20)?, `shipping_address` (200)?, `shipping_country` (2)?, `shipping_department_id`?, `shipping_department_name` (120)?, `shipping_city_id`?, `shipping_city_name` (120)?, `note` (1000)?, `currency`, `pricing_mode`, `subtotal`, `discount_amount`, `tax_amount`, `total`, `consent_at`, `consent_ip` (45), `consent_policy_url` (500), `consent_policy_set_at`, `received_at`, `lease_member_id`?, `lease_until`?, `converted_by`?, `converted_at`?, `order_id`?, `customer_id`?, `order_number` (32)?, `order_total`?, `total_difference`?, `discarded_by`?, `discarded_at`?, `discard_reason` (16)?, `discard_note` (500)?, `version` | Unique `IX_order_requests_tenant_number`, unique `IX_order_requests_tenant_client_request`, `IX_order_requests_tenant_status_received`, `IX_order_requests_tenant_converted_by`, unique `IX_order_requests_order` where `order_id` not null, `IX_order_requests_tenant_billing_phone` (`tenant_id`, `billing_phone_e164`); `CK_order_requests_status`, `CK_order_requests_discard_reason`, `CK_order_requests_shipping` (`has_shipping` ⇔ the required shipping columns are not null), `CK_order_requests_billing_lane` and `CK_order_requests_shipping_lane` (`country = 'CO'` ⇔ department and city ids not null). The shipping block is an `OwnsOne` mapped to columns, never JSON, so the inbox can be queried |
| `order_request_lines` | `request_id` FK cascade, `position`, `variant_id`, `product_id`, `name` (200), `quantity` numeric(18,2), `unit_price`, `discount_percentage` numeric(5,2), `discount_origin` (16), `discounted_unit_price`, `line_total` | PK `(request_id, position)` |
| `request_counters` | `tenant_id` PK, `next_value` bigint | |

`StoreUnitOfWork.SaveChangesAsync` translates by **index name**, never by `SqlState` alone:
- `IX_stores_slug` → `ResourceConflictException("store.settings.slug_taken")`.
- `PK_store_domains` → the same code. Another tenant's custom row could own the hostname.
- `IX_order_requests_tenant_client_request` → `ResourceConflictException("store.order_request.duplicate_client_request")`. The public handler catches it and re-reads (Task 10).
- `DbUpdateConcurrencyException` on `OnlineStore` → `RequestConcurrencyException("concurrency.conflict")` (412).
- `DbUpdateConcurrencyException` on `StoreOrderRequest` → `ResourceConflictException("store.order_request.conflict")` (409, spec §5.2).

The translation lives in Infrastructure (project rule).

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact] public async Task AStoreWithFeaturedCategoriesRoundTrips();
[Fact] public async Task AStoreWithTwoCurrenciesRoundTripsInOrder();       // [EUR→B, COP→A], primary COP → same order, accounts and primary after reload
[Fact] public async Task ReplacingTheCurrenciesLeavesOnlyTheNewList();     // [COP, EUR] then [EUR] → one store_currencies row
[Fact] public async Task TheDatabaseRejectsAnAccountInAnotherCurrency();   // raw INSERT → CK_store_currencies_account_currency
[Fact] public async Task TwoTenantsCannotShareASlug();                    // ResourceConflictException store.settings.slug_taken
[Fact] public async Task ReplacingTheSubdomainLeavesOneRowPerTenant();
[Fact] public async Task ARequestRoundTripsWithItsLinesInOrder();
[Fact] public async Task ARequestRoundTripsItsBillingAndShipping();     // both blocks, then one with Shipping null → has_shipping false, Shipping null after reload
[Fact] public async Task TheDatabaseRejectsAHalfShipping();             // raw INSERT has_shipping true, shipping_address null → CK_order_requests_shipping
[Fact] public async Task TheDatabaseRejectsAColombianBillingWithoutCityId(); // CK_order_requests_billing_lane
[Fact] public async Task AForeignBillingRoundTripsWithItsCityText();     // ES, ids null, city_name "Madrid"
[Fact] public async Task SalesCountriesRoundTripInOrder();
[Fact] public async Task TheSameClientRequestIdCannotBeStoredTwice();     // duplicate_client_request
[Fact] public async Task AConcurrentRequestEditIsAConflictNot412();       // two contexts, BeginConversion on both → store.order_request.conflict
[Fact] public async Task RequestNumbersAreConsecutivePerTenantAndStartAtOne();
[Fact] public async Task CountsSeparateUntakenMineAndToday();
[Fact] public async Task TheDatabaseRejectsAnUnknownStatus();             // raw INSERT → CK_order_requests_status
```

- [ ] **Step 2: Run to verify RED.** `$env:Seed__Enabled = "false"`, then `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter FullyQualifiedName~StorePersistenceTests` → FAIL: does not compile.
- [ ] **Step 3: Implement** the context, repositories, unit of work and wiring. Generate `InitialStore`, and check that it creates schema `store`, the `store_currencies` table with its CHECK, and does not touch `audit.entries`. The module is new, so the currencies table goes in `InitialStore`, not in a second migration. «primary is one of the entries» lives in the domain (`ValidateCurrencies`), not in a cross-table constraint. Replacing the list must delete removed owned rows, not orphan them (`ReplacingTheCurrenciesLeavesOnlyTheNewList`). Run `dotnet restore Backend.slnx`.
- [ ] **Step 4: Run to verify GREEN.** Step 2 filter → PASS.
- [ ] **Step 5: Commit.** `feat(store): add persistence and initial migration`

---

### Task 7: Store composition: permission, authorization, layer tests

**Files:**
- Create in `Modules.Store.Application/`: `StorePermissions.cs` (`SettingsManage = "store.settings.manage"`, `OrderManage = "quotations.order.manage"`, the latter re-declared as Shopify does in `ShopifyAuthorization.cs:12-25`), `StoreAuthorization.cs` (copy of `LoansAuthorization`, guarding `TenantModuleKeys.Store`), `StoreAuditActions.cs`, `StoreOptions.cs`, `StoreNotFound.cs`
- Create: `src/Modules/Store/Modules.Store.Api/Modules.Store.Api.csproj`, `StoreEndpoints.cs` (empty group `/api/v1/tenants/{tenantId:guid}/store`, tag `Store`), `PublicStoreEndpoints.cs` (empty group)
- Create: `src/Modules/Store/Modules.Store.Infrastructure/StoreOptionsValidator.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs`. Add the `using StorePermissions = …` next to `:65`, the role `admin` (`:1457`, next to `LoansPermissions.Manage` at `:1574`), and the `PermissionDefinition` next to `:2231`.
- Modify: `src/Api/Program.cs:271` (`app.MapStoreEndpoints();`), `src/Api/Api.csproj`, `Backend.slnx`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`, `src/Api/appsettings.json`, `src/Api/appsettings.example.json`
- Test: `tests/ArchitectureTests/ArchitectureTests/StoreLayerTests.cs`, `CompositionRootTests.cs:184-198`, `ConfigurationExampleTests.cs`, `tests/Modules/Store/Modules.Store.UnitTests/StoreOptionsTests.cs`

**Interfaces:**
- Produces:
  - `StoreAuditActions`: `SettingsUpdated = "store.settings.updated"`, `Published = "store.settings.published"`, `Unpublished = "store.settings.unpublished"`, `RequestReceived = "store.order_request.received"`, `RequestConverted = "store.order_request.converted"`, `RequestDiscarded = "store.order_request.discarded"`; resource types `store`, `order_request`.
  - `StoreOptions` (section `Store`): `RootDomain` (string, default `lulocrm.com`); `ConversionLease` (TimeSpan, default `00:02:00`); `DailyRequestCap` (int, default 500); `Storefront:SharedSecret` (string, ≥ 32 chars, required outside `Testing`); `Turnstile:Enabled` (bool, default true); `Turnstile:SecretKey` (required when enabled); `Turnstile:VerifyUrl` (default `https://challenges.cloudflare.com/turnstile/v0/siteverify`).
  - `PermissionDefinition(StorePermissions.SettingsManage, "Configurar la tienda en línea", "Permite publicar la tienda, cambiar su marca, contacto, monedas con sus cuentas y modo de precios.", "Tienda", "medium", RequiredModules: [ModuleKeys.Store])`.
  - `StoreAuthorization.EnsureAsync(IExecutionContext, ITenantModules, Guid tenantId, string permission, CancellationToken)`.

- [ ] **Step 1: Write the failing tests.**
  - `StoreLayerTests` is a copy of `LoansLayerTests` with the types `OnlineStore`, `StorePermissions`, `StoreInfrastructureExtensions` and `StoreEndpoints`.
  - `PermissionDiscoveryFindsTheSeventyThreeConstants` becomes `…SeventyFourConstants`, with `Assert.Equal(74, …)`.
  - Also add:

```csharp
[Fact] public void TheStoreSettingsPermissionRequiresTheStoreModuleAndGoesOnlyToAdmin();
[Fact] public void AShortStorefrontSecretFailsValidation();          // StoreOptionsTests: 31 chars → failure naming Store:Storefront:SharedSecret
[Fact] public void TurnstileEnabledWithoutASecretFailsValidation();
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~StoreLayerTests|FullyQualifiedName~CompositionRootTests|FullyQualifiedName~ConfigurationExampleTests"` → FAIL.
- [ ] **Step 3: Implement.** Bind `StoreOptions` with `ValidateOnStart` in `AddStoreInfrastructure`. `appsettings.json` gets non-secret defaults only. `appsettings.example.json` documents `Store__Storefront__SharedSecret` and `Store__Turnstile__SecretKey` as placeholders, and the test factory sets both. Run `dotnet restore Backend.slnx`.
- [ ] **Step 4: Run to verify GREEN.** The Step 2 filter, plus `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter FullyQualifiedName~StoreOptionsTests` → PASS. Then `$env:Seed__Enabled = "false"` and `dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter FullyQualifiedName~AuthorizationCatalogApiTests` → PASS.
- [ ] **Step 5: Commit.** `feat(store): wire module permission, options and layers`

---

### Task 8: Store settings endpoints and their adapters

**Files:**
- Create in `Modules.Store.Application/`: `IStoreTenantLookup.cs`, `IStoreFileLookup.cs`, `IStoreCatalogReader.cs` (settings part: `CategoryNamesAsync`, `CurrenciesInUseAsync`), `IStoreBillingAccounts.cs`, `IStoreCountries.cs`, `StoreDtos.cs`, `GetStoreSettings.cs`, `UpdateStoreSettings.cs`, `PublishStore.cs`, `StoreSettingsComposer.cs`
- Create in `src/Bootstrapper/`: `StoreTenantLookup.cs` (over `ITenantDirectory`: display name, country, active, logo through `TenantLogoStorage`'s public key), `StoreFileLookup.cs` (over `IFileResourceRepository` + `IPublicObjectStorage`, like `ProductImageLookup`), `StoreCatalogReader.cs` (settings part), `StoreBillingAccounts.cs` (over `QuotationBillingAccountResolver`'s lookup, like `ShopifyBillingAccounts`), `StorePhoneNormalizer.cs` (copy of `ServiceContractsPhoneNormalizer`, over `IPhoneNumberNormalizer`; it implements `IStoreCountries` and, in Task 11, `IStorePhoneNormalizer`)
- Modify: `StoreEndpoints.cs`, `QepServiceCollectionExtensions.cs` (handlers, validators, ports)
- Modify (Storage): `src/Modules/Storage/Modules.Storage.Domain/FileResourceEnums.cs` (`Store = 7`, with its comment: the store banner, `OwnerId` is the tenant id), `src/Modules/Storage/Modules.Storage.Application/FileOwnerModules.cs:18-29` (`[FileOwnerType.Store] = TenantModuleKeys.Store`). Persisted by name in `character varying(20)`, so no migration.
- Create: `src/Modules/Store/Modules.Store.Application/PreviewStoreColors.cs` (`PreviewStoreColorsQuery(Guid TenantId, string? PrimaryColor, string? OnPrimaryColor, string? AccentTextColor) : IQuery<StoreColorPreviewDto>`, authorizes, parses, calls `StoreColorScheme.Resolve`, no repository) and `src/Modules/Store/Modules.Store.Application/ListStoreBillingAccounts.cs`; extend `src/Bootstrapper/StoreBillingAccounts.cs` with the listing over the Companies repository (active companies only).
- Test: `tests/Modules/Store/Modules.Store.IntegrationTests/StoreSettingsApiTests.cs`, `StoreAccessApiTests.cs`, `tests/Modules/Storage/Modules.Storage.UnitTests/FileOwnerModulesTests.cs` (the existing every-value test now covers `Store`)

**Interfaces:**
- Consumes: Task 5 `OnlineStore`, Task 6 repositories, Task 7 authorization.
- Produces:
  - `GetStoreSettingsQuery(Guid TenantId) : IQuery<StoreSettingsDto>`
  - `UpdateStoreSettingsCommand(Guid TenantId, long ExpectedVersion, StoreSettingsRequest Body) : ICommand<StoreSettingsDto>`
  - `PublishStoreCommand(Guid TenantId, long ExpectedVersion, bool Publish) : ICommand<StoreSettingsDto>`
  - `StoreSettingsDto` matching § HTTP contract A.
  - `IStoreTenantLookup.GetAsync(Guid tenantId, CancellationToken) → Task<StoreTenantRef?>`, where `StoreTenantRef(string DisplayName, string Country, bool IsActive, string? LogoUrl, StoreRegionalFormat Regional)` and `StoreRegionalFormat(string TimeZone, string DateFormat, string NumberFormat, string DefaultCurrency, string Country)`, read from `ITenantDirectory.GetRegionalSettingsAsync` (`ITenantDirectory.cs:35`), the source of `/auth/me`.
  - `IStoreFileLookup.FindPublicImageAsync(Guid tenantId, Guid fileId, CancellationToken) → Task<string?>`. Returns the public URL, or `null` if the file is not the tenant's, its owner type is not `Store`, it is not an image, or it is not public.
  - `IStoreBillingAccounts.ListAsync(Guid tenantId, string? currency, CancellationToken) → Task<IReadOnlyList<StoreBillingAccountOption>>`, `StoreBillingAccountOption(Guid CompanyId, string CompanyName, string BankName, string AccountNumber, string Currency)`; `ListStoreBillingAccountsQuery(Guid TenantId, string? Currency)`, guarded like the other settings handlers.
  - `IStoreCatalogReader.CategoryNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken) → Task<IReadOnlyDictionary<Guid, string>>`; `CurrenciesInUseAsync(Guid tenantId, CancellationToken) → Task<IReadOnlyCollection<string>>`, over `ICurrenciesInUse` (`Modules.Catalog.Application/ICurrenciesInUse.cs:8`).
  - `IStoreBillingAccounts.ValidateAsync(Guid tenantId, Guid companyId, string bankName, string accountNumber, string currency, CancellationToken)`. Called once per entry. It throws the Quotations codes through the adapter and returns `StoreBillingAccount`.
  - `IStoreCountries.IsKnown(string country) → bool`, over `IPhoneNumberNormalizer.IsKnownRegion` (§ Verified facts 17), the same check as `ServiceContractsPhoneNormalizer.IsKnownCountry` (`ServiceContractsPhoneNormalizer.cs:16`).
  - `StoreSettingsRequest.SalesCountries: IReadOnlyList<string>`, `StoreSettingsDto.SalesCountries`.
  - `StoreSettingsRequest.Currencies: IReadOnlyList<StoreCurrencyRequest>`, `StoreCurrencyRequest(string Currency, StoreBillingAccountRequest BillingAccount)`, `string? PrimaryCurrency`. `StoreSettingsDto.Currencies: IReadOnlyList<StoreCurrencyDto>`, `PrimaryCurrency`.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact] public async Task ANeverSavedStoreReadsAsDefaultsWithVersionZero();  // primaryColor null, resolvedColors null, pricingMode RetailOnly, every blocker
[Fact] public async Task SavingWithIfMatchZeroCreatesTheStore();
[Fact] public async Task SavingWithoutIfMatchIs428AndWithAStaleOneIs412();
[Fact] public async Task ASlugTakenByAnotherTenantIs409();
[Fact] public async Task AReservedSlugIs422();
[Fact] public async Task ChangingTheSlugMovesTheHostname();                  // store_domains: only "nuevo.lulocrm.com"
[Fact] public async Task TwoCurrenciesWithTheirAccountsRoundTrip();          // PUT [COP→A, EUR→B] primary COP → GET same list, order and primary
[Fact] public async Task AnAccountInAnotherCurrencyThanItsEntryIs422();      // { EUR, account COP } → store.settings.currency_mismatch, nothing saved
[Fact] public async Task ARepeatedCurrencyIs422();                           // store.settings.currency_duplicate
[Fact] public async Task APrimaryCurrencyOutsideTheListIs422();              // store.settings.primary_currency_invalid
[Fact] public async Task ACurrencyWithoutPricedProductsIs422();              // USD not in currencies-in-use → store.settings.currency_invalid
[Fact] public async Task TheSecondEntrysAccountIsCheckedToo();               // COP valid, EUR account number unknown → quotation.billing.* code
[Fact] public async Task PublishingWithoutCurrenciesIs422();                 // slug + policy set, [] → store.settings.currency_required
[Fact] public async Task ColorsRoundTripWithTheirResolvedValuesAndChecks();  // primary #FFD400, no overrides → resolved onPrimary #111111, accentText darker, flags false
[Fact] public async Task ALowContrastOverrideIsSavedAndFlagged();             // onPrimary #FFFFFF on #FFD400 → 200, onPrimaryLowContrast true
[Fact] public async Task AMalformedColorIs422();                               // "blue" → store.settings.color_invalid
[Fact] public async Task AMissingPrimaryColorIs422();
[Fact] public async Task ThePreviewEqualsTheSavedResult();                    // preview {#FFD400} == resolvedColors/colorChecks after PUT with the same colors
[Fact] public async Task ThePreviewWritesNothing();                           // version and updatedAt unchanged, no audit entry
[Fact] public async Task APreviewWithAMalformedColorIs422();                  // onPrimaryColor "white" → store.settings.color_invalid
[Fact] public async Task APreviewWithLowContrastIsFlaggedNot422();
[Fact] public async Task ThePreviewNeedsTheStorePermission();                 // 403 without store.settings.manage; 403 with the module off
[Fact] public async Task BillingAccountsAreListedAcrossCompaniesByCurrency(); // two companies, COP and EUR accounts → ?currency=EUR lists only EUR, with company names
[Fact] public async Task BillingAccountsSkipInactiveCompanies();
[Fact] public async Task BillingAccountsNeedTheStorePermission();             // 403 without store.settings.manage
[Fact] public async Task ABannerWithAnotherOwnerTypeIs422();                  // a Product image → store.settings.banner_invalid
[Fact] public async Task SalesCountriesRoundTripUppercase();               // PUT ["co", "es"] → GET ["CO", "ES"]
[Fact] public async Task AnUnknownSalesCountryIs422();                       // ["XX"] → store.settings.sales_country_invalid, nothing saved
[Fact] public async Task PublishingWithoutSalesCountriesIs422();             // slug, policy, currency set, [] → store.settings.sales_country_required
[Fact] public async Task ABannerThatIsNotPublicIs422();
[Fact] public async Task PublishingWithABlockerIs422WithItsCode();           // store.settings.data_policy_required
[Fact] public async Task PublishThenUnpublishAuditsBoth();
[Fact] public async Task TheEffectiveNameFallsBackToTheTenantName();
// StoreAccessApiTests
[Fact] public async Task WithoutThePermissionSettingsAre403();
[Fact] public async Task AnotherTenantInTheRouteIs403Never404();
[Fact] public async Task WithTheModuleOffSettingsAre403();
```

- [ ] **Step 2: Run to verify RED.** `$env:Seed__Enabled = "false"`, then `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreSettingsApiTests|FullyQualifiedName~StoreAccessApiTests"` → FAIL.
- [ ] **Step 3: Implement.**
  - The endpoints follow the ETag / `If-Match` / 428 contract of `ShopifyEndpoints.UpdateStoreAsync`.
  - `UpdateStoreSettingsHandler` order: authorize; validate the body (FluentValidation, limits of § A, ≤ 10 entries); normalize the slug; `OnlineStore.ValidateCurrencies` and `OnlineStore.ValidateSalesCountries(…, countries.IsKnown)` (pure, before any I/O); check every entry's currency is in use (one `CurrenciesInUseAsync` call); validate each entry's account; check the banner; check the categories. Then call `Configure`. If the slug changed, replace the subdomain row. Audit with the changed fields (`currencies` as the list of codes, never account numbers; `salesCountries`). Save.
  - The "in use" currency check reuses `GET /catalog/currencies-in-use`'s source.
- [ ] **Step 4: Run to verify GREEN.** Step 2 filter → PASS.
- [ ] **Step 5: Commit.** `feat(store): add store settings and publishing`

---

### Task 9: Storefront gateway: shared secret, visitor IP, rate limits, CSRF exemption

**Files:**
- Create: `src/Bootstrapper/StorefrontPaths.cs` (`Prefix = "/api/v1/public/stores/"`, `IsStorefront(PathString)`, ordinal), `src/Bootstrapper/StorefrontClient.cs`
- Create: `src/Modules/Store/Modules.Store.Api/StorefrontKeyFilter.cs`, an endpoint filter for the public group. It answers 401 `store.public.client_unauthorized` without a valid key.
- Modify: `src/Bootstrapper/Csrf/RequireCsrfHeaderMiddleware.cs:51-63` (add `|| StorefrontPaths.IsStorefront(context.Request.Path)` with its comment), `src/Api/RateLimiterPolicies.cs` (add `StorefrontRead = "storefront-read"` and `StorefrontSubmit = "storefront-submit"`), `src/Api/Program.cs:81-157` (two policies) and `:252` (`app.MapPublicStoreEndpoints(RateLimiterPolicies.StorefrontRead, RateLimiterPolicies.StorefrontSubmit);`). If request-failure capture keys off paths (`RequestFailureCapture`, which `ExternalApiPaths` feeds), make sure it never records the key header.
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/RequireCsrfHeaderMiddlewareTests.cs`, `StorefrontClientTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/StorefrontGatewayApiTests.cs`

**Interfaces:**
- Produces:
  - `StorefrontClient.TryAuthenticate(HttpContext ctx, string sharedSecret) → bool`. Compares the header in constant time with `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes.
  - `StorefrontClient.VisitorIp(HttpContext ctx, string sharedSecret) → string`. With a valid key and a parseable `X-Qep-Visitor-Ip` (`IPAddress.TryParse`), that IP. Otherwise `RemoteIpAddress?.ToString() ?? "unknown"`.
  - The rate-limit partition is `VisitorIp(...)`. `storefront-read` allows 300/min with `QueueLimit` 0. `storefront-submit` allows 5 per 10 min.
  - `HttpContext.Items["store.visitor_ip"]` set by the filter. The submit handler reads it for the consent IP.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact] public void ThePublicStorePrefixSkipsTheCsrfHeader();               // POST /api/v1/public/stores/x/order-requests without X-Qep-Client → next called
[Fact] public void ANearMissPrefixStillNeedsTheCsrfHeader();               // /api/v1/public/storesx/… → 403
[Fact] public void TheVisitorIpIsUsedOnlyWithAValidKey();
[Fact] public void AnUnparseableVisitorIpFallsBackToTheRemoteIp();
// StorefrontGatewayApiTests
[Fact] public async Task WithoutTheKeyEveryPublicRouteIs401();
[Fact] public async Task TheVisitorIpHeaderIsIgnoredWithoutTheKey();       // 6 submits with rotating X-Qep-Visitor-Ip and no key → 401s, none reach the handler
[Fact] public async Task TheSixthSubmitFromOneVisitorIs429();
[Fact] public async Task TwoVisitorsBehindTheSameServerHaveSeparateBuckets();
[Fact] public async Task ThePublicRoutesIgnoreTheSessionCookie();          // a valid session cookie does not change the 401 without key
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~RequireCsrfHeaderMiddlewareTests|FullyQualifiedName~StorefrontClientTests"` → FAIL.
- [ ] **Step 3: Implement.** The public group is mapped with `.AllowAnonymous()`, the filter, `.WithMetadata(new RequestSizeLimitAttribute(65_536))`, and the read policy. `order-requests` overrides the policy with the submit one. The policies read the secret from `IOptions<StoreOptions>` through `httpContext.RequestServices`, as the `External` policy does (`Program.cs:147-156`).
- [ ] **Step 4: Run to verify GREEN.** Step 2 filter, then `$env:Seed__Enabled = "false"` and `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter FullyQualifiedName~StorefrontGatewayApiTests` → PASS.
- [ ] **Step 5: Commit.** `feat(store): authenticate and rate-limit the storefront server`

---

### Task 10: Public read endpoints and cart quote

**Files:**
- Create in `Modules.Store.Application/`: `PublicStoreDtos.cs`, `StorefrontResolver.cs` (host → published store of an active tenant with the module on, else `ResourceNotFoundException("store.public.not_found")`), `GetPublicStore.cs`, `ListPublicCategories.cs`, `ListPublicProducts.cs`, `GetPublicProduct.cs`, `QuotePublicCart.cs`, `IStoreOrders.cs`, `IStoreGeography.cs`, `ListPublicGeography.cs`
- Create: `src/Bootstrapper/StoreOrdersAdapter.cs` (`IStoreOrders` over `IStoreOrderGateway`, a new scope per call like `ShopifyOrderWriter.cs:19-50`). Extend `StoreCatalogReader.cs` with the public reads. Create `src/Bootstrapper/StoreGeography.cs` (`IStoreGeography` over `IDepartmentRepository` and `ICityRepository`, `Modules.Geography.Application`, § Verified facts 16).
- Modify: `PublicStoreEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Store/Modules.Store.UnitTests/PublicCategoryTreeTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/PublicCatalogApiTests.cs`, `PublicCartQuoteApiTests.cs`, `PublicGeographyApiTests.cs`

**Interfaces:**
- Consumes: Task 3 `IStoreOrderGateway`, Task 4 repository reads, Task 8 lookups, Task 9 filter.
- Produces:
  - `IStoreCatalogReader` (public part):
    - `SearchAsync(Guid tenantId, string currency, string? q, Guid? categoryId, int page, int pageSize, CancellationToken) → Task<(IReadOnlyList<StoreProductCard> Items, int Total)>`
    - `CategoriesAsync(Guid tenantId, string currency, CancellationToken) → Task<IReadOnlyList<StoreCategoryCount>>` (direct counts)
    - `ProductAsync(Guid tenantId, Guid productId, string currency, bool includeScales, CancellationToken) → Task<StoreProductView?>`
    - `SellableVariantsAsync(Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken) → Task<IReadOnlyDictionary<Guid, StoreSellableVariant>>`. Only variants that are active, of an active product, visible in the store, and priced in `currency`. `StoreSellableVariant(Guid VariantId, Guid ProductId, string Name, string? ImageUrl)`.
  - `IStoreOrders.QuoteAsync(Guid tenantId, string currency, StorePricingMode mode, IReadOnlyList<(Guid VariantId, int Quantity)> lines, CancellationToken) → Task<StoreQuoteView>`. It maps `IStoreOrderGateway.QuoteAsync`, and turns `quotation.item.*` exceptions into `StoreDomainException("store.order_request.line_unavailable")`.
  - `StorefrontResolver.ResolveAsync(string host, CancellationToken) → Task<ResolvedStorefront>` and `StorefrontResolver.RequireCurrency(OnlineStore store, string? currency) → string`. The latter uppercases and returns the code when `store.Sells(currency)`, else throws `StoreDomainException("store.public.currency_invalid")`. Every public handler but `GET /` calls it right after resolving the store, before any catalog read.
  - `ListPublicCategoriesQuery(string Host, string? Currency)`, `ListPublicProductsQuery(string Host, string? Currency, Guid? Category, string? Q, int Page, int PageSize)`, `GetPublicProductQuery(string Host, Guid ProductId, string? Currency)`. `Currency` is nullable at the boundary so a missing one gets the store code, not a model-binding 400.
  - `QuotePublicCartQuery(string Host, string? Currency, IReadOnlyList<PublicCartLineRequest> Lines) : IQuery<PublicCartQuoteDto>`, shared by Task 11 through `internal PublicCartPricing.PriceAsync(Guid tenantId, string currency, StorePricingMode mode, lines, ct) → (PublicCartQuoteDto Quote, IReadOnlyList<Guid> Unavailable)`.
  - `IStoreGeography` (Colombia only): `DepartmentsAsync(CancellationToken) → Task<IReadOnlyList<StoreGeoItem>>`; `CitiesAsync(Guid departmentId, CancellationToken) → Task<IReadOnlyList<StoreGeoItem>?>` (`null` = unknown department); `FindCityAsync(Guid cityId, CancellationToken) → Task<StoreCityRef?>`, where `StoreGeoItem(Guid Id, string Name)` and `StoreCityRef(Guid CityId, string CityName, Guid DepartmentId, string DepartmentName)`. Task 11 uses `FindCityAsync` to check the lane.
  - `ListPublicDepartmentsQuery(string Host)`, `ListPublicCitiesQuery(string Host, Guid DepartmentId)`; both resolve the store first.
  - `PublicStoreDto.SalesCountries` = `store.SalesCountries`; `Colors` = `StoreColorScheme.Resolve(...)` without the checks; `RegionalFormat` = `StoreTenantRef.Regional`.
  - `PublicStoreDto.Currencies` = `store.CurrenciesForVisitors()`, `PrimaryCurrency` = `store.PrimaryCurrency`. The featured-categories filter uses the union of `CategoriesAsync` over the store's currencies.
  - `PublicCategoryTree.Build(IReadOnlyList<StoreCategoryCount> direct, IReadOnlyList<(Guid Id, string Name, Guid? ParentId)> all) → IReadOnlyList<PublicCategoryDto>`. Rolls up like `PosCategoryLookup.RollUp`, drops zeros, and nulls out a missing parent.

- [ ] **Step 1: Write the failing tests.**

```csharp
// Unit
[Fact] public void CountsRollUpToAncestorsAndZerosAreDropped();
[Fact] public void AChildOfADroppedParentBecomesARoot();
// PublicCatalogApiTests
[Fact] public async Task AnUnpublishedStoreAndAnUnknownHostAreTheSame404();     // identical body except traceId
[Fact] public async Task WithTheModuleOffThePublishedStoreIs404();
[Fact] public async Task TheStoreDtoCarriesNoBillingAccountOrTenantId();         // assert JSON has no "billing", "tenantId", "companyId"
[Fact] public async Task OnlyVisibleActivePricedProductsAreListed();
[Fact] public async Task ACategoryIncludesItsDescendants();
[Fact] public async Task TheProductDetailHidesCodesAndScalesInRetailMode();     // no "code"/"barcode"; scales == []
[Fact] public async Task InScalesModeEachTierCarriesItsFinal();                 // final == PriceScale.FinalFor(price, discount)
[Fact] public async Task AHiddenProductDetailIs404();                            // store.public.product_not_found
[Fact] public async Task GetsAreCacheable();                                     // Cache-Control: public, max-age=60, stale-while-revalidate=300
[Fact] public async Task TheStoreListsItsCurrenciesAndThePrimary();             // store [COP, EUR] primary EUR → currencies ["COP","EUR"], primaryCurrency "EUR", no "billing"
[Fact] public async Task AMissingCurrencyIs422();                                // GET /products without ?currency → store.public.currency_invalid
[Fact] public async Task ACurrencyInUseButNotInTheStoreIs422();                 // catalog has USD prices, store [COP] → ?currency=USD → store.public.currency_invalid on /categories, /products, /products/{id}
[Fact] public async Task ProductsWithoutAPriceInTheRequestedCurrencyAreExcluded(); // A COP+EUR, B COP only: ?currency=EUR lists [A] with EUR priceFrom; /products/B?currency=EUR → 404 product_not_found
[Fact] public async Task CategoryCountsArePerCurrency();                         // same fixture: category of A and B → COP 2, EUR 1
[Fact] public async Task TheCurrencyIsCaseInsensitive();                        // ?currency=eur → 200, prices in EUR
[Fact] public async Task TheStoreCarriesTheTenantRegionalFormat();          // tenant numberFormat/dateFormat/timeZone → same values as /auth/me for that tenant
[Fact] public async Task TheStoreCarriesOnlyResolvedColors();               // JSON has colors.primary/onPrimary/accentText, no "lowContrast", no "onPrimaryColor"
[Fact] public async Task TheStoreListsItsSalesCountries();                       // [CO, ES] → salesCountries ["CO","ES"]
// PublicGeographyApiTests
[Fact] public async Task DepartmentsAreListedByNameWithoutDivipolaCodes();     // JSON has no "divipolaCode"
[Fact] public async Task CitiesOfADepartmentAreListed();
[Fact] public async Task AnUnknownDepartmentIs404();                            // store.public.department_not_found
[Fact] public async Task GeographyOfAnUnpublishedStoreIs404();                  // store.public.not_found
[Fact] public async Task GeographyIsCachedForAnHour();                          // Cache-Control: public, max-age=3600, stale-while-revalidate=86400
// PublicCartQuoteApiTests
[Fact] public async Task TheQuoteMatchesTheOrderEngineIncludingGrouping();      // same fixture as Task 3 grouping test → 10% on both lines
[Fact] public async Task AStaleLineIsReportedAndTheRestIsPriced();              // hidden variant in "unavailable", totals exclude it
[Fact] public async Task RepeatedVariantsAreMergedIntoOneLine();
[Fact] public async Task MoreThan100LinesIs422();
[Fact] public async Task AQuoteInTheSecondCurrencyUsesItsPrices();             // currency "EUR" → currency "EUR", unitPrice 25, total from EUR prices
[Fact] public async Task ALineWithoutAPriceInTheNewCurrencyIsUnavailable();    // cart [A, B], currency "EUR" → B in unavailable, A priced, totals exclude B
[Fact] public async Task AQuoteInACurrencyOutsideTheStoreIs422();              // store.public.currency_invalid
```

- [ ] **Step 2: Run to verify RED.** `$env:Seed__Enabled = "false"`, then `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicCatalogApiTests|FullyQualifiedName~PublicCartQuoteApiTests|FullyQualifiedName~PublicGeographyApiTests"` → FAIL.
- [ ] **Step 3: Implement.**
  - `StoreCatalogReader` follows `PosProductLookup` for categories, display names (`VariantDisplayName`), option values, tax and images in one call per page.
  - `priceFrom`/`priceTo` come from the sellable variants already loaded.
  - The cart pricing step is: require the currency; merge repeated variants; call `SellableVariantsAsync` with that currency; split available from unavailable; call `IStoreOrders.QuoteAsync` with the available lines only, skipping the call when there are none; add `imageUrl` from the sellable map; set `scaleApplied = discountPercentage > 0`.
- [ ] **Step 4: Run to verify GREEN.** Step 2 filter, plus `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter FullyQualifiedName~PublicCategoryTreeTests` → PASS.
- [ ] **Step 5: Commit.** `feat(store): serve the public catalog and price carts`

---

### Task 11: Public order requests with Turnstile

**Files:**
- Create in `Modules.Store.Application/`: `ICaptchaVerifier.cs`, `IStorePhoneNormalizer.cs`, `SubmitOrderRequest.cs`
- Create: `src/Modules/Store/Modules.Store.Infrastructure/Captcha/CloudflareTurnstileVerifier.cs` (named `HttpClient`, registered like `IntegrationsInfrastructureExtensions.cs:62-88`, timeout 5 s). Extend `src/Bootstrapper/StorePhoneNormalizer.cs` (Task 8) with `IStorePhoneNormalizer`
- Modify: `PublicStoreEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Store/Modules.Store.UnitTests/CloudflareTurnstileVerifierTests.cs`, `SubmitOrderRequestValidatorTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderRequestApiTests.cs`

**Interfaces:**
- Consumes: Task 10 `PublicCartPricing` and `IStoreGeography.FindCityAsync`; Task 5 `StorePlace`; Task 6 number generator and repository; Task 9 visitor IP.
- Produces:
  - `ICaptchaVerifier.VerifyAsync(string token, string remoteIp, string expectedHostname, CancellationToken) → Task<bool>`. It returns `false` when `success` is false or `hostname` ≠ the expected one, and throws `ServiceUnavailableException("store.captcha.unavailable")` on a timeout, a network error or a 5xx. With `Turnstile:Enabled = false` (tests only) a pass-through verifier is registered instead.
  - `IStorePhoneNormalizer.ToE164(string phone, string country) → string?`. Each block's phone is normalized with **that block's** country.
  - `PublicOrderRequestBody(Guid ClientRequestId, string TurnstileToken, string? Currency, PublicBillingInput Billing, PublicShippingInput? Shipping, string? Note, bool AcceptsDataPolicy, IReadOnlyList<PublicCartLineRequest> Lines)`.
  - `SubmitOrderRequestCommand(string Host, string VisitorIp, PublicOrderRequestBody Body) : ICommand<(PublicOrderRequestReceiptDto Receipt, bool Created)>`. The endpoint returns 201 in both cases.

Handler order (so that a rejected submission writes nothing and costs no number):
1. Resolve the store (404).
2. Validate the body with FluentValidation, limits of § E.
3. `StorefrontResolver.RequireCurrency(store, body.Currency)` → `store.public.currency_invalid`.
4. Check `acceptsDataPolicy` → `consent_required`.
5. Look up `clientRequestId`, and return the existing receipt if found (in its own currency, even if this retry names another).
6. Places, for billing and then shipping: country in `store.SalesCountries`, else `country_not_sold`; the lane check, else `city_invalid`; for `CO`, `FindCityAsync(cityId)` must exist and carry the sent `departmentId`, else `city_invalid`, and it gives the names to freeze. These run **before** Turnstile, so a bad form costs no Cloudflare call.
7. Normalize each phone with its block's country → `phone_invalid`.
8. Run Turnstile.
9. Check the daily cap: `CountReceivedSinceAsync(now - 24h) >= DailyRequestCap` → `RequestRateLimitedException("store.order_request.daily_cap")`.
10. Price the cart in the required currency. Any unavailable line → `line_unavailable`.
11. Open a transaction: number, `StoreOrderRequest.Create` with that `currency`, the billing block and the shipping block (or `null`), and consent `(now, visitorIp, store.DataPolicyUrl, store.DataPolicySetAt)`, `PublishAnonymous(RequestReceived)`, save, commit.
12. On `duplicate_client_request`, discard the pending changes and return the winner's receipt.

- [ ] **Step 1: Write the failing tests.**

```csharp
// Unit: verifier against a stub HttpMessageHandler
[Fact] public async Task ASuccessForAnotherHostnameIsRejected();
[Fact] public async Task ATimeoutIsServiceUnavailable();
[Fact] public async Task TheSecretAndRemoteIpAreSentAsForm();                // secret, response, remoteip
[Fact] public void ABillingBlockWithoutDocumentIsInvalid();             // identificationType or number missing → validation.failed
[Fact] public void AnUnknownIdentificationTypeIsInvalid();               // "RUT" → validation.failed
[Fact] public void AShippingBlockWithoutPhoneIsInvalid();
[Fact] public void ABillingBlockWithoutEmailIsInvalid();               // email missing → validation.failed, errors["Billing.Email"]
[Fact] public async Task ASubmissionWithoutEmailIs422WithTheField();  // API: 422 validation.failed, errors has "Billing.Email", no row, Turnstile not called
// PublicOrderRequestApiTests (fake verifier controllable per test)
[Fact] public async Task ASubmissionFreezesTheRepricedLinesAndConsent();     // lines, totals, consent ip == X-Qep-Visitor-Ip, policy url/date
[Fact] public async Task TheSameClientRequestIdReturnsTheSameReceipt();     // one row, same number
[Fact] public async Task AFailedCaptchaWritesNothingAndConsumesNoNumber();  // next success gets T-0001
[Fact] public async Task WithoutConsentIs422();
[Fact] public async Task APhoneWithoutCountryCodeThatDoesNotNormalizeIs422();
[Fact] public async Task ASubmissionWithAStaleLineIs422();                  // store.order_request.line_unavailable
[Fact] public async Task ThePublicEndpointNeverCreatesACustomer();          // customers count unchanged
[Fact] public async Task TheDailyCapIs429();                                 // DailyRequestCap = 2 in the factory
[Fact] public async Task ABodyOver64KbIs413();
[Fact] public async Task ASubmissionStoresItsCurrency();                    // currency "EUR" → row currency "EUR", receipt currency "EUR", lines at EUR prices
[Fact] public async Task ASubmissionInACurrencyOutsideTheStoreWritesNothing(); // 422 store.public.currency_invalid, no row, Turnstile not called, next success gets T-0001
[Fact] public async Task AMissingCurrencyIs422WithTheStoreCode();           // body without currency → store.public.currency_invalid, never validation.failed
[Fact] public async Task ASubmissionFreezesBillingAndShipping();           // department and city names frozen; shipping null with «mismos datos»
[Fact] public async Task ABillingCountryTheStoreDoesNotSellToIs422();      // store [CO], billing ES → country_not_sold, Turnstile not called
[Fact] public async Task AShippingCountryTheStoreDoesNotSellToIs422();
[Fact] public async Task ACityOfAnotherDepartmentIs422();                  // store.order_request.city_invalid
[Fact] public async Task AForeignBlockWithDivipolaIdsIs422();              // store [CO, ES], billing ES + cityId → city_invalid
[Fact] public async Task AForeignBillingWithACityTextIsAccepted();         // ES + cityName "Madrid" → billing_city_name "Madrid", ids null, phone normalized as ES
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter "FullyQualifiedName~CloudflareTurnstileVerifierTests|FullyQualifiedName~SubmitOrderRequestValidatorTests"` → FAIL.
- [ ] **Step 3: Implement** following the handler order above.
- [ ] **Step 4: Run to verify GREEN.** Step 2 filter, then `$env:Seed__Enabled = "false"` and `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter FullyQualifiedName~PublicOrderRequestApiTests` → PASS.
- [ ] **Step 5: Commit.** `feat(store): receive public order requests`

---

### Task 12: Inbox: list, summary, candidates, discard and take

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerRepository.cs:171` (add `Task<IReadOnlyList<Customer>> ListByPhoneE164Async(Guid tenantId, string phoneE164, int take, CancellationToken ct)`, for active, non-merged customers), `.../Infrastructure/Persistence/CustomerRepository.cs`, `CustomerProvisioning.cs` (`CompleteCustomerData.ClassificationId` → `Guid?`, § Verified facts 3; `CreateCompleteAsync` gains optional `Guid? actorUserId = null, string? source = null` so the take is audited with its person; new `EnsureAddressesAsync`), `Customer.cs` (new `EnsureAddress`)
- Create in `Modules.Store.Application/`: `IStoreCustomerDirectory.cs`, `IStoreMemberDirectory.cs`, `ListOrderRequests.cs`, `GetOrderRequestSummary.cs`, `GetOrderRequest.cs`, `GetCustomerCandidates.cs`, `DiscardOrderRequest.cs`, `TakeOrderRequest.cs`, `OrderRequestRowComposer.cs`
- Create: `src/Bootstrapper/StoreCustomerDirectory.cs` (over `ICustomerRepository` + `ICustomerProvisioning`, the same way `ServiceContractsCustomerProvisioning` uses them), `src/Bootstrapper/StoreMemberDirectory.cs`
- Extend: `IStoreOrders.CreateAsync` in `StoreOrdersAdapter.cs`
- Modify: `StoreEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerEnsureAddressTests.cs`, `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerProvisioningIntegrationTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/StoreInboxApiTests.cs`, `StoreTakeApiTests.cs`

**Interfaces:**
- Consumes: Task 1A foreign addresses; Task 3 `IStoreOrderGateway.CreateAsync` and `ExternalKeyFor`; Task 5 lease methods; Task 6 repositories.
- Produces:
  - `IStoreCustomerDirectory`:
    - `FindCandidatesAsync(Guid tenantId, string phoneE164, string? identificationType, string? identificationNumber, CancellationToken) → Task<IReadOnlyList<StoreCustomerCandidate>>`
    - `KnownAsync(Guid tenantId, IReadOnlyCollection<(string PhoneE164, string? Type, string? Number)> keys, CancellationToken) → Task<IReadOnlySet<int>>`. The indexes of the keys that match, one query per kind for a whole page.
    - `FindEligibleAsync(Guid tenantId, Guid customerId, CancellationToken) → Task<StoreCustomerRef?>`, `null` when unknown, of another tenant, inactive or merged. `StoreCustomerRef(Guid Id, bool IsComplete, bool WithRetention, bool VatSurplus)`.
    - `CreateAsync(Guid tenantId, Guid actorUserId, StoreNewCustomer data, CancellationToken) → Task<(StoreCustomerRef Customer, bool Created)>`, through `CreateCompleteAsync` with source `"store"`. `StoreNewCustomer(string Name, string IdentificationType, string IdentificationNumber, string Phone, string Email, string Address, string Country, Guid? CityId, string? CityName, Guid? ClassificationId)` is the billing block.
    - `EnsureAddressesAsync(Guid tenantId, Guid customerId, IReadOnlyList<StoreAddressInput> addresses, CancellationToken) → Task<IReadOnlyList<StoreAddressOutcome>>`, one outcome per input, in order. `StoreAddressInput(string Name, string Address, string? Phone, string Country, Guid? CityId, string? CityName)`. The adapter maps it to Customers' `ICustomerProvisioning.EnsureAddressesAsync`.
  - In Customers (no session, like the other provisioning methods):
    - `Customer.EnsureAddress(CustomerAddressDetails details, Func<string?, string?> phoneToE164, DateTimeOffset now) → (CustomerAddress Address, bool Added)`. It looks for an entry with the same country, the same normalized address (trim, `ToUpperInvariant`, runs of whitespace → one space), the same city (`CityId` for `CO`, the same normalization of `CityName` otherwise) and the same E.164 phone (both null matches). Found → returned untouched. Not found → `AddAddress(details, isPrincipal: false, now)`. Throws `customers.customer.incomplete`, like `AddAddress`.
    - `ICustomerProvisioning.EnsureAddressesAsync(Guid tenantId, Guid customerId, IReadOnlyList<CustomerAddressDetails> addresses, CancellationToken) → Task<IReadOnlyList<EnsuredAddress>>`, with `EnsuredAddress(Guid AddressId, EnsuredAddressKind Kind)` and `Reused | Added`. An incomplete customer throws `customers.customer.incomplete` (the take completes it before, so this only signals a bug). One save. A `DbUpdateConcurrencyException` surfaces as `RequestConcurrencyException`, which the Store adapter turns into `ResourceConflictException("store.order_request.conflict")`. Each new entry is audited like the address endpoint does.
    - `ICustomerProvisioning.CompleteFromStoreAsync(Guid tenantId, Guid customerId, CompleteCustomerData data, Guid actorUserId, CancellationToken) → Task<ProvisionedCustomer>`. Loads the customer (`null` / merged / inactive → `customers.customer.not_found`-style outcome that Store maps to 422 `store.order_request.customer_not_found`). Already complete → returns it untouched. Otherwise merges field by field, **existing non-blank value wins**: name, identification type + number (as a pair), phone, email, address, country; the city follows the merged country: if the customer already has a country, the billing city is used only when the billing country is the same, else the existing city lane stays (and, if it is empty, completion fails with the rules' `validation.failed` and the take releases its lease). Then the same steps as `CompleteExternalSaleAsync`: POST rules on the merged command, `PrepareAsync`, `customer.Complete(…)` (`Customer.cs:432`), audit `CustomerAuditActions.Completed` with the actor, save. `IdentificationTaken` on save → `ResourceConflictException("store.order_request.customer_document_taken")` (409), never a silent switch to the other customer.
    - Store port: `IStoreCustomerDirectory.CompleteAsync(Guid tenantId, Guid actorUserId, Guid customerId, StoreNewCustomer billing, CancellationToken) → Task<StoreCustomerRef>`, the adapter over `CompleteFromStoreAsync`.
  - `StoreCustomerCandidate(Guid Id, string Name, string? PhoneE164, string? IdentificationType, string? IdentificationNumber, bool IsComplete, IReadOnlyList<string> MatchedBy)`.
  - `IStoreMemberDirectory`: `ResolveCallerAsync(Guid tenantId, CancellationToken) → Task<(Guid MembershipId, Guid UserId)>` (over `IMembershipDirectory.FindActiveMembershipIdAsync`, 403 without one) and `NamesAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken)`.
  - `IStoreOrders.CreateAsync(StoreOrderCreate create, CancellationToken) → Task<StoreOrderCreated>`, where `StoreOrderCreated(Guid OrderId, string OrderNumber, decimal Total, Guid AdvisorMembershipId, bool AlreadyExisted)`.
  - The commands and queries of § HTTP contract D.

Take handler order (spec §5.2; § Verified facts 2):
1. Authorize: `quotations.order.manage` + `store` module.
2. Validate the body.
3. Resolve the caller.
4. Load the request (404).
5. Resolve the account: `store.BillingAccountFor(request.Currency)`. `null` (the currency was removed from the store) → 422 `store.order_request.currency_not_in_store`, before any lease, customer or order.
6. `BeginConversion(caller, now, ConversionLease)` and save. A concurrency loss becomes 409 `conflict`. Another lease gives `being_taken`, a non-`New` status gives `not_new`.
7. Resolve the customer: with `customerId`, `FindEligibleAsync` (422 `customer_not_found` for unknown, merged or inactive); if it is incomplete, `CompleteAsync` with the billing block (blank fields only). With `newCustomer`, `CreateAsync` from the billing block plus `classificationId`. A field that already has a value is never written.
8. Address book: `EnsureAddressesAsync` with the billing address (name = billing name) and, when `shipping` is not null, the shipping address (name = `effectiveRecipientName`). Phones go as E.164. It is idempotent, so a retake after a crash reuses what the first take added.
9. Create the order through `IStoreOrders.CreateAsync` with the step-5 account, the `Billing` and `Shipping` parties of § D «The order it creates», and the customer's `WithRetention`/`VatSurplus` as the billing tax profile. Advisor = the caller. `IsRetail` comes from the pricing mode **in the store now**. Lines come from the request. Notes = the request note.
10. `MarkConverted` with `ConvertedBy = result.AdvisorMembershipId`. That is the original taker when `AlreadyExisted`.
11. Audit `RequestConverted` and save.

On any exception in steps 7 to 9: `ReleaseConversion()`, save, rethrow. A customer created in step 7 or an address added in step 8 stays; the retake finds them by document and by content, so nothing is duplicated.

- [ ] **Step 1: Write the failing tests.**

```csharp
// CustomerEnsureAddressTests (unit)
[Fact] public void TheSameAddressIgnoringCaseSpacesAndPhoneFormatIsReused(); // "Cra 7  # 12-30" vs "CRA 7 # 12-30", "300 123 4567" vs "+573001234567"
[Fact] public void ADifferentCityIsANewAddress();
[Fact] public void AForeignAddressMatchesByCityText();                    // ES, "madrid " vs "Madrid"
[Fact] public void AnEnsuredAddressIsNeverPrincipalUnlessItIsTheFirst();
// CustomerProvisioningIntegrationTests
[Fact] public async Task CreateCompleteWithoutClassificationHasACucWithoutPrefix();
[Fact] public async Task CompleteFromStoreFillsOnlyBlankFields();         // POS-registered customer (name + CC, no email/city) → email, address, city from billing; name and CC kept; cuc set
[Fact] public async Task CompleteFromStoreNeverOverwritesAnExistingValue(); // customer phone "+573001112233", billing phone other → phone unchanged
[Fact] public async Task CompleteFromStoreRejectsMergedAndInactive();
[Fact] public async Task CompleteFromStoreOnACompleteCustomerChangesNothing(); // version unchanged
// StoreInboxApiTests
[Fact] public async Task TheViewsSplitUntakenMineAndAll();
[Fact] public async Task TheSummaryCountsTodayInTheTenantCalendar();      // tenant TZ America/Bogota, request at 23:30 local yesterday not counted
[Fact] public async Task KnownCustomerIsFlaggedByPhoneOrDocument();
[Fact] public async Task CandidatesSuggestOnlyAnUnambiguousMatch();       // phone → A, document → B ⇒ suggestedCustomerId null, both listed
[Fact] public async Task CandidatesExcludeInactiveAndMergedCustomers();
[Fact] public async Task DiscardingAConvertedRequestIs409();
[Fact] public async Task DiscardWithOtherWithoutNoteIs422();
[Fact] public async Task WithTheStoreModuleOffTheInboxIs403();
// StoreTakeApiTests
[Fact] public async Task TakingWithAnExistingCustomerCreatesAStoreOrder();
// order source Store, advisor = caller, isStorePickup false, externalReference "T-0001", request Converted with orderId
[Fact] public async Task TakingWithANewCustomerCreatesACompleteCustomerFromTheBilling(); // isComplete, cuc set, billing country/city/phone/email, classification null
[Fact] public async Task TheNewCustomerGetsTheClassificationTheDialogChose();
[Fact] public async Task TakingWithAnExistingCustomerNeverTouchesItsData(); // name, document, phone, email, address, country, city unchanged
[Fact] public async Task AnAddressAlreadyInTheBookIsReused();             // addresses.billing "Reused", book count unchanged
[Fact] public async Task ADifferentShippingAddressIsAdded();              // addresses.shipping "Added", not principal
[Fact] public async Task ARetakeDoesNotAddTheAddressTwice();              // order creation fails after step 8, retake → book +1 only
[Fact] public async Task AnIncompleteCustomerIsCompletedOnlyInItsBlankFields(); // customerCompleted true, isComplete, existing name/document kept
[Fact] public async Task CompletingNeverOverwritesAnExistingValue();     // WhatsApp customer with name and phone → both kept, billing fills document/email/address/city
[Fact] public async Task AddressesAreAddedAfterCompletion();             // after completion addresses.billing/shipping Reused or Added, never an error
[Fact] public async Task AMergedOrInactiveCustomerIsStillRejected();     // 422 customer_not_found, request New, no lease
[Fact] public async Task ACompletionWhoseDocumentIsTakenIs409();         // store.order_request.customer_document_taken, lease released
[Fact] public async Task AForeignBillingCreatesAForeignCustomerAndAddress(); // ES: customer country ES with cityName; book entry with cityName
[Fact] public async Task TheOrderCarriesTheBillingAndShippingParties();
[Fact] public async Task WithTheSameDataTheShippingPartyRepeatsTheBilling();
[Fact] public async Task TheBillingTaxProfileIsTheCustomers();            // existing customer WithRetention true → order PartyWithRetention true
[Fact] public async Task TheTakenOrderTotalEqualsTheQuotedTotal();       // totalDifference null
[Fact] public async Task APriceChangeAfterSubmissionShowsATotalDifference(); // raise the base price, take → totalDifference > 0
[Fact] public async Task TwoConcurrentTakesCreateOneOrder();             // Task.WhenAll of two members → one 200, one 409; one order row
[Fact] public async Task TheLoserOfATakeCreatesNoCustomer();             // both send newCustomer; customers +1, no address from the loser
[Fact] public async Task ARetakeAfterACrashMarksTheExistingOrder();
// create order via IStoreOrderGateway with ExternalKeyFor(id) and advisor A, expire lease, B takes → 200, convertedBy A, one order
[Fact] public async Task AFailedTakeReleasesTheLease();                  // variant deactivated → 422 product_inactive, then isBeingTaken false
[Fact] public async Task RetailModeTakesAsRetail();                       // order.isRetail true, globalScaleFloor null
[Fact] public async Task TakingAEurRequestUsesTheEurAccount();            // store [COP→A, EUR→B] primary COP, EUR request → order.currency "EUR", billingAccount == B
[Fact] public async Task TakingARequestWhoseCurrencyWasRemovedIs422AndKeepsItNew();
// EUR request, then PUT settings with [COP] → take 422 currency_not_in_store; request New, isBeingTaken false, no order, customers unchanged
// StoreInboxApiTests
[Fact] public async Task AConvertedRowCarriesItsCustomerId();             // New row customerId null; after take == order clientId
[Fact] public async Task EachRowCarriesItsOwnCurrency();                  // one COP and one EUR request → rows "COP" and "EUR", totals as frozen
```

- [ ] **Step 2: Run to verify RED.** `$env:Seed__Enabled = "false"`, then `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreInboxApiTests|FullyQualifiedName~StoreTakeApiTests"` → FAIL.
- [ ] **Step 3: Implement.**
  - `OrderRequestRowComposer` builds a page with one names lookup, one `KnownAsync` and the tenant clock.
  - `isBeingTaken` = `IsBeingTaken(now)`. `customerId` = the order's client, stored on the request at `MarkConverted` (column `customer_id`, Task 6).
  - Use the `MutableTenantClock` copied from `LoansApiHarness` to expire the lease in tests.
- [ ] **Step 4: Run to verify GREEN.** Step 2 filter, plus `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter FullyQualifiedName~CustomerEnsureAddressTests` and `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter FullyQualifiedName~CustomerProvisioning` → PASS. Then `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter FullyQualifiedName~Shopify`: the `ClassificationId` change touches Shopify's callers.
- [ ] **Step 5: Commit.** `feat(store): add the store inbox and take-to-order`

---

### Task 13: `storeRequest` in the order detail, error mapping, docs, full run

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/IOrderStoreRequestLookup.cs`, `src/Bootstrapper/OrderStoreRequestLookup.cs` (over `IStoreOrderRequestRepository.FindByOrderAsync`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersDtos.cs:299-308` (`OrderDetailResponse` gains `OrderStoreRequestResponse? StoreRequest`, last), `OrderResponseComposer.cs:13-63` (query only when `order.Source == "Store"`), `QepServiceCollectionExtensions.cs`
- Modify: `README.md` (a «Tienda pública» section with the two secrets, the public prefix and the deploy order: backend first), `CLAUDE.md` (built modules list)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderResponseComposerTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/StoreOrderDetailApiTests.cs`, `StoreErrorMappingApiTests.cs`

**Interfaces:**
- Produces:
  - `IOrderStoreRequestLookup.FindAsync(Guid tenantId, Guid orderId, CancellationToken) → Task<OrderStoreRequestRef?>`
  - `OrderStoreRequestRef(Guid Id, string Number, Guid CustomerId, string CustomerName, string Phone, string PhoneE164, string Email, decimal DisplayedTotal, string Currency, decimal? TotalDifference)`
  - `OrderStoreRequestResponse` with the same fields, camelCase in JSON.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Fact] public async Task AManualOrderHasANullStoreRequestAndNoLookup();   // fake lookup records zero calls
[Fact] public async Task AStoreOrderDetailCarriesTheRequestPhone();       // GET /orders/{id} → storeRequest.phoneE164
[Fact] public async Task TheOrdersListFiltersByStoreSource();             // GET /orders?source=Store
// StoreErrorMappingApiTests: one representative per type
[Fact] public async Task StoreErrorsMapToTheirStatus();                   // slug_taken 409, not_new 409, consent_required 422, public.currency_invalid 422, currency_not_in_store 422, not_found 404, captcha unavailable 503
```

- [ ] **Step 2: Run to verify RED.** `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter FullyQualifiedName~OrderResponseComposerTests` → FAIL.
- [ ] **Step 3: Implement** the port and adapter, then the docs.
- [ ] **Step 4: Run to verify GREEN.** Run the Step 2 filter, then these full projects, one per line:
  - `dotnet test tests/ArchitectureTests/ArchitectureTests`
  - `dotnet test tests/Modules/Store/Modules.Store.UnitTests`
  - `dotnet test tests/Modules/Store/Modules.Store.IntegrationTests`
  - `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests`
  - `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests`
  - `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests`
  - `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests`
  - `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests`
  - `dotnet test tests/Modules/Reporting/Modules.Reporting.UnitTests`
  - `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests`

  Every one must PASS. Then run `dotnet build Backend.slnx` → `Build succeeded` with no new warnings.
- [ ] **Step 5: Commit.** `feat(store): expose the store request in the order detail`

---

## Open items (not resolvable from code)

1. **Roles.** Settled (owner, 2026-11-03, spec §4.2): `store.settings.manage` goes only to `admin`; another role gets it from the role editor.
2. **Storefront authentication.** The header names (`X-Qep-Storefront-Key`, `X-Qep-Visitor-Ip`), the shared secret's rotation, and where it lives (Vercel env + the backend secret store) must match the Astro plan. A signed request (HMAC with a timestamp) is stronger but more work. This plan uses a constant-time-compared secret over TLS.
3. **Colors.** Settled (owner, 2026-11-03, spec §4.2): free hex colors with automatic on-primary and accent text, low contrast allowed and flagged. Picked by this plan: `#111111` as the near-black, 1 % lightness steps, and `primaryColor` required on every `PUT`.
4. **Concrete limits.** These are values this plan picked: 300/min reads, 5 per 10 min submits, a 500-per-day store cap, a 2-minute lease, 24/48 page size, integer quantities 1–100000, 100 lines, and the `T-0001` request number format.
5. **Retention and VAT surplus.** Settled (owner, 2026-11-03, spec §5.2): the cart is priced for an anonymous buyer, without either. At «Tomar» the billing party carries the chosen customer's flags (§ Verified facts 12), so a customer with retention or VAT surplus changes the order total, and that shows as `totalDifference`.
6. **«Escribir por WhatsApp» with an existing conversation by phone** (spec §4.4, §9). There is no backend lookup in this plan. If Messaging has no «conversation by phone» query, the frontend plan needs a backend addition.
7. **Currencies and their accounts.** Multi-currency is decided (owner, 2026-11-03): a list of `{ currency, billingAccount }` plus a primary, each account in its own currency (§ Verified facts 4). Values this plan picked and the owner may want to change: at most 10 currencies; the take uses the account configured **at take time**, not one frozen at submission; removing a currency with `New` requests is allowed and only blocks taking them (spec §5.2 says «el asesor lo resuelve en la configuración»).
8. **A configured currency that loses all its prices.** Settings only check `currencies-in-use` on save. If every price in that currency is later removed, the store keeps offering it and its pages are empty. This plan does not add a publish blocker for it.
9. **Featured categories with several currencies.** `GET /` takes no currency (spec §6), so it keeps a featured category that has visible products in any store currency. A category empty in the chosen currency still shows on the home page until the Astro app cross-checks `GET /categories?currency`.
10. **Party email.** Settled (owner, 2026-11-03, spec §4.1): the billing email is required and both parties take it, so the order engine's email rule (`OrderParty.cs:45-51`) holds unchanged.
11. **An existing incomplete customer.** Settled (owner, 2026-11-03, spec §5.2 «Cliente incompleto»): «Tomar» completes it with the billing block, filling only blank fields. Picked by this plan: when the customer's existing country differs from the billing country, its own city lane is kept, and a document clash is a 409 instead of switching customers.
12. **Sales countries as a publish blocker.** An empty list blocks publishing (`SalesCountryRequired`), like currencies. Picked by this plan.
13. **Foreign addresses created from the customer form.** Task 1A lets the address book hold foreign addresses but keeps `POST /customers` seeding none for a foreign customer, as today. Whether the customer form should now seed one is outside this feature.
14. **Geography cache.** One hour plus a day of stale-while-revalidate for the public departments and cities. Picked by this plan.
15. **Tasks 1A and 1B change Customers and Quotations for everyone.** They need the frontend to read the new nullable fields (`cityId`, `departmentId` of a foreign address; `country`, `cityName` of a party) before any foreign row exists. Only the store creates foreign addresses and parties at first, so deploying backend first is safe, but the backoffice must render a foreign address before the first foreign store order is taken.

## Self-review notes

- **Spec coverage:**
  - §3 (isolation, reserved slugs): Tasks 5 and 9.
  - §4.1 (public experience): Tasks 10 and 11.
  - §4.2 (settings, currencies list with an account each and a primary): Tasks 5, 6 and 8.
  - Multi-currency end to end: settings (Tasks 5, 6, 8), public `currency` (Tasks 10, 11), the request's currency (Task 11), the account of that currency at take (Task 12), the order in that currency (Task 3).
  - §4.2 «Países de venta» and «Ciudad fuera de Colombia»: Tasks 1A, 1B, 5, 8, 11 and 12.
  - §4.1 billing and shipping form, §6 public geography: Tasks 10 and 11.
  - §5.2 «Cliente», «Libreta de direcciones», «Partes del pedido», «Entrega»: Tasks 1A, 1B, 2, 3 and 12.
  - §4.3 (visibility): Task 4.
  - §4.4 (inbox, phone in the order detail): Tasks 12 and 13.
  - §5.1 and §5.2 (request model, take): Tasks 5, 3 and 12.
  - §5.3 (`store_domains`): Tasks 6 and 8.
  - §6 (public contract): Tasks 9–11.
  - §7 (constraints): Global Constraints, Tasks 7 and 9.
  - SEO, sitemap and DNS: not backend.
- **Types:** `IStoreOrderGateway` (Quotations) is consumed only through `IStoreOrders` (Store port). `ExternalKeyFor` is used in Tasks 3 and 12. `StorePricingMode` maps to `IsRetail` in exactly two places, `PublicCartPricing` and `TakeOrderRequestHandler`, both through `StorePricingModes.IsRetail(mode)` (Task 5 domain helper).
