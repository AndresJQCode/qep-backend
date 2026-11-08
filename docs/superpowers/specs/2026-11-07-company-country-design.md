# Company country and document type — backend design

Date: 2026-11-07. Cross-repo work: this spec covers `qep-backend`; the frontend half is
`qep-frontend/docs/superpowers/specs/2026-11-07-company-country-design.md`. Both ship together, backend first (§9).

Status: design, not built. No code, migration or test exists yet for anything below.

## 1. Problem

Tenants outside Colombia must be able to register their billing companies (Companies module, «empresas»).
Today they cannot: `Company.CityId` is a required `Guid` (`Company.cs:90`), checked non-empty by the domain
(`Company.cs:298-303`) and by the validator (`CompanyWriteRules.cs:63`), resolved against the Colombian DIVIPOLA
catalogue before every write (`CreateCompany.cs:50-53`, `UpdateCompany.cs:50-53`), and enforced by a
non-null FK to `geography.cities` (`20260831004523_AddCompanyCity.cs:26-60`). The document is also implicitly a NIT:
`TaxId` has no type (`Company.cs:74-79`) and every printed consumer labels it «NIT».

### Success criteria

1. A company can be created and edited with any country in the frontend's existing `COUNTRIES` catalogue
   (`qep-frontend/src/features/customers/utils/countries.ts`: the Americas plus ES, PT, FR, IT). The backend
   validates only the ISO-3166-1 alpha-2 shape (§4.2), not membership in that list.
2. Colombian companies keep working exactly as today (department + DIVIPOLA city).
3. Every company carries a document type, valid for its country.
4. Nothing that prints or exports a company's document calls a Spanish NIF a «NIT», and nothing that shows a
   company's city breaks on a company without `cityId`.
5. Existing rows become `CO` + `NIT` with their city untouched.

## 2. Decisions (owner)

| # | Decision |
| --- | --- |
| D1 | Company gets **`country`**, ISO-3166-1 alpha-2, **required**. |
| D2 | Company gets **`documentType`** (new). The allowed set depends on the country: **CO** → NIT, Cédula de ciudadanía, Cédula de extranjería, Pasaporte. **ES** → NIF, NIE, Pasaporte. **Any other country** → Identificación tributaria (generic), Pasaporte. |
| D3 | The document number **is the existing `taxId`**, still only length-validated (1–32). No per-country format or check digit. |
| D4 | City: with `CO`, DIVIPOLA `cityId` as today; with any other country, free-text `cityName` and `cityId` null. |
| D5 | In the form, changing country clears document type and city (frontend spec). |
| D6 | Existing companies migrate to `CO` + `NIT`. |
| D7 | Non-goals: per-country format/check-digit validation; document-type lists for countries other than CO and ES. |
| D8 | **Taxes and withholding are out of scope, permanently for this work, and not a follow-up.** Tax, IVA and retention calculation stays exactly as today regardless of the company's country. |
| D9 | The country list stays as it is (`COUNTRIES`: the Americas plus ES, PT, FR, IT). Moving `countries.ts` is a separate frontend follow-up. |
| D10 | The printed/display label for `TAX_ID` is «Identificación tributaria». |
| D11 | The ERP accepts the hyphen: the orders export stops cutting the company document at the first `-`, for every type, NIT included (§7, C4). |

## 3. Precedents reused

- **Suppliers** is the model for the country/city lane:
  - `Supplier.ColombiaCountryCode = "CO"` (`Suppliers.Domain/Supplier.cs:39`), `CityId: Guid?` and `CityName: string?`
    (`Supplier.cs:66-69`), `CityNameMaxLength = 120` (`Supplier.cs:25`).
  - Lane invariant `EnsureValidLocation` (`Supplier.cs:225-244`): CO → `cityId` required, `cityName` dropped; other →
    `cityName` required (`suppliers.supplier.city_name_required`), `cityId` dropped.
  - Country normalization (trim, 2 ASCII letters, upper-case) with `suppliers.supplier.country_invalid`
    (`SupplierContactInfo.cs:46-56`).
  - Validator lane rules on `CityId` / `CityName` keyed by country (`SupplierWriteRules.cs:73-95`, `IsColombian`
    at `:170-175`, which treats an absent country as Colombia for choosing which city to report).
  - Mapping that resolves the DIVIPOLA city only when the country is CO (`SupplierMapping.cs:84-91`), and the response
    shape `Country`, `City?`, `Department?`, `CityName?` (`SuppliersDtos.cs:30-45`).
- **Customers** for the DB-level lane check: `CK_customer_addresses_city_lane`
  (`CustomersDbContext.cs:274-277`).
- **Document type wire values.** Every document-type catalogue in the backend travels as upper-case Spanish codes:
  `NIT`, `CC`, `CE`, `PASAPORTE` (`Suppliers.Domain/IdentificationType.cs:19-36`,
  `Customers.Domain/IdentificationType.cs:27-39`, `Quotations.Domain/PartyFiscalTypes.cs:42-62`,
  `Pos.Application/IPosCustomerLookup.cs:54`, `Store.Domain/StoreRequestBilling.cs:13`,
  `Shopify.Application/ShopifyDocumentTypes.cs:12`). Companies follows that convention rather than PascalCase
  enum names (§5.1).
- **Backward-compatible nullable request field:** `UpdateTenantSettings` treats a null `Country` as «keep the stored
  value» during the deployment window (`Tenancy.Application/UpdateTenantSettings.cs:18-21,91`).

## 4. Domain (`Modules.Companies.Domain`)

### 4.1 `CompanyDocumentType` (new)

New enum + parser, copied in shape from `Suppliers.Domain/IdentificationType.cs` (a module does not reference
another module's domain):

| Enum member | Wire value | Allowed for |
| --- | --- | --- |
| `Nit` | `NIT` | CO |
| `Cc` | `CC` | CO |
| `Ce` | `CE` | CO |
| `Pasaporte` | `PASAPORTE` | every country |
| `Nif` | `NIF` | ES |
| `Nie` | `NIE` | ES |
| `TaxId` | `TAX_ID` | every country except CO and ES |

*Builder's call:* wire values follow the existing upper-case Spanish catalogue (`NIT`, `CC`, `CE`, `PASAPORTE`) and
add `NIF`, `NIE`, `TAX_ID`. `PASAPORTE` is one value shared by all countries, not one per country. `TAX_ID` is the
generic «Identificación tributaria»; it is English because there is no single Spanish acronym for it, and it stays
`TAX_ID` (not `TAXID`) for readability.

`CompanyDocumentTypes.AllowedFor(string country)` returns the set for a normalized country (CO → 4, ES → 3,
other → 2). It is the **single** backend definition of D2; the validator and the domain both call it.

Parse failure → `companies.company.document_type_invalid` (new). A valid type outside the country's set →
`companies.company.document_type_not_allowed` (new).

### 4.2 `Company` changes (`Company.cs`)

- New constants: `CountryLength = 2`, `CityNameMaxLength = 120` (same width as Suppliers/Customers),
  `ColombiaCountryCode = "CO"`.
- New properties: `string Country`, `CompanyDocumentType DocumentType`, `string? CityName`.
- `CityId` becomes `Guid?` (`Company.cs:90`). XML doc updated: DIVIPOLA city, **only for Colombia**.
- `TaxId` keeps name, width and rules (D3). Its XML doc (`Company.cs:74-79`) changes from «NIT» to «document number,
  whose kind is `DocumentType`».
- `Create(...)` and `Update(...)` (`Company.cs:117-170`) take `country`, `documentType`, `cityId: Guid?`,
  `cityName: string?` in place of `cityId: Guid`. Normalization order stays «everything to locals, then assign»
  (`Company.cs:150-160`):
  1. `NormalizeCountry` — trim, exactly 2 ASCII letters, upper-case; else `companies.company.country_invalid` (new).
  2. `EnsureDocumentTypeAllowed(country, documentType)` — `companies.company.document_type_not_allowed`.
  3. `EnsureValidLocation(country, cityId, cityName)` — replaces `EnsureValidCityId` (`Company.cs:298-303`):
     - CO: `cityId` non-empty else `companies.company.city_required` (existing code, kept); `cityName` → null.
     - other: `cityName` trimmed, non-blank else `companies.company.city_name_required` (new), ≤ 120 else
       `companies.company.city_name_too_long` (new); `cityId` → null.
     A stray value in the lane that does not apply is **dropped, not rejected** — same as Suppliers
     (`Supplier.cs:220-224`): the form may leave a stale value when the user switches country.
- `CompaniesDomainException` stays `(code, message)`; field mapping comes from the validator (§6.2).

### 4.3 Error codes (summary)

| Code | New? | When |
| --- | --- | --- |
| `companies.company.country_invalid` | new | country not 2 ASCII letters |
| `companies.company.document_type_invalid` | new | unknown wire value |
| `companies.company.document_type_not_allowed` | new | type not in `AllowedFor(country)` |
| `companies.company.city_required` | existing (`Company.cs:301`) | CO without `cityId` |
| `companies.company.city_not_found` | existing (`CreateCompany.cs:52`) | CO `cityId` not in Geography |
| `companies.company.city_name_required` | new | non-CO without `cityName` |
| `companies.company.city_name_too_long` | new | `cityName` > 120 |

Naming follows the module's `companies.company.<snake_case>` pattern (`Company.cs:189,244,289`).

## 5. Persistence (`Modules.Companies.Infrastructure`)

### 5.1 Model (`CompaniesDbContext.ConfigureCompany`, `CompaniesDbContext.cs:19-75`)

| Column | Type | Notes |
| --- | --- | --- |
| `country` | `varchar(2)` NOT NULL | |
| `document_type` | `varchar(16)` NOT NULL | stored as the wire value via `HasConversion` (`ToWire` / `Parse`) |
| `city_id` | `uuid` **NULL** (was NOT NULL) | FK `FK_companies_cities_city_id` and `IX_companies_city` stay |
| `city_name` | `varchar(120)` NULL | |

Check constraints (*Builder's call*, mirroring `CK_customer_addresses_city_lane`):

- `CK_companies_city_lane`: `(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)`.
- `CK_companies_document_type`: `document_type IN ('NIT','CC','CE','PASAPORTE','NIF','NIE','TAX_ID')`.

The per-country subset is **not** a DB constraint: it is a domain rule that will grow by country, and a constraint
would need a migration for every change.

### 5.2 Migration `AddCompanyCountryAndDocumentType`

Generated with the design-time factory (backend `CLAUDE.md`, «Las migraciones se generan con el factory de diseño»).
`Up`, in order:

1. Add `country varchar(2) NOT NULL DEFAULT 'CO'` and `document_type varchar(16) NOT NULL DEFAULT 'NIT'` — the
   defaults are the backfill (D6).
2. Drop both defaults (`AlterColumn` without default) so a new row must set them explicitly.
3. Add `city_name varchar(120) NULL`.
4. `city_id` → nullable. The FK added by hand in `AddCompanyCity` is untouched (a nullable FK is valid).
5. Add both check constraints. Every existing row is `CO` + non-null `city_id` + null `city_name`, so they pass.

`Down`: drop constraints and the three columns, `city_id` back to NOT NULL. It **fails** if any non-CO company
exists (null `city_id`). *Builder's call:* leave it failing loudly rather than inventing a city; write that in the
migration comment.

## 6. Application (`Modules.Companies.Application`)

### 6.1 Contract (`CompaniesDtos.cs`)

Request — `CreateCompanyRequest` / `UpdateCompanyRequest` (`CompaniesDtos.cs:95-111`) and both commands
(`CreateCompany.cs:8-16`, `UpdateCompany.cs:8-17`), plus `ICompanyWriteCommand` (`CompanyWriteRules.cs:16-31`):

```text
name, bankAccounts, taxId,                      (unchanged)
country: string?        NEW — ISO-3166-1 alpha-2
documentType: string?   NEW — NIT | CC | CE | PASAPORTE | NIF | NIE | TAX_ID
cityId: Guid?           was Guid (required)
cityName: string?       NEW
phone, email, address                            (unchanged)
```

`country` and `documentType` are nullable **on the wire only** for the deployment window (§9); the domain always
receives a value.

Response — `CompanyDto` / `CompanyResponse` (`CompaniesDtos.cs:21-48`):

```text
... taxId,
country: string                 NEW
documentType: string            NEW (wire value)
city: CompanyCityDto?           was non-null; null outside Colombia
department: CompanyDepartmentDto?  was non-null; null outside Colombia
cityName: string?               NEW; null in Colombia
...
```

List row — `CompanyListItemResponse` (`CompaniesDtos.cs:66-74`):

- `city: string` keeps its name and type: the DIVIPOLA name in Colombia, `cityName` elsewhere. The grid paints one
  column and must not branch (BFF; write the reason in the DTO doc).
- `country: string` (new) and `documentType: string` (new), so the grid can label the document and show the
  country next to a foreign city without fetching the detail. *Builder's call.*

### 6.2 Validation (`CompanyWriteRules.cs`)

Replace `RuleFor(command => command.CityId).NotEmpty()` (`:63`) with, in the Suppliers shape
(`SupplierWriteRules.cs:73-95`):

- `Country`: when present, length 2 + ASCII letters. Absent is allowed during the window (§9).
- `DocumentType`: `NotEmpty` **when `Country` is present**; when present, parses (`document_type_invalid` message)
  and is in `AllowedFor(effective country)` (message for `document_type_not_allowed`). The error key is
  `DocumentType`, so the form marks the select.
- `CityId`: required (non-empty Guid) when the effective country is CO.
- `CityName`: `NotEmpty` when the effective country is not CO; `MaximumLength(120)` when present.

«Effective country» = `Country` trimmed/upper-cased when present. When absent (legacy client, §9): on **POST** it is
`CO` (with `DocumentType` defaulting to `NIT`); on **PUT** it is the **stored** country of the company, and the stored
document type is kept too. To make that possible the PUT validator receives the effective country and document type
from the handler (a validation context value or a resolved command), never a hard-coded `CO`. Choosing the city lane
from an absent country is the same idea as `IsColombian` (`SupplierWriteRules.cs:170-175`). Error keys travel PascalCase (`Country`, `DocumentType`, `CityId`, `CityName`);
update the doc comment at `CompanyWriteRules.cs:44-47` that lists them.

### 6.3 Handlers

- `UpdateCompanyHandler` changes order: authorize → **load the company** (`repository.FindAsync`, 404 if missing) →
  validate with the effective country/type (stored values when the request omits `country`) → resolve city → update.
  Today it validates before loading (`UpdateCompany.cs:40-48`); authorization still runs first, so a caller from
  another tenant still gets 403 before anything else (reason at `CreateCompany.cs:38-42`). *Orchestrator's call* (§9).
- `CreateCompanyHandler` / `UpdateCompanyHandler`: with the effective country and document type resolved, call `geographyLookup.FindCityAsync` **only when the country is CO** (today unconditional:
  `CreateCompany.cs:50-53`, `UpdateCompany.cs:50-53`). Outside CO there is no city
  to resolve and the DTO gets `city: null`.
- `CompanyMapping.ToDto(company, CompanyCityRef? city)` (`CompanyMapping.cs:9-27`): `City`/`Department` null when
  `city` is null; adds `Country`, `DocumentType.ToWire()`, `CityName`.
- `CompanyMapping.ToDtoAsync` (`CompanyMapping.cs:39-49`): resolves only when `company.CityId` has a value; a CO company
  whose city is missing is still a 500 (data corruption), as today.
- `ListCompaniesHandler` (`ListCompanies.cs:38-53`): batch-resolve only the non-null `CityId`s; `ResolveCity` returns
  null for a non-CO company instead of throwing.
- `CompanyEndpoints.ToResponse` / `ToListItem` (`CompanyEndpoints.cs:198-225`): new fields; list `City` =
  `company.City?.Name ?? company.CityName ?? string.Empty` (`:223` today reads `company.City.Name`).

### 6.4 Seeder

`CompaniesSeeder` (`Seed/CompaniesSeeder.cs:90-138`) creates companies through `Company.Create`; it passes
`country: "CO"`, `documentType: Nit`, the resolved `cityId` and `cityName: null`. The owner's seed rows are Colombian NITs.

## 7. Consumers outside Companies

Verified by reading every adapter that touches `ICompanyRepository` (`src/Bootstrapper/*Company*`, `*BillingAccounts*`,
`ShopifyStoreReferences.cs`). **No consumer outside Companies reads a company's city or department**: neither
`QuotationCompanyRef` (`Quotations.Application/IQuotationCompanyLookup.cs:20-30`) nor `PosCompanyRef`
(`Pos.Application/IPosCompanyLookup.cs:12`) carries one. What does break is the **document label**, and one ERP
transform that drops the NIT check digit.

| # | Where | Today | Change |
| --- | --- | --- | --- |
| C1 | `Bootstrapper/QuotationCompanyLookup.cs:24-34` → `QuotationCompanyRef` | name, `TaxId`, address, phone, accounts | add `DocumentType` (wire string) to the ref |
| C2 | `QuotationResponseComposer.ToBillingResponse` (`QuotationResponseComposer.cs:237-249`) → `QuotationBillingResponse` (`QuotationsDtos.cs:627-637`) | `CompanyTaxId` only | add `CompanyDocumentType: string?` (null when the company was deleted, like `CompanyTaxId`). Used by quotations and orders (`OrderResponseComposer.cs:51`) and preview. |
| C3 | Quotation/order PDF: `quotation.typ:183-185` | prints `NIT #companyTaxId` hard-coded | print `<label> <number>`. `QuotationPdfBillingAccount` (`QuotationPdfDocument.cs:125-133`) gains `CompanyDocumentLabel`, filled by `QuotationPdfDocumentMapper.BillingAccountFor` (`QuotationPdfDocumentMapper.cs:210-222`) from a label table (below); null type (old/deleted) → `NIT`. Same template renders orders (`OrderPdfProvider.cs:9`). |
| C4 | Orders export: `OrdersExportProcessor.ErpTaxId` (`OrdersExportProcessor.cs:539-549`), used at `:235` | strips everything from the first `-` and all dots/spaces — the NIT check-digit convention (comment at `OrdersExportProcessor.cs:538`: «sin dígito de verificación, puntos ni espacios») | **Owner decision: the ERP accepts the hyphen.** Stop cutting at the first `-` for **every** document type, NIT included: `900.123.456-7` → `900123456-7`, NIF `B-12345678` → `B-12345678`. *Orchestrator's call:* keep removing dots and whitespace. Update the comment to «sin puntos ni espacios; el guion y el dígito de verificación se conservan». No branch on document type is needed. **Behavior change for every existing CO tenant export** (owner confirmed the ERP accepts it). Side effect: a value with a hyphen is no longer a canonical number, so `OrdersExportLayoutProjection.FixedCellFor` (`OrdersExportLayoutProjection.cs:78-81`) writes it as **text**, not a number. |
| C5 | POS cash session snapshot: `PosCompanyRef` (`IPosCompanyLookup.cs:12`), `PosCompanySnapshot` (`Pos.Domain/PosCompanySnapshot.cs:7-8`), `CashSession.CompanyTaxId` (`CashSession.cs:32,107`), `company_tax_id` column (`PosDbContext.cs:52`) | tax id frozen at session open, no type | add `DocumentType` to `PosCompanyRef` (`Bootstrapper/PosCompanyLookup.cs:19-20`), to the snapshot (`OpenCashSession.cs:65`) and a `CompanyDocumentType` column `company_document_type varchar(16) NOT NULL` on the cash session, Pos migration backfilling `'NIT'`. Frozen with the number: the ticket reprint must say the same thing (`PosCompanySnapshot.cs:3-6`). |
| C6 | POS responses: `PosCompanyOption` (`PosDtos.cs:10`, built at `GetRegisterContext.cs:33` and `PosSessionMapping.cs:24`), `PosCompanyHeader` (`PosDtos.cs:12`, `PosSessionMapping.cs:40`), `PosIssuerResponse` (`PosDtos.cs:177`, `PosSaleResponses.cs:39`) | `TaxId` only | add `DocumentType` (wire string) to the three. |
| C7 | `OrdersExportColumnCatalog.cs:76` (`"NIT Empresa"`), seeded label `"Nit"` (`QuotationsSeeder.cs:153`) | column header | **no change** — it is a per-tenant layout label, not data. Noted so nobody «fixes» it here. |
| — | `ShopifyBillingAccounts.cs`, `StoreBillingAccounts.cs`, `ShopifyStoreReferences.cs` | name and accounts only | no change |

**PDF label table** (*Builder's call*, short form for print; the frontend holds its own dictionary for screens):
`NIT`→`NIT`, `CC`→`CC`, `CE`→`CE`, `PASAPORTE`→`Pasaporte`, `NIF`→`NIF`, `NIE`→`NIE`,
`TAX_ID`→`Identificación tributaria` (owner, D10).
It lives in Quotations Application next to the PDF mapper; Quotations does not reference Companies, so it keys on
the wire string, and an unknown string prints as-is.

Out of scope, observed: `quotation.typ:280` prints `NIT` for the **billing party** (a customer, whose document may be a
CC). Not a company field; not touched here.

## 8. Architecture

No new module reference. `CompaniesLayerTests` stays green unchanged. Quotations and Pos learn the document type only as
a string through their existing ports.

## 9. Deployment and backward compatibility

Order: **backend first**, then frontend (frontend spec §9).

- **Old frontend → new backend.** The old form sends no `country`/`documentType`, and always a DIVIPOLA `cityId`.
  - **POST** without `country` ⇒ `CO` + `NIT`. *Builder's call.*
  - **PUT** without `country` ⇒ **keeps the stored country and document type**; it never forces `CO` + `NIT` on an
    update. The handler loads the company before validating (§6.3). Same precedent as `UpdateTenantSettings`, where a
    null `Country` keeps the stored value (`UpdateTenantSettings.cs:18-21,91`). *Orchestrator's call.* A legacy PUT on
    a foreign company therefore validates against the foreign lane and fails on `CityName` (the old form sends only
    `cityId`); that is correct, because the old form cannot represent that company.
  - With `country` present, `documentType` is required.
- **Old frontend reading a non-CO company** (`city: null`) would fail in `$companyId.tsx:67-68` of the old frontend.
  Such a company can only be created by the new frontend, so the window is the gap between the two deploys; accepted.
- **New frontend → old backend**: frontend spec §9.
- **Removing the fallback.** *Builder's call (orchestrator):* the no-country fallback is temporary. Follow-up after the
  new frontend is in production: make `country` and `documentType` non-nullable in the request and drop both legacy
  branches. Not part of this work.
- Pos migration and Companies migration ship in the same deploy; both are additive with backfill.

## 10. Testing (TDD: RED before GREEN, literal evidence of both)

Run per project, one at a time, `-- xUnit.MaxParallelThreads=4` (workspace `CLAUDE.md`, Testcontainers).

### 10.1 Unit (`tests/Modules/Companies/Modules.Companies.UnitTests`)

- `CompanyDocumentTypeTests` (new): parse/`ToWire` round-trip for the 7 values; unknown → `document_type_invalid`;
  `AllowedFor` for `CO`, `ES`, `US`, lower-case input.
- `CompanyTests`: CO requires `cityId` and drops `cityName`; non-CO requires `cityName`, drops `cityId`; `cityName`
  > 120 rejected; country normalized to upper-case, `"Col"`/`"C1"` rejected; `NIF` with CO and `NIT` with ES →
  `document_type_not_allowed`; `PASAPORTE` valid everywhere; `TAX_ID` valid for `US`, invalid for CO/ES;
  `Update` CO→US clears `CityId`; a failed `Update` leaves the aggregate unchanged (existing pattern).
- `CompanyWriteRulesTests`: error keys `Country`, `DocumentType`, `CityId`, `CityName` for each failure; on create, absent
  country + absent type passes as CO/NIT; on update with stored `ES`/`NIF`, absent country validates against ES, not
  CO; present country + absent type fails on `DocumentType`.

### 10.2 Integration (`Modules.Companies.IntegrationTests`, Testcontainers)

- `CompanyWriteApiTests`: POST ES + `NIF` + `cityName` → 201, body has `country`, `documentType`, `cityName`,
  `city: null`, `department: null`; POST CO without `cityId` → 422 `errors.CityId`; POST ES + `NIT` → 422
  `errors.DocumentType`; POST US without `cityName` → 422 `errors.CityName`; legacy POST body (no country/type) → 201 CO/NIT;
  legacy PUT (no country/type) on a CO company → 200 and country/type unchanged; legacy PUT on an ES/NIF company keeps
  `ES`/`NIF` (never rewritten to CO/NIT) and, sending only `cityId`, answers 422 `errors.CityName`;
  PUT CO→US → `city_id` null in the DB; PUT US→CO without `cityId` → 422.
- `CompanyApiTests`: list mixes a CO and a foreign company — `city` is the DIVIPOLA name and the typed name
  respectively, `country`/`documentType` present; detail of a foreign company does not 500.
- Migration test (new, precedent `QuotationSettingsMigrationTests`): migrate to `AddCompanyCity`, insert a row, migrate
  to latest → `country = 'CO'`, `document_type = 'NIT'`, `city_id` unchanged; finish with `MigrateAsync(null)` before
  calling the API (backend `CLAUDE.md`). A raw insert of a CO row with `city_name` set violates `CK_companies_city_lane`.
- **Raw-body sweep** (backend `CLAUDE.md`, «Endurecer una regla de dominio…»): harnesses that post companies —
  `PosApiHarness`, `PosIsolationApiTests`, `QuotationsApiHarness`, `ReportingApiHarness`, `ShopifyApiHarness`,
  `StoreApiHarness`, `StoreSettingsApiTests`. The legacy fallback keeps them green; still check each one. Direct
  `Company.Create` callers — `CompanyTests`, `CompanyBankAccountTests`, `Bootstrapper.UnitTests/ShopifyAdaptersTests.cs:796`
  — must be updated for the new signature.

### 10.3 Consumers

- Quotations: `QuotationBillingResponse.companyDocumentType` on quotation and order detail; PDF mapper label per type
  and `NIT` fallback for null.
- Orders export (C4), **RED first**: change the existing expectations before touching `ErpTaxId`, watch them fail,
  then fix. They assert the old behavior today:
  - `Modules.Quotations.UnitTests/OrdersExportProcessorTests.cs:2068-2085`
    (`NitEmpresaDropsDotsSpacesAndTheCheckDigit`, expects `OfNumber(901851609)`): rename it and expect
    `OfText("901851609-1")` for `901851609-1`, `901.851.609-1` and ` 901.851 609 - 1 ` (spaces around the hyphen are
    removed), and `OfNumber(901851609)` for `901 851 609`; add `B-12345678` → `OfText("B-12345678")`.
  - `Modules.Quotations.IntegrationTests/OrderExportApiTests.cs:236-240` (regex `^901\d{6}$` + numeric cell) and
    `:709-713` (`Split('-')[0]` + numeric cell): expect the hyphen and check digit kept, and a text cell.
  - Update the comments in those tests that say «sin dígito de verificación».
- Pos: opening a session freezes `CompanyDocumentType`; ticket/closing/register responses carry `documentType`;
  editing the company after opening does not change the ticket; Pos migration backfills `'NIT'`.

## 11. Out of scope

- **Taxes and withholding (D8), permanently for this work and not a follow-up.** Tax, IVA and retention calculation
  stays exactly as today regardless of the company's country; nothing in this work reads `country` or `documentType`
  to compute taxes.
- Per-country format or check digit (D7). `taxId` uniqueness stays undecided, as today (`Company.cs:74-78`).
- Document-type lists beyond CO and ES (D7): new countries get `TAX_ID` + `PASAPORTE` until a list is added to
  `CompanyDocumentTypes.AllowedFor`.
- Widening the country catalogue (D9); moving `countries.ts` is a separate frontend follow-up.
- A world geography module; foreign cities are free text.
- The billing-party `NIT` label in `quotation.typ:280`.

## 12. Builder's and orchestrator's calls (summary)

1. Wire values `NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID` (§4.1).
2. Stray value in the inactive city lane is dropped, not rejected (§4.2).
3. Two check constraints; per-country subset stays out of the DB (§5.1).
4. `Down` fails if foreign companies exist (§5.2).
5. List row keeps `city: string` and adds `country`, `documentType` (§6.1).
6. Legacy POST without country ⇒ `CO` + `NIT` (§9).
7. *Orchestrator's call:* legacy PUT without country keeps the stored country and document type; the handler loads
   the company before validating (§6.2, §6.3, §9).
8. *Builder's call (orchestrator):* the no-country fallback is temporary; removing it after the frontend deploy is a
   follow-up (§9).
9. POS freezes the document type with the number (C5).
10. Short PDF label table, `NIT` fallback for a null type (C3).
11. *Orchestrator's call:* the ERP transform keeps removing dots and whitespace; per the owner it no longer cuts at
    the hyphen, for any document type (C4).
