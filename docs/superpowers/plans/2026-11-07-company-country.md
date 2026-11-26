# Company Country and Document Type — Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a company carry `country`, `documentType` and, outside Colombia, a free-text `cityName`, and make every consumer (quotation billing, PDF, orders export, POS) label the document by its type.

**Architecture:** A new `CompanyDocumentType` enum + `CompanyDocumentTypes.AllowedFor` in `Modules.Companies.Domain` is the single definition of D2. `Company` gets the Suppliers-style city lane (`CityId` for CO, `CityName` otherwise). The legacy no-country request is resolved in the handlers (POST → `CO`/`NIT`, PUT → stored values) **before** validation, so the validator always sees an effective country. Quotations and Pos learn the type only as a wire string through their existing ports.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit, Testcontainers, Typst PDF template.

**Spec:** `docs/superpowers/specs/2026-11-07-company-country-design.md` (frontend half: `qep-frontend/docs/superpowers/specs/2026-11-07-company-country-design.md`; frontend plan: `qep-frontend/docs/superpowers/plans/2026-11-07-company-country.md`).

## Global Constraints

- Deploy order: **this backend first**, then the frontend. Companies and Pos migrations ship in the same deploy (spec §9).
- Wire values, exactly: `NIT`, `CC`, `CE`, `PASAPORTE`, `NIF`, `NIE`, `TAX_ID` (spec §4.1). `AllowedFor`: `CO` → NIT, CC, CE, PASAPORTE · `ES` → NIF, NIE, PASAPORTE · other → TAX_ID, PASAPORTE.
- New error codes, exactly: `companies.company.country_invalid`, `companies.company.document_type_invalid`, `companies.company.document_type_not_allowed`, `companies.company.city_name_required`, `companies.company.city_name_too_long`. Kept: `companies.company.city_required`, `companies.company.city_not_found`.
- 422 field keys (PascalCase): `Country`, `DocumentType`, `CityId`, `CityName`.
- JSON fields (camelCase): request `country`, `documentType`, `cityId`, `cityName`; detail adds `country`, `documentType`, `cityName`, `city`/`department` nullable; list row adds `country`, `documentType` and keeps `city: string`; `QuotationBillingResponse.companyDocumentType`; POS `documentType` on `PosCompanyOption`, `PosCompanyHeader`, `PosIssuerResponse`.
- `Company.CountryLength = 2`, `Company.CityNameMaxLength = 120`, `Company.ColombiaCountryCode = "CO"`; `document_type varchar(16)`, `city_name varchar(120)`, `country varchar(2)`.
- `taxId` rules unchanged (1–32, no format, no check digit — D3). Taxes/IVA/withholding untouched (D8): nothing reads `country`/`documentType` to compute anything.
- TDD: RED before GREEN with literal output of both pasted into the task report.
- Before any `dotnet build/test/ef`: make sure `Api.exe` is not running (do not stop it if you did not launch it — ask).
- Integration tests: `$env:Seed__Enabled = "false"` first; one project at a time, `-- xUnit.MaxParallelThreads=4`, filtered to the touched class while iterating. Full integration run only in Task 9.
- Migrations: `dotnet ef migrations add <Name> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext -o Persistence/Migrations`.
- Commits on `develop` (check `git branch --show-current` right before each commit; stop if `main`), conventional commits, **no AI attribution, no Co-Authored-By**, always `git commit -m "…" -- <paths>`.
- Code comments in Spanish with tú (repo rule); fix voseo only in lines you touch.

## Review Focus

1. **Legacy PUT on a foreign company** (old frontend open in a tab during the deploy): must keep `ES`/`NIF` and answer 422 `errors.CityName`, never rewrite to `CO`/`NIT`. Pinned in Task 5 (`LegacyPutOnSpanishCompanyKeepsCountryAndFailsOnCityName`).
2. **Country switch with a stale value in the other lane** (`country: "US"` + leftover `cityId`): stray value dropped, not rejected, and no Geography lookup for non-CO. Pinned in Task 2 (`NonColombianCompanyDropsCityId`) and Task 5 (`PutColombiaToUsClearsCityIdInTheDatabase`).
3. **Lower-case / padded country** (`" es "`): normalized to `ES` everywhere — domain, `AllowedFor`, and the validator's effective country — so `NIF` is accepted. Pinned in Task 1 (`AllowedForIsCaseInsensitive`) and Task 4 (`PaddedLowerCaseCountryAcceptsNif`).
4. **Orders export with a hyphenated document**: kept as text cell, dots and spaces removed, including spaces around the hyphen. Pinned in Task 7.
5. **POS ticket reprint after the company changes its type**: frozen value wins. Pinned in Task 8 (`TicketKeepsDocumentTypeFrozenAtOpen`).

---

### Task 1: `CompanyDocumentType` and `AllowedFor`

**Files:**
- Create: `src/Modules/Companies/Modules.Companies.Domain/CompanyDocumentType.cs`
- Test: `tests/Modules/Companies/Modules.Companies.UnitTests/CompanyDocumentTypeTests.cs`

**Interfaces:**
- Produces: `enum CompanyDocumentType { Nit, Cc, Ce, Pasaporte, Nif, Nie, TaxId }`; `static class CompanyDocumentTypes` with `CompanyDocumentType Parse(string wire)` (throws `CompaniesDomainException("companies.company.document_type_invalid", …)`), `bool TryParse(string? wire, out CompanyDocumentType type)` (case-insensitive, trims), `string ToWire(this CompanyDocumentType type)`, `IReadOnlySet<CompanyDocumentType> AllowedFor(string country)` (trims + upper-cases its input).

- [ ] **Step 1: Write the failing tests** — `[Theory]` round-trip over the 7 pairs (`Parse(wire).ToWire() == wire`); `Parse("nif") == Nif`; `Parse("RUT")` throws with `Code == "companies.company.document_type_invalid"`; `AllowedFor("CO")` is exactly `{Nit, Cc, Ce, Pasaporte}`; `AllowedFor("ES")` exactly `{Nif, Nie, Pasaporte}`; `AllowedFor("US")` exactly `{TaxId, Pasaporte}`; `AllowedForIsCaseInsensitive`: `AllowedFor(" es ")` equals `AllowedFor("ES")`.
- [ ] **Step 2: Run, expect FAIL** (compile error: `CompanyDocumentType` not found).
  `dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --filter "FullyQualifiedName~CompanyDocumentTypeTests"`
- [ ] **Step 3: Implement** in the shape of `Suppliers.Domain/IdentificationType.cs` (dictionary with `OrdinalIgnoreCase`, unknown fails — never defaults). XML doc: `AllowedFor` is the single backend definition of D2; the wire values follow the upper-case Spanish catalogue; `TAX_ID` is the generic «Identificación tributaria».
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(companies): add company document type catalogue`.

### Task 2: `Company` aggregate — country, document type, city lane

**Files:**
- Modify: `src/Modules/Companies/Modules.Companies.Domain/Company.cs` (ctor `:27-54`, `TaxId` doc `:74-79`, `CityId` `:81-90`, `Create`/`Update` `:117-170`, `EnsureValidCityId` `:294-303`)
- Modify (compile only, interim): `src/Modules/Companies/Modules.Companies.Application/CreateCompany.cs:56-69`, `UpdateCompany.cs:61-72`, `CompanyMapping.cs:47`, `ListCompanies.cs:38,50`
- Modify: `src/Modules/Companies/Modules.Companies.Infrastructure/Seed/CompaniesSeeder.cs` (the `Company.Create` call)
- Modify: `tests/Modules/Companies/Modules.Companies.UnitTests/CompanyBankAccountTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs:796`
- Test: `tests/Modules/Companies/Modules.Companies.UnitTests/CompanyTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `Company.Create(CompanyId id, Guid tenantId, string name, IReadOnlyCollection<CompanyBankAccount> bankAccounts, string taxId, string country, CompanyDocumentType documentType, Guid? cityId, string? cityName, CompanyContactInfo contact, DateTimeOffset occurredAt)`; `Update(string name, IReadOnlyCollection<CompanyBankAccount> bankAccounts, string taxId, string country, CompanyDocumentType documentType, Guid? cityId, string? cityName, CompanyContactInfo contact, DateTimeOffset occurredAt)`; properties `string Country`, `CompanyDocumentType DocumentType`, `Guid? CityId`, `string? CityName`; constants `CountryLength`, `CityNameMaxLength`, `ColombiaCountryCode`.

- [ ] **Step 1: Write the failing tests** in `CompanyTests` (update the existing `Create`/`Update` helpers to the new signature with `"CO"`, `Nit`, a city id, `null`):
  - `ColombianCompanyRequiresCityId` → `city_required`; `ColombianCompanyDropsCityName` (`cityName: "Bogotá"` → `CityName == null`).
  - `NonColombianCompanyRequiresCityName` (`"US"`, `cityName: "  "`) → `city_name_required`; `CityNameLongerThan120IsRejected` → `city_name_too_long`; `NonColombianCompanyDropsCityId` (`"US"`, a Guid, `"Miami"`) → `CityId == null`, `CityName == "Miami"`.
  - `CountryIsNormalized` (`" es "` → `"ES"`); `[Theory] "Col"`, `"C1"`, `""` → `country_invalid`.
  - `NifWithColombiaIsNotAllowed`, `NitWithSpainIsNotAllowed` → `document_type_not_allowed`; `[Theory]` `Pasaporte` valid for `CO`, `ES`, `US`; `TaxId` valid for `US`, rejected for `CO` and `ES`.
  - `UpdateColombiaToUsClearsCityId`; `FailedUpdateLeavesTheAggregateUnchanged` (existing pattern: `Update` to `ES` + `Nit` throws, and `Country`, `DocumentType`, `CityId` keep their old values).
- [ ] **Step 2: Run, expect FAIL** (compile errors on the new signature).
  `dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --filter "FullyQualifiedName~CompanyTests"`
- [ ] **Step 3: Implement.** Order inside both methods (locals first, then assign — keep the `:150-155` comment): `NormalizeCountry` → `EnsureDocumentTypeAllowed(country, documentType)` → `EnsureValidLocation(country, cityId, cityName)` returning `(Guid? CityId, string? CityName)` — CO: non-empty `cityId` else `city_required`, `cityName` → null; other: trim, blank → `city_name_required`, `> 120` → `city_name_too_long`, `cityId` → null (stray value dropped, as `Supplier.cs:220-244`). Update the `TaxId` doc to «número del documento, cuyo tipo es `DocumentType`» and the `CityId` doc to «sólo para Colombia; nula en el extranjero». Private EF ctor sets `Country = string.Empty`.
  Interim compile fixes (replaced in Task 5): handlers pass `Company.ColombiaCountryCode`, `CompanyDocumentType.Nit`, `command.CityId`, `null`; `ToDtoAsync`/`ListCompanies` use `company.CityId!.Value`. Seeder passes `country: Company.ColombiaCountryCode`, `documentType: CompanyDocumentType.Nit`, the resolved `cityId`, `cityName: null` (final form, spec §6.4). `CompanyBankAccountTests` and `ShopifyAdaptersTests:796` pass the same CO/NIT values.
- [ ] **Step 4: Run, expect PASS**, then `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ShopifyAdaptersTests"` and `dotnet build` of the solution (0 errors).
- [ ] **Step 5: Commit** — `feat(companies): add country, document type and city lane to company`.

### Task 3: Persistence and migration `AddCompanyCountryAndDocumentType`

**Files:**
- Modify: `src/Modules/Companies/Modules.Companies.Infrastructure/Persistence/CompaniesDbContext.cs:19-75`
- Create: `src/Modules/Companies/Modules.Companies.Infrastructure/Persistence/Migrations/<timestamp>_AddCompanyCountryAndDocumentType.cs` (+ Designer, snapshot)
- Test: `tests/Modules/Companies/Modules.Companies.IntegrationTests/CompanyCountryMigrationTests.cs`

**Interfaces:**
- Consumes: Task 2 properties.
- Produces: columns `country`, `document_type`, `city_name`, nullable `city_id`; constraints `CK_companies_city_lane`, `CK_companies_document_type`.

- [ ] **Step 1: Write the failing test** (precedent `Modules.Quotations.IntegrationTests/QuotationSettingsMigrationTests.cs`): start the factory, `IMigrator.MigrateAsync("20260831004523_AddCompanyCity")`, insert a raw `companies.companies` row with a `city_id` from `SELECT id FROM geography.cities LIMIT 1`, migrate to the new migration → `country = 'CO'`, `document_type = 'NIT'`, `city_id` unchanged, `city_name IS NULL`. Second fact: on the latest schema a raw insert of a CO row with `city_name` set throws `PostgresException` with `ConstraintName == "CK_companies_city_lane"`. Both end with `MigrateAsync(null)`.
- [ ] **Step 2: Run, expect FAIL** (migration name not found).
  `$env:Seed__Enabled = "false"` then
  `dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests --filter "FullyQualifiedName~CompanyCountryMigrationTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Mapping: `Country` → `country`, `HasMaxLength(Company.CountryLength)`; `DocumentType` → `document_type`, `HasConversion(type => type.ToWire(), wire => CompanyDocumentTypes.Parse(wire))`, `HasMaxLength(16)`; `CityName` → `city_name`, `HasMaxLength(Company.CityNameMaxLength)`; both check constraints via `ToTable("companies", "companies", table => …)` with the exact SQL of spec §5.1. Generate the migration with the design-time factory, then hand-order `Up` as spec §5.2 (add `country`/`document_type` with defaults `'CO'`/`'NIT'` → `AlterColumn` dropping both defaults → add `city_name` → `city_id` nullable → constraints). `Down` drops constraints and columns and sets `city_id` NOT NULL; comment that it fails on purpose if a foreign company exists.
- [ ] **Step 4: Run, expect PASS.** Also run `CompanyApiTests` and `CompanyWriteApiTests` filtered — still green (handlers are interim CO/NIT).
- [ ] **Step 5: Commit** — `feat(companies): persist company country, document type and city name`.

### Task 4: Write rules and legacy resolution

**Files:**
- Modify: `src/Modules/Companies/Modules.Companies.Application/CompanyWriteRules.cs` (`ICompanyWriteCommand` `:16-31`, doc `:44-47`, rule `:63`)
- Modify: `CreateCompany.cs:8-16`, `UpdateCompany.cs:8-17` (command records)
- Create: `src/Modules/Companies/Modules.Companies.Application/CompanyLegacyWrite.cs`
- Test: `tests/Modules/Companies/Modules.Companies.UnitTests/CompanyWriteRulesTests.cs`

**Interfaces:**
- Consumes: Task 1, Task 2 constants.
- Produces: `ICompanyWriteCommand` gains `string? Country`, `string? DocumentType`, `Guid? CityId` (was `Guid`), `string? CityName`; both command records gain the same, in request order (`…, TaxId, Country, DocumentType, CityId, CityName, Phone, …`). `static class CompanyLegacyWrite`: `CreateCompanyCommand Resolve(CreateCompanyCommand command)` — `Country` null ⇒ `"CO"` and `DocumentType ?? "NIT"`; `UpdateCompanyCommand Resolve(UpdateCompanyCommand command, Company stored)` — `Country` null ⇒ `stored.Country` and `DocumentType ?? stored.DocumentType.ToWire()`. With `Country` present both return the command untouched.

- [ ] **Step 1: Write the failing tests** (validate with `new CreateCompanyValidator()` / `UpdateCompanyValidator()` after `CompanyLegacyWrite.Resolve`):
  - Keys: `"Col"` → `Country`; `"ES"` + `"RUT"` → `DocumentType`; `"ES"` + `"NIT"` → `DocumentType`; `"CO"` + no `CityId` → `CityId`; `"US"` + no `CityName` → `CityName`; `"US"` + 121 chars → `CityName`.
  - `CountryWithoutDocumentTypeFailsOnDocumentType`.
  - `LegacyCreateResolvesToColombiaAndNit` — no country, no type, a city → valid, resolved `Country == "CO"`, `DocumentType == "NIT"`.
  - `LegacyUpdateUsesStoredSpanishCountry` — stored `ES`/`NIF`/`cityName "Madrid"`, request without country sending only `CityId` → error key `CityName` (not valid as CO).
  - `PaddedLowerCaseCountryAcceptsNif` — `" es "` + `"NIF"` + `"Madrid"` → valid.
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --filter "FullyQualifiedName~CompanyWriteRulesTests"`
- [ ] **Step 3: Implement.** Replace `:63` with the Suppliers shape (`SupplierWriteRules.cs:73-95`): `Country` — when present, length 2 + `char.IsAsciiLetter`; `DocumentType` — `NotEmpty` when `Country` present; when present, `CompanyDocumentTypes.TryParse` (message «The document type is not recognized.») and, when the country is valid, in `AllowedFor(effective)` (message «The document type is not allowed for the country.»); `CityId` non-empty Guid when effective country is CO; `CityName` `NotEmpty` when not CO, `MaximumLength(Company.CityNameMaxLength)` when present. Effective country = `Country` trimmed + upper-cased, absent ⇒ CO (same idea as `IsColombian`, `SupplierWriteRules.cs:170-175`; after `Resolve` it is never absent). Update the doc at `:44-47` to list `Country, DocumentType, CityId, CityName`. Doc on `CompanyLegacyWrite`: temporary window for the old frontend (spec §9), removal is a follow-up.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat(companies): validate company country, document type and city lane`.

### Task 5: Contract, handlers, mapping and endpoints

**Files:**
- Modify: `src/Modules/Companies/Modules.Companies.Application/CompaniesDtos.cs:21-48,66-74,93-111`
- Modify: `CreateCompany.cs:34-87`, `UpdateCompany.cs:35-85`, `CompanyMapping.cs:12-52`, `ListCompanies.cs:38-53`
- Modify: `src/Modules/Companies/Modules.Companies.Api/CompanyEndpoints.cs:120-150,198-225`
- Test: `tests/Modules/Companies/Modules.Companies.IntegrationTests/CompanyWriteApiTests.cs`, `CompanyApiTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces: `CreateCompanyRequest`/`UpdateCompanyRequest(string Name, IReadOnlyList<CompanyBankAccountPayload> BankAccounts, string TaxId, string? Country, string? DocumentType, Guid? CityId, string? CityName, string? Phone, string? Email, string? Address)`; `CompanyDto`/`CompanyResponse` gain `string Country`, `string DocumentType`, `CompanyCityDto? City`, `CompanyDepartmentDto? Department`, `string? CityName`; `CompanyListItemResponse` gains `string Country`, `string DocumentType`; `CompanyMapping.ToDto(this Company company, CompanyCityRef? city)`.

- [ ] **Step 1: Write the failing tests** in `CompanyWriteApiTests` (raw JSON bodies):
  - `PostSpanishCompanyWithNif` → 201, body `country "ES"`, `documentType "NIF"`, `cityName "Madrid"`, `city` null, `department` null.
  - `PostColombiaWithoutCityId` → 422 `errors.CityId`; `PostSpainWithNit` → 422 `errors.DocumentType`; `PostUsWithoutCityName` → 422 `errors.CityName`.
  - `LegacyPostWithoutCountryCreatesColombiaNit` → 201, `CO`/`NIT`.
  - `LegacyPutOnColombianCompanyKeepsCountryAndType` → 200, `CO`/`NIT`.
  - `LegacyPutOnSpanishCompanyKeepsCountryAndFailsOnCityName` — create ES/NIF, PUT without country/type with only `cityId` → 422 `errors.CityName`; GET still `ES`/`NIF`.
  - `PutColombiaToUsClearsCityIdInTheDatabase` (read `city_id` raw → null); `PutUsToColombiaWithoutCityId` → 422 `errors.CityId`.
  In `CompanyApiTests`: `ListMixesColombianAndForeignCities` — `city` is the DIVIPOLA name and `"Madrid"`, `country`/`documentType` present on both; `ForeignCompanyDetailDoesNotFail` → 200.
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Companies/Modules.Companies.IntegrationTests --filter "FullyQualifiedName~CompanyWriteApiTests|FullyQualifiedName~CompanyApiTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.**
  - Create: authorize → `command = CompanyLegacyWrite.Resolve(command)` → validate → if effective country is CO, `FindCityAsync(command.CityId!.Value)` else `city_not_found` (unchanged) — outside CO no lookup → `Company.Create(…, command.Country!, CompanyDocumentTypes.Parse(command.DocumentType!), command.CityId, command.CityName, …)` → `ToDto(city)`.
  - Update (spec §6.3, orchestrator's call): authorize → `FindAsync` (404) → `Resolve(command, company)` → validate → CO-only city lookup → `Update` → `ToDto(city)`. Keep the «autorizar antes de validar» comment; add why loading moved before validating.
  - `ToDto`: `City`/`Department` null when `city` is null; `Country`, `DocumentType.ToWire()`, `CityName`. `ToDtoAsync`: look up only when `CityId` has a value; CO company whose city is missing stays `InvalidOperationException`. `ListCompanies`: batch only non-null `CityId`s; `ResolveCity` returns null when `CityId` is null.
  - Endpoints: map the new request fields into the commands; `ToListItem` `City = company.City?.Name ?? company.CityName ?? string.Empty`. DTO docs: list `City` is one column for both lanes so the grid never branches (BFF); `Country`/`DocumentType` let the grid label the document without the detail. Update the `:93-94` comment («CityId … es obligatorio») to the lane rule.
- [ ] **Step 4: Run, expect PASS**, plus the whole Companies unit project and `CompaniesSeedTests`.
- [ ] **Step 5: Commit** — `feat(companies): expose company country, document type and foreign city`.

### Task 6: Quotations — billing response and PDF label (C1–C3)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationCompanyLookup.cs:20-30`, `src/Bootstrapper/QuotationCompanyLookup.cs:24-34`
- Modify: `QuotationResponseComposer.cs:237-249`, `QuotationsDtos.cs:627-637`
- Create: `src/Modules/Quotations/Modules.Quotations.Application/CompanyDocumentLabels.cs`
- Modify: `QuotationPdfDocument.cs:125-133`, `QuotationPdfDocumentMapper.cs:210-222`, `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ:183-185`
- Modify (constructor call sites): `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTemplateTests.cs`, `OrderPdfDocumentMapperTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/PriceRoundingParityTests.cs` — whichever build `QuotationCompanyRef`/`QuotationBillingResponse`/`QuotationPdfBillingAccount`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfDocumentMapperTests.cs`, new `tests/Modules/Quotations/Modules.Quotations.UnitTests/CompanyDocumentLabelsTests.cs`

**Interfaces:**
- Consumes: `Company.DocumentType.ToWire()` (Task 1) in the Bootstrapper adapter.
- Produces: `QuotationCompanyRef(…, string TaxId, string DocumentType, bool IsActive, …)`; `QuotationBillingResponse(…, string? CompanyTaxId, string? CompanyDocumentType, string? CompanyAddress, …)`; `QuotationPdfBillingAccount(…, string? CompanyTaxId, string CompanyDocumentLabel, …)`; `static string CompanyDocumentLabels.For(string? wire)`.

- [ ] **Step 1: Write the failing tests** — `CompanyDocumentLabelsTests`: `[Theory]` `NIT→NIT`, `CC→CC`, `CE→CE`, `PASAPORTE→Pasaporte`, `NIF→NIF`, `NIE→NIE`, `TAX_ID→Identificación tributaria`, `null→NIT`, `"XYZ"→XYZ`. `QuotationPdfDocumentMapperTests`: billing account with `CompanyDocumentType "NIF"` → `CompanyDocumentLabel == "NIF"`; with `null` → `"NIT"`. A unit test on `QuotationResponseComposer.ToBillingResponse` (internals are visible to the unit project): company ref `DocumentType "NIF"` → `CompanyDocumentType == "NIF"`; deleted company (`null` ref) → `null`.
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~CompanyDocumentLabels|FullyQualifiedName~QuotationPdfDocumentMapperTests|FullyQualifiedName~OrderPdfDocumentMapperTests"`
- [ ] **Step 3: Implement.** Label table keyed on the wire string (Quotations does not reference Companies); comment that it must match the frontend `companyDocumentPrefix` (`qep-frontend/src/components/company-document-types.ts`). Typst: replace `NIT #data.billingAccount.companyTaxId` with `#data.billingAccount.companyDocumentLabel #data.billingAccount.companyTaxId`. Do **not** touch `quotation.typ:280` (billing party, out of scope) nor `OrdersExportColumnCatalog.cs:76` / `QuotationsSeeder.cs:153` (C7).
- [ ] **Step 4: Run, expect PASS**; then the Quotations integration tests that render a quotation/order detail and PDF, filtered (`QuotationPdf`, `OrderDetail`), to confirm `billingAccount.companyDocumentType` is `"NIT"` for the harness company.
- [ ] **Step 5: Commit** — `feat(quotations): label the billing company document by its type`.

### Task 7: Orders export keeps the hyphen (C4)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/OrdersExportProcessor.cs:538-549`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs:2068-2085`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/OrderExportApiTests.cs:236-240,709-713`

**Interfaces:** none new; `ErpTaxId(string?) → string` keeps its signature.

- [ ] **Step 1: Change the expectations first (RED).** Rename `NitEmpresaDropsDotsSpacesAndTheCheckDigit` → `NitEmpresaDropsDotsAndSpacesAndKeepsTheHyphen`: `"901851609-1"`, `"901.851.609-1"`, `" 901.851 609 - 1 "` → `ExportCell.OfText("901851609-1")`; separate fact `"901 851 609"` → `OfNumber(901851609m)`; new `"B-12345678"` → `OfText("B-12345678")`. Integration `:236-240`: the cell keeps hyphen and check digit, `NumericCells[1][37]` is false; `:709-713`: expected `CompanyTaxId.Replace(".", "").Replace(" ", "")`, `NumericCells[1][46]` false. Rewrite the comments that say «sin dígito de verificación».
- [ ] **Step 2: Run, expect FAIL** (old code returns `OfNumber(901851609)`).
  `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrdersExportProcessorTests"`
- [ ] **Step 3: Implement.** `ErpTaxId` stops cutting at the first `-`; it keeps removing dots and all whitespace. No branch on document type. Comment at `:538`: «sin puntos ni espacios; el guion y el dígito de verificación se conservan» (owner D11).
- [ ] **Step 4: Run, expect PASS** (unit), then `OrderExportApiTests` filtered with `-- xUnit.MaxParallelThreads=4`.
- [ ] **Step 5: Commit** — `fix(quotations): keep the document hyphen in the orders export`.

### Task 8: POS freezes the document type (C5–C6)

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Application/IPosCompanyLookup.cs:12`, `src/Bootstrapper/PosCompanyLookup.cs:19-20`
- Modify: `src/Modules/Pos/Modules.Pos.Domain/PosCompanySnapshot.cs:7-8`, `CashSession.cs:16,32,107`
- Modify: `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContext.cs:52`
- Create: `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/Migrations/<timestamp>_AddCashSessionCompanyDocumentType.cs`
- Modify: `src/Modules/Pos/Modules.Pos.Application/OpenCashSession.cs:65`, `PosDtos.cs:10,12,177`, `GetRegisterContext.cs:33`, `PosSessionMapping.cs:24,40`, `PosSaleResponses.cs:39`
- Modify (constructor call sites): `tests/Modules/Pos/Modules.Pos.UnitTests/PosTestDoubles.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosPersistenceTests.cs`
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/CashSessionTests.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosRegisterApiTests.cs`, `PosSaleApiTests.cs`, new `tests/Modules/Pos/Modules.Pos.IntegrationTests/CashSessionDocumentTypeMigrationTests.cs`

**Interfaces:**
- Consumes: `Company.DocumentType.ToWire()`.
- Produces: `PosCompanyRef(Guid Id, string Name, string TaxId, string DocumentType, string? Address, string? Phone, bool IsActive)`; `PosCompanySnapshot(Guid CompanyId, string Name, string TaxId, string DocumentType, string? Address, string? Phone)`; `CashSession.CompanyDocumentType`; `PosCompanyOption(Guid Id, string Name, string TaxId, string DocumentType)`; `PosCompanyHeader(string Name, string TaxId, string DocumentType)`; `PosIssuerResponse(string Name, string TaxId, string DocumentType, string? Address, string? Phone)`.

- [ ] **Step 1: Write the failing tests** — `CashSessionTests`: `Open` with a snapshot `DocumentType "NIF"` → `CompanyDocumentType == "NIF"`. `PosRegisterApiTests`: register context lists companies with `documentType "NIT"`; open session → `company.documentType "NIT"`. `PosSaleApiTests.TicketKeepsDocumentTypeFrozenAtOpen`: open a session, PUT the company to a CO `CC`, create a sale → `issuer.documentType == "NIT"`. Migration test (precedent `SaleLinesVariantMigrationTests`): migrate to `20261027184352_AddSalePriceRounding`, insert a raw `pos.cash_sessions` row, migrate up → `company_document_type = 'NIT'`; end with `MigrateAsync(null)`.
- [ ] **Step 2: Run, expect FAIL.**
  `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~CashSessionTests"`; then `dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosRegisterApiTests|FullyQualifiedName~PosSaleApiTests|FullyQualifiedName~CashSessionDocumentTypeMigrationTests" -- xUnit.MaxParallelThreads=4`
- [ ] **Step 3: Implement.** Column `company_document_type`, `HasMaxLength(16)`, NOT NULL. Migration: add with default `'NIT'` as the backfill, then drop the default like the Companies migration. Snapshot doc (`PosCompanySnapshot.cs:3-6`): the type is frozen with the number because the reprint must say the same thing.
- [ ] **Step 4: Run, expect PASS**, plus `dotnet test tests/Modules/Pos/Modules.Pos.UnitTests`.
- [ ] **Step 5: Commit** — `feat(pos): freeze the company document type in the cash session`.

### Task 9: Raw-body sweep, full verification and state

**Files:**
- Check (fix only if red): `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs`, `PosIsolationApiTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationsApiHarness.cs`, `tests/Modules/Reporting/Modules.Reporting.IntegrationTests/ReportingApiHarness.cs`, `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyApiHarness.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/StoreApiHarness.cs`, `StoreSettingsApiTests.cs`
- Modify: `docs/estado.md` (row + Registro)

- [ ] **Step 1: Read each harness's company POST body** and confirm it is the legacy shape (no `country`) that `CompanyLegacyWrite` resolves to `CO`/`NIT`; note any body that sends `cityId` as `null`.
- [ ] **Step 2: Architecture tests** — `dotnet test tests/ArchitectureTests/ArchitectureTests` → PASS (`CompaniesLayerTests`, `PosLayerTests` unchanged).
- [ ] **Step 3: Full integration run, once, one project at a time** with `-- xUnit.MaxParallelThreads=4`: Companies, Pos, Quotations, Reporting, Shopify, Store, then the rest. Expected: 0 failed. A red test that passed before this branch is a regression of this work; fix it in the owning task's files and commit as `fix(…)`.
- [ ] **Step 4: Update `docs/estado.md`** — Companies row: country/document type built; follow-up «quitar el fallback sin `country` cuando el frontend nuevo esté en producción» (spec §9); deploy note «backend antes que frontend; migraciones de Companies y Pos en el mismo despliegue»; behavior change of the orders export (hyphen kept, text cell) for every CO tenant.
- [ ] **Step 5: Commit** — `docs: record company country and document type in project state` with `-- docs/estado.md`.
