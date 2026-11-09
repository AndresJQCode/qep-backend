# Buyer document type by country — backend design

Date: 2026-11-08. Cross-repo work: this spec covers `qep-backend`. The siblings are
`qep-storefront/docs/superpowers/specs/2026-11-08-buyer-document-type-by-country-design.md` and
`qep-frontend/docs/superpowers/specs/2026-11-08-buyer-document-type-by-country-design.md`. All three ship together in
the order given in §9: backend, then frontend, then storefront.

Status: design, not built. No code, migration or test exists yet for anything below. Backend line numbers are from `main`
at `5c2ea3f0` (`develop` has no extra commits). Revised on 2026-11-08 after the owner resolved the former pending decision
"should Customers and parties also enforce type-allowed-for-country?" with **strict everywhere** (§1).

## 1. Intent

The storefront checkout must offer identification document types according to the buyer's country, the same way the
back office does when a tenant creates a company. The tenant already chooses the countries the store sells to
(`OnlineStore.SalesCountries`). The buyer picks the billing country from that list, and the document-type options
follow it. Today the store and the buyer profile accept exactly four types for every country. A Spanish buyer cannot
give a NIF, and can pick a Colombian cédula.

**Owner decision (2026-11-08), resolving the former pending decision 1: do not ship until "Tomar" works.** An order
request carrying any of the seven codes must be taken (`TakeOrderRequest`) end to end. That means the customer is created
or matched, and the order's billing party is built, with no failure. The scope therefore covers Customers and Quotations
too (§6.4, §6.5).

**Owner decision (2026-11-08), resolving the former pending decision 2: strict everywhere.** Customers and
quotation/order billing parties also enforce type-allowed-for-country, with the same `CO`/`ES`/other table from the shared
`BuildingBlocks.Domain` catalogue (§6.1), not only the known set. When "Tomar" fills an incomplete customer's document
from the request, the customer also takes the request's country, so a customer inferred as `CO` that takes an `ES` +
`NIF` request ends up `ES` + `NIF` (§6.4.3).

*Owner-stated, not verifiable from the code:* every existing customer is Colombian and holds a Colombian type, so no
legacy cleanup or exemption is built. The consequence, if a non-conforming record exists anyway, is that it must be
corrected the next time someone edits it, because `Customer.Update` re-validates the whole record (`Customer.cs:858-901`).
§8 gives an optional read-only SQL check, and §6.4.5 notes one code path (Shopify) that could have produced such records.

What exists today:

- **Company catalogue (the reference).** `Modules.Companies.Domain/CompanyDocumentType.cs` defines seven wire codes
  `NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID` (`:22-32`) and `AllowedFor(country)` (`:81-87`): `CO` →
  `NIT, CC, CE, PASAPORTE`; `ES` → `NIF, NIE, PASAPORTE`; any other country → `TAX_ID, PASAPORTE`. It is enforced by
  the validator (`CompanyWriteRules.cs:81-93`) and by the domain (`Company.cs:351-358`,
  `companies.company.document_type_not_allowed`). The back office mirrors it in
  `qep-frontend/src/components/company-document-types.ts:6-37`.
- **Order request.** `PublicOrderRequestBodyValidator` hardcodes `["NIT", "CC", "CE", "PASAPORTE"]`
  (`SubmitOrderRequest.cs:79`, rule at `:125-128`), with no link to `Billing.Country`. The domain repeats the list
  (`StoreRequestBilling.cs:13`, check at `:39-43`, which throws `validation.failed` as a domain code). The handler
  rejects a billing or shipping country the store does not sell (`SubmitOrderRequest.cs:442-446`,
  `store.order_request.country_not_sold`).
- **Buyer profile.** `BuyerProfileValidator` hardcodes the same four (`BuyerProfileValidator.cs:25`, rule at `:59-62`).
  It is a copy of the Store validator, tied to it by `BuyerParityTests` (`tests/Bootstrapper/Bootstrapper.UnitTests`).
  The profile is stored whole as `jsonb` (`BuyersDbContext.cs:65-71`). `BuyerProfile.Create` and `Replace`
  (`BuyerProfile.cs:39-46`) enforce nothing.
- **Customers.** `IdentificationType` is a closed four-value enum (`Modules.Customers.Domain/IdentificationType.cs:14-20`).
  Its parser maps the four wire codes (`:24-31`), and `Parse` throws `customers.customer.identification_type_invalid`
  for anything else, with a message that lists the four (`:50-60`). `SupportedWireValues` (`:62`) is the list that
  every validator and adapter reads. `Customer` **has a country**: `Customer.Country`, ISO alpha-2, nullable only on an
  incomplete customer (`Customer.cs:224-232`). The only rule that links type and country is the NIT rule for a legal
  entity in Colombia (`EnsurePersonTypeMatches`, `Customer.cs:1238-1255`, called from the constructor `:69`, `Complete`
  `:460` and `Update` `:888`).
- **Quotations.** The quotation and order parties carry `PartyIdentificationType`, a second four-value copy
  (`PartyFiscalTypes.cs:14-24`). Its parser throws `quotations.party.identification_type_invalid`
  (`PartyFiscalTypes.cs:84-97`). **A party has a country**: `QuotationPartyDetails.Country`, ISO alpha-2, where `null`
  means Colombia (`QuotationPartyDetails.cs:36-39`); `CO` is stored as `null` (`:110-126`). Only the billing party keeps
  a document: the shipping party has it stripped before normalisation (`QuotationParty.cs:120-124`,
  `OrderParty.cs:79-83`).

## 2. Goals

1. `POST /api/v1/public/stores/{host}/order-requests` accepts any of the seven company codes in the billing block, and
   only the codes allowed for `Billing.Country`.
2. The buyer profile accepts and stores the same codes under the same per-country rule. That covers
   `PUT …/buyer/profile`, `register` with a profile, the checkout signup and the checkout "update my saved data".
3. **"Tomar" succeeds for every one of the seven codes**, whether it creates a new customer, completes an incomplete
   one, or uses a complete one. The order's billing party keeps the code.
4. Customers and quotation/order parties know the seven codes. A customer or a party holding `NIF`, `NIE` or `TAX_ID`
   reads, edits, merges, exports and is found by document like any other.
5. **Customers and quotation/order billing parties accept only the codes allowed for their own country**, in the
   validator and in the domain, like Companies.
6. The backend has one per-country table. Store, Buyers, Customers and Quotations use it, it is the same table as
   Companies, and a test keeps them equal. A second test ties the Customers and Quotations code sets to it.
7. A rejection is a field error the form can mark: `422 validation.failed` with the field key, backed by a domain
   invariant with its own code (§7).

## 3. Non-goals

- **Per-type number format or check digit.** The number stays required, max 32, as in Companies. See *Pending owner
  decision* 1.
- **Shopify's own code list** (`ShopifyDocumentTypes.cs:12`) keeps its four codes. An order attribute with another code
  falls back to the store's default type, as today. Shopify is still subject to the strict customer rule; §6.4.5 says
  what that changes.
- **Pos quick-create** (`PosCustomerRules.IdentificationTypes`, `IPosCustomerLookup.cs:54`) keeps its four codes. The
  register creates Colombian customers only (`Customer.CreateFromRegister` sets `CO`, `Customer.cs:415`).
- **Customer Excel import and service-contract import** stay Colombian (§6.4.4).
- **Suppliers** keep their own enum (`Modules.Suppliers.Domain/IdentificationType.cs`).
- The shipping block, which carries no document.
- Widening the country list beyond what the tenant configures.
- **Changing a complete customer during "Tomar".** A complete customer is never modified by "Tomar" today
  (`CustomerProvisioning.cs:314-318`; `TakeOrderRequest.cs:136-140` does not even call it), and this spec keeps that.
  See *Pending owner decision* 2.
- **Prefilling a foreign party from a foreign customer in the back office.** The party form prefills every customer as a
  Colombian party (frontend spec §6.2). That is a separate slice.

## 4. Anti-goals

- **A catalogue that drifts from the back office's.** No new code, no different label, no different per-country subset.
  A country that needs its own list gets it in Companies and the store at the same time (§6.1).
- **Letting the browser decide.** The storefront and the back office filter the options, but the backend is the
  authority. Every write path validates type-against-country on its own.
- **Governing the type by the shipping country.** The document identifies the billing party.
- **Breaking reads of existing data.** A stored profile, order request, customer or party whose type is now invalid for
  its country must still load (§8). The check runs on writes, never in an EF constructor or a `jsonb` record constructor.
- **Creating an invalid pair silently.** No merge path (store "Tomar", customer merge) may combine one record's document
  with another record's country (§6.4.3, §6.4.6).

## 5. Constraints

- **Architecture tests forbid reusing `Modules.Companies.Domain`.** Each module's Application may reference only itself
  and Tenancy among the business modules:
  - `StoreLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` (`StoreLayerTests.cs:55-64`);
  - `BuyersLayerTests.ApplicationAndInfrastructureReferenceNoOtherBusinessModule` (`BuyersLayerTests.cs:61-77`);
  - `CustomersLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` (`CustomersLayerTests.cs:65-75`);
  - `QuotationsLayerTests` (`:67-76`), the same rule.
- **`BuildingBlocks.Domain` is allowed and has precedent.** The Store, Buyers, Customers, Quotations and Companies
  Domain projects all reference it (`Modules.Customers.Domain.csproj:4`, `Modules.Quotations.Domain.csproj:4`,
  `Modules.Store.Domain.csproj:4`, `Modules.Buyers.Domain.csproj:4`). No architecture test restricts its content.
  `BuildingBlocks.Domain/Pricing/` already holds a rule that Quotations and Pos share (`VatIncludedLine.cs`, "the line
  formula shared between quotation, order and POS").
- **Parity is a test, not a convention.** `BuyerParityTests.TheProfileRulesMatchTheOrderRulesForBilling`
  (`BuyerParityTests.cs:121-164`) runs the same cases through both validators and fails if they disagree.
  `PosCustomerLookupTests.TheIdentificationTypesMatchTheCustomersModule` (`PosCustomerLookupTests.cs:97-104`) requires
  the Pos list to **equal** `IdentificationTypeParser.SupportedWireValues`. Widening Customers turns it red, which is
  intended (§6.4.4).
- **Field errors.** `ValidationException` produces the `errors` map (`ApiExceptionHandler.cs:70-77`), and so does a
  domain exception that implements `IHasFieldErrors` with a non-empty map (`:78-84`). `CustomersDomainException` and
  `QuotationsDomainException` implement it (`CustomersDomainException.cs:9`, `QuotationsDomainException.cs:5`), with
  precedent in `person_type_requires_nit` (`Customer.cs:1250-1254`) and `quotations.party.city_lane_invalid`
  (`QuotationPartyDetails.cs:104-106`). `StoreDomainException` and `BuyerDomainException` do not, so their domain codes
  arrive as `422` with the code and no map (`ApiExceptionHandler.cs:187-188`). The storefront maps
  `Billing.IdentificationType` to the input (`qep-storefront/src/features/checkout/field-errors.ts:6-8`).
- **Enum storage.** Customers and Quotations store their enum by **member name**, not by wire code. `HasConversion<string>()`
  writes `Nit`, `Cc`, `Ce` or `Pasaporte` (`CustomersDbContext.cs:106-109`, `QuotationsDbContext.cs:338-341` and
  `:749-752`). The Customers report emits the same member name (`CustomerReportSource.cs:380`; `ReportingDtos.cs:127`
  documents it as `Nit`).
- Messages that reach a person are Colombian Spanish with *tú* (`CLAUDE.md` § Cómo se escriben los mensajes). The
  Customers and Quotations validators write their messages in English today (`CustomerWriteRules.cs:91`,
  `QuotationEdits.cs:124`), and the new rules follow their neighbours. Codes stay as they are.

## 6. Design

### 6.1 Shared catalogue (new) — `BuildingBlocks.Domain`

*Design call (not owner-confirmed):* add a new static class to `BuildingBlocks.Domain` that holds the wire codes as
strings. A suggested name is `BuildingBlocks.Domain/Identification/DocumentTypesByCountry.cs`. The builder picks the final
name, but it must not be `Identity`, which is a module. The class has:

- the seven codes, exactly `NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID` (`All`);
- `AllowedFor(string country)` → the codes for that country. It uses the same table as `CompanyDocumentTypes.AllowedFor`,
  and trims and upper-cases the country itself;
- `IsKnown(string? type)` and `IsAllowed(string? type, string country)`, case-insensitive and trimmed, like
  `CompanyDocumentTypes.TryParse` (`CompanyDocumentType.cs:70-74`).

Why here and not a copy per module: Store, Buyers, Customers and Quotations all need it, and Companies already owns the
table. Every extra copy is one more place to forget when a country is added. `BuildingBlocks.Domain` is visible to all of
them, and the architecture tests allow it (§5).

Customers and Quotations hold typed enums, so each module adds a small typed helper over the shared table (for example
`IdentificationTypeParser.IsAllowedFor(IdentificationType, string country)` going through `ToWireValue`). The builder
picks the shape; the table itself is not copied.

Companies keeps its typed enum (`CompanyDocumentType`) unchanged. **Whether `CompanyDocumentTypes.AllowedFor` delegates
to the shared table is the builder's call.** Either way, a parity test (§10.1) asserts that both tables give the same codes
for `CO`, `ES`, a generic country and lower-case input. Companies behaviour does not change.

### 6.2 Store (`Modules.Store.Domain`, `Modules.Store.Application`)

- **Validator** (`PublicOrderRequestBodyValidator.BillingValidator`, `SubmitOrderRequest.cs:120-142`). Replace the
  four-item list (`:79`) with the shared catalogue, in two rules on `IdentificationType` that mirror
  `CompanyWriteRules.cs:81-93`:
  1. The code is known. The message stays «El tipo de documento no es válido.».
  2. The code is allowed for the billing country. The new message is «El tipo de documento no es válido para el país.».
     This rule runs only when the code is known and `Country` has a valid shape (`CountryShape`, `:110`), so a bad
     country is reported on `Country` alone.
  The property name stays `Billing.IdentificationType`, the key the storefront already reads.
- **Domain** (`StoreRequestBilling` constructor, `StoreRequestBilling.cs:29-53`). Replace the private list (`:13`) with
  the shared catalogue. After the known-code check, if the type is not allowed for `place.Country`, throw
  `StoreDomainException(StoreErrorCodes.DocumentTypeNotAllowed, …)`. The **new code** is
  `store.order_request.document_type_not_allowed`, named after `companies.company.document_type_not_allowed` and the
  `store.order_request.*` family (`StoreErrorCodes.cs:33-47`). The `place` null check moves before this check. The
  private EF constructor (`:17-27`) is untouched, so existing rows still materialise (§8).
- The comment at `StoreRequestBilling.cs:6` says "the same four types as the customer and the order parties". Rewrite
  it to say that the store, the customer and the order party share the seven codes and the same per-country table.
- No handler change. The validator runs before the country-sold check (`SubmitOrderRequest.cs:206` vs `:442`), and both
  stay where they are.

### 6.3 Buyers (`Modules.Buyers.Domain`, `Modules.Buyers.Application`)

- **Validator** (`BuyerProfileValidator.BillingValidator`, `BuyerProfileValidator.cs:54-76`). It gets the same two rules
  and the same messages as §6.2, using the shared catalogue, and the private list (`:25`) goes away. This keeps
  `BuyerParityTests` green by construction. The test gets the new cases (§10.1).
- **Domain invariant (new).** `BuyerProfile.Create` and `BuyerProfile.Replace` (`BuyerProfile.cs:39-46`) reject a billing
  type that is not allowed for `Billing.Country`, with `BuyerDomainException(BuyerErrorCodes.DocumentTypeNotAllowed, …)`.
  The **new code** is `store.buyer.document_type_not_allowed`, in the `store.buyer.*` family (`BuyerErrorCodes.cs:6-15`).
  **The check goes in the factory methods, never in the `BuyerBillingData` record constructor.** The record is
  deserialised from `jsonb` (`BuyersDbContext.cs:71`), and a check there would turn every legacy profile (§8) into a 500
  on read.
- **Signup path.** `BuyerSignup` writes the profile from the checkout without re-validating it
  (`BuyerSignup.cs:74,96`) and relies on the order validator. After this change the two agree, so the invariant cannot
  fire there, with one **exception**: a `store.buyer-signup-requested.v1` event that the old backend enqueued just before
  the deploy (the old Store accepted `CC` with `ES`). The worker retries a failing message, and after `MaxAttempts = 3` it
  closes the message without an account (`BuyerSignupRequestedWorker.cs:30,103-106,129-132`). The buyer would silently
  get no account.
  *Design call (not owner-confirmed):* in the signup path, if this invariant rejects the profile, the profile is dropped
  and the account is created without one. This is logged without personal data, instead of failing the message. The
  builder decides how: a catch at `BuyerSignup`, or a pre-check before `UpsertAsync`.
- `ReplaceBuyerProfileFromOrder` runs after the order has committed, and already swallows and logs any exception
  (`SubmitOrderRequest.cs:403-422`). No change.

### 6.4 Customers (`Modules.Customers.Domain`, `Modules.Customers.Application`, Bootstrapper adapters)

#### 6.4.1 Enum and parser

Append `Nif`, `Nie` and `TaxId` to `IdentificationType` (`IdentificationType.cs:14-20`). Map them in the parser
(`:24-31`) to `NIF`, `NIE` and `TAX_ID`, and in `ToWireValue` (`:34-43`).

- *Design call (not owner-confirmed):* the member names are `Nif`, `Nie` and `TaxId`. They become stored values and
  report values (§5), so they are fixed here rather than left to the builder.
- The `Parse` message (`:57-59`) is built from `SupportedWireValues` instead of listing four codes.
- Whether `ByWireValue` is derived from the shared catalogue is the builder's call. Either way, a parity test asserts
  `SupportedWireValues` equals the shared `All` (§10.1).

Everything that reads `SupportedWireValues` or `Parse` widens with it. These are verified to need no other change:

- the duplicate check `GET …/identification-matches` (`CustomerDuplicates.cs:84-86`, `:110`). It looks up a document
  pair, not a country, so it stays known-set only;
- the store candidate lookup and the inbox "known customer" flag (`StoreCustomerDirectory.cs:166-175`). Today a `NIF`
  silently skips the document match there; after the change it matches;
- the Shopify document lookup (`ShopifyCustomerDirectory.cs:29-39`). Shopify itself only sends the four codes;
- `CustomerProvisioning.WasCompletedWith` (`:459-464`);
- merge preview (`CustomerMerge.cs:124`, Application), mapping (`CustomerMapping.cs:40`, `:171`), the Excel export
  (`ClosedXmlCustomerExportBuilder.cs:107`) and the report (`CustomerReportSource.cs:380`);
- the lookups for Pos and Quotations (`PosCustomerLookup.cs:36,78`, `QuotationCustomerLookup.cs:125`). A Pos sale
  snapshots the wire code in `customer_identification_type varchar(10)` (`PosDbContext.cs:109`), and `PASAPORTE` (9)
  still fits.

The only exhaustive references to members are the NIT checks (`Customer.cs:1242`, `ImportCustomers.cs:575`), and the new
members do not affect them.

#### 6.4.2 Per-country rule (new)

**Validator.** `CustomerWriteRules` (`CustomerWriteRules.cs:87-92`) gets a second rule on `IdentificationType`: the code is
allowed for `Country`. It runs only when the code is known and `Country` has a valid shape (the rules at `:111-117`), so a
bad country is reported on `Country` alone, as in §6.2. The key stays `IdentificationType`, which the back-office form
already maps. Message, in English like its neighbours: "The identification type is not allowed for the country.".
`CustomerWriteRules` backs the back-office create and update and every `CustomerProvisioning` path that validates
(`CreateCompleteAsync` `:185`, `CompleteExternalSaleAsync` `:268`, `CompleteFromStoreAsync` `:321`), so all of them get
the rule.

**Domain invariant.** A new private check next to `EnsurePersonTypeMatches`, for example
`EnsureDocumentAllowedForCountry(IdentificationType, string country)`, called at the same three points: the constructor
used by `Create` (`Customer.cs:69`), `Complete` (`:460`) and `Update` (`:888`). It runs before any assignment, inside the
all-or-nothing guarantee those methods already document. It throws
`CustomersDomainException(CustomersErrorCodes.DocumentTypeNotAllowed, …)` with field errors on `IdentificationType`,
like `person_type_requires_nit` (`Customer.cs:1250-1254`). The **new code** is
`customers.customer.document_type_not_allowed`, named after the Companies one.

- `CreateFromRegister` (`Customer.cs:386-424`) also calls it with `CO`. Its callers already restrict the type to
  `AllowedFor("CO")` (§6.4.4), so it cannot fire; it keeps the invariant total for records that carry a document.
- The incomplete factories without a document (`CreateIncomplete` `:348-376`, `CreateIncompleteFromPhone` `:530-561`,
  `CreateIncompleteFromInstagram` `:591-610`, `CreateIncompleteFromExternalSale` `:622-662`) have nothing to check.
- The EF constructor (`:49-52`) has no check, so a non-conforming row still loads (§8).
- The NIT rule for a Colombian legal entity (`Customer.cs:1238-1255`) stays as it is. Both rules can fire on the same
  record; the builder orders them so the per-country rule runs first (a `CO` legal entity with `NIF` is a country
  problem before it is a NIT problem).

**Consequence (owner-stated assumption, §1).** `Update` re-validates the whole record. A customer whose stored pair is
not allowed, if one exists, cannot be saved again until someone changes its country or its document. `GET` keeps working.

#### 6.4.3 "Tomar": the document and the country travel together

"Tomar" reaches Customers through four paths (`TakeOrderRequest.cs:128-152`, `StoreCustomerDirectory.cs:87-135`). Only
one of them merges the request's document into an existing record:

| Path | What happens today | After |
| --- | --- | --- |
| **New** (`newCustomer`) | `CreateCompleteAsync` with the request's billing block (`StoreCustomerDirectory.cs:93-107`): document and country both come from the request | Unchanged. The pair is the request's, which the store already validated (§6.2) |
| **New, lost race** (`IdentificationTaken`) | The winner is read back by `(type, number)` (`CustomerProvisioning.cs:219-229`). If complete, it is used as is; if incomplete, it goes through `CompleteAsync` (`TakeOrderRequest.cs:145-151`) and, having a document, keeps its own pair | Unchanged. Same type means both countries allow it, or the winner's stored pair is the non-conforming case of §6.4.2 |
| **Existing incomplete** (picked by id, or a candidate) | `CompleteFromStoreAsync` → `MergeForStore` (`CustomerProvisioning.cs:377-403`) | **Changed** when the customer has no document (below). A customer with a document keeps its own document **and** its own country, as today |
| **Existing complete** (picked by id, or a candidate) | Never modified (`TakeOrderRequest.cs:136-140`; `CustomerProvisioning.cs:314-318`) | Unchanged (§3, *Pending owner decision* 2). The order's billing party still carries the request's country and document (`TakeOrderRequest.cs:279-290`), which §6.5 validates against each other |

The candidate list (`FindCandidatesAsync`, `StoreCustomerDirectory.cs:24-57`) only proposes ids; picking one goes through
the "existing" rows above.

**`MergeForStore` change.** Today the document is a pair taken whole from the customer or whole from the store (`:379`,
`:392-393`), but the country is merged field by field (`:380`). An incomplete WhatsApp customer whose country was
inferred from a `+57` phone (`IncompleteCustomerProfile.CountryFor`, `IncompleteCustomerProfile.cs:27-41`), taken
against an `ES` + `NIF` request, would complete as `CO` + `NIF`, which the strict rule rejects. The new rule:

- **The customer has a document** (`hasDocument`, `:379`): the command carries the customer's document and the
  customer's country, as today (`Keep(customer.Country, data.Country)`).
- **The customer has no document**: the command carries the request's document **and the request's country**
  (`data.Country`), whatever country the customer had.

The other fields, checked against the code for the case where the country is replaced:

- **City.** With the request's country, `sameCountry` (`:381`) is always true, so the city comes out as
  `customer.CityId ?? data.CityId` for `CO` and `Keep(customer.CityName, data.CityName)` elsewhere (`:383-386`). No
  incomplete factory sets `CityId` or `CityName` (`Customer.cs:348-376`, `:386-424`, `:530-561`, `:591-610`,
  `:622-662`), so in practice the city is the request's. No change needed.
- **CUC, principal address.** `PrepareAsync` derives them from the merged command (`CustomerProvisioning.cs:522-560`): a
  `CO` city gives its department code and the first address-book entry; any other country gives
  `CucFormatter.ForeignDepartmentCode` and no entry. Both follow the new country. No change needed.
- **Free-text address.** `Keep(customer.Address, data.Address)` (`:396`). No incomplete factory sets `Address`, and the
  customer merge only copies it from other incomplete customers (`CustomerMerge.cs:62`, domain), so in practice it is the
  request's. *Design call (not owner-confirmed):* when the country is replaced, the command takes `data.Address`, so a
  street never ends up under another country.
- **Phone.** This is the one real side effect. `Customer.Complete` recomputes `PhoneE164` from the kept `Phone` and the
  **new** country (`Customer.cs:469`), and the normaliser parses with that region (`LibPhoneNumberNormalizer.cs:39-55`).
  A phone stored with `+` is unaffected, because the region is ignored for it (`Customer.cs:37`). That covers the WhatsApp
  customers (`CreateIncomplete`, E.164 with `+`, `:342-347`) and the phone-only ones (`CreateIncompleteFromPhone`). A
  Shopify incomplete customer keeps the phone text Shopify sent (`CreateIncompleteFromExternalSale`, `:634-636`), which
  may be national. Re-parsed with another country, its `PhoneE164` becomes `null` or a different number, and phone
  matching and the inbox "known customer" flag stop finding it.
  *Design call (not owner-confirmed):* when the merged country differs from a non-null `customer.Country`, and the
  customer's phone is kept, the command carries `customer.PhoneE164` (already normalised with the old country) instead of
  `customer.Phone` when `PhoneE164` is non-null. The stored phone text then becomes the E.164 form of the same number.
  If `PhoneE164` is null, the raw phone is kept as today.
- **Person type.** `MergeForStore` keeps `customer.PersonType` (`CustomerProvisioning.cs:327`). An incomplete customer
  only gets a person type together with a document (`CustomerMergePlan`, `CustomerMerge.cs:42,71`, domain), so a
  customer with no document has none, and the NIT rule cannot fire on this path.
- **Name and email** are kept as today.

Pre-existing, not changed here: an incomplete customer **with** a document (from the register, always `CO`) taken against
a foreign request keeps `CO` with no city, so `CustomerWriteRules` fails on `CityId` (`CustomerWriteRules.cs:127-130`),
as the interface comment already says (`CustomerProvisioning.cs:126-128`).

`CompleteFromStoreAsync`'s interface comment (`:125-131`) says the customer's country wins. It is rewritten to say that the
document and the country come together, from the customer when it has a document and from the store otherwise.

#### 6.4.4 Colombian-only entry points keep `AllowedFor("CO")`

These paths cannot record a country other than `CO`, so they validate against `AllowedFor("CO")` from the shared
catalogue, instead of the widened `SupportedWireValues`. This is now the strict rule applied to a fixed country, not an
exception to it:

- **Customer Excel import.** Apply this to the row rule (`ImportCustomers.cs:72-78`, error
  `customers.import.row.identification_type_invalid`) and to the template's dropdown
  (`GetCustomerImportTemplate.cs:49`). The import forces `CO` (`ImportCustomers.cs:605-614`, `:660`). The parses at
  `:321` and `:795` run only on rows that already passed the rule. The frontend's static message
  «… uno de: CC, CE, NIT, PASAPORTE.» (`qep-frontend/src/features/customers/services/customers.api.ts:977-978`) stays
  true.
- **Service-contract import.** `ServiceContractsIdentificationTypes.SupportedWireValues`
  (`ServiceContractsCustomerProvisioning.cs:71-73`) returns `AllowedFor("CO")`. It feeds the row rule
  (`ExcelServiceContractsRowRules.cs:33-35`) and the template (`GetServiceContractsImportTemplate.cs:24`). Its customers
  are created by `CreateFromRegister` with `CO` (`ServiceContractsCustomerProvisioning.cs:49-52`).
- **Pos quick-create.** `PosCustomerRules.IdentificationTypes` stays at four. Change
  `PosCustomerLookupTests.TheIdentificationTypesMatchTheCustomersModule` to compare the Pos list with `AllowedFor("CO")`,
  and add a second assertion that every Pos code is in `SupportedWireValues`.

#### 6.4.5 Shopify: a consequence of strict

Shopify creates a complete customer from the order's **shipping** country (`ShopifyCustomerResolver.cs:255-256`) and one
of its four Colombian codes (`CompleteData`, `:222-238`), through `CreateCompleteAsync`. Today a buyer shipping to the
`US` with `CC` becomes a complete `US` + `CC` customer. **That is also the one code path that could have produced the
non-Colombian customers the owner says do not exist** (§1, §8).

After this change `CustomerWriteRules` rejects that pair. `ShopifyCustomerDirectory.CreateCompleteAsync` already turns a
`ValidationException` into `ShopifyCustomerNotCompletableException` (`ShopifyCustomerDirectory.cs:79-97`), and the
resolver then creates an incomplete customer and the `customer_incomplete` notice (`ShopifyCustomerResolver.cs:138-147`).
The order still imports. The same holds for `CompleteExternalSaleAsync` (`CustomerProvisioning.cs:267-272`, outcome
`NotCompletable`). A Colombian Shopify buyer is unaffected.

- Add `customers.customer.document_type_not_allowed` to `NotCompletableCodes` (`ShopifyCustomerDirectory.cs:23-24`) as
  a guard, in case the domain invariant ever fires without the validator.
- No Shopify code list changes (§3).

#### 6.4.6 Customer merge: the document and the country travel together

`CustomerMergePlan` fills an incomplete survivor's blank fields from the merged customers, each field from the **first**
merged customer that has it (`CustomerMerge.cs:58-63`, domain). The document and the country are picked independently:
an incomplete survivor with no document and no country, merged with a WhatsApp customer inferred as `ES` and then a
register customer with `CO` + `CC`, ends up `ES` + `CC`. Nothing rejects that today, because incomplete records are not
validated; under the strict rule it would make the next "Tomar" or completion of that customer fail (§6.4.3, the
customer has a document and keeps its own pair).

*Design call (not owner-confirmed):* for an incomplete survivor,

- the document is filled from the first merged customer whose document is allowed for the resulting country: the
  survivor's own country when it has one, otherwise that merged customer's country;
- when the survivor has no country and the document is filled, the country comes from the **same** merged customer;
  when no document is filled, the country is filled as today.

A merged customer skipped for the document keeps it, exactly as any merged customer that is not the document's source
does today (`MarkMerged`, `CustomerMerge.cs:144-157`); the partial unique index already ignores merged rows. The preview's
`FilledFields` (`CustomerMerge.cs:76-85`) follows the plan, so it stays honest.

#### 6.4.7 Error codes

`customers.customer.identification_type_invalid` keeps its meaning: an unknown code. The new
`customers.customer.document_type_not_allowed` means a known code not allowed for the customer's country (§7).

### 6.5 Quotations (`Modules.Quotations.Domain`, `Modules.Quotations.Application`)

- **Enum and parser.** Append `Nif`, `Nie` and `TaxId` to `PartyIdentificationType` (`PartyFiscalTypes.cs:18-24`), and map
  them in `IdentificationTypes` (`:39-46`) and `ToWireValue` (`:57-66`). Use the same member names as Customers (§6.4.1).
  The class comment (`:14-17`) still says it duplicates Customers. Add a parity test against the shared `All` (§10.1).
- **Messages that list four codes.** Build these from `SupportedIdentificationTypes`: `PartyFiscalTypes.cs:96`,
  `QuotationEdits.cs:124` and `OrderEdits.cs:168`. The rules themselves already read the parser's list
  (`QuotationEdits.cs:121-125`, `OrderEdits.cs:165-169`). The comment on `QuotationPartyRequest.IdentificationType`
  (`QuotationsDtos.cs:224-225`) and on `QuotationPartyDetails.IdentificationType` (`QuotationPartyDetails.cs:22-23`)
  name four codes and are rewritten.
- **Per-country rule on the billing party (new).** A party has its own country (§1), so the rule is the same as for
  customers, against the party's country, where a blank country means `CO` (`QuotationPartyDetails.IsColombian`,
  `:66-68`). No other source of truth is needed: for a store order, the party's country is the request's billing
  country (`TakeOrderRequest.cs:279-290` → `IStoreOrderGateway.cs:242-253`), the same country the store validated the
  document against.
  - **Validators.** `QuotationPartiesRequestValidator` (`QuotationEdits.cs:121-125`, key `Parties.Billing.IdentificationType`)
    and `OrderEdits.AddBillingRules` (`OrderEdits.cs:165-169`, key `Billing.Party.IdentificationType`) each get a second
    rule: the known code is allowed for the party's `Country` (blank → `CO`). It runs only when the code is known and the
    country is blank or two ASCII letters, so a malformed country stays the domain's `quotations.party.country_invalid`.
    Message: "The identification type is not allowed for the country.".
  - **Domain.** `QuotationPartyDetails.Normalized` (`:76-82`) checks it after `NormalizeCountry`, when
    `IdentificationType` is not null, and throws `QuotationsDomainException("quotations.party.document_type_not_allowed",
    …)` with field errors on `{fieldPrefix}.IdentificationType`, the same shape as `LaneInvalid` (`:104-106`). The
    **new code** joins the `quotations.party.*` family. The shipping party has its document stripped before
    `Normalized` (`QuotationParty.cs:120-124`, `OrderParty.cs:79-83`), so it is never checked.
  - **Every party write re-validates.** `OrderParty.Create` and `Apply` both go through `Normalized`: creation
    (`Order.cs:1295`, `:632`), edits (`Order.cs:660`) and quotation-to-order conversion. A stored party whose pair is not
    allowed, if one exists, loads, but must be corrected when the quotation or order is edited, and blocks converting
    that quotation until then. The shipping-city correction (`Order.cs:1219`) touches the shipping party only.
  - **Shopify orders** carry no billing party (`ShopifyOrderWriter.cs:140`, `Billing: null`), so the rule never reaches
    them.
- **A quotation or order without its own billing party** bills to the customer's data and stores no party row
  (`QuotationParties`, `QuotationPartyDetails.cs:190-195`). The customer's own pair is governed by §6.4.
- **NIT rule.** `BillingPartyFiscalRules.EnsureConsistent` (`BillingPartyFiscalRules.cs:18-36`) is unchanged. It only
  rejects a legal entity that is not identified by NIT when the **tenant** is Colombian. A store order never carries a
  person type (`StoreOrderParty` has none; `IStoreOrderGateway.cs:242-253`), so "Tomar" never reaches it.
- **PDF.** The party block prints the document number only, never its type (`QuotationPdfDocumentMapper.cs:170-172`).
  `CompanyDocumentLabels` (`CompanyDocumentLabels.cs:14-24`) labels the **issuing company's** document, and already
  knows the seven codes. No change.
- **Order export.** `OrdersExportProcessor` writes the wire code as text (`:455`, `:484`). The new codes appear in the
  spreadsheet as they are. No change.
- **Electronic invoicing.** The code has no DIAN or electronic-invoicing integration (no matches for DIAN, UBL, CUFE,
  Siigo or Alegra in `src/`). The only DIAN mention is a comment on the person-type requirement
  (`PersonTypeRequirement.cs:28`). Nothing downstream rejects `NIF`, `NIE` or `TAX_ID`. If the order export feeds a
  tenant's accounting import outside QEP, that system's list of document types is outside this code (§11).

### 6.6 "Tomar" end to end — every place the type is mapped or validated

In order, from `POST …/store/order-requests/{id}/take` (`TakeOrderRequest.cs`):

| # | Step | Where | Today with `NIF` | After |
| --- | --- | --- | --- | --- |
| 0 | Candidates and "known customer" in the inbox | `StoreCustomerDirectory.TryKey` (`:166-175`), used by `FindCandidatesAsync` (`:24-57`) and `KnownAsync` (`:59-85`) | No document match; phone only | Matches by document |
| 1 | Billing data for the customer | `TakeOrderRequest.BillingData` (`:244-259`) | Raw string, no check | Unchanged |
| 2a | New customer | `StoreCustomerDirectory.CreateAsync` (`:93-107`) → `CustomerProvisioning.CreateCompleteAsync` → `CustomerWriteRules` (`CustomerWriteRules.cs:87-92`) | **Fails:** `422 validation.failed`, `errors["IdentificationType"]` | Passes both rules (the request's pair) |
| 2b | Same, after validation | `PrepareAsync` → `CustomerMapping.ToIdentification` → `IdentificationTypeParser.Parse` (`CustomerMapping.cs:171`, `CustomerProvisioning.cs:549`); `Customer.Create` invariant | Not reached | Parses to `Nif`; the invariant passes |
| 2c | Lost race on the document | `FindByIdentificationAsync` after `IdentificationTaken` (`CustomerProvisioning.cs:219-229`) | Not reached | Finds the winner by `(Nif, number)` |
| 3 | Incomplete customer picked or found | `CompleteAsync` (`StoreCustomerDirectory.cs:109-135`) → `CompleteFromStoreAsync` → `MergeForStore` (`CustomerProvisioning.cs:377-403`) → same validator, parser and `Customer.Complete` invariant | **Fails** as 2a when the customer has no document | Passes. Without a document, the customer takes the request's document **and country** (§6.4.3); with one, it keeps its own pair |
| 4 | Complete customer picked | `FindEligibleAsync` (`:87-91`) | No type involved | Unchanged; the customer is not modified |
| 5 | Address book | `EnsureAddressesAsync` | No type involved | Unchanged |
| 6 | Billing party of the order | `TakeOrderRequest.BillingParty` (`:279-290`) → `StoreOrdersAdapter.ToParty` (`StoreOrdersAdapter.cs:116-119`) → `StoreOrderGateway.ToParty` (`IStoreOrderGateway.cs:242-253`) → `QuotationPartiesRequest.ToDomain` → `PartyFiscalTypesParser.ParseIdentificationType` (`QuotationMapping.cs:171`) → `OrderParty.Create` → `Normalized` | **Fails** with domain code `quotations.party.identification_type_invalid`, reached only when step 4 skipped the customer checks | Parses to `Nif`; the per-country check passes because the party carries the request's billing country |
| 7 | NIT rule | `BillingPartyFiscalRules.EnsureConsistent` (`IStoreOrderGateway.cs:188`) | No person type, no effect | Unchanged |
| 8 | Responses | `OrderMapping.cs:112`, `CustomerMapping.cs:40`: `ToWireValue` | Not reached | `NIF` |

On any failure, the lease is released and the request stays *New* (`TakeOrderRequest.cs:88-98`). A customer created in
step 2 stays, as the comment there says. The shipping party never carries a document (`TakeOrderRequest.cs:292-313`).

### 6.7 Persistence — no migration

| Column | Type | Check constraint on the type | Longest new value |
| --- | --- | --- | --- |
| `store.order_requests.billing_identification_type` | `varchar(16)` (`StoreDbContext.cs:232`) | none | `PASAPORTE` (9), wire code |
| `buyers.profiles.data` | `jsonb`, no schema (`BuyersDbContext.cs:65-71`) | none | — |
| `customers.customers.identification_type` | `varchar(32)`, enum name (`CustomersDbContext.cs:106-109`; `20260819175351_InitialCustomers.cs:39`) | none. `CK_customers_complete_fields` only requires non-null on a complete customer (`CustomersDbContext.cs:41`, `:75`) | `Pasaporte` (9), `TaxId` (5) |
| `quotations.quotation_parties` / `quotations.order_parties` `.identification_type` | `varchar(16)`, enum name (`QuotationsDbContext.cs:338-341`, `:749-752`; `20261027121648_AddPartyPersonTypeAndIdentificationType.cs`) | none | same |
| `pos.sales.customer_identification_type` | `varchar(10)`, wire code (`PosDbContext.cs:109`) | none | `PASAPORTE` (9) |

EF Core does not record an enum's members in the model snapshot, and appending a member does not change any column. The
unique index `IX_customers_tenant_identification` (`CustomersDbContext.cs:240-248`) indexes the stored text, and needs no
change. The only check constraint on document types is the Companies one (`CompaniesDbContext.cs:32-33`), which already
has seven codes.

**No migration is needed.** The builder proves it with `dotnet ef migrations has-pending-model-changes` for
`CustomersDbContext` and `QuotationsDbContext` (§10.2). The per-country subset stays out of the database, as in
Companies: a check constraint would make a non-conforming row (§8) fail on any unrelated write.

### 6.8 Contract

There is no new field, route or DTO. `identificationType` stays a string in `PublicBillingInput`, `BuyerBillingDto`, the
customer DTOs, the party DTOs and the inbox DTOs; only the set of accepted values changes, and now depends on the record's
country. `BuyerParityTests.TheSignupEventContractMatches` is unaffected. The Customers report (`ReportingDtos.cs:127-135`)
emits the member names `Nif`, `Nie` and `TaxId`, next to `Nit`, `Cc`, `Ce` and `Pasaporte`.

## 7. Error codes (summary)

| Situation | Response | Where |
| --- | --- | --- |
| Unknown code in the store or the profile | `422 validation.failed`, `errors["Billing.IdentificationType"]` (unchanged) | both validators |
| Known code, not allowed for the billing country | `422 validation.failed`, `errors["Billing.IdentificationType"]` | both validators |
| Same, reaching the domain past the validator | `422 store.order_request.document_type_not_allowed` (**new**), no `errors` map | `StoreRequestBilling` |
| Same, in the profile domain | `422 store.buyer.document_type_not_allowed` (**new**), no `errors` map | `BuyerProfile.Create/Replace` |
| Unknown code on a customer | `422 validation.failed` with `errors["IdentificationType"]`, or domain code `customers.customer.identification_type_invalid` (unchanged; the message lists seven codes) | `CustomerWriteRules`, `IdentificationTypeParser` |
| Known code not allowed for the customer's country | `422 validation.failed` with `errors["IdentificationType"]` | `CustomerWriteRules` |
| Same, reaching the domain | `422 customers.customer.document_type_not_allowed` (**new**) with `errors["IdentificationType"]` | `Customer` constructor, `Complete`, `Update`, `CreateFromRegister` |
| `NIF`/`NIE`/`TAX_ID` in the Colombian-only imports | row error `customers.import.row.identification_type_invalid` (unchanged) and the service-contract equivalent | §6.4.4 |
| Unknown code on a party | field error at `Parties.Billing.IdentificationType` (quotations) or `Billing.Party.IdentificationType` (orders), unchanged, the message lists seven codes; or `quotations.party.identification_type_invalid` | `QuotationEdits`, `OrderEdits`, `PartyFiscalTypesParser` |
| Known code not allowed for the party's country | `422 validation.failed` at the same two keys | `QuotationEdits`, `OrderEdits` |
| Same, reaching the domain | `422 quotations.party.document_type_not_allowed` (**new**) with `errors["Parties.Billing.IdentificationType"]` | `QuotationPartyDetails.Normalized` |

The storefront maps the two new Store/Buyers codes in `src/lib/copy.ts` (storefront spec §6.5). The back office reads
the `errors` map for the Customers and Quotations ones (frontend spec §6.2).

## 8. Existing data

- **Buyer profiles.** The old validator accepted `NIT`, `CC` and `CE` with any country, so `buyers.profiles` may hold, for
  example, `CC` with `ES`. Reads are untouched (§6.3): `GET` returns the profile as stored. The storefront resets such a
  type when it pre-fills the form (storefront spec §6.3). A `PUT …/buyer/profile` that sends it back unchanged is
  rejected with `422 validation.failed` on `Billing.IdentificationType`, and the buyer picks a valid type and saves. No
  backfill.
  *Design call (not owner-confirmed):* no data migration rewrites legacy profiles; the next save fixes them.
- **Order requests.** Existing rows keep whatever they have. They load through the private EF constructor and are never
  re-validated. "Tomar" uses their billing country for the customer (§6.4.3) and the party (§6.5), so an old request
  with `ES` + `CC` would now fail at "Tomar" with `422 validation.failed` on `IdentificationType`. Such a request could
  only exist if a foreign buyer picked a Colombian type before the deploy; the operator discards it or asks the buyer to
  submit again.
- **Customers and parties.** Existing rows hold only the four old member names. **Owner-stated (2026-11-08), not
  verifiable from the code:** every existing customer is Colombian with a Colombian type, so no cleanup or exemption is
  built. If a non-conforming row exists anyway (for example, a Shopify customer, §6.4.5), it reads normally and must be
  corrected the next time it is edited (§6.4.2, §6.5).
- *Optional, for the owner before deploy:* a read-only check that confirms the assumption. It only reads, it counts, and
  it prints no personal data. Today only the four old members exist, so "not allowed" reduces to `Nit`, `Cc` or `Ce`
  outside Colombia; a party with a blank country is Colombian.

  ```sql
  -- Customers whose stored pair the strict rule would reject.
  SELECT completeness, country, identification_type, count(*) AS customers
  FROM customers.customers
  WHERE identification_type IN ('Nit', 'Cc', 'Ce')
    AND upper(coalesce(country, '')) <> 'CO'
  GROUP BY completeness, country, identification_type;

  -- Billing parties whose stored pair the strict rule would reject (shipping parties hold no type).
  SELECT 'quotation_parties' AS source, country, identification_type, count(*) AS parties
  FROM quotations.quotation_parties
  WHERE identification_type IN ('Nit', 'Cc', 'Ce') AND country IS NOT NULL AND upper(country) <> 'CO'
  GROUP BY country, identification_type
  UNION ALL
  SELECT 'order_parties', country, identification_type, count(*)
  FROM quotations.order_parties
  WHERE identification_type IN ('Nit', 'Cc', 'Ce') AND country IS NOT NULL AND upper(country) <> 'CO'
  GROUP BY country, identification_type;
  ```

  Zero rows confirms the assumption. Rows mean someone corrects those records, or accepts that they must be fixed on
  their next edit. Column names are from `CustomersDbContext.cs:106-109,140-141,203` and
  `QuotationsDbContext.cs:311,338-341,355,724,749-752,766`.

## 9. Deployment

**Order: backend → frontend → storefront.** Nothing creates `NIF`, `NIE` or `TAX_ID` data until the storefront ships, so
the back office must already handle those codes by then.

0. *Optional:* the owner runs the read-only check of §8 against production.
1. **Backend, one release.** Store, Buyers, Customers and Quotations reach `develop` together, or Customers and Quotations
   first. The store must never accept the new codes in an environment where Customers and Quotations reject them: that
   is the owner's "do not ship until resolved".
2. **Frontend** (sibling spec). It widens the customer and party forms, filters them by country and resets a type that
   the new country does not allow. Old frontend → new backend: the old customer form offers the four Colombian codes for
   every country, so saving a foreign customer with `CC`, `CE` or `NIT` now gets the field error on «Tipo de documento»
   (`PASAPORTE` still works). An old frontend that opens a customer created with `NIF` through the API cannot save it,
   which is why the frontend ships before the storefront and soon after the backend.
3. **Storefront** (sibling spec).

Cross-version behaviour:

- **Old storefront → new backend.** The old form offers the four codes for every country. A Colombian buyer is
  unaffected. A buyer from another country who picks `CC`, `CE` or `NIT` gets the field error on «Tipo de documento», and
  can still pick «Pasaporte», which is valid everywhere. A store selling only to `CO` sees no change.
- **New storefront → old backend.** The old backend rejects `NIF`, `NIE` and `TAX_ID` on `Billing.IdentificationType`.
  That is why the backend goes first.
- **Signup events in flight** across the deploy: §6.3.
- **Shopify** imports after the deploy: a foreign buyer with a Colombian type becomes an incomplete customer (§6.4.5).

## 10. Testing (TDD: RED before GREEN, literal evidence of both)

### 10.1 Unit

`tests/BuildingBlocks` has no project today. *Builder's call:* put the catalogue tests in `Modules.Companies.UnitTests`
(Companies.Domain already references `BuildingBlocks.Domain`), or create a BuildingBlocks test project.

- **Catalogue.** `AllowedFor("CO")` is `NIT, CC, CE, PASAPORTE`; `"ES"` is `NIF, NIE, PASAPORTE`; `"US"` and `"MX"` are
  `TAX_ID, PASAPORTE`. `" es "` and `"co"` normalise. `IsKnown` rejects `XX`, `""` and `null`. `IsAllowed("cc", "CO")` is
  true and `IsAllowed("CC", "ES")` is false.
- **Companies parity (new).** For `CO`, `ES`, `US` and `co`, `CompanyDocumentTypes.AllowedFor(c)` mapped through
  `ToWire` equals the shared `AllowedFor(c)`, and the full code sets are equal. RED until the shared class exists.
- **Customers parity (new, `Modules.Customers.UnitTests`).** `IdentificationTypeParser.SupportedWireValues` equals the
  shared `All`. For each of the seven codes, `Parse(code)` and `Parse(code.ToLowerInvariant())` round-trip through
  `ToWireValue`. `Parse("XX")` throws `customers.customer.identification_type_invalid`, and its message names `NIF`. RED
  on the four-value enum.
- **`CustomerWriteRules`** (`Modules.Customers.UnitTests`). Valid: `ES` + `NIF`, `ES` + `NIE`, `US` + `TAX_ID`,
  `CO` + `CC`, `ES` + `PASAPORTE`. Invalid on `IdentificationType` only: `CO` + `NIF`, `ES` + `CC`, `US` + `NIT`,
  `CO` + `TAX_ID`. `COL` + `NIF` reports on `Country` and not on the type. `XX` keeps the unknown-code message. RED on the
  current rules, which accept `ES` + `CC`.
- **`Customer` domain (new tests).** `Create`, `Complete` and `Update` with `ES` + `Cc` throw
  `customers.customer.document_type_not_allowed` with `FieldErrors["IdentificationType"]`, and leave the aggregate
  unchanged (for `Update`, the name passed in the same call is not assigned). `ES` + `Nif` succeeds. A `CO` legal entity
  with `Nif` reports the per-country code, not `person_type_requires_nit`.
- **`ExcelCustomerRowRules`.** A row with `NIF` fails with `customers.import.row.identification_type_invalid`, and `CC`
  passes. The template's document list is `AllowedFor("CO")`. RED once the enum widens and before the import is
  restricted.
- **`CustomerProvisioningTests`.**
  - `CreateCompleteAsync` with `ES` + `NIF` creates a complete customer with `IdentificationType.Nif`.
  - `CompleteFromStoreAsync` on an incomplete customer with country `CO` and no document, given `ES` + `NIF` data,
    completes it as **`ES` + `NIF`**, with the request's city name. RED on today's `MergeForStore`, which keeps `CO`.
  - Same customer with a national phone stored as from Shopify (`3001234567`, country `CO`, `PhoneE164`
    `+573001234567`): after completing as `ES`, `PhoneE164` is still `+573001234567`. RED today.
  - An incomplete customer with `CO` + `CC` (register), given `CO` + `CC` data with a city, keeps its document and `CO`.
  - `CreateCompleteAsync` with `US` + `CC` throws `ValidationException` on `IdentificationType`.
- **`CustomerMergePlan`** (domain). Survivor incomplete with no document and no country; merged in order: a customer
  with `ES` and no document, then one with `CO` + `CC`. The survivor gets `CC` **and** `CO` from the second, and
  `FilledFields` holds `Identification` and `Country`. Survivor incomplete with `ES` and no document, merged with a
  `CO` + `CC` customer: no document is filled, `FilledFields` has no `Identification`, and the merged customer keeps its
  document. RED on today's plan.
- **Shopify** (`Modules.Shopify.UnitTests` or the directory tests in `Bootstrapper.UnitTests`, whichever holds
  `ShopifyCustomerResolver` today). A snapshot shipping to `US` with document type `CC` creates an **incomplete** customer
  and does not throw. RED today, when it creates a complete one.
- **`PosCustomerLookupTests`** (`Bootstrapper.UnitTests`). The parity test compares with `AllowedFor("CO")`, and every
  Pos code is in `SupportedWireValues`. RED as written today once the enum widens.
- **Service contracts** (`Bootstrapper.UnitTests`). `ServiceContractsIdentificationTypes.SupportedWireValues` equals
  `AllowedFor("CO")`.
- **Quotations parity (new, `Modules.Quotations.UnitTests`).** `PartyFiscalTypesParser.SupportedIdentificationTypes`
  equals the shared `All`, and the seven codes round-trip. `ParseIdentificationType("XX")` throws
  `quotations.party.identification_type_invalid`.
- **`QuotationEdits` / `OrderEdits` validators.** Valid billing parties: `ES` + `NIF`, `ES` + `NIE`, `US` + `TAX_ID`, and
  blank country + `CC`. Invalid: `ES` + `CC`, blank country + `NIF`, `CO` + `TAX_ID`, at
  `Parties.Billing.IdentificationType` (quotations) and `Billing.Party.IdentificationType` (orders). `XX` fails at the same
  keys with a message that names `NIF`. RED today on `NIF` (unknown) and on `ES` + `CC` (accepted).
- **`QuotationPartyDetails` / `OrderParty` (domain).** A billing party with `ES` + `Cc` throws
  `quotations.party.document_type_not_allowed` with `FieldErrors["Parties.Billing.IdentificationType"]`. A shipping party
  given `ES` + `Cc` does not throw (the document is stripped). A billing party with `ES` + `Nif` keeps it.
- **`OrderStoreFactoryTests`.** `Order.CreateFromStore` with a billing party `US` + `TaxId` keeps it.
- **`SubmitOrderRequestValidatorTests`** (`Modules.Store.UnitTests`). Valid: `ES` + `NIF`, `ES` + `NIE`, `US` + `TAX_ID`,
  `CO` + `CC`. Invalid, with the error on `Billing.IdentificationType` only: `ES` + `CC`, `CO` + `NIF`, `US` + `NIT`,
  `CO` + `TAX_ID`. `COL` + `NIF` reports on `Billing.Country` and not on the type. `XX` keeps «El tipo de documento no
  es válido.».
- **`StoreOrderRequestTests`** (domain). Constructing `StoreRequestBilling` with `ES` + `CC` throws
  `store.order_request.document_type_not_allowed`, and `ES` + `NIF` succeeds.
- **`BuyerProfileValidatorTests`** (`Modules.Buyers.UnitTests`). The same matrix as the Store validator.
- **`BuyerProfile`** (domain, new tests). `Create` and `Replace` with `ES` + `CC` throw
  `store.buyer.document_type_not_allowed`; with `ES` + `NIF` they succeed.
- **`BuyerSignupTests`.** A signup request whose profile is `ES` + `CC` creates the account with no profile (§6.3).
- **`BuyerParityTests.BillingCases`** (`Bootstrapper.UnitTests`). Add `valid ES with NIF`, `valid US with TAX_ID`,
  `ES with CC`, `CO with NIF` and `lowercase nif with es`. RED on the Buyers side if only Store is changed, and vice versa.

### 10.2 Integration (Testcontainers; one project at a time, `-- xUnit.MaxParallelThreads=4`)

- **`StoreTakeApiTests`** (`Modules.Store.IntegrationTests`, rig `StoreInboxRig`). This file proves goal 3. Use a store
  selling to `CO`, `ES` and `US`, with one case each for `ES` + `NIF`, `ES` + `NIE` and `US` + `TAX_ID`:
  - Taking with `newCustomer` returns 200. `customers.customers` holds the customer with `identification_type` `Nif`,
    `Nie` or `TaxId` and the billing country. The order's billing party holds the same member name and country.
    `GET` of the order returns the wire code (`NIF`, …). The request is *Converted*.
  - Taking with the id of an incomplete customer seeded with country `CO` and no document (`SeedIncompleteAsync`)
    completes it with the request's code **and country** (`ES`), and returns 200.
  - Taking with the id of a complete `CO` + `CC` customer, against an `ES` + `NIF` request, returns 200; the customer
    row is unchanged and the order's billing party is `ES` + `Nif`.
  - Taking with the id of a complete customer that already has `NIF` and the same number returns 200, and creates no
    second customer.
  - The candidates endpoint for a request with `NIF` lists an existing customer with that document, with `matchedBy`
    containing `Document`.
  - RED today on the first case at the customer validator (`422 validation.failed`), and on the incomplete case at the
    country.
- **`StoreOrderGatewayTests`** (`Modules.Quotations.IntegrationTests`). A store draft whose billing party is `US` +
  `TAX_ID` creates the order, and the party round-trips. A draft with `ES` + `CC` is rejected with
  `quotations.party.document_type_not_allowed` and creates no order.
- **Customers API** (`Modules.Customers.IntegrationTests`). `POST` a customer with `ES` + `NIF`, then `GET` it: the
  answer is `NIF`. `PUT` keeps it. `POST` with `ES` + `CC` → `422 validation.failed` with `errors["IdentificationType"]`.
  A customer inserted directly as `ES` + `Cc` (a non-conforming row) still reads with 200, a `PUT` that keeps `CC` gets
  the same 422, and a `PUT` with `NIF` succeeds.
- **Quotations / Orders API.** `PUT` of a quotation with its own billing party `ES` + `CC` → 422 at
  `Parties.Billing.IdentificationType`; with `ES` + `NIF` → 200 and the party round-trips. The order edit with `ES` +
  `CC` → 422 at `Billing.Party.IdentificationType`.
- **`PublicOrderRequestApiTests`** (`Modules.Store.IntegrationTests`). Use a store selling to `CO` and `ES`. `ES` +
  `NIF` → 201, and the row stores `NIF`. `ES` + `CC` → 422 `validation.failed` with
  `errors["Billing.IdentificationType"]`, no row, and no number consumed.
- **`PublicOrderBuyerAccountApiTests`.** A checkout with `createAccount` and `ES` + `NIE` → the profile in
  `buyers.profiles` holds `NIE`. With `updateProfile`, the saved profile is replaced with `NIE`.
- **`BuyerAccountApiTests`** (`Modules.Buyers.IntegrationTests`). `PUT /profile` with `ES` + `NIF` → 200, and it is
  stored. `ES` + `CC` → 422 on `Billing.IdentificationType`. A profile inserted directly as `ES` + `CC` (legacy row)
  still reads with 200.
- **`StorePersistenceTests`.** An order request with `TAX_ID` round-trips through `billing_identification_type`.
- **No pending model changes.** `dotnet ef migrations has-pending-model-changes` reports none for `CustomersDbContext`
  and `QuotationsDbContext`. Record the literal output as evidence.

### 10.3 Architecture

No new module reference. `StoreLayerTests`, `BuyersLayerTests`, `CustomersLayerTests` and `QuotationsLayerTests` stay
green unchanged, which is the evidence that the catalogue placement is legal.

## 11. Risks

- **The owner's "all customers are Colombian" is not verified by the code** (§1, §8). Shopify can create foreign complete
  customers with Colombian types today (§6.4.5). If any exist, they and their parties must be corrected on their next
  edit, and a quotation with such a party cannot be converted until then. The optional check in §8 removes the doubt.
- **"Tomar" rewrites an incomplete customer's country** when it takes the request's document (§6.4.3). The inferred
  country (WhatsApp prefix, Shopify shipping) is replaced by the one the buyer declared. Intended by the owner.
- **Phone text of a Shopify incomplete customer** becomes its E.164 form when "Tomar" changes its country (§6.4.3 design
  call). Without it, its phone match would break.
- **Behaviour change for foreign Shopify buyers:** they become incomplete customers instead of complete ones (§6.4.5).
- **Member names become data.** `Nif`, `Nie` and `TaxId` are written to the customer and party tables and to the
  Customers report. Renaming them later needs a data migration.
- **Order export consumers.** The Excel order export carries the new wire codes (§6.5). An external accounting import
  that only knows the four would need its own mapping. This is outside the code, and noted for the owner.
- **Back-office gap until the frontend ships.** The old customer form offers Colombian codes for every country and now
  gets a field error for a foreign customer; it cannot show or save the new codes (frontend spec §1). The rollout order
  in §9 keeps the gap short.
- **A foreign customer's own billing party in the back office** is prefilled as Colombian (§3), so the person must pick
  a Colombian type or bill to the customer's data. Not new, but now enforced.
- **Signup events across the deploy** (§6.3), if the design call is not applied.
- **Behaviour change for foreign buyers:** `CC`, `CE` and `NIT` with a non-`CO` billing country stop being accepted in
  the store and the profile. Intended.
- **Four copies of the table across repos** (Companies/shared in the backend, back office, storefront). There is no
  automated tie across repos. The storefront and frontend specs pin their copies with literal tests, and the backend
  tables are tied by §10.1.

## 12. Left to the builder

1. The final name and folder of the shared catalogue (§6.1), and the typed helpers in Customers and Quotations.
2. Whether `CompanyDocumentTypes.AllowedFor`, `IdentificationTypeParser` and `PartyFiscalTypesParser` derive from the
   catalogue (§6.1, §6.4.1, §6.5). The parity tests are required either way.
3. How the signup path drops an invalid profile (§6.3).
4. Where the catalogue unit tests live (§10.1).
5. The order of the per-country check and the NIT check inside `Customer` (§6.4.2), as long as the test in §10.1 holds.
6. Updating `CLAUDE.md` § Gotchas. The `BuyerParityTests` entry must say that the document-type rule now comes from the
   shared catalogue, and the Pos parity test must be noted as compared with `AllowedFor("CO")`. Add that customers and
   billing parties check the type against their own country, and that `MergeForStore` takes the request's country with
   its document. Also record the change in `docs/estado.md` on `develop` at merge time.

## 13. Decisions recorded here

Owner:

1. The scope is the store order request and the buyer profile.
2. (2026-11-08) Option B: do not ship until "Tomar" works for every code, so Customers and Quotations are in scope. This
   replaces the earlier "Customers, Quotations and conversion are out".
3. (2026-11-08) **Strict everywhere:** customers and quotation/order billing parties enforce type-allowed-for-country
   with the shared table. When "Tomar" fills a customer's document from the request, the customer also takes the
   request's country. Owner-stated: all existing customers are Colombian with Colombian types, so there is no cleanup or
   exemption. This replaces design call 8 of the first version ("no per-country rule in Customers and parties").

*Design calls (not owner-confirmed), made by the orchestrator:*

1. Reuse the exact company catalogue: seven codes, same per-country table (§6.1).
2. The billing country governs the type, not the shipping country (§4).
3. Store and Buyers validate type-against-country in FluentValidation and in the domain, mirroring Companies (§6.2,
   §6.3).
4. New codes `store.order_request.document_type_not_allowed` and `store.buyer.document_type_not_allowed` (§7).
5. The shared catalogue lives in `BuildingBlocks.Domain`, not in a copy per module (§6.1).
6. Legacy profiles are not rewritten; the next save fixes them (§8).
7. A signup whose profile fails the new rule creates the account without a profile (§6.3).
8. New codes `customers.customer.document_type_not_allowed` and `quotations.party.document_type_not_allowed`, with field
   errors, in the validator and the domain (§6.4.2, §6.5).
9. The Colombian-only entry points (customer Excel import, service-contract import, Pos quick-create) keep
   `AllowedFor("CO")` (§6.4.4).
10. The new enum members are `Nif`, `Nie` and `TaxId` in Customers and Quotations (§6.4.1).
11. When "Tomar" replaces an incomplete customer's country, it also takes the request's address, and keeps the phone as
    its E.164 form computed with the old country (§6.4.3).
12. The customer merge fills the document and the country of an incomplete survivor as a pair (§6.4.6).
13. The per-country subset stays out of the database (§6.7).

## 14. Resolved owner decisions (2026-11-08)

1. **Number format per type** (for example, the NIF/NIE check letter) stays out of scope for buyers, customers and
   parties, as in Companies (D7 of the company-country spec). Number: required, max 32. *Owner-confirmed.*
2. **"Tomar" never changes a complete customer** (§6.4.3): a complete `CO` + `CC` customer taking an `ES` + `NIF`
   request stays `CO` + `CC`, and only the order's billing party is `ES` + `NIF`. Only an incomplete customer without a
   document takes the request's country together with its document. *Owner-confirmed.*

No owner decisions remain pending.
