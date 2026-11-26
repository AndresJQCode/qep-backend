# Address region persisted (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The workspace rules in **Global Constraints** override the skill's "one agent and one review per task": this plan runs in **3 blocks**, one implementer and one review per block.

**Goal:** Every address of a country with a region catalogue other than CO (today only ES) stores its `RegionId` next to `CityName`, and the detail reads return it resolved as `region: { id, name } | null`.

**Architecture:** Soft reference `region_id uuid null` (no cross-schema FK) on six entities in four modules. The four existing Geography ports (`ICustomerGeographyLookup`, `ICompanyGeographyLookup`, `ISupplierGeographyLookup`, `IQuotationGeographyLookup`) gain one batch method, `FindRegionsAsync`. The Bootstrapper implements it against `IRegionRepository`, which gains `ListByIdsAsync`. Write rules: the FluentValidation validator rejects a region with CO, giving a per-field 422. The domain repeats that rule as a safety net. The handler resolves the region through the port and rejects one that is unknown or from another country. Reads resolve the name with the same batch call.

**Tech Stack:** .NET 10, EF Core + Npgsql (one migration per module), FluentValidation, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-23-provincia-guardada-design.md` (owner approved). Frontend sibling spec: `qep-frontend/docs/superpowers/specs/2026-11-23-provincia-guardada-design.md`.

**Scope addition (orchestrator, 2026-11-23):** the embedded customer view of quotations and orders also carries `region`. That covers `QuotationCustomerRef`/`QuotationCustomerAddressRef` → `QuotationClientResponse`/`QuotationClientAddressResponse`, which the frontend reads as `QuoteClient`/`QuoteClientAddress`, both for the domicile and for each address. Reason: the frontend seeds a party from the saved customer address, and without the region the party would be saved without one. → Task 8.

## Global Constraints

- **TDD mandatory: RED before GREEN, with literal evidence of both.** When the expected RED failure is a compile error, the RED **does not start containers**: the evidence is the `dotnet build` output (the `CS…` lines). Integration tests run in GREEN.
- **3 blocks at most, one implementer per block, one review per block** (not per task). Each task keeps its own commit. **One strong whole-branch review at the end.**
- While iterating, integration tests are filtered **by test name**: `dotnet test <project> --filter "FullyQualifiedName~<TestName>"`. Never filter by a whole class. The whole class runs **once, at the end of the block**.
- **The whole solution suite never runs.** Before merging to `develop`, run only the test projects of the touched modules, one at a time, each with `-- xUnit.MaxParallelThreads=4`. Touched: Customers, Companies, Suppliers, Quotations, Geography (unit), Bootstrapper.UnitTests, ArchitectureTests.
- **Never two integration runs at once on the machine.** Before a long run, check `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`. If anything is listed, wait. **Never stop a process you did not start.** Subagents run tests **in the foreground**.
- Commands for the human are **PowerShell**. **Always `Test-Path` the test project before `dotnet test`**, because an empty path runs everything: `$p = "tests/Modules/Customers/Modules.Customers.UnitTests"; if (Test-Path $p) { dotnet test $p --filter "FullyQualifiedName~X" } else { "missing $p" }`.
- `Api.exe` running locks `dotnet build`/`test`/`ef`. If it is running, warn the developer. Do not stop it.
- Identifiers and comments in English. Exception messages and UI copy in neutral Spanish where they reach a person; the existing exception messages in these modules are English, so keep English there. Conventional commits, **no AI attribution lines**. Never print secrets. Never commit to `main`. Run `git branch --show-current` before the first edit **and again before every commit**. It must print `feature/region-persist`. Merges end in `develop`.
- **Do not invent** fields, routes or permissions. Anything not in the spec or the code is an owner decision and stays out. Names proposed here that the spec left open (columns, codes, DTO types) are marked *proposal*. Confirm them against the code and keep them consistent across tasks.
- No data migration, no backfill. Existing rows keep `region_id = null`.
- Lists and Excel exports do not change shape. Region appears only in detail responses.
- `ArchitectureTests`: no new project references. Customers, Companies, Suppliers and Quotations never reference Geography. All coupling lives in `src/Bootstrapper`.
- `docs/estado.md` is updated in `develop` at merge time, never on this branch (`qep-backend/CLAUDE.md`).
- Migrations: `dotnet ef migrations add <Name> --project src/Modules/<M>/Modules.<M>.Infrastructure --context <M>DbContext --output-dir Persistence/Migrations`. Each module has a design-time `*DbContextFactory`; whether `--startup-project` is also needed is for the implementer to confirm. After each one, run `dotnet ef migrations has-pending-model-changes` with the same `--project/--context`. Expected: no changes.

## Review Focus

1. **A `PUT` without `regionId` erases the stored region** (replace semantics, same as `CityName`), for customer, address, company, supplier and order billing party. *Pinned:* `UpdatingWithoutRegionClearsIt` in Tasks 3, 4, 5, and `SavingBillingWithoutRegionClearsIt` in Task 7.
2. **Every path that keeps the stored `CityName` keeps `RegionId` too**: an Excel import row without a city, `MergeForStore` keeping the customer's city, the quotation→order conversion (`ToDetails()`), and `with { … }` copies. *Pinned:* `ImportRowWithoutCityKeepsRegion` and `StoreMergeKeepingCityKeepsRegion` (Task 3), `ConvertingCopiesPartyRegion` (Task 7), `ToDetailsCarriesRegion` (Task 6).
3. **CO (or a missing country) with a region → 422**, at three levels: the validator (key `RegionId`), the domain net, and the database `CHECK` on the two tables that have a lane `CHECK`. *Pinned:* the validator tests in Tasks 3, 4, 5, the domain tests in Tasks 2, 4, 5, 6, and `CheckRejectsColombiaWithRegion` (Task 2 `customer_addresses`, Task 4 `companies`).
4. **A region from another country, a country without a catalogue, or a CO department id sent as `regionId` → 422 `<module>.<entity>.region_invalid`**. `GET /countries/CO/regions` returns department ids, so the frontend can hold one. *Pinned:* `RegionOfAnotherCountryIsRejected` and `DepartmentIdAsRegionIsRejected` in Tasks 3, 4, 5, 7.
5. **A DTO field that never reaches HTTP.** These are hand-mapped and silently drop a new DTO field: `CustomerResponse`, `CustomerMergeResponse`, `CompanyResponse`, `QuotationPartyResponse` (built in **two** composers), and `QuotationClientResponse`/`QuotationClientAddressResponse`. *Pinned:* every integration test reads `region` from the real JSON response, never from the DTO.

---

## Block 1 — Geography port + Customers (Tasks 1–3, one implementer)

### Task 1: Region lookup on the four Geography ports

**Files:**
- Modify: `src/Modules/Geography/Modules.Geography.Application/IRegionRepository.cs`, `src/Modules/Geography/Modules.Geography.Infrastructure/Persistence/RegionRepository.cs`
- Modify ports: `src/Modules/Customers/Modules.Customers.Application/ICustomerGeographyLookup.cs`, `src/Modules/Companies/Modules.Companies.Application/ICompanyGeographyLookup.cs`, `src/Modules/Suppliers/Modules.Suppliers.Application/ISupplierGeographyLookup.cs`, `src/Modules/Quotations/Modules.Quotations.Application/IQuotationGeographyLookup.cs`
- Modify adapters: `src/Bootstrapper/CustomerGeographyLookup.cs`, `CompanyGeographyLookup.cs`, `SupplierGeographyLookup.cs`, `QuotationGeographyLookup.cs`
- Modify test doubles that implement the ports (they stop compiling): `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationCustomerWithRetentionTests.cs` (`EmptyGeography`), `tests/Modules/Customers/Modules.Customers.UnitTests/{CustomerWriteDoubles.cs (Geography), CustomerDetailContactsTests.cs, ImportCustomersCompanyTests.cs, ListCustomersContactsTests.cs, SetCustomerCompanyHandlerTests.cs}`, `tests/Modules/Companies/Modules.Companies.UnitTests/CreateCompanyDefaultTaxRatesTests.cs`, `tests/Modules/Suppliers/Modules.Suppliers.UnitTests/{ExportSuppliersHandlerTests.cs, LinkedRelationshipStatusTests.cs, SupplierLinkingTests.cs}`, `tests/Modules/Quotations/Modules.Quotations.UnitTests/{QuotationsTestDoubles.cs, BuyerOrderGatewayTests.cs}`. Each gets a `FindRegionsAsync` that returns an empty dictionary, or a configurable one where later tasks need it.
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/GeographyLookupTests.cs`, plus `CountingRegionRepository` in `GeographyTestDoubles.cs`

**Interfaces:**
- Produces (Geography): `Task<IReadOnlyList<Region>> IRegionRepository.ListByIdsAsync(IReadOnlyCollection<RegionId> regionIds, CancellationToken cancellationToken)` (`AsNoTracking`, `Where(ids.Contains(region.Id))`).
- Produces (one per port, same shape):
  - `Task<IReadOnlyDictionary<Guid, CustomerRegionRef>> FindRegionsAsync(IReadOnlyCollection<Guid> regionIds, CancellationToken)` with `public sealed record CustomerRegionRef(Guid Id, string CountryCode, string Name);`
  - `CompanyRegionRef`, `SupplierRegionRef`, `QuotationRegionRef`: same three fields, each declared in its own module's port file.
- Adapter rules: no ids → empty dictionary **without touching the repository**; distinct ids; one repository call. Each adapter constructor gains `IRegionRepository regionRepository`. It is already registered by `GeographyInfrastructureExtensions`, so the DI in `QepServiceCollectionExtensions` stays as is.

- [ ] **Step 1: Write the failing tests** in `GeographyLookupTests`:

```csharp
[Fact] public async Task CustomerFindRegionsWithoutIdsDoesNotReachGeography()
{   // regions.Calls == 0, found empty
}
[Fact] public async Task CustomerFindRegionsResolvesCountryAndNameWithOneQuery()
{   var madrid = Region.Create(RegionId.New(), "ES", "28", "Madrid");
    // lookup.FindRegionsAsync([madrid.Id.Value, madrid.Id.Value])
    Assert.Equal(("ES", "Madrid"), (found[madrid.Id.Value].CountryCode, found[madrid.Id.Value].Name));
    Assert.Equal(1, regions.Calls);
}
[Fact] public async Task QuotationFindRegionsOmitsUnknownIds() { /* unknown Guid → not in dictionary */ }
```
Update the existing `new CustomerGeographyLookup(cities, departments)` constructions to pass `new CountingRegionRepository()`. (How `RegionId` is constructed — `RegionId.New()` or not — is for the implementer to confirm in `RegionId.cs`.)

- [ ] **Step 2: RED (compile).** `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`. Expected: `CS1061 … does not contain a definition for 'FindRegionsAsync'` / `CS7036` on the constructors. Paste the lines.
- [ ] **Step 3: Implement** `ListByIdsAsync`, the four port methods and records, the four adapters, and update the listed doubles.
- [ ] **Step 4: GREEN.** `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~FindRegions"`. Expected: 3 passed. Then `dotnet build` of the four modules' unit test projects is clean.
- [ ] **Step 5: Commit.** `feat(geography): resolve regions through the module geography ports`

### Task 2: Customer and address store `RegionId` (domain + persistence)

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerContactInfo.cs` (`public Guid? RegionId { get; init; }`, copied in `Normalized()`)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (`public Guid? RegionId { get; private set; }`; `EnsureValidLocation` returns `(Country, CityId, CityName, RegionId)`; `Assign(CustomerContactInfo)` sets it, lines ~1042–1053 and ~1384–1403)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerAddress.cs` (`CustomerAddressDetails.RegionId`; `CustomerAddress.RegionId`; `Apply` copies it; `EnsureCityLane` → also checks region)
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (`region_id` on `customers` near line 152 and on `customer_addresses` near line 387; extend `CK_customer_addresses_city_lane`, line 354)
- Create: migration `AddCustomerRegion` in `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs`, `CustomerAddressTests.cs`; `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerAddressesApiTests.cs` (CK test, same style as `CompanyCountryMigrationTests.ACompanyInColombiaCannotCarryAFreeTextCityName`)

**Interfaces:**
- Produces: `Customer.RegionId`, `CustomerContactInfo.RegionId`, `CustomerAddressDetails.RegionId`, `CustomerAddress.RegionId` (all `Guid?`).
- Domain codes (*proposal*): `customers.customer.region_lane_invalid`, `customers.address.region_lane_invalid`. They are thrown when the country is `CO` and `RegionId` is not null. Outside CO the region is kept as given; `Guid.Empty` is normalized to `null`.
- New CHECK (*proposal*): `(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL AND region_id IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)`.

- [ ] **Step 1: Failing tests.**
  - `CustomerTests.ForeignCustomerKeepsRegion`: `Update(... new CustomerContactInfo { Country = "ES", CityName = "Alcalá", RegionId = madrid, Phone = … })`, then `Assert.Equal(madrid, customer.RegionId)`.
  - `CustomerTests.ColombianCustomerWithRegionIsRejected`: `Assert.Throws<CustomersDomainException>(…)`, then `.Code == "customers.customer.region_lane_invalid"`.
  - `CustomerTests.UpdateWithoutRegionClearsIt`.
  - `CustomerAddressTests.ColombianAddressWithRegionIsRejected` (code `customers.address.region_lane_invalid`) and `ForeignAddressKeepsRegion`.
- [ ] **Step 2: RED (compile).** `dotnet build tests/Modules/Customers/Modules.Customers.UnitTests`. Expected: `CS0117 'CustomerContactInfo' does not contain a definition for 'RegionId'`.
- [ ] **Step 3: Implement** the domain, the EF mapping (`HasColumnName("region_id")` on both tables, CK text above) and `dotnet ef migrations add AddCustomerRegion …`. Review the generated migration: it must have two `AddColumn<Guid>` (nullable), plus `DropCheckConstraint` + `AddCheckConstraint` for `CK_customer_addresses_city_lane`, and nothing else.
- [ ] **Step 4: GREEN (unit).** `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~Region"`.
- [ ] **Step 5: Integration test** `CustomerAddressesApiTests.CheckRejectsColombiaWithRegion`: raw `INSERT INTO customers.customer_addresses (… country, city_id, city_name, region_id …) VALUES (… 'CO', <city>, NULL, <madrid>)`. Expect `PostgresException` with `ConstraintName == "CK_customer_addresses_city_lane"`. Madrid comes from `SELECT id FROM geography.regions WHERE country_code = 'ES' AND code = '28'`. Run with `--filter "FullyQualifiedName~CheckRejectsColombiaWithRegion"`, after the `Get-Process` check.
- [ ] **Step 6: Commit.** `feat(customers): store the region of foreign customers and addresses`

### Task 3: Customers write and read the region over HTTP

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs` (`ICustomerWriteCommand.RegionId`; rules)
- Modify: `CreateCustomer.cs`, `UpdateCustomer.cs` (commands gain trailing `Guid? RegionId = null`; handlers resolve the region before touching the aggregate, then set `RegionId` in `CustomerContactInfo`, both in the `Complete` and the `Update` branch of `UpdateCustomer.cs` ~lines 130–185)
- Modify: `ManageCustomerAddresses.cs` (`ICustomerAddressWriteCommand.RegionId`; `Add/UpdateCustomerAddressCommand` trailing `Guid? RegionId = null`; `CustomerAddressWriteRules`; handlers)
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerRegionRules.cs`
- Modify: `CustomersDtos.cs`: `CustomerRegionDto`; `CustomerDto` and `CustomerAddressDto` gain trailing `CustomerRegionDto? Region = null`; `CustomerResponse` and `CustomerMergeResponse` gain trailing `CustomerRegionDto? Region = null`; `CreateCustomerRequest`, `UpdateCustomerRequest`, `CustomerAddressRequest` gain trailing `Guid? RegionId = null`
- Modify: `CustomerMapping.cs` (`ToDto`/`ToAddressDto` take regions; `ToDtoAsync` resolves them)
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (pass `RegionId` in create/update/address endpoints; `ToResponse` and the merge response carry `Region`; `ToListItem` unchanged)
- Modify (conservation rule): `ImportCustomers.cs` ~line 865–868; `CustomerProvisioning.cs` `MergeForStore` ~line 400–416
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteHandlersTests.cs` (rules with the `CustomerWriteDoubles.Geography` double now configurable with regions), `ImportCustomersCompanyTests.cs` or a new `CustomerRegionConservationTests.cs`; integration `CustomerWriteApiTests.cs`, `CustomerAddressesApiTests.cs`, `CustomerMergeApiTests.cs`

**Interfaces:**
- Consumes: `ICustomerGeographyLookup.FindRegionsAsync`, `CustomerRegionRef` (Task 1); domain props (Task 2).
- Produces:
  - `public sealed record CustomerRegionDto(Guid Id, string Name);`
  - `internal static class CustomerRegionRules { public static Task<CustomerRegionRef?> ResolveRegionAsync(ICustomerGeographyLookup lookup, string? country, Guid? regionId, string invalidCode, CancellationToken ct); }`. It returns `null` when `regionId` is null. It throws `CustomersDomainException(invalidCode, …, fieldErrors { ["RegionId"] = … })` when the region is not found or `!CountryCode.Equals(country.Trim().ToUpperInvariant())`. A country without a catalogue never has regions in `geography.regions`, so it fails the same way, and `CountryCatalogs` is not needed in the module. The constructor that takes field errors is for the implementer to confirm in `CustomersDomainException.cs:14`.
  - Codes (*proposal*): `customers.customer.region_invalid`, `customers.address.region_invalid`.
  - Validator (both rule sets): `RuleFor(c => c.RegionId).Null().WithMessage("A Colombian address takes its region from the city, not from a region id.").When(IsColombian)` and `RuleFor(c => c.RegionId).NotEqual(Guid.Empty)`. `CustomerWriteRules` has no `IsColombian`, so add one equal to `CustomerAddressWriteRules.IsColombian` (blank or `CO`).
  - `CustomerMapping.ToDto(..., IReadOnlyDictionary<Guid, CustomerRegionRef>? regionsById = null)`; `ToDtoAsync` makes **one** `FindRegionsAsync` call with `customer.RegionId` plus every `address.RegionId`. List and export pass `null`, so their region is `null`.
  - Conservation: in Import, `RegionId = candidate.City is null ? customer.RegionId : null`. In `MergeForStore`, pass `RegionId: customer.RegionId` only when the resulting `cityName` is the customer's own (not CO, `customer.CityName` kept); otherwise `null`. External flows (store, Shopify, POS, `CustomerProvisioning.EnsureAddressesAsync`) do not write a region.

- [ ] **Step 1: Failing unit tests.**
  - `ColombianCommandWithRegionFailsValidation`: `ValidationException` with error key `RegionId`. Covers `Country = "co"` and `Country = ""`.
  - `RegionOfAnotherCountryIsRejected`: the double returns `("ES","Madrid")`, the command has `Country = "FR"` → code `customers.customer.region_invalid`. Same for an address → `customers.address.region_invalid`.
  - `DepartmentIdAsRegionIsRejected`: an unknown id with `Country = "ES"` → `region_invalid`.
  - `ForeignCustomerDetailCarriesRegion`: `ToDtoAsync`, then `dto.Region == new CustomerRegionDto(madrid, "Madrid")` and `dto.Addresses[0].Region` resolved, with **one** `FindRegionsAsync` call in the double's `calls`.
  - `ImportRowWithoutCityKeepsRegion` and `StoreMergeKeepingCityKeepsRegion`.
- [ ] **Step 2: RED (compile).** `dotnet build tests/Modules/Customers/Modules.Customers.UnitTests`. Expected: `CS1739 … does not have a parameter named 'RegionId'` / `CS0246 'CustomerRegionDto'`.
- [ ] **Step 3: Implement** the signatures above and the endpoint wiring.
- [ ] **Step 4: GREEN (unit).** `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~Region"`.
- [ ] **Step 5: Integration tests**, each reading `region` from the response JSON:
  - `CustomerWriteApiTests.ForeignCustomerRoundTripsRegion`: POST ES + Madrid, then GET has `region.id == madrid` and `region.name == "Madrid"`.
  - `CustomerWriteApiTests.UpdatingWithoutRegionClearsIt`: PUT without `regionId`, then `region == null`.
  - `CustomerWriteApiTests.ColombianCustomerWithRegionIs422`: 422 with `errors.RegionId`.
  - `CustomerAddressesApiTests.ForeignAddressRoundTripsRegion`.
  - `CustomerMergeApiTests.MergeResponseCarriesRegion`.

  Filter each one by name.
- [ ] **Step 6: End of block.** Run the whole classes touched (`CustomerWriteApiTests`, `CustomerAddressesApiTests`, `CustomerMergeApiTests`) once, then the Customers unit project and `Bootstrapper.UnitTests` complete, one at a time, with `-- xUnit.MaxParallelThreads=4`.
- [ ] **Step 7: Commit.** `feat(customers): accept and return the region of customers and addresses`

**Block 1 review** (one reviewer, the whole block's diff): Review Focus items 1–5 applied to Customers.

---

## Block 2 — Companies + Suppliers (Tasks 4–5, one implementer; needs Task 1)

### Task 4: Billing company region

**Files:**
- Modify: `src/Modules/Companies/Modules.Companies.Domain/Company.cs`: `public Guid? RegionId { get; private set; }`. `Create(..., bool chargesVat = true, Guid? regionId = null)` and `Update(..., bool chargesVat, Guid? regionId = null)`. `EnsureValidLocation` (~line 417) returns `(CityId, CityName, RegionId)`.
- Modify: `src/Modules/Companies/Modules.Companies.Infrastructure/Persistence/CompaniesDbContext.cs` (`region_id`; extend `CK_companies_city_lane`, line 27); migration `AddCompanyRegion`
- Modify: `src/Modules/Companies/Modules.Companies.Application/CompanyWriteRules.cs` (interface `RegionId` + the two rules); `CreateCompany.cs`, `UpdateCompany.cs` (commands with trailing `Guid? RegionId = null`; resolve after the city check ~line 63); `CompaniesDtos.cs` (`CompanyRegionDto`; `CompanyDto`/`CompanyResponse` trailing `CompanyRegionDto? Region = null`; `Create/UpdateCompanyRequest` trailing `Guid? RegionId = null`); `CompanyMapping.cs` (`ToDto(..., CompanyRegionRef? region = null)`, `ToDtoAsync` resolves); create `CompanyRegionRules.cs`
- Modify: `src/Modules/Companies/Modules.Companies.Api/CompanyEndpoints.cs` (pass `RegionId`; `ToResponse` adds `company.Region`; `ToListItem` unchanged)
- Test: `tests/Modules/Companies/Modules.Companies.UnitTests/{CompanyTests.cs, CompanyWriteRulesTests.cs}`; integration `CompanyWriteApiTests.cs`, `CompanyCountryMigrationTests.cs`

**Interfaces:**
- Consumes: `ICompanyGeographyLookup.FindRegionsAsync`, `CompanyRegionRef` (Task 1).
- Produces: `public sealed record CompanyRegionDto(Guid Id, string Name);` and `internal static class CompanyRegionRules { public static Task<CompanyRegionRef?> ResolveRegionAsync(ICompanyGeographyLookup lookup, string country, Guid? regionId, CancellationToken ct); }`. Codes (*proposal*): `companies.company.region_invalid`, `companies.company.region_lane_invalid` (domain net). `CompaniesDomainException(code, message)` takes no field errors; that matches how `city_not_found` already behaves.
- Same CK change as Task 2, on `companies`. `CompaniesSeeder.cs:132` stays unchanged (default `null`).

- [ ] **Step 1: Failing tests.**
  - `CompanyTests.ColombianCompanyWithRegionIsRejected` (`region_lane_invalid`) and `ForeignCompanyKeepsRegion`.
  - `CompanyWriteRulesTests.ColombianCompanyWithRegionFailsOnRegionId`.
  - Handler test `RegionOfAnotherCountryIsRejected`, using `FixedGeographyLookup` from `CreateCompanyDefaultTaxRatesTests` extended with regions.
- [ ] **Step 2: RED (compile).** `dotnet build tests/Modules/Companies/Modules.Companies.UnitTests`, then paste the `CS…` lines.
- [ ] **Step 3: Implement** the domain, the EF/CK change and the migration (review it: one `AddColumn` plus drop/add of the CK), the rules, the handlers, the DTO/Response and the endpoint.
- [ ] **Step 4: GREEN (unit)** with `--filter "FullyQualifiedName~Region"`.
- [ ] **Step 5: Integration tests**, all reading the response JSON:
  - `CompanyWriteApiTests.ForeignCompanyRoundTripsRegion`
  - `UpdatingWithoutRegionClearsIt`
  - `DepartmentIdAsRegionIsRejected`: a CO department id from `GET /api/v1/countries/CO/regions` sent with `country: "ES"` → 422 `companies.company.region_invalid`
  - `CompanyCountryMigrationTests.CheckRejectsColombiaWithRegion`: `ConstraintName == "CK_companies_city_lane"`
- [ ] **Step 6: Commit.** `feat(companies): store and return the region of foreign billing companies`

### Task 5: Supplier region

**Files:**
- Modify: `src/Modules/Suppliers/Modules.Suppliers.Domain/SupplierWriteData.cs` (trailing `Guid? RegionId = null`), `Supplier.cs` (`RegionId`; `EnsureValidLocation` ~line 290 returns it; set in the write method ~line 270)
- Modify: `src/Modules/Suppliers/Modules.Suppliers.Infrastructure/Persistence/SuppliersDbContext.cs` (`region_id`, **no** new CHECK); migration `AddSupplierRegion`
- Modify: `src/Modules/Suppliers/Modules.Suppliers.Application/SupplierWriteRules.cs` (interface + rules), `CreateSupplier.cs`, `UpdateSupplier.cs` (commands trailing `Guid? RegionId = null`, `From(...)` maps it), `SuppliersDtos.cs` (`SupplierRegionDto`; `SupplierResponse` trailing `SupplierRegionDto? Region = null`; `SupplierWriteRequest` trailing `Guid? RegionId = null`), `SupplierMapping.cs` (`ToResponse(..., SupplierRegionRef? region = null)`; `ToResponseAsync` resolves; `ToWriteData` maps; new `ResolveRegionAsync(this ISupplierWriteCommand, ISupplierGeographyLookup, CancellationToken)` next to `ResolveCityAsync`, ~line 135)
- Unchanged on purpose: `src/Bootstrapper/WholesaleSupplierLinker.cs` (wholesale flow: no region, per the spec's non-goals), `ExportSuppliers.cs` and `ListSuppliers` (region `null`)
- Test: `tests/Modules/Suppliers/Modules.Suppliers.UnitTests/{SupplierTests.cs, SupplierWriteRulesTests.cs}`; integration `SupplierWriteApiTests.cs`

**Interfaces:**
- Consumes: Task 1 port.
- Produces: `public sealed record SupplierRegionDto(Guid Id, string Name);` and `Task<SupplierRegionRef?> SupplierMapping.ResolveRegionAsync(this ISupplierWriteCommand command, ISupplierGeographyLookup lookup, CancellationToken ct)`. Codes (*proposal*): `suppliers.supplier.region_invalid`, `suppliers.supplier.region_lane_invalid`. `SupplierResponse` is itself the HTTP body, so there is no separate Response to forget.

- [ ] **Step 1: Failing tests.**
  - `SupplierTests.ColombianSupplierWithRegionIsRejected` and `ForeignSupplierKeepsRegion`.
  - `SupplierWriteRulesTests.ColombianSupplierWithRegionFailsOnRegionId`.
  - Handler `RegionOfAnotherCountryIsRejected`, using the `FakeGeography` double from `SupplierLinkingTests` extended.
- [ ] **Step 2: RED (compile).** `dotnet build tests/Modules/Suppliers/Modules.Suppliers.UnitTests`.
- [ ] **Step 3: Implement**, migration included (one `AddColumn`).
- [ ] **Step 4: GREEN (unit)** with `--filter "FullyQualifiedName~Region"`.
- [ ] **Step 5: Integration tests:** `SupplierWriteApiTests.ForeignSupplierRoundTripsRegion`, `UpdatingWithoutRegionClearsIt`, `RegionOfAnotherCountryIsRejected` (Madrid with `country: "FR"` → 422 `suppliers.supplier.region_invalid`).
- [ ] **Step 6: End of block.** Run the whole classes `CompanyWriteApiTests`, `CompanyCountryMigrationTests` and `SupplierWriteApiTests` once, then the Companies and Suppliers unit projects, one at a time.
- [ ] **Step 7: Commit.** `feat(suppliers): store and return the region of foreign suppliers`

**Block 2 review:** Review Focus 1, 3, 4, 5 on Companies and Suppliers.

---

## Block 3 — Quotations parties + quote customer view + docs (Tasks 6–8, one implementer; needs Task 1)

### Task 6: Parties carry `RegionId` (domain + persistence)

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPartyDetails.cs`: `public Guid? RegionId { get; init; }`. In `EnsureLane` (line 87), when `IsColombian && RegionId is not null && RegionId != Guid.Empty`, throw the existing `LaneInvalid($"{fieldPrefix}.RegionId", "A Colombian party takes its region from the department, not from a region id.")`. In `NormalizedFields`, `RegionId = NormalizeOptionalId(RegionId)`.
- Modify: `QuotationParty.cs` and `OrderParty.cs` (`RegionId` property; `Apply` copies it; `ToDetails()` returns it)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationChangeSummary.cs` (`Describe`, line ~403: append `party.RegionId`, so a region-only change is recorded as a parties change in the history; this is the spec's "audit includes the region")
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`party.Property(value => value.RegionId).HasColumnName("region_id");` for `quotation_parties` ~line 476 and `order_parties` ~line 998; no CHECK); migration `AddPartyRegion`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/{QuotationPartyTests.cs, OrderPartyTests.cs}`

**Interfaces:**
- Produces: `QuotationPartyDetails.RegionId`, `QuotationParty.RegionId`, `OrderParty.RegionId`. The details record's equality now includes `RegionId`, on purpose (spec), so `Order.SameExceptCity` and `CorrectExternalShippingCity` (`Order.cs` ~2190, 2410) treat a region change as a location change. `Order.Create` from a quotation (`Order.cs:526`) copies through `ToDetails()`.

- [ ] **Step 1: Failing tests.**
  - `QuotationPartyTests.ColombianPartyWithRegionIsLaneInvalid`: code `quotations.party.city_lane_invalid`, error key `Parties.Billing.RegionId`.
  - `QuotationPartyTests.ForeignPartyKeepsRegion`.
  - `OrderPartyTests.ToDetailsCarriesRegion`.
  - `OrderPartyTests.EmptyRegionIdIsNoRegion`: `Guid.Empty` → `null`.
- [ ] **Step 2: RED (compile).** `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests`. Expected: `CS0117 'QuotationPartyDetails' does not contain a definition for 'RegionId'`.
- [ ] **Step 3: Implement**, plus the migration (two `AddColumn`, nothing else).
- [ ] **Step 4: GREEN** with `--filter "FullyQualifiedName~Region"` on the Quotations unit project.
- [ ] **Step 5: Commit.** `feat(quotations): store the region of quotation and order parties`

### Task 7: Parties write and read the region over HTTP; conversion copies it

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs`: `QuotationPartyRequest` gains trailing `Guid? RegionId = null`. `QuotationPartyDto` gains trailing `Guid? RegionId = null` (the id, for the composers to resolve). `QuotationPartyResponse` gains trailing `QuotationRegionDto? Region = null`. New `public sealed record QuotationRegionDto(Guid Id, string Name);`
- Modify: `QuotationMapping.cs` (`ToDomain(this QuotationPartyRequest?)` maps `RegionId`, line ~231; `ToDto(QuotationParty)` passes `RegionId`), `OrderMapping.cs` (`ToDto(OrderParty)` ~line 149)
- Modify: `QuotationResponseComposer.cs` (`ToPartyResponse(QuotationPartyDto party, IReadOnlyDictionary<Guid, QuotationRegionRef> regionsById)`, ~line 415; one `FindRegionsAsync` call per composition over the parties' `RegionId`s), `OrderResponseComposer.cs` (the inline `new QuotationPartyResponse(...)` ~line 207 adds `Region`, with the same single call)
- Create: `src/Modules/Quotations/Modules.Quotations.Application/PartyRegionRules.cs`
- Call sites of the rule: `QuotationEditsApplication.cs` (before `quotation.UpdateDetails`, ~line 120; the context already has `Geography`, line 295), `OrderDraftBuilder.cs` (before `return (header, input.Parties.ToDomain(), lines)`, ~line 169; inject `IQuotationGeographyLookup` into its constructor; how the builder is constructed and registered is for the implementer to confirm), `SaveOrderEdits.cs` (before `OrderBillingEdits.Apply` when `Billing?.Party` is not null; `geographyLookup` already injected, line 63). The previews (`PreviewQuotation`, `PreviewOrderEdits`) do not call it: the preview is computed while the person types, the same criterion as `strict` in `OrderBillingEdits.Apply`. Unchanged: `IStoreOrderGateway`, `IExternalOrderWriter`, `IBuyerOrderGateway`, `IWholesaleOrderGateway` (they build parties without a region).
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderResponseComposerTests.cs` and a new `PartyRegionRulesTests.cs` (the `QuotationsTestDoubles.cs` geography double gains `regions`); integration `QuotationPartiesApiTests.cs`, `OrderConvertApiTests.cs`, `OrderEditsApiTests.cs`

**Interfaces:**
- Consumes: `IQuotationGeographyLookup.FindRegionsAsync`, `QuotationRegionRef` (Task 1); Task 6 props.
- Produces: `internal static class PartyRegionRules { public static Task EnsureAsync(IQuotationGeographyLookup geography, QuotationParties parties, CancellationToken ct); public static Task EnsureAsync(IQuotationGeographyLookup geography, QuotationPartyDetails? party, string fieldPrefix, CancellationToken ct); }`. The `QuotationParties` overload batches billing and shipping into one lookup. It throws `QuotationsDomainException("quotations.party.region_invalid", …, { [$"{prefix}.RegionId"] = … })` when the region is unknown or its `CountryCode` differs from the party's normalized country. CO with a region is left to `EnsureLane`. Prefixes: `Parties.Billing` / `Parties.Shipping`, the same as the domain (`OrderParty.Apply`). The order-edits validator uses `Billing.Party.*`; whether to align it is for the implementer to confirm.
- `QuotationRegionDto` is reused by Task 8.

- [ ] **Step 1: Failing unit tests.**
  - `PartyRegionRulesTests.RegionOfAnotherCountryIsRejected`: Madrid with `Country = "FR"` → `region_invalid`, key `Parties.Billing.RegionId`.
  - `DepartmentIdAsRegionIsRejected`.
  - `ForeignPartyWithItsRegionPasses`.
  - `OrderResponseComposerTests.PartyResponseCarriesRegion`: `response.Parties[0].Region == new QuotationRegionDto(madrid, "Madrid")`.
- [ ] **Step 2: RED (compile).** `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests`.
- [ ] **Step 3: Implement** the signatures and the call sites above.
- [ ] **Step 4: GREEN (unit)** with `--filter "FullyQualifiedName~Region"`.
- [ ] **Step 5: Integration tests**, each reading `parties[*].region` from the JSON:
  - `QuotationPartiesApiTests.ForeignBillingPartyRoundTripsRegion`
  - `QuotationPartiesApiTests.ColombianPartyWithRegionIs422`: `quotations.party.city_lane_invalid`
  - `QuotationPartiesApiTests.RegionOfAnotherCountryIs422`
  - `OrderConvertApiTests.ConvertingCopiesPartyRegion`: a quotation with an ES billing party + Madrid, converted; the order detail billing party has `region.id == madrid`
  - `OrderEditsApiTests.SavingBillingWithoutRegionClearsIt`
- [ ] **Step 6: Commit.** `feat(quotations): accept and return the region of quotation and order parties`

### Task 8: Quote customer view carries the region (scope addition) + README

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IQuotationCustomerLookup.cs`: `QuotationCustomerRef` gains trailing `QuotationRegionDto? Region = null`. `QuotationCustomerAddressRef` gains trailing `QuotationRegionDto? Region = null`, after `Country = "CO"`.
- Modify: `QuotationsDtos.cs`: `QuotationClientResponse` gains trailing `QuotationRegionDto? Region = null`. `QuotationClientAddressResponse` gains trailing `QuotationRegionDto? Region = null`.
- Modify: `QuotationResponseComposer.ToClientResponse` (~line 257). It maps both `Region`s. `OrderResponseComposer` already reuses it.
- Modify: `src/Bootstrapper/QuotationCustomerLookup.cs`. `FindAsync` (~line 43) and `FindManyAsync` (~line 65) each make **one** `geographyLookup.FindRegionsAsync` call (the `ICustomerGeographyLookup` from Task 1) over `customer.RegionId` plus every `address.RegionId`. `ToRef` takes `IReadOnlyDictionary<Guid, CustomerRegionRef> regionsById` and builds `new QuotationRegionDto(r.Id, r.Name)`.
- Modify: `README.md` § «Regiones y ciudades por país» (line 557). Replace "La región … **sólo sirve para filtrar ciudades: no se guarda**" with: the region is stored as `region_id` for countries with a catalogue other than CO, it is read back as `region { id, name }` in detail responses, CO keeps deriving it from `CityId`, existing rows have none. List the six entities and the quote client view.
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/QuotationCustomerWithRetentionTests.cs` (or a new `QuotationCustomerLookupRegionTests.cs` with the same doubles; its `EmptyGeography` gains regions); `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationResponseComposerClientContactsTests.cs`; integration `QuotationPartiesApiTests.cs`

**Interfaces:**
- Consumes: `ICustomerGeographyLookup.FindRegionsAsync` (Task 1), `Customer.RegionId`/`CustomerAddress.RegionId` (Task 2), `QuotationRegionDto` (Task 7).
- Produces: `client.region` and `client.addresses[*].region` in every quotation and order detail. The frontend reads them as `QuoteClient.region` and `QuoteClientAddress.region`.

- [ ] **Step 1: Failing tests.**
  - `QuotationCustomerLookupRegionTests.ForeignCustomerAndAddressCarryRegion`: `ref.Region == new QuotationRegionDto(madrid, "Madrid")` and `ref.Addresses!.Single().Region` resolved, with one regions call.
  - `QuotationResponseComposerClientContactsTests.ClientResponseCopiesRegions`.
- [ ] **Step 2: RED (compile).** `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`.
- [ ] **Step 3: Implement**, then the README.
- [ ] **Step 4: GREEN (unit)** on both projects with `--filter "FullyQualifiedName~Region"`.
- [ ] **Step 5: Integration test.** `QuotationPartiesApiTests.QuoteClientCarriesCustomerRegion`: create an ES customer with Madrid through `POST /customers`, create a quotation for that customer, and check that `GET` returns `client.region.name == "Madrid"`. If the Quotations harness cannot create customers through the Customers API, the implementer confirms that, keeps the unit coverage and says so in the block report.
- [ ] **Step 6: End of block.** Run the whole classes `QuotationPartiesApiTests`, `OrderConvertApiTests` and `OrderEditsApiTests` once, then the Quotations and Bootstrapper unit projects, one at a time.
- [ ] **Step 7: Commit.** `feat(quotations): return the customer region in the quotation client view`

**Block 3 review:** Review Focus 2 (conversion, `with`), 4 and 5, with both composers checked.

---

## Before merging to `develop`

- [ ] Final whole-branch review (strongest model), against the spec and this plan's Review Focus.
- [ ] `has-pending-model-changes` clean for Customers, Companies, Suppliers and Quotations.
- [ ] Touched test projects, one at a time, each preceded by `Test-Path` and the `Get-Process` check: `tests/ArchitectureTests/ArchitectureTests`, `tests/Bootstrapper/Bootstrapper.UnitTests`, `tests/Modules/Geography/Modules.Geography.UnitTests`, then the unit and integration projects of Customers, Companies, Suppliers and Quotations, each with `-- xUnit.MaxParallelThreads=4`.
- [ ] After the merge, in `develop`: update `docs/estado.md` and record that the backend deploys first (the frontend reads `region` and tolerates its absence).
