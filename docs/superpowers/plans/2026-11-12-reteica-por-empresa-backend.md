# ReteICA per billing company and operation municipality — backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **The workspace test budget below overrides the skill's one-agent-and-one-review-per-task process.**

**Goal:** Move ReteICA from the customer to the billing company and the operation municipality: tax types declare a territorial scope, municipal rates carry a city, companies assign their municipal rates, customers list the municipalities where they withhold, and quotations and orders resolve, freeze, explain and let the advisor edit the municipal withholding.

**Architecture:** `TaxCountries` (BuildingBlocks) gains `TaxScope`; every rule asks "is the type `Municipal`?", never "is it ReteICA?". Catalog keeps the rate (`tax_rates.city_id`), Companies stores assignments (`company_withholding_tax_rates`), Customers stores agent municipalities (`customer_withholding_agent_cities`). Quotations never reads those modules directly: Application ports with Bootstrapper adapters build a `MunicipalContext` (operation city + what the company offers + whether the customer is an agent) that the aggregates consume; the domain function `MunicipalWithholdings` decides the frozen row and, on read, the explanatory `status`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, ClosedXML, Typst, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-12-reteica-por-empresa-design.md`](../specs/2026-11-12-reteica-por-empresa-design.md) (backend, owns the HTTP contract) · sibling: `qep-frontend/docs/superpowers/specs/2026-11-12-reteica-por-empresa-design.md`. Baseline: `2026-11-10-impuestos-por-tipo-design.md` and its plan `2026-11-10-impuestos-por-tipo.md`.

---

## Before you start

- Worktree `qep-backend-worktrees/reteica-por-empresa`, branch `feature/reteica-por-empresa` (from `develop` `bfe9d8d0`). Before **every** commit run `git branch --show-current`; it must print `feature/reteica-por-empresa`, otherwise stop and ask. Never commit on `main`, never merge to `main`, do not touch `docs/estado.md` on this branch. **Do not push `develop` after merging (owner R12)** until this work is merged.
- PowerShell only, one command per line, no `&&` (`A; if ($?) { B }`).
- If `Api.exe` is running, `dotnet build`, `dotnet test` and `dotnet ef` fail on a locked file. Do not stop it (it belongs to the developer): report it and wait.
- Integration tests: Docker up and `$env:Seed__Enabled = "false"` on its own line. Before any integration run: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` — if one is running, wait (another session's run). Never two integration runs at once. Run in the foreground.
- Migrations use the design-time factory:
  `dotnet ef migrations add <Name> --project src/Modules/<Module>/Modules.<Module>.Infrastructure --context <Module>DbContext -o Persistence/Migrations`
  then `dotnet ef migrations has-pending-model-changes --project src/Modules/<Module>/Modules.<Module>.Infrastructure --context <Module>DbContext` → `No changes have been made to the model since the last migration.`
- Commits: conventional commits in English, **no `Co-Authored-By`, no AI attribution**, always `git commit -m "<msg>" -- <paths>` (new files `git add <path>` first: another session may be staging in the same checkout).

### Test budget (workspace rule, owner 2026-11-12 — overrides the skill)

- Three blocks, **one implementer per block**, tasks in order, **one commit per task**. One review per block (fresh reviewer over the block's diff); one strong review over the whole branch at the end (Task 15).
- RED that fails to compile: the evidence is the `dotnet build`/`dotnet test` compile output; no containers.
- Unit tests run per task. Integration tests while iterating: filtered by test name, `--filter "FullyQualifiedName~<TestName>"`. The **full integration class** of each touched class runs once, at the end of its block (listed in each block's closing step).
- The full suite runs only in Task 15, before merging to `develop`, one test project at a time.

## Global Constraints

- Scope (R1): `enum TaxScope { National, Municipal }`; Colombia: `IVA` National, `Retefuente` National, `ReteICA` Municipal. Wire value is the enum name (`"National"`/`"Municipal"`). Logic asks the scope (`TaxCountries.IsMunicipal`), never compares with `"ReteICA"`.
- Percentage edit (R7): `0 < p ≤ 100`, at most 3 decimals, `numeric(6,3)`; only `Municipal` types; quotation editable (`EnsureEditable`, `quotation.quotation.not_editable`), order only if `Order.IsEditable` (`order.order.not_editable`).
- New error codes, exact (spec §10): `catalog.tax_rate.city_required`, `catalog.tax_rate.city_not_allowed`, `catalog.tax_rate.city_invalid`, `companies.company.withholding_tax_rate_invalid`, `companies.company.withholding_municipality_repeated`, `customers.customer.withholding_tax_rate_municipal`, `customers.customer.withholding_agent_type_invalid`, `customers.customer.withholding_agent_city_invalid`, `quotation.billing.operation_city_invalid`, `order.billing.operation_city_invalid`, `quotation.billing.withholding_percentage_invalid`, `order.billing.withholding_percentage_invalid`, `quotation.billing.withholding_percentage_not_editable`, `order.billing.withholding_percentage_not_editable`. Reused: `catalog.tax_rate.in_use` (message becomes "…at least one product, customer or company uses it."), `quotation.billing.withholding_tax_rate_invalid`, `order.billing.withholding_tax_rate_invalid`, `quotation.quotation.not_editable`, `order.order.not_editable`. All are `DomainException` → 422 through `src/Api/ApiExceptionHandler.cs`.
- No new permission: `CompositionRootTests` stays at **80**. Company writes use `companies.company.manage`; quotation save `quotations.quotation.manage`; order edit `quotations.order.manage`.
- New audit actions: `quotation.quotation.withholding_rate_edited`, `quotation.order.withholding_rate_edited`. New history event `WithholdingRateEdited`, appended **last** to `QuotationHistoryEventType` and `OrderHistoryEventType` (stored as text, `event_type varchar(50)`, no migration).
- History copy, exact (spec §7.8): «Cambió la tarifa de {name} de {from} a {to}.»; «Restableció la tarifa de {name} a {catalog}.»; «Se volvió a calcular la {type} por cambio de municipio o de empresa; se descartó la tarifa editada ({p}).»; «Se quitó la {type} aplicada a mano al cambiar la empresa o el municipio: el cliente no es agente retenedor ahí.»; order selector suffix «; el cliente no está registrado como agente retenedor de {type} en {city}»; quotation own-billing summary «Aplicó {type} {city} por facturación propia»; operation city «Cambió el municipio de la operación de {from} a {to}.». Percentages render with the tenant decimal separator, no trailing zeros, up to 3 decimals, plus « %» (`0,966 %`, `0,8 %`).
- Module isolation: Catalog, Companies, Customers, Geography and Quotations never reference each other; ports in Application, adapters in `src/Bootstrapper/`. Application never references EF Core/Npgsql. `ArchitectureTests` unchanged.
- Frozen documents (baseline D17): reading a document never moves rows or totals; `status` and `companyRate` are computed on read only to explain. Re-resolution runs only on the three triggers of spec §7.6.
- Store checkout and the Shopify webhook never fail because of municipal resolution (baseline D22).
- Old-frontend compatibility (spec §12): every new request field is optional with "absent = keep"; every response field is additive.
- Code comments in Spanish, tuteando (repo rule); exception messages in English like their neighbours; UI/history copy exactly as quoted above.

## Plan decisions (what the spec leaves to the builder)

Each is reversible by the owner; the owning task repeats it in a code comment.

| # | Decision | Why |
| --- | --- | --- |
| P1 | `operationCityId` in request bodies is a non-positional `JsonElement OperationCityId { get; init; }` property, parsed by `OperationCityChoice.From(JsonElement)` into `Keep` (absent), `Company` (`null`) or `Explicit(Guid)`. A non-GUID string is `{prefix}.billing.operation_city_invalid`. | The contract distinguishes absent from `null` (spec §9.4). Property, not constructor parameter: the OpenAPI exporter 500s on a `JsonElement` default (precedent `UpdateTaxRateRequest.MinimumBase`). |
| P2 | Order: `operationCity`, `billingCompanyCity` and `municipalWithholdings` go at the **root of `OrderDetailResponse`** (next to `order`, `billingAccount`), not inside `OrderResponse`. Quotation: root of `QuotationResponse`. Row fields (`catalogPercentage`, `percentageOverridden`, `cityId`) go in `WithholdingResponse` and `WithholdingTaxRateResponse` everywhere. | The explanation needs lookups, which only the composers have; `OrderResponse` is also returned by `/approve`, `/cancel`… without a composer. Tell the frontend. |
| P3 | `WithholdingRate` gains positional `Guid? CityId = null` and `decimal CatalogPercentage { get; init; } = Percentage;`; an edit is `rate with { Percentage = p }`; `Overridden => Percentage != CatalogPercentage`. | Keeps every existing construction compiling and gives the spec's "catalog = effective" default. |
| P4 | Domain inputs: `MunicipalOffer(string Type, WithholdingRate? CompanyRate, bool CustomerIsAgent)` and `MunicipalContext(Guid? OperationCityId, IReadOnlyList<MunicipalOffer> Offers)`. Application computes the target city with `Quotation.OperationCityFor` / `Order.OperationCityFor` **before** querying, then hands the context to the aggregate, which detects the triggers (company id or city changed) itself. | The aggregate cannot query; the trigger rule stays in the domain. |
| P5 | Quotation own billing (`Billing` source) on re-resolution follows spec §7.6: a selected municipal rate moves to the company's rate for the new municipality, or empties. Only the order (`Document` source) re-resolves with the customer rule and logs «Se quitó la … aplicada a mano…». | §7.6 and the §13 domain test are explicit; confirmed by the owner on 2026-11-13 (O1). |
| P6 | A11 (owner 2026-11-13, option c) lives in Application: when the context changed in the same body, `WithholdingSelection.ResolveAsync` runs with `MunicipalSelectorScope.IgnoreUnmatched = true`: a municipal id in `AllowedMunicipalRateIds` (the new context's offers = the new company's rate for the resulting city) is accepted and replaces the re-resolved row of its type; any other municipal id is skipped silently, and for each type without an accepted id the already re-resolved municipal row is appended to the selection. Municipal `withholdingPercentages` entries are applied when the current source has a row of that type after selectors, and skipped silently otherwise (no `withholding_percentage_not_editable`). Without a context change the scope rejects (`IgnoreUnmatched = false`) and percentages validate as usual. | Deciding "is this id municipal" needs the catalog. |
| P7 | The explanation (`municipalWithholdings`, `operationCity`, `billingCompanyCity`) is built by one Application class, `MunicipalWithholdingExplanation`, shared by both composers and previews. `companyRate` comes from `ForCompanyMunicipalAsync` with the document's UTC creation date (only name and percentage are read). | One rule for four endpoints (spec anti-goal: never in the browser). |
| P8 | Every Catalog handler that returns a `TaxRateDto` (list, get, create, update, activate, deactivate) resolves `city` through `ITaxRateGeographyLookup` (batch in list). | The spec's POST test expects `city` in the create response. |
| P9 | `CustomerImportRowData.ReteIca` (failed-rows export payload) stays in the record, always `null` in responses and ignored in requests; `ExcelCustomerRow.ReteIca` and `CustomerImportColumns.ReteIca` are removed. | The frontend echoes row data back; removing a positional field from the HTTP record is a needless break. Tell the frontend. |
| P10 | `HistoryFormat.Percentage(decimal)` → `value.ToString("0.###", Numbers) + " %"`; the PDF uses the same pattern with `RegionalNumberFormat.Separators(numberFormat)`. | `HistoryFormat.Number` rounds to 2 decimals; 0,966 needs 3. |
| P11 | City references in all responses share the shape `{ id, name, departmentId, departmentName }`: `TaxRateCityDto` (Catalog), `CompanyMunicipalityDto` (Companies), `CustomerMunicipalityDto` (Customers), `QuotationCityResponse` (Quotations). Existing `CompanyCityDto`/`CustomerCityRef` are not reused because their shapes differ (DIVIPOLA codes). | Spec §9.1: one "municipality reference" shape. |

## Owner decisions found while planning

- **O1 — resolved by the owner on 2026-11-13: §7.6 wins (P5 stands); R11 and §13 corrected in the spec.** Original note: R11 said a later company/municipality change "re-resolves with the customer rule and removes" the hand-applied rate "in an editable order **and in a quotation with own billing**", and §13 says "lo mismo en la cotización con facturación propia"; but §7.6 and the §13 domain test say the own-billing selector **moves** to the company's rate for the new municipality. This plan implements §7.6 (P5). If the owner wants R11's removal for quotations too, only Task 9's re-resolution branch for `Billing` and its test change.
- **O2** (test budget): this branch touches BuildingBlocks (`TaxCountries`) and migrations of four modules, which the workspace rule classifies as transversal → every test project of the solution, one at a time, before merging (Task 15). The owner may narrow it to Catalog, Companies, Customers, Quotations, Store, Shopify, Reporting, Pos, `Bootstrapper.UnitTests` and `ArchitectureTests`.
- Spec §14.1 (legal literal "sin establecimiento") does not affect the backend.

## Review Focus

1. **Resending the same municipal selector after a percentage edit** (the frontend re-sends what it shows on every save): `WithholdingSelection.IsUnchanged` must keep the frozen photo, so the edited 0,8 % survives. Pinned in Task 12 (`ResendingTheSameMunicipalSelectorKeepsTheEditedPercentage`) and Task 13 (order twin).
2. **Legacy document saved with the same company** (`operation_city_id` and `billing_company_city_id` null): `billingCompanyCity` gets filled, no re-resolution, no history entry; `operationCity` stays `null` and `status = NoOperationCity`. Pinned in Task 12 (`ResendingTheSameCompanyFillsTheCompanyCityWithoutReResolving`).
3. **Store checkout and Shopify webhook with a CO company and a customer whose agent list or the company's assignments don't match** (company rate in another city, inactive assigned rate): the order is created, no exception. Pinned in Task 13 (`StoreOrderNeverFailsOnMunicipalResolution`, `ExternalOrderNeverFailsOnMunicipalResolution`).
4. **Editable order switching to a company of a country without municipal types** while it had an edited ReteICA: the municipal row is removed, `operationCity` becomes `null`, history records the discarded 0,8 %. Pinned in Task 10 (`CompanyOfACountryWithoutMunicipalTypesDropsMunicipalRows`).
5. **`catalog_percentage` backfill**: rows frozen by the baseline get `catalog_percentage = percentage`, so `percentageOverridden` is `false` and totals don't move. Pinned in Task 8 (`CatalogPercentageIsBackfilledFromPercentage`).
6. **A11 in a body that changes company or municipality** (owner 2026-11-13): the new company's ReteICA id for the resulting city is accepted and a ReteICA percentage is applied; the old company's id is ignored without 422 and the row stays the re-resolved one; a percentage with no row of its type left is ignored without 422; without a context change a non-offered id is still 422. History order: discarded override first, then the new edit. Known residual risk (spec §7.6): a non-SPA client sending a stale percentage with a company change gets it applied. Pinned in Task 11 (`WithholdingSelectionTests`), Task 12 and Task 13 (`…ChangesAcceptsTheNewCompanysRate`, `…IgnoresTheOldCompanysRate`).

---

## File map

| File | Responsibility | Task |
| --- | --- | --- |
| `src/BuildingBlocks/BuildingBlocks.Domain/Taxes/TaxCountries.cs` | `TaxScope`, `MunicipalTypes`, `IsMunicipal` | 1 |
| `src/Modules/Catalog/Modules.Catalog.Domain/TaxRate.cs`, `Infrastructure/Persistence/CatalogDbContext.cs` (`ConfigureTaxRate`), `Infrastructure/Seed/CatalogSeeder.cs`, migration `AddTaxRateCity` | Rate with city | 2 |
| `src/Modules/Catalog/Modules.Catalog.Application/{CreateTaxRate,ListTaxRates,GetTaxRate,UpdateTaxRate,ActivateTaxRate,DeactivateTaxRate,TaxRateMapping,CatalogDtos,GetTaxRateOptions}.cs`, `ITaxRateGeographyLookup.cs` (NEW), `Modules.Catalog.Api/TaxRateEndpoints.cs`, `src/Bootstrapper/TaxRateGeographyLookup.cs` (NEW) | Rate contract | 3 |
| `src/Modules/Companies/Modules.Companies.Domain/{Company,CompanyTaxRate}.cs` (`CompanyTaxRate` NEW), `Infrastructure/Persistence/CompaniesDbContext.cs`, migration `AddCompanyWithholdingTaxRates` | Company assignments | 4 |
| `src/Modules/Companies/Modules.Companies.Application/{CreateCompany,UpdateCompany,GetCompany,ActivateCompany,DeactivateCompany,CompanyMapping,CompaniesDtos,ICompanyRepository}.cs`, `ICompanyTaxRateLookup.cs`, `CompanyWithholdingRules.cs` (NEW), `Modules.Companies.Api/CompanyEndpoints.cs`, `Infrastructure/Persistence/CompanyRepository.cs`; Catalog `ITaxRateCompanyUsage.cs` (NEW), `{DeactivateTaxRate,DeleteTaxRate}.cs`; `src/Bootstrapper/{CompanyTaxRateLookup,TaxRateCompanyUsage}.cs` (NEW), `QepServiceCollectionExtensions.cs` | Company contract, "in use" | 5 |
| `src/Modules/Customers/Modules.Customers.Domain/{Customer,CustomerWithholdingAgentCity}.cs` (NEW record), `Infrastructure/Persistence/CustomersDbContext.cs`, migration `AddCustomerWithholdingAgentCities`, `Application/{CustomerWriteRules,CreateCustomer,UpdateCustomer,CustomerMapping,CustomersDtos}.cs`, `Modules.Customers.Api/CustomerEndpoints.cs` | Municipal rates rejected; agent municipalities | 6 |
| `src/Modules/Customers/Modules.Customers.Application/{IExcelCustomerImporter,ImportCustomers,CustomersDtos}.cs`, `Infrastructure/Excel/{ClosedXmlCustomerImporter,ClosedXmlCustomerImportTemplateBuilder,ClosedXmlCustomerExportBuilder}.cs` | Excel without «ReteICA» | 7 |
| `src/Modules/Quotations/Modules.Quotations.Domain/{WithholdingRate,DocumentWithholding,WithholdingCandidates,WithholdingSelectorRules}.cs`, `MunicipalWithholdings.cs` (NEW), `QuotationsDbContext.cs` (`ConfigureWithholding`), migration `AddWithholdingCityAndCatalogPercentage` | Photo, resolution function | 8 |
| `src/Modules/Quotations/Modules.Quotations.Domain/{Quotation,QuotationBillingAccount,QuotationHistoryEventType}.cs`, `OperationCityChoice.cs` (NEW), `QuotationsDbContext.cs`, migration `AddOperationCity`; `Application/{IQuotationCompanyLookup,QuotationBillingAccountVerifier,QuotationBillingAccountResolver}.cs`, `src/Bootstrapper/QuotationCompanyLookup.cs` | Quotation city, re-resolution, percentage | 9 |
| `src/Modules/Quotations/Modules.Quotations.Domain/{Order,OrderHistoryEventType}.cs` | Order twin | 10 |
| `src/Modules/Quotations/Modules.Quotations.Application/{IQuotationWithholdingLookup,IQuotationGeographyLookup,OrderCustomerWithholdings,WithholdingSelection}.cs`, `MunicipalContexts.cs` (NEW), `src/Bootstrapper/{QuotationWithholdingLookup,QuotationGeographyLookup}.cs` | Ports and context building | 11 |
| `src/Modules/Quotations/Modules.Quotations.Application/{QuotationEdits,QuotationEditsApplication,QuotationDraftBuilder,SaveQuotation,PreviewQuotation,PreviewNewQuotation,ChangeQuotationClient,QuotationMapping,QuotationResponseComposer,QuotationsDtos,QuotationChangeSummary,HistoryFormat}.cs`, `MunicipalWithholdingExplanation.cs` (NEW) | Quotation contract | 12 |
| `src/Modules/Quotations/Modules.Quotations.Application/{OrderEdits,SaveOrderEdits,PreviewOrderEdits,OrderDraftBuilder,IStoreOrderGateway,IExternalOrderWriter,OrderMapping,OrderResponseComposer,OrdersDtos,OrderChangeSummary}.cs` | Order contract | 13 |
| `src/Modules/Quotations/Modules.Quotations.Application/{QuotationPdfDocument,QuotationPdfDocumentMapper}.cs`, `Infrastructure/Pdf/quotation.typ` | PDF percentage | 14 |

---

# Block A — tax types, Catalog, Companies, Customers (Tasks 1–7)

One implementer. Close the block with Step A-close after Task 7.

### Task 1: `TaxCountries` — territorial scope

**Files:**
- Modify: `src/BuildingBlocks/BuildingBlocks.Domain/Taxes/TaxCountries.cs` (record `:11`, Colombia `:36-41`)
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/TaxCountriesTests.cs`

**Interfaces:**
- Produces: `enum TaxScope { National, Municipal }`; `TaxTypeDefinition(string Type, TaxBehavior Behavior, bool AllowsFraction, bool AllowsMinimumBase, TaxScope Scope)`; `static IReadOnlyList<TaxTypeDefinition> TaxCountries.MunicipalTypes(string? country)` (empty for unknown/null country); `static bool TaxCountries.IsMunicipal(string? country, string? type)` (`FindType(...)?.Scope == Municipal`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void ColombiaScopes() {
    Assert.Equal(TaxScope.National, TaxCountries.FindType("CO", "IVA")!.Scope);
    Assert.Equal(TaxScope.National, TaxCountries.FindType("CO", "Retefuente")!.Scope);
    Assert.Equal(TaxScope.Municipal, TaxCountries.FindType("CO", "ReteICA")!.Scope);
}
[Fact] public void MunicipalTypesOfColombiaIsReteIca() =>
    Assert.Equal(["ReteICA"], TaxCountries.MunicipalTypes(" co ").Select(t => t.Type));
[Fact] public void MunicipalTypesOfUnknownCountryIsEmpty() {
    Assert.Empty(TaxCountries.MunicipalTypes("ES")); Assert.Empty(TaxCountries.MunicipalTypes(null)); }
[Fact] public void IsMunicipal() {
    Assert.True(TaxCountries.IsMunicipal("CO", "ReteICA")); Assert.False(TaxCountries.IsMunicipal("CO", "Retefuente"));
    Assert.False(TaxCountries.IsMunicipal("ES", "ReteICA")); }
```

- [ ] **Step 2: RED** — `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~TaxCountriesTests"` → compile error (`TaxScope` does not exist). Paste the build output.
- [ ] **Step 3: Implement** spec §7.1 verbatim, XML doc on `TaxScope` («Regional entra con el spec de España»). Extend `GetTaxRateOptions`'s `TaxRateTypeOptionDto` only in Task 3.
- [ ] **Step 4: GREEN** — same command → PASS.
- [ ] **Step 5: Commit** — `feat(taxes): give tax types a territorial scope`.

### Task 2: `TaxRate` with a municipality

**Files:**
- Modify: `src/Modules/Catalog/Modules.Catalog.Domain/TaxRate.cs` (`Create` `:121-147`; new property next to `MinimumBase` `:82`; `Update` `:151` unchanged)
- Modify: `src/Modules/Catalog/Modules.Catalog.Infrastructure/Persistence/CatalogDbContext.cs` (`ConfigureTaxRate`), `Infrastructure/Seed/CatalogSeeder.cs` (pass `cityId: null`)
- Create (generated): `…/Persistence/Migrations/<ts>_AddTaxRateCity.cs` + Designer; Modify snapshot
- Modify (compile): `CreateTaxRate.cs` (interim `cityId: null`, wired in Task 3)
- Test: `tests/Modules/Catalog/Modules.Catalog.UnitTests/TaxRateTests.cs`

**Interfaces:**
- Consumes: `TaxCountries.IsMunicipal` (Task 1).
- Produces: `TaxRate.Create(TaxRateId id, Guid tenantId, string name, decimal percentage, string type, string country, decimal? minimumBase, Guid? cityId, DateTimeOffset occurredAt)`; `Guid? TaxRate.CityId`; computed (EF-ignored) `TaxScope TaxRate.Scope`.

- [ ] **Step 1: Write the failing tests** (update the file's helpers to the new signature with `cityId: null` for national rates):
  - `MunicipalRateWithoutCityIsRejected` — `Create(…, "ReteICA", "CO", null, cityId: null, …)` → `CatalogDomainException` with `Code == "catalog.tax_rate.city_required"`.
  - `NationalRateWithCityIsRejected` — `"Retefuente"` with `cityId: Guid.NewGuid()` → `"catalog.tax_rate.city_not_allowed"`; same for `"IVA"`.
  - `MunicipalRateWithCityIsAccepted` — `0.966m`, `"ReteICA"`, city `c` → `CityId == c`, `Scope == TaxScope.Municipal`.
  - `EmptyCityIsRequired` — `cityId: Guid.Empty` on ReteICA → `city_required`.
  - `UpdateKeepsTheCity` — after `Update("ReteICA Bogotá 2", 0.8m, null, now)`, `CityId == c`.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Catalog/Modules.Catalog.UnitTests --filter "FullyQualifiedName~TaxRateTests"` → compile error.
- [ ] **Step 3: Implement.** City check after the type check (the type decides the scope), same "not empty" criterion as `Company.EnsureValidLocation` (`Company.cs:361-365`). Map `CityId` → `city_id uuid null`, **no FK** (A9: Catalog migrates before Geography), index `IX_tax_rates_tenant_type_city` on `(TenantId, Type, CityId)`, not unique (several CIIU tariffs per municipality). `Ignore(Scope)`. Generate `AddTaxRateCity`; check `has-pending-model-changes`.
- [ ] **Step 4: GREEN** — Catalog unit tests → PASS; `dotnet build` clean.
- [ ] **Step 5: Commit** — `feat(catalog): give municipal tax rates a city`.

### Task 3: Tax rate contract — `scope`, `cityId`, `city`

**Files:**
- Create: `src/Modules/Catalog/Modules.Catalog.Application/ITaxRateGeographyLookup.cs`, `src/Bootstrapper/TaxRateGeographyLookup.cs` (copy the shape of `CompanyGeographyLookup.cs`), register next to `QepServiceCollectionExtensions.cs:1567`
- Modify: `CreateTaxRate.cs` (command gains `Guid? CityId`; validate city after `TaxRate.Create`, before `Add`), `TaxRateMapping.cs`, `CatalogDtos.cs` (`TaxRateDto`/`TaxRateResponse` `:360-384`, `CreateTaxRateRequest` `:394`, `TaxRateTypeOptionDto` `:428`), `GetTaxRateOptions.cs:31-32`, `ListTaxRates.cs`, `GetTaxRate.cs`, `UpdateTaxRate.cs`, `ActivateTaxRate.cs`, `DeactivateTaxRate.cs` (P8), `Modules.Catalog.Api/TaxRateEndpoints.cs` (`ToResponse`, create mapping `:129`)
- Sweep: every test that creates a ReteICA by API or by domain (`rg -l "ReteICA" tests`) now passes a real city id (pattern `CompaniesApiHarness.EnsureCityIdAsync`); tests that assign a ReteICA **to a customer** are fixed in Task 6.
- Test: `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/TaxRateApiTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/GeographyLookupTests.cs`

**Interfaces:**
- Consumes: Task 2.
- Produces: `ITaxRateGeographyLookup.FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, CancellationToken) : Task<IReadOnlyDictionary<Guid, TaxRateCityRef>>`; `TaxRateCityRef(Guid Id, string Name, Guid DepartmentId, string DepartmentName)` (Geography is DIVIPOLA only, so "found" means "exists and is CO"); `TaxRateCityDto(Guid Id, string Name, Guid DepartmentId, string DepartmentName)`; `TaxRateDto`/`TaxRateResponse` append `string Scope, Guid? CityId, TaxRateCityDto? City`; `CreateTaxRateRequest` appends `Guid? CityId = null`; `TaxRateTypeOptionDto` appends `string Scope`; `TaxRateMapping.ToDto(this TaxRate, TaxRateCityRef? city)`.

- [ ] **Step 1: Write the failing integration tests** (`TaxRateApiTests`):
  - `CreatingAMunicipalRateReturnsItsCity` — POST `{ name: "ReteICA Bogotá", percentage: 0.966, type: "ReteICA", country: "CO", minimumBase: 4, cityId }` → 201; `scope == "Municipal"`, `cityId == city`, `city.id == city`, `city.name`/`city.departmentName` equal Geography's values for that id.
  - `MunicipalRateWithoutCityIs422` → `code == "catalog.tax_rate.city_required"`.
  - `NationalRateWithCityIs422` (`type: "Retefuente"`, `cityId`) → `"catalog.tax_rate.city_not_allowed"`.
  - `UnknownCityIs422` (`cityId = Guid.NewGuid()`) → `"catalog.tax_rate.city_invalid"`.
  - `UpdateIgnoresCityId` — PUT with another `cityId` → 200, `cityId` unchanged.
  - `OptionsCarryTheScope` — `GET …/tax-rate-options` → Colombia types `[("IVA","National"),("Retefuente","National"),("ReteICA","Municipal")]`.
  - `NationalRatesHaveNullCity` — an IVA → `scope == "National"`, `cityId == null`, `city == null`.
  - Bootstrapper unit: `TaxRateGeographyLookupReturnsNameAndDepartment` (in `GeographyLookupTests`, same doubles as the company lookup test).
- [ ] **Step 2: RED** — `dotnet build` → compile errors on the new members (no containers).
- [ ] **Step 3: Implement.** Handler order: authorize → validate → `TaxRate.Create` (domain codes first) → if `CityId` not null: lookup, missing → `CatalogDomainException("catalog.tax_rate.city_invalid", …)` → existing UVT check → save. Comment the DTO with the BFF reason: `city` lets the rate list paint "Bogotá, D.C." without asking Geography.
- [ ] **Step 4: GREEN** — `$env:Seed__Enabled = "false"`; `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~CreatingAMunicipalRateReturnsItsCity|FullyQualifiedName~MunicipalRateWithoutCityIs422|FullyQualifiedName~NationalRateWithCityIs422|FullyQualifiedName~UnknownCityIs422|FullyQualifiedName~UpdateIgnoresCityId|FullyQualifiedName~OptionsCarryTheScope|FullyQualifiedName~NationalRatesHaveNullCity" -- xUnit.MaxParallelThreads=4` → PASS; Catalog and Bootstrapper unit tests → PASS.
- [ ] **Step 5: Commit** — `feat(catalog): expose scope and city on tax rates`.

### Task 4: `Company` holds its municipal withholding rates

**Files:**
- Modify: `src/Modules/Companies/Modules.Companies.Domain/Company.cs` (after `BankAccounts` `:85`); Create: `CompanyTaxRate.cs` (`sealed record CompanyTaxRate(Guid TenantId, Guid TaxRateId)`, twin of `CustomerTaxRate`)
- Modify: `src/Modules/Companies/Modules.Companies.Infrastructure/Persistence/CompaniesDbContext.cs` (owned collection next to `ConfigureBankAccounts` `:114`, copy of `CustomersDbContext.cs:250-264`)
- Create (generated): migration `AddCompanyWithholdingTaxRates`
- Test: `tests/Modules/Companies/Modules.Companies.UnitTests/CompanyTests.cs`

**Interfaces:**
- Produces: `IReadOnlyCollection<Guid> Company.WithholdingTaxRateIds`; `bool Company.SetWithholdingTaxRates(IReadOnlyCollection<Guid> taxRateIds, DateTimeOffset occurredAt)` (same semantics as `Customer.SetWithholdingTaxRates`, `Customer.cs:854-877`: equal set → `false`, no version bump; otherwise replace, bump `Version`, set `UpdatedAt`). Table `companies.company_withholding_tax_rates (tenant_id uuid, company_id uuid, tax_rate_id uuid)`, PK `PK_company_withholding_tax_rates (company_id, tax_rate_id)`, index `IX_company_withholding_tax_rates_tenant_tax_rate (tenant_id, tax_rate_id)`, no FK to `catalog`, `tax_rate_id` `ValueGeneratedNever()`.

- [ ] **Step 1: Write the failing tests** — `SettingTheSameRatesDoesNotBumpTheVersion` (set `[a,b]`, then `[b,a]` → returns `false`, `Version` unchanged); `SettingRatesReplacesThem` (`[a,b]` then `[b,c]` → `WithholdingTaxRateIds` equals `{b,c}`, returns `true`, `Version` +1); `EmptyListClearsThem`.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --filter "FullyQualifiedName~CompanyTests"` → compile error.
- [ ] **Step 3: Implement** domain + mapping; generate the migration; `has-pending-model-changes` clean.
- [ ] **Step 4: GREEN** — Companies unit tests → PASS.
- [ ] **Step 5: Commit** — `feat(companies): store a company's municipal withholding rates`.

### Task 5: Company contract and "in use" by companies

**Files:**
- Create: `src/Modules/Companies/Modules.Companies.Application/ICompanyTaxRateLookup.cs`, `CompanyWithholdingRules.cs`; `src/Modules/Catalog/Modules.Catalog.Application/ITaxRateCompanyUsage.cs`; `src/Bootstrapper/CompanyTaxRateLookup.cs` (over `ITaxRateRepository`, copy of `CustomerTaxRateLookup.cs`), `src/Bootstrapper/TaxRateCompanyUsage.cs` (copy of `TaxRateCustomerUsage.cs`); register both next to `QepServiceCollectionExtensions.cs:1566-1567`
- Modify: `CompaniesDtos.cs` (`CompanyDto`, `CompanyResponse`, `CreateCompanyRequest`, `UpdateCompanyRequest` `:21-132`), `CreateCompany.cs`, `UpdateCompany.cs` (`:8-21`, handler `:38-98`), `CompanyMapping.cs` (`ToDto`, `ToDtoAsync`), `GetCompany.cs`, `ActivateCompany.cs`, `DeactivateCompany.cs`, `ICompanyRepository.cs`, `Infrastructure/Persistence/CompanyRepository.cs`, `Modules.Companies.Api/CompanyEndpoints.cs`; Catalog `DeactivateTaxRate.cs:44-60`, `DeleteTaxRate.cs:30-65`
- Test: `tests/Modules/Companies/Modules.Companies.IntegrationTests/CompanyWithholdingTaxRatesApiTests.cs` (NEW), `tests/Modules/Companies/Modules.Companies.UnitTests/CompanyWithholdingRulesTests.cs` (NEW), `tests/Modules/Catalog/Modules.Catalog.IntegrationTests/{TaxRateActivationApiTests,TaxRateDeletionApiTests}.cs`

**Interfaces:**
- Consumes: Tasks 1, 3, 4; `ICompanyGeographyLookup.FindCitiesAsync` (exists).
- Produces:
  - `CompanyTaxRateRef(Guid Id, string Name, string Type, string Country, decimal Percentage, bool IsActive, bool Withholds, Guid? CityId)`; `ICompanyTaxRateLookup.FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken) : Task<IReadOnlyDictionary<Guid, CompanyTaxRateRef>>`.
  - `CompanyWithholdingRules.Validate(IReadOnlyDictionary<Guid, CompanyTaxRateRef> found, IReadOnlyCollection<Guid> requested, IReadOnlyCollection<Guid> alreadyAssigned, string companyCountry)` — throws `companies.company.withholding_tax_rate_invalid` (missing, not withholding, not `IsMunicipal`, other country, or inactive **and** not in `alreadyAssigned`) and `companies.company.withholding_municipality_repeated` (two with the same `(Type, CityId)`).
  - `CreateCompanyCommand`/`UpdateCompanyCommand`/requests append `IReadOnlyList<Guid>? WithholdingTaxRateIds = null` (absent/`null` keep; in create, none; `[]` clears).
  - `CompanyMunicipalityDto(Guid Id, string Name, Guid DepartmentId, string DepartmentName)`; `CompanyWithholdingTaxRateDto(Guid Id, string Name, string Type, string Country, decimal Percentage, bool IsActive, CompanyMunicipalityDto? City)`; `CompanyDto`/`CompanyResponse` append `IReadOnlyList<CompanyWithholdingTaxRateDto> WithholdingTaxRates` ordered by department name, city name, type (Ordinal). `CompanyListItemResponse` unchanged.
  - `ICompanyRepository.AnyWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken) : Task<bool>`; `ITaxRateCompanyUsage.AnyCompanyWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken) : Task<bool>`.

- [ ] **Step 1: Write the failing unit tests** (`CompanyWithholdingRulesTests`): `[Theory]` missing / IVA / Retefuente (national) / `ES` rate / new inactive → `withholding_tax_rate_invalid`; `TwoInTheSameCityAndTypeAreRepeated` → `withholding_municipality_repeated`; `BogotaAndMedellinPass`; `AlreadyAssignedInactiveIsTolerated` (A16).
- [ ] **Step 2: Write the failing integration tests** (`CompanyWithholdingTaxRatesApiTests`; two distinct seeded cities taken from `/api/v1/cities`, rates created by API with those cities):
  - `AssigningBogotaAndMedellinPasses` — PUT `withholdingTaxRateIds: [bogota, medellin]` → 200; `withholdingTaxRates` has both, each with `city.name`, `city.departmentName`, `percentage`, `isActive: true`.
  - `TwoInBogotaAre422` → `companies.company.withholding_municipality_repeated`.
  - `RetefuenteIs422` → `companies.company.withholding_tax_rate_invalid`.
  - `PutWithoutTheFieldKeepsThem` — PUT the old body (no `withholdingTaxRateIds`) → still both.
  - `EmptyListClearsThem`.
  - `GetReturnsThem` — `GET /{companyId}` carries `withholdingTaxRates`; `GET /` items have no such field.
  - `ResendingAnAlreadyAssignedInactiveRatePasses` — assign, deactivate by SQL (the API refuses: in use), PUT with the same id → 200, kept; `ANewInactiveRateIs422`.
  - `ChangingCountryRevalidatesStoredRates` — company with ReteICA, PUT with `country: "ES"` (valid ES body) and no `withholdingTaxRateIds` → `withholding_tax_rate_invalid`.
  - Catalog: `ARateAssignedOnlyToACompanyCannotBeDeactivated` and `…CannotBeDeleted` → `catalog.tax_rate.in_use`.
- [ ] **Step 3: RED** — `dotnet build` → compile errors.
- [ ] **Step 4: Implement.** In create/update handlers, after validating shape and city: list = `command.WithholdingTaxRateIds ?? company.WithholdingTaxRateIds` (create: `?? []`), `found = FindManyAsync`, `CompanyWithholdingRules.Validate(found, list, company.WithholdingTaxRateIds, command.Country!)`, then `SetWithholdingTaxRates`. `ToDtoAsync(company, geographyLookup, taxRateLookup, ct)` resolves rates and their cities in one batch each. Catalog handlers OR the company usage into the existing condition and change the message.
- [ ] **Step 5: GREEN** — Companies unit tests; integration filtered to `CompanyWithholdingTaxRatesApiTests` test names one by one while iterating, then `--filter "FullyQualifiedName~ARateAssignedOnlyToACompany"` on Catalog.IntegrationTests → PASS.
- [ ] **Step 6: Commit** — `feat(companies): assign municipal withholding rates to billing companies`.

### Task 6: Customers — no municipal rates; agent municipalities

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Domain/CustomerWithholdingAgentCity.cs` (`sealed record CustomerWithholdingAgentCity(Guid TenantId, string Country, string Type, Guid CityId)`)
- Modify: `Customer.cs` (next to `:288`, `:854-877`), `CustomersDbContext.cs` (owned collection after `:264`), migration `AddCustomerWithholdingAgentCities`
- Modify: `CustomerWriteRules.cs` (`CustomerWithholdingRules.Validate` `:206-227`; NEW `CustomerWithholdingAgentRules`), `CreateCustomer.cs`, `UpdateCustomer.cs`, `CustomerMapping.cs`, `CustomersDtos.cs` (`CustomerDto` `:95-108`, requests `:277`, `:298`), `Modules.Customers.Api/CustomerEndpoints.cs:367`, `:399`
- Sweep: tests that assign a ReteICA to a customer (`CustomerMergeApiTests.cs:170`, `QuotationWithholdingsApiTests`, `OrderWithholdingsApiTests`, Catalog `TaxRateCustomerFixtures`, Bootstrapper `QuotationWithholdingLookupTests`, `QuotationCustomerWithRetentionTests`) switch to a second **national** rate or drop the ReteICA leg; Quotations ones are rewritten for real in Tasks 12–13, here only make them compile and not depend on a customer ReteICA.
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWithholdingRulesTests.cs`, `CustomerTests.cs`, `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWithholdingTaxRatesApiTests.cs`

**Interfaces:**
- Consumes: Task 1; `ICustomerGeographyLookup.FindCitiesAsync` (exists).
- Produces: `WithholdingAgentCityEntry(string Country, string Type, Guid CityId)` (Domain); `IReadOnlyCollection<CustomerWithholdingAgentCity> Customer.WithholdingAgentCities`; `bool Customer.SetWithholdingAgentCities(IReadOnlyCollection<WithholdingAgentCityEntry> entries, DateTimeOffset occurredAt)` (normalizes country to upper case, collapses duplicates, equal set → `false`); `bool Customer.IsWithholdingAgent(string country, string type, Guid cityId)`; table `customers.customer_withholding_agent_cities (tenant_id, customer_id, country char(2), type varchar(32), city_id uuid)`, PK `(customer_id, country, type, city_id)`, **FK `city_id` → `geography.cities`** (add by hand in the migration, like `Company.cs:101-111`); `WithholdingAgentCityRequest(string Type, string Country, Guid CityId)`; requests/commands append `IReadOnlyList<WithholdingAgentCityRequest>? WithholdingAgentCities = null`; `CustomerMunicipalityDto(Guid Id, string Name, Guid DepartmentId, string DepartmentName)`; `CustomerWithholdingAgentCityDto(string Type, string Country, CustomerMunicipalityDto City)`; `CustomerDto` appends `IReadOnlyList<CustomerWithholdingAgentCityDto>? WithholdingAgentCities = null` with the same never-null init pattern as `WithholdingTaxRates`; list items do not carry it.

- [ ] **Step 1: Write the failing unit tests:** `MunicipalRateIsRejectedBeforeRepeated` — `Validate({reteIca}, [reteIca])` → `customers.customer.withholding_tax_rate_municipal`; a ReteICA plus two Retefuente → still `withholding_tax_rate_municipal` (checked first). `AgentCitiesCollapseDuplicates`; `SameAgentSetDoesNotBumpVersion`; `IsWithholdingAgentMatchesCountryTypeAndCity`.
- [ ] **Step 2: Write the failing integration tests** (`CustomerWithholdingTaxRatesApiTests`): `AssigningAReteIcaRateIs422` (`withholding_tax_rate_municipal`); `AgentWithRetefuenteTypeIs422` (`[{type:"Retefuente",country:"CO",cityId}]` → `customers.customer.withholding_agent_type_invalid`); `AgentWithUnknownCityIs422` (`withholding_agent_city_invalid`); `AgentCitiesRoundTrip` (POST with Bogotá and Medellín → ficha `withholdingAgentCities` with `type`, `country`, `city.name`, `city.departmentName`); `EditWithoutAgentCitiesKeepsThem`; `EmptyListClearsThem`; `MergeKeepsTheSurvivorsList` (survivor's list untouched, merged one's not copied).
- [ ] **Step 3: RED** — `dotnet build` → compile errors.
- [ ] **Step 4: Implement.** `CustomerWithholdingAgentRules.ValidateAsync(ICustomerGeographyLookup geography, IReadOnlyCollection<WithholdingAgentCityRequest> entries, CancellationToken)`: per entry `TaxCountries.IsMunicipal(country, type)` else `withholding_agent_type_invalid`; then all city ids in one `FindCitiesAsync`, a missing one → `withholding_agent_city_invalid`. Mapping resolves agent cities in the same geography batch the ficha already uses.
- [ ] **Step 5: GREEN** — Customers unit tests; integration filtered by the names above → PASS.
- [ ] **Step 6: Commit** — `feat(customers): reject municipal rates and list withholding agent municipalities`.

### Task 7: Customers Excel without «ReteICA»

**Files:**
- Modify: `IExcelCustomerImporter.cs` (remove `ReteIca` `:58-59`, from `Ordered` `:92`; `WithholdingTypes = [Retefuente]` `:101`; remove `ExcelCustomerRow.ReteIca` `:128`), `ImportCustomers.cs:450`, `:520-524`, `:1178`, `ClosedXmlCustomerImporter.cs:83`, `ClosedXmlCustomerImportTemplateBuilder.cs:139`, `ClosedXmlCustomerExportBuilder.cs:135`, `CustomersDtos.cs:336` (P9: keep, comment as inherited, always `null`)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/Excel/{ClosedXmlCustomerExportBuilderTests,ClosedXmlCustomerImportTemplateBuilderTests}.cs`, `tests/Modules/Customers/Modules.Customers.IntegrationTests/{CustomerStatusAndImportApiTests,CustomerExportApiTests}.cs`

- [ ] **Step 1: Write the failing tests:** `TemplateHasNoReteIcaColumn` and `ExportHasNoReteIcaColumn` (header row does not contain `"ReteICA"`, contains `"Retefuente"`); integration `ImportWithoutReteIcaColumnWorks`; `OldFileWithReteIcaColumnIsImportedIgnoringIt` (a file with a «ReteICA» column holding a rate name → row imported, customer has no municipal rate, no row error).
- [ ] **Step 2: RED** — unit run fails (column still present).
- [ ] **Step 3: Implement** the removals; unknown headers are already ignored by the importer.
- [ ] **Step 4: GREEN** — Customers unit tests; integration filtered to the two names → PASS.
- [ ] **Step 5: Commit** — `feat(customers): drop the ReteICA column from the customers Excel`.

### Step A-close (end of Block A)

- [ ] `dotnet test tests/ArchitectureTests/ArchitectureTests` → PASS (`CompositionRootTests` 80 permissions).
- [ ] Full classes, one at a time (check `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` first): `dotnet test tests/Modules/Catalog/Modules.Catalog.IntegrationTests --filter "FullyQualifiedName~TaxRateApiTests|FullyQualifiedName~TaxRateActivationApiTests|FullyQualifiedName~TaxRateDeletionApiTests" -- xUnit.MaxParallelThreads=4`; Companies `--filter "FullyQualifiedName~CompanyWithholdingTaxRatesApiTests|FullyQualifiedName~CompanyWriteApiTests|FullyQualifiedName~CompanyApiTests"`; Customers `--filter "FullyQualifiedName~CustomerWithholdingTaxRatesApiTests|FullyQualifiedName~CustomerStatusAndImportApiTests|FullyQualifiedName~CustomerExportApiTests|FullyQualifiedName~CustomerMergeApiTests"`.
- [ ] Block review (one fresh reviewer, diff `bfe9d8d0..HEAD` minus docs). Fixes go in a `fix:` commit inside the block.

---

# Block B — Quotations domain and persistence (Tasks 8–10)

One implementer. Unit tests only, plus two persistence classes. Close with Step B-close.

### Task 8: Photo with city and catalog percentage; `MunicipalWithholdings`

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/WithholdingRate.cs` (P3), `DocumentWithholding.cs` (ctor `:31`, properties `:44-70`, `Rate` `:61`), `WithholdingCandidates.cs:17-37`, `WithholdingSelectorRules.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/MunicipalWithholdings.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`ConfigureWithholding` `:239-267`)
- Create (generated): migration `AddWithholdingCityAndCatalogPercentage` (edit `Up`: add `catalog_percentage` nullable, `UPDATE quotations.quotation_withholdings SET catalog_percentage = percentage`, same for `order_withholdings`, then `NOT NULL`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/{MunicipalWithholdingsTests (NEW),QuotationWithholdingsTests,TestWithholdings}.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationWithholdingsPersistenceTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `WithholdingRate(..., decimal? TaxUnitValue, Guid? CityId = null)` with `decimal CatalogPercentage { get; init; } = Percentage;` and `bool Overridden`.
  - `DocumentWithholding.CityId`, `.CatalogPercentage`, `.Overridden`; `internal void SetPercentage(decimal percentage)`; `Rate` round-trips both.
  - `enum MunicipalWithholdingStatus { Resolved, NoOperationCity, NoCompanyRate, CustomerNotAgent, NotSelected }`.
  - `sealed record MunicipalOffer(string Type, WithholdingRate? CompanyRate, bool CustomerIsAgent)`; `sealed record MunicipalContext(Guid? OperationCityId, IReadOnlyList<MunicipalOffer> Offers)` with `static MunicipalContext None` (null city, no offers).
  - `static class MunicipalWithholdings`: `WithholdingRate? ByCustomerRule(MunicipalOffer offer, Guid? operationCityId)`; `WithholdingRate? BySelectorRule(MunicipalOffer offer, Guid? operationCityId, bool hadSelection)`; `MunicipalWithholdingStatus Describe(bool hasRow, Guid? operationCityId, bool companyHasRate, bool? customerIsAgent, bool ownBilling)` (precedence of spec §9.4).
  - `WithholdingCandidates.For(source, billsToFinalConsumer, billingAccount, currency, Guid? operationCityId)` — a municipal rate applies only if `CityId == operationCityId`.
  - `WithholdingSelectorRules.Ensure(rates, companyCountry, Guid? operationCityId, codePrefix)` — a municipal rate with `CityId != operationCityId` → `{prefix}.withholding_tax_rate_invalid`.

- [ ] **Step 1: Write the failing unit tests** (`MunicipalWithholdingsTests`; `bogota`, `medellin` GUIDs; `reteIca = Rate("ReteICA Bogotá", "ReteICA", 0.966m) with CityId = bogota`):

```csharp
Assert.Equal(reteIca, MunicipalWithholdings.ByCustomerRule(new("ReteICA", reteIca, CustomerIsAgent: true), bogota));
Assert.Null(MunicipalWithholdings.ByCustomerRule(new("ReteICA", reteIca, CustomerIsAgent: false), bogota));
Assert.Null(MunicipalWithholdings.ByCustomerRule(new("ReteICA", null, true), bogota));
Assert.Null(MunicipalWithholdings.ByCustomerRule(new("ReteICA", reteIca, true), null));
Assert.Null(MunicipalWithholdings.BySelectorRule(new("ReteICA", reteIca, false), bogota, hadSelection: false));
Assert.Equal(reteIca, MunicipalWithholdings.BySelectorRule(new("ReteICA", reteIca, false), bogota, hadSelection: true));
Assert.Equal(MunicipalWithholdingStatus.Resolved, MunicipalWithholdings.Describe(true, null, false, false, false));
Assert.Equal(MunicipalWithholdingStatus.NoOperationCity, MunicipalWithholdings.Describe(false, null, true, null, false));
Assert.Equal(MunicipalWithholdingStatus.NoCompanyRate, MunicipalWithholdings.Describe(false, bogota, false, true, false));
Assert.Equal(MunicipalWithholdingStatus.CustomerNotAgent, MunicipalWithholdings.Describe(false, bogota, true, false, false));
Assert.Equal(MunicipalWithholdingStatus.NotSelected, MunicipalWithholdings.Describe(false, bogota, true, true, false));
Assert.Equal(MunicipalWithholdingStatus.NotSelected, MunicipalWithholdings.Describe(false, bogota, true, null, ownBilling: true));
```

  Plus: `OverriddenIsFalseWithEqualPercentages` (`rate.Overridden == false`; `(rate with { Percentage = 0.8m }).Overridden == true`, `CatalogPercentage == 0.966m`); `CandidatesDropAMunicipalRateOfAnotherCity` (`For([reteIcaBogota, retefuente], false, coAccount, "COP", medellin)` → only Retefuente); `CandidatesKeepNationalAsToday` (`operationCityId: null` → Retefuente applies); `SelectorRulesRejectAMunicipalRateOfAnotherCity` → `quotation.billing.withholding_tax_rate_invalid`.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~MunicipalWithholdingsTests"` → compile error.
- [ ] **Step 3: Implement.** Both aggregates pass `null` as `operationCityId` to `For`/`Ensure` for now (Tasks 9–10 pass the real one). Map `CityId` → `city_id uuid null`, `CatalogPercentage` → `catalog_percentage numeric(6,3) not null`. Generate the migration, edit the `Up` as listed.
- [ ] **Step 4: Write the failing persistence test** `CatalogPercentageIsBackfilledFromPercentage` (pattern `RetentionToWithholdingsMigrationTests`: `IMigrator` down to the previous migration, insert a quotation and a `quotation_withholdings` row with `percentage = 2.5`, migrate to `null`, `SELECT catalog_percentage` → `2.500`, `city_id` null; end with `MigrateAsync(null)`), plus `PhotoRoundTripsCityAndCatalogPercentage`. Run filtered → FAIL before the migration exists / PASS after.
- [ ] **Step 5: GREEN** — Quotations unit tests → PASS; `--filter "FullyQualifiedName~CatalogPercentageIsBackfilledFromPercentage|FullyQualifiedName~PhotoRoundTripsCityAndCatalogPercentage"` on Quotations.IntegrationTests → PASS.
- [ ] **Step 6: Commit** — `feat(quotations): freeze the city and catalog percentage of each withholding`.

### Task 9: Quotation — operation city, re-resolution, edited percentage

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (`Create` `:410-447`, `UpdateDetails` `:650-688`, `SetBillingWithholdings` `:702-724`, `EnsureSelectableWithholdings` `:726`, `ApplyBillingAccount` `:742`, `ChangeClient` `:795-817`, `RecalculateTotals` `:1217`), `QuotationBillingAccount.cs` (`Guid? CityId`), `QuotationHistoryEventType.cs` (append `WithholdingRateEdited`)
- Create: `src/Modules/Quotations/Modules.Quotations.Domain/OperationCityChoice.cs`, `MunicipalReResolution.cs`
- Modify: `QuotationsDbContext.cs` (`OwnsOne(BillingAccount)` `:188` and `:644` → `billing_company_city_id`; `OperationCityId` → `operation_city_id` on both tables), migration `AddOperationCity` (both tables, nullable, no FK, no backfill — A8)
- Modify: `Application/IQuotationCompanyLookup.cs:20-36` (`QuotationCompanyRef` appends `Guid? CityId`), `QuotationBillingAccountVerifier.cs:8`, `:55` (`VerifiedBillingAccount` appends `Guid? CityId`), `QuotationBillingAccountResolver.cs:41-47`, `src/Bootstrapper/QuotationCompanyLookup.cs:24-36`
- Modify (compile, interim `MunicipalContext.None`): callers of `Quotation.Create`, `UpdateDetails`, `ChangeClient` in Application (wired in Task 12)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationMunicipalWithholdingsTests.cs` (NEW)

**Interfaces:**
- Consumes: Task 8.
- Produces:
  - `enum OperationCityChoiceKind { Keep, Company, Explicit }`; `readonly record struct OperationCityChoice(OperationCityChoiceKind Kind, Guid? CityId)` with `static Keep`, `static Company`, `static Explicit(Guid id)`.
  - `sealed record MunicipalReResolution(bool Triggered, Guid? OperationCityBefore, Guid? OperationCityAfter, IReadOnlyList<WithholdingRate> DiscardedOverrides, IReadOnlyList<WithholdingRate> RemovedManual)` with `static None`.
  - `sealed record WithholdingPercentageChange(string Name, decimal From, decimal To, bool Restored)`.
  - `Guid? Quotation.OperationCityId`; `Guid? Quotation.OperationCityFor(QuotationBillingAccount? billingAccount, OperationCityChoice choice)` — `null` if `TaxCountries.MunicipalTypes(account?.Country)` is empty (A7); `Explicit` → the id; `Company` → `account.CityId`; `Keep` → `account.CompanyId != BillingAccount?.CompanyId ? account.CityId : OperationCityId` (A6).
  - `Quotation.Create(…, IReadOnlyList<WithholdingRate> customerWithholdings, MunicipalContext municipal, MemberId createdBy, DateTimeOffset occurredAt)` — Customer source = national photos + `ByCustomerRule` per offer; `OperationCityId = municipal.OperationCityId`.
  - `MunicipalReResolution Quotation.UpdateDetails(…existing…, MunicipalContext municipal, MemberId updatedBy, DateTimeOffset occurredAt)` — after `ApplyBillingAccount`: trigger = company id changed **or** `municipal.OperationCityId != OperationCityId`; sets the city; on trigger, per municipal type of the offers: Customer source by `ByCustomerRule`, Billing source by `BySelectorRule(hadSelection: old Billing row of that type existed)` (P5); rows of municipal types with no offer (country without municipal types) are removed; replaced in place (`DocumentWithholding.ReplaceSource`); overridden old rows go to `DiscardedOverrides`. Re-sending the same company with a legacy `null` city fills `BillingAccount.CityId` without triggering.
  - `MunicipalReResolution Quotation.ChangeClient(Guid clientId, bool customerVatSurplus, IReadOnlyList<WithholdingRate> customerWithholdings, MunicipalContext municipal, MemberId updatedBy, DateTimeOffset occurredAt)` — Customer source = national + `ByCustomerRule` (trigger 3); Billing emptied as today.
  - `WithholdingPercentageChange? Quotation.SetWithholdingPercentage(string type, decimal? percentage, MemberId updatedBy, DateTimeOffset occurredAt)` — `EnsureEditable`; type not `IsMunicipal` or no row of that type in the **active** source → `quotation.billing.withholding_percentage_not_editable`; `p <= 0 || p > 100 || decimal.Round(p, 3) != p` → `quotation.billing.withholding_percentage_invalid`; `null` → back to `CatalogPercentage`; no-op returns `null`; else `Touch` (recalculates totals).
  - `SetBillingWithholdings` and `RecalculateTotals` pass `OperationCityId` to `WithholdingSelectorRules`/`WithholdingCandidates`.

- [ ] **Step 1: Write the failing tests** (`QuotationMunicipalWithholdingsTests`; account `CO`/`COP` with `CityId = bogota`; offers built in the test):
  - `FixingTheCompanySetsTheOperationCityToTheCompanys` — draft without account, `UpdateDetails(account, context(bogota, agent))` → `OperationCityId == bogota`, Customer source has ReteICA Bogotá, `RetentionAmount` includes it.
  - `ChangingTheCompanyResetsTheCityUnlessExplicit` — `OperationCityFor(accountMedellin, Keep) == medellin`; `OperationCityFor(accountMedellin, Explicit(bogota)) == bogota`; `OperationCityFor(sameCompany, Keep) == current`.
  - `ChangingTheCityReResolvesAndDiscardsTheEditedPercentage` — set 0.8 on ReteICA Bogotá, then `UpdateDetails(sameAccount, context(medellin, companyRate: null))` → no ReteICA row; result `DiscardedOverrides` has the 0.8 rate.
  - `ChangeClientReResolvesWithTheNewCustomersAgents` (new client not agent → row removed).
  - `OwnBillingSelectorMovesToTheNewCitysRateOrEmpties` (P5).
  - `CountryWithoutMunicipalTypesDropsMunicipalRowsAndCity` (`OperationCityFor(esAccount, Explicit(bogota)) == null`).
  - `ResendingTheSameCompanyFillsCityWithoutTriggering` — legacy quotation (account without `CityId`, `OperationCityId` null), same company with `CityId` → `Triggered == false`, `BillingAccount.CityId == bogota`, `OperationCityId == null`.
  - `SetWithholdingPercentage`: `0m` → `percentage_invalid`; `0.8125m` → `percentage_invalid`; `100m` passes; `100.001m` → `percentage_invalid`; `"Retefuente"` → `percentage_not_editable`; ReteICA without row → `percentage_not_editable`; `null` restores `0.966m` with `Restored == true`; on `Sent` passes; on converted → `quotation.quotation.not_editable`; `NetTotal` uses 0.8 (`base 1_000_000` → amount `8_000`).
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationMunicipalWithholdingsTests"` → compile error.
- [ ] **Step 3: Implement** the domain; mapping and `AddOperationCity` migration; `has-pending-model-changes` clean. The resolver and verifier copy `CityId` from `QuotationCompanyRef.CityId` (adapter reads `company.CityId`).
- [ ] **Step 4: GREEN** — all Quotations unit tests → PASS (existing `QuotationTests`/`QuotationWithholdingsTests` adjusted only for the new parameters).
- [ ] **Step 5: Commit** — `feat(quotations): resolve municipal withholdings by operation city on quotations`.

### Task 10: Order — operation city, re-resolution, edited percentage

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Order.cs` (`FromQuotation` `:368-428`, `Build` used by `CreateManual`/`Preview`/`CreateExternal`/`CreateFromStore`/`QuoteForStore` `:434-620`, `SetWithholdings` `:760-783`, `SetBillingAccount` `:798-822` replaced, `RecalculateTotals`), `OrderHistoryEventType.cs` (append `WithholdingRateEdited`)
- Modify (compile): `Application/OrderEdits.cs:121-174` (interim `MunicipalContext.None`, wired in Task 13)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/{OrderMunicipalWithholdingsTests (NEW),OrderWithholdingsTests,OrderTests}.cs`

**Interfaces:**
- Consumes: Tasks 8–9.
- Produces: `Guid? Order.OperationCityId`; `Guid? Order.OperationCityFor(QuotationBillingAccount? billingAccount, OperationCityChoice choice)` (same rule as the quotation); factories set `OperationCityId` from `billingAccount.CityId` when the country has municipal types (header withholdings already include municipal rows, Task 13); `FromQuotation` copies `quotation.OperationCityId` and the active rows (effective and catalog percentage through `Rate`); `sealed record OrderBillingContextChange(bool AccountChanged, MunicipalReResolution Municipal)`; `OrderBillingContextChange Order.ApplyBillingContext(QuotationBillingAccount? billingAccount, MunicipalContext municipal, MemberId updatedBy, DateTimeOffset occurredAt)` (`null` account keeps the account; keeps P10 currency rule `order.billing.currency_change_not_allowed`; re-resolution with the customer rule; a removed row whose offer has a company rate but `CustomerIsAgent == false` goes to `RemovedManual`); `WithholdingPercentageChange? Order.SetWithholdingPercentage(string type, decimal? percentage, MemberId updatedBy, DateTimeOffset occurredAt)` (codes `order.billing.withholding_percentage_*`, `Order.IsEditable` else `order.order.not_editable`). `SetWithholdings` passes `OperationCityId` to `WithholdingSelectorRules`.

- [ ] **Step 1: Write the failing tests** (`OrderMunicipalWithholdingsTests`):
  - `ConvertingInheritsCityRowsAndEditedPercentage` — quotation with ReteICA Bogotá at 0.8 → order `OperationCityId == bogota`, row `Percentage == 0.8m`, `CatalogPercentage == 0.966m`, same `RetentionAmount`.
  - `StoreAndManualOrdersStartAtTheCompanysCity` — `CreateFromStore`/`CreateManual` with header holding ReteICA Bogotá and account `CityId = bogota` → `OperationCityId == bogota`, row applied.
  - `PreparedOrInvoicedRejectsCityAndPercentage` — `ApplyBillingContext(null, context(medellin))` and `SetWithholdingPercentage("ReteICA", 0.8m, …)` on a prepared order → `order.order.not_editable`; same on invoiced.
  - `ChangingTheCityRemovesAHandAppliedRate` — row ReteICA Bogotá chosen by selector, customer not agent; `ApplyBillingContext(null, context(medellin, companyRate: medellinRate, agent: false))` → no row, `RemovedManual` has the Bogotá rate.
  - `CompanyOfACountryWithoutMunicipalTypesDropsMunicipalRows` (Review Focus 4) — edited 0.8 ReteICA, ES company with a COP account → no municipal row, `OperationCityId == null`, `DiscardedOverrides` has the 0.8 rate.
  - `SelectorWithAMunicipalRateOfAnotherCityIs422` → `order.billing.withholding_tax_rate_invalid`.
  - Percentage twins of Task 9 with `order.billing.*` codes.
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderMunicipalWithholdingsTests"` → compile error.
- [ ] **Step 3: Implement.** Migrate the existing `SetBillingAccount` tests to `ApplyBillingContext(account, MunicipalContext.None, …).AccountChanged`.
- [ ] **Step 4: GREEN** — all Quotations unit tests → PASS.
- [ ] **Step 5: Commit** — `feat(quotations): resolve municipal withholdings by operation city on orders`.

### Step B-close (end of Block B)

- [ ] `dotnet build` → `0 Warning(s) 0 Error(s)`; Quotations unit tests full → PASS.
- [ ] Full classes: `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationWithholdingsPersistenceTests|FullyQualifiedName~OrderWithholdingsPersistenceTests|FullyQualifiedName~RetentionToWithholdingsMigrationTests" -- xUnit.MaxParallelThreads=4`.
- [ ] Block review (fresh reviewer, Block B diff).

---

# Block C — Quotations application, contract, history, PDF (Tasks 11–15)

One implementer. Close with Task 15.

### Task 11: Ports — company municipal rates, agents, cities; context builder

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationWithholdingLookup.cs`, `IQuotationGeographyLookup.cs`, `OrderCustomerWithholdings.cs`, `WithholdingSelection.cs`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/MunicipalContexts.cs`
- Modify: `src/Bootstrapper/QuotationWithholdingLookup.cs` (adds `ICompanyRepository`; `Photo` fills `CityId`; `ForCustomerAsync` drops municipal rates), `src/Bootstrapper/QuotationGeographyLookup.cs`
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationWithholdingLookupTests.cs`, `tests/Modules/Quotations/Modules.Quotations.UnitTests/{WithholdingSelectionTests,MunicipalContextsTests (NEW)}.cs`

**Interfaces:**
- Consumes: Tasks 4, 6, 8–10.
- Produces:
  - `IQuotationWithholdingLookup.ForCompanyMunicipalAsync(Guid tenantId, Guid companyId, string country, Guid cityId, DateOnly documentDate, CancellationToken) : Task<IReadOnlyList<WithholdingRate>>` (active, withholding, municipal, that country, `CityId == cityId`, as photos); `IsWithholdingAgentAsync(Guid tenantId, Guid customerId, string country, string type, Guid cityId, CancellationToken) : Task<bool>`; `ForCustomerAsync` returns national rates only.
  - `QuotationCityRef(Guid Id, string Name, Guid DepartmentId, string DepartmentName)`; `IQuotationGeographyLookup.FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, CancellationToken) : Task<IReadOnlyDictionary<Guid, QuotationCityRef>>`.
  - `MunicipalContexts.ResolveAsync(IQuotationWithholdingLookup lookup, IQuotationGeographyLookup geography, Guid tenantId, Guid customerId, QuotationBillingAccount? billingAccount, Guid? operationCityId, bool explicitCity, DateOnly documentDate, string codePrefix, CancellationToken) : Task<MunicipalContext>` — explicit city not found → `{codePrefix}.billing.operation_city_invalid`; country without municipal types → `MunicipalContext.None`; else one `MunicipalOffer` per `TaxCountries.MunicipalTypes(country)` (company rate of that type, or `null`; agent flag).
  - `MunicipalSelectorScope(IReadOnlySet<Guid> AllowedMunicipalRateIds, bool IgnoreUnmatched)`; `WithholdingSelection.ResolveAsync(IQuotationWithholdingLookup lookup, Guid tenantId, IReadOnlyCollection<Guid> ids, string? companyCountry, DateOnly documentDate, MunicipalSelectorScope scope, string codePrefix, CancellationToken cancellationToken)` (callers that have no municipal context pass `MunicipalSelectorScope.None` = empty set, not ignoring) — a municipal candidate in `AllowedMunicipalRateIds` always passes; one outside it is skipped silently when `IgnoreUnmatched` (A11, body that changed company or city), else `{prefix}.billing.withholding_tax_rate_invalid`.
  - `OrderCustomerWithholdings.ResolveAsync` (same signature) appends municipal rows by the customer rule at `billingAccount.CityId`; never throws.

- [ ] **Step 1: Write the failing tests:** Bootstrapper `ForCustomerAsyncReturnsOnlyNationalRates`, `ForCompanyMunicipalAsyncFiltersByCityAndActive`, `IsWithholdingAgentReadsTheCustomersList`; `WithholdingSelectionTests`: `MunicipalIdNotOfferedIs422`, `UnmatchedMunicipalIdIgnoredWhenScopeSaysSo` (returns only national photos, no 422), `OfferedMunicipalIdPassesEvenWhenIgnoringUnmatched` (A11), `OfferedMunicipalIdPasses`; `MunicipalContextsTests`: `ExplicitUnknownCityIs422` (`quotation.billing.operation_city_invalid`), `NonMunicipalCountryGivesNone`, `OneOfferPerMunicipalType`.
- [ ] **Step 2: RED** — `dotnet build` → compile errors.
- [ ] **Step 3: Implement**; register nothing new (both adapters already registered, `QepServiceCollectionExtensions.cs:1561`).
- [ ] **Step 4: GREEN** — Quotations and Bootstrapper unit tests → PASS.
- [ ] **Step 5: Commit** — `feat(quotations): read company municipal rates and customer agents through ports`.

### Task 12: Quotation — bodies, history, explanation

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/MunicipalWithholdingExplanation.cs`
- Modify: `QuotationEdits.cs` (`IQuotationEdits` gains members), `QuotationsDtos.cs` (`WithholdingDto` `:135`, `WithholdingTaxRateDto` `:148`, `QuotationDto` `:32`, `QuotationBillingAccountDto` `:217`, `CreateQuotationRequest` `:304`, `SaveQuotationRequest` `:432`, `QuotationResponse` `:545`, `WithholdingResponse` `:654`, `WithholdingTaxRateResponse` `:667`), `QuotationEditsApplication.cs`, `QuotationDraftBuilder.cs:70-95`, `SaveQuotation.cs:100-130`, `PreviewQuotation.cs`, `PreviewNewQuotation.cs`, `ChangeQuotationClient.cs`, `QuotationMapping.cs`, `QuotationResponseComposer.cs` (ctor `:25-30`, `:109-111`, `:130-143`), `QuotationChangeSummary.cs`, `HistoryFormat.cs` (P10)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationMunicipalWithholdingsApiTests.cs` (NEW), `QuotationWithholdingsApiTests.cs` (rewrite `TwoWithholdingsGiveTwoLinesAndTheirSum` with company assignment + agent instead of a customer ReteICA), `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationChangeSummaryTests.cs`

**Interfaces:**
- Consumes: Tasks 9, 11.
- Produces:
  - Requests: `WithholdingPercentageRequest(string Type, decimal? Percentage)`; `CreateQuotationRequest` and `SaveQuotationRequest` append `IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null` and the P1 property `JsonElement OperationCityId { get; init; }`; `IQuotationEdits` gains `OperationCityChoice OperationCity { get; }` and `IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages { get; }`.
  - DTO/Response twins append `decimal CatalogPercentage, bool PercentageOverridden, Guid? CityId` to `WithholdingDto`/`WithholdingResponse` and `WithholdingTaxRateDto`/`WithholdingTaxRateResponse`; `QuotationDto` appends `Guid? OperationCityId`; `QuotationBillingAccountDto` appends `Guid? CityId`; `QuotationCityResponse(Guid Id, string Name, Guid DepartmentId, string DepartmentName)`; `MunicipalCompanyRateResponse(Guid TaxRateId, string Name, decimal Percentage)`; `MunicipalWithholdingResponse(string Type, string Country, MunicipalCompanyRateResponse? CompanyRate, string Status, bool? CustomerIsAgent)`; `QuotationResponse` appends `QuotationCityResponse? OperationCity, QuotationCityResponse? BillingCompanyCity, IReadOnlyList<MunicipalWithholdingResponse> MunicipalWithholdings`.
  - `MunicipalWithholdingExplanation.ExplainAsync(IQuotationWithholdingLookup lookup, IQuotationGeographyLookup geography, Guid tenantId, Guid customerId, QuotationBillingAccountDto? billingAccount, Guid? operationCityId, bool ownBilling, IReadOnlyList<WithholdingTaxRateDto> activeRows, DateOnly documentDate, CancellationToken) : Task<MunicipalExplanation>`; `MunicipalExplanation(QuotationCityResponse? OperationCity, QuotationCityResponse? BillingCompanyCity, IReadOnlyList<MunicipalWithholdingResponse> Entries)`. `customerIsAgent` is `null` with own billing or without city.
  - `HistoryFormat.Percentage(decimal value) : string`; `QuotationChangeSummary.WithholdingRateEdited(string name, decimal from, decimal to, HistoryFormat format)`, `.WithholdingRateRestored(string name, decimal catalog, HistoryFormat format)`, `.WithholdingOverrideDiscarded(string type, decimal percentage, HistoryFormat format)`, `.OperationCityChanged(string? from, string to)`, `.ManualMunicipalWithholdingApplied(string type, string city)`.
- Order inside `QuotationEditsApplication.ApplyAsync` (spec §7.7): account → `OperationCityFor` → `MunicipalContexts.ResolveAsync` → `UpdateDetails(…, context)` → selectors with `MunicipalSelectorScope(allowed = offers' company rate ids, IgnoreUnmatched = reResolution.Triggered)` and, when `Triggered`, the current municipal Billing row appended for each type without an accepted id (P6) → shipping → items → percentages (when `Triggered`, a municipal entry is applied if the current source has a row of that type, else skipped silently; A11). History: a discarded override from the re-resolution is logged before a percentage edit from the same body. `QuotationEditsOutcome` gains `MunicipalReResolution Municipal`, `IReadOnlyList<WithholdingPercentageChange> PercentageChanges`, `string? ManualMunicipalSummary`.

- [ ] **Step 1: Write the failing unit tests** (`QuotationChangeSummaryTests`, `HistoryFormat.Colombian`): `WithholdingRateEdited("ReteICA Bogotá", 0.966m, 0.8m)` == `"Cambió la tarifa de ReteICA Bogotá de 0,966 % a 0,8 %."`; `WithholdingRateRestored(…, 0.966m)` == `"Restableció la tarifa de ReteICA Bogotá a 0,966 %."`; `WithholdingOverrideDiscarded("ReteICA", 0.8m)` == `"Se volvió a calcular la ReteICA por cambio de municipio o de empresa; se descartó la tarifa editada (0,8 %)."`; `OperationCityChanged("Bogotá, D.C.", "Medellín")` == `"Cambió el municipio de la operación de Bogotá, D.C. a Medellín."`; `ManualMunicipalWithholdingApplied("ReteICA", "Bogotá, D.C.")` == `"Aplicó ReteICA Bogotá, D.C. por facturación propia"`; `HistoryFormat` with `.` decimals → `"0.966 %"`.
- [ ] **Step 2: Write the failing integration tests** (`QuotationMunicipalWithholdingsApiTests`; fixture: UVT, company CO in Bogotá with ReteICA Bogotá 0.966 % assigned, customer agent in Bogotá, two seeded cities):
  - `CompanyInBogotaAndAgentCustomerAppliesReteIca` — create with `billingAccount` → `withholdings` has ReteICA with `cityId == bogota`, `percentage == 0.966`, `catalogPercentage == 0.966`, `percentageOverridden == false`; `operationCity.id == bogota`; `billingCompanyCity.id == bogota`; `municipalWithholdings == [{ type: "ReteICA", country: "CO", companyRate: { name: "ReteICA Bogotá", percentage: 0.966 }, status: "Resolved", customerIsAgent: true }]`.
  - `OperationCityWithoutCompanyRateIsNoCompanyRate` — save with `operationCityId: medellin` → no ReteICA row, `status == "NoCompanyRate"`, `companyRate == null`.
  - `EditingThePercentageRecalculatesAndLogs` — save `withholdingPercentages: [{ type: "ReteICA", percentage: 0.8 }]` → amount `= round(base × 0.8 / 100, 2)`, `netTotal` drops accordingly, history has `eventType == "WithholdingRateEdited"` with `"Cambió la tarifa de ReteICA Bogotá de 0,966 % a 0,8 %."`; audit `quotation.quotation.withholding_rate_edited`.
  - `ResendingTheSameMunicipalSelectorKeepsTheEditedPercentage` (Review Focus 1, own billing).
  - `PercentageForRetefuenteIs422` (`quotation.billing.withholding_percentage_not_editable`); `PercentageWithFourDecimalsIs422` (`…_invalid`); `UnknownOperationCityIs422` (`quotation.billing.operation_city_invalid`).
  - `PreviewsReturnOperationCityAndMunicipalWithholdings` (`POST /quotations/preview` and `/{id}/preview`, no history written).
  - `OwnBillingCanApplyTheCompanysRateByHand` (R11) — own billing, `billingWithholdingTaxRateIds: [reteIcaBogota]` for a customer that is not agent → 200, `status == "Resolved"`, `customerIsAgent == null`, `Edited` summary contains `"Aplicó ReteICA"`.
  - `OwnBillingSelectorOfAnotherCityIs422` (`quotation.billing.withholding_tax_rate_invalid`).
  - A11 (owner 2026-11-13), both starting from a quotation with an edited ReteICA 0.8 and a second CO company in Medellín with ReteICA Medellín assigned (customer agent in Medellín):
    - `CompanyChangeAcceptsTheNewCompanysRate` — one PUT switches company and carries ReteICA Medellín's id, `withholdingPercentages: [{ type: "ReteICA", percentage: 0.7 }]` and a Retefuente id → ReteICA row is Medellín at 0.7 (`percentageOverridden == true`), Retefuente applied; history has «se descartó la tarifa editada (0,8 %)» **then** «Cambió la tarifa de ReteICA Medellín de … a 0,7 %.».
    - `CompanyChangeIgnoresTheOldCompanysRate` — same PUT with ReteICA Bogotá's (old company) id and a 0.7 percentage → 200 (no 422), ReteICA row comes from re-resolution and gets 0.7; variant where the re-resolution leaves no ReteICA row (new company without rate in the city) → 200, no row, percentage ignored (no `withholding_percentage_not_editable`).
    - Both against `POST /quotations/{id}/preview` too (no history written).
  - `ResendingTheSameCompanyFillsTheCompanyCityWithoutReResolving` (Review Focus 2) — legacy quotation by SQL (`operation_city_id`, `billing_company_city_id` null) → `GET`: `billingCompanyCity == null`, `status == "NoOperationCity"`; PUT same body → `billingCompanyCity.id == bogota`, `operationCity == null`, no new history; PUT `operationCityId: bogota` → ReteICA resolved.
  - `StatusFollowsTodaysAgentsWithoutMovingThePhoto` (A14) — remove the customer's Bogotá agent entry, `GET` → `customerIsAgent == false` and still `status == "Resolved"`, same `retentionAmount`; on a quotation without row → `CustomerNotAgent`.
  - `ChangingTheClientReResolvesWithItsAgents`.
- [ ] **Step 3: RED** — `dotnet build` → compile errors (no containers).
- [ ] **Step 4: Implement.** Composer gains `IQuotationWithholdingLookup` and `IQuotationGeographyLookup`. `SaveQuotation` writes one `WithholdingRateEdited` entry (and one audit) per percentage change, discarded override and removed manual row; city change and manual application go into the `Edited` summary with names from `FindCitiesAsync`. Previews never write history. Comment the DTOs with the BFF reasons (A12, A14).
- [ ] **Step 5: GREEN** — Quotations unit tests; integration filtered test by test while iterating (`--filter "FullyQualifiedName~<Name>"`) → PASS.
- [ ] **Step 6: Commit** — `feat(quotations): explain and edit municipal withholdings on quotations`.

### Task 13: Order — edits, creation paths, explanation

**Files:**
- Modify: `OrdersDtos.cs` (`SaveOrderEditsRequest` `:176-196`, `OrderDto`, `OrderDetailResponse` `:351`), `OrderEdits.cs` (`IOrderEdits`, `OrderWithholdingEdits` `:121-174`, outcome `:105`), `SaveOrderEdits.cs:255-301`, `PreviewOrderEdits.cs`, `OrderDraftBuilder.cs:131`, `IStoreOrderGateway.cs:184`, `IExternalOrderWriter.cs:180`, `OrderMapping.cs`, `OrderResponseComposer.cs` (ctor `:13-17`, `ComposeAsync`), `OrderChangeSummary.cs` (`WithholdingsChanged` `:95-96`)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderMunicipalWithholdingsApiTests.cs` (NEW), `OrderWithholdingsApiTests.cs`, `StoreOrderGatewayTests.cs`, `ExternalOrderServiceTests.cs`, `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderChangeSummaryTests.cs`

**Interfaces:**
- Consumes: Tasks 10–12.
- Produces: `SaveOrderEditsRequest` appends `IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null` and P1 `JsonElement OperationCityId { get; init; }`; `IOrderEdits` gains `OperationCity`, `WithholdingPercentages`; `OrderDto` appends `Guid? OperationCityId`; `OrderDetailResponse` appends `QuotationCityResponse? OperationCity, QuotationCityResponse? BillingCompanyCity, IReadOnlyList<MunicipalWithholdingResponse> MunicipalWithholdings` (P2); `OrderChangeSummary.WithholdingsChanged(string before, string after, (string Type, string City)? notAgent = null)` (suffix exact per Global Constraints); `OrderWithholdingEditsOutcome` gains `string? OperationCitySummary`, `IReadOnlyList<string> WithholdingRateEditedSummaries`. `CreateOrderRequest` unchanged (A10/R16).
- Order inside `OrderWithholdingEdits.ApplyAsync`: account → `OperationCityFor` → context → `ApplyBillingContext` → selectors (scope, P6) → percentages; `PartiesChanged` entries for account, city and selectors as today; `WithholdingRateEdited` entries plus audit `quotation.order.withholding_rate_edited`.

- [ ] **Step 1: Write the failing unit test:** `WithholdingsChanged("ninguna", "ReteICA Bogotá", ("ReteICA", "Bogotá, D.C."))` == `"Cambió las retenciones de ninguna a ReteICA Bogotá; el cliente no está registrado como agente retenedor de ReteICA en Bogotá, D.C.."` — the double period is what the literal spec text produces; if the reviewer objects, the fix is in this method only.
- [ ] **Step 2: Write the failing integration tests** (`OrderMunicipalWithholdingsApiTests`, same fixture as Task 12):
  - `ConvertedOrderInheritsCityAndEditedPercentage`.
  - `StoreOrderStartsWithTheCompanysCityAndCustomerRule`; `ManualOrderStartsWithTheCompanysCity` (body without `operationCityId`).
  - `HandAppliedRateIsResolvedWithCustomerIsAgentFalse` (R11) — edit with `withholdingTaxRateIds: [reteIcaBogota]`, customer not agent → 200, `municipalWithholdings[0].status == "Resolved"`, `customerIsAgent == false`, `PartiesChanged` detail ends with the not-agent suffix; then edit `operationCityId: medellin` → row gone, `WithholdingRateEdited` «Se quitó la ReteICA aplicada a mano al cambiar la empresa o el municipio: el cliente no es agente retenedor ahí.».
  - `StatusBranchesOnOrders` — no row + customer not agent → `CustomerNotAgent`; no row + agent + company rate → `NotSelected`.
  - `PreparedOrderRejectsCityAndPercentage` → `order.order.not_editable`.
  - A11 on orders (owner 2026-11-13): `CityChangeAcceptsTheNewCompanysRate` (edit with `operationCityId: medellin`, ReteICA Medellín's id and a percentage → both applied; R11 unchanged: accepted even if the customer is not agent in Medellín, with the not-agent suffix) and `CityChangeIgnoresTheOldCompanysRate` (ReteICA Bogotá's id → 200, ignored, row from re-resolution, percentage applied only if a ReteICA row remains), each with its preview twin (`POST /orders/{id}/preview`).
  - `ResendingTheSameMunicipalSelectorKeepsTheEditedPercentage` (order twin of Review Focus 1).
  - `StoreOrderNeverFailsOnMunicipalResolution` and `ExternalOrderNeverFailsOnMunicipalResolution` (Review Focus 3); the existing Shopify webhook regression in `ExternalOrderServiceTests` stays green.
- [ ] **Step 3: RED** — `dotnet build` → compile errors.
- [ ] **Step 4: Implement**; composer gains the two lookups and uses `MunicipalWithholdingExplanation` with `ownBilling: false`.
- [ ] **Step 5: GREEN** — unit tests; integration filtered by name → PASS.
- [ ] **Step 6: Commit** — `feat(quotations): explain and edit municipal withholdings on orders`.

### Task 14: PDF — percentage of every withholding

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfDocument.cs:110`, `QuotationPdfDocumentMapper.cs:130-133` (pass the tenant separator from `RegionalNumberFormat.Separators(numberFormat)` already computed at `:31`/`:87`), `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ:422-426`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/{QuotationPdfDocumentMapperTests,OrderPdfDocumentMapperTests,QuotationTemplateTests}.cs`

**Interfaces:**
- Produces: `QuotationPdfWithholding(string Name, decimal Amount, string PercentageLabel)`; label = effective `WithholdingResponse.Percentage`, `"0.###"` with the tenant decimal separator, plus `" %"`.

- [ ] **Step 1: Write the failing tests:** mapper `PercentageLabelUsesTheTenantSeparator` (`0.966m` → `"0,966 %"` with comma format, `"0.966 %"` with dot format); `PercentageLabelHasNoTrailingZeros` (`2.5m` → `"2,5 %"`, `4m` → `"4 %"`); `EditedPercentageWinsOverCatalog` (row `percentage 0.8`, `catalogPercentage 0.966` → `"0,8 %"`); same three in `OrderPdfDocumentMapperTests`; `QuotationTemplateTests`: row text `"ReteICA Bogotá 0,966 %:"` and `"Retefuente 2,5 % 2,5 %:"` (no deduplication).
- [ ] **Step 2: RED** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~PdfDocumentMapperTests|FullyQualifiedName~QuotationTemplateTests"` → compile error.
- [ ] **Step 3: Implement**; template label `retencion.name + " " + retencion.percentageLabel + ":"`.
- [ ] **Step 4: GREEN** — same filter → PASS.
- [ ] **Step 5: Commit** — `feat(quotations): print each withholding's percentage on the PDF`.

### Task 15: Branch verification (before merging to `develop`)

- [ ] **Step 1: Block C close** — full classes once: Quotations.IntegrationTests `--filter "FullyQualifiedName~QuotationMunicipalWithholdingsApiTests|FullyQualifiedName~OrderMunicipalWithholdingsApiTests|FullyQualifiedName~QuotationWithholdingsApiTests|FullyQualifiedName~OrderWithholdingsApiTests|FullyQualifiedName~StoreOrderGatewayTests|FullyQualifiedName~ExternalOrderServiceTests|FullyQualifiedName~QuotationPdfExportApiTests" -- xUnit.MaxParallelThreads=4`. Block C review (fresh reviewer).
- [ ] **Step 2: Build and architecture** — `dotnet build` → `0 Warning(s) 0 Error(s)`; `dotnet test tests/ArchitectureTests/ArchitectureTests` → PASS (`CatalogLayerTests`, `CompaniesLayerTests`, `CustomersLayerTests`, `QuotationsLayerTests`, `CompositionRootTests` 80).
- [ ] **Step 3: Full suite, one project at a time** (O2: every test project, because BuildingBlocks and four modules' migrations are touched; unless the owner narrows it). Unit projects first, then each `*.IntegrationTests` project with `$env:Seed__Enabled = "false"` and `-- xUnit.MaxParallelThreads=4`, each only after `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` is empty. A failure that also fails on `develop` is reported as pre-existing, not fixed here. If a run is cut, look for orphan `Modules.*.IntegrationTests.exe` under the worktree path.
- [ ] **Step 4: Spec walk** — map every code of spec §10 and every bullet of §13 to a test name from Tasks 1–14; paste the table in the report. A missing one becomes a test in the owning task, not a note.
- [ ] **Step 5: Strong review** — one fresh reviewer on the most capable model over `develop..HEAD`.
- [ ] **Step 6: State** — `git branch --show-current` → `feature/reteica-por-empresa`; `git status --short` clean; `git log --oneline develop..HEAD` one commit per task (+ block fixes); `git diff --stat develop..HEAD -- docs/estado.md` empty.
- [ ] **Step 7: Merge notes** (not executed on the branch): merge to `develop` only, never `main`; **do not push `develop`** until the frontend half is merged (R12); deploy backend first (hard cut, all instances at once, spec §12); tell the frontend P1, P2, P9 and O1; update `docs/estado.md` on `develop` after the merge.
