# Buyer Document Type by Country Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Accept the seven company document codes (`NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID`) in the store order request, the buyer profile, customers and quotation/order billing parties, each one restricted to the codes its own country allows, so that "Tomar" works end to end for every code.

**Architecture:** One per-country table in `BuildingBlocks.Domain` (`DocumentTypesByCountry`), tied to the Companies table by a parity test. Store and Buyers read it directly as strings; Customers and Quotations widen their typed enums and add a typed `IsAllowedFor` helper over it. Every write path enforces it twice (FluentValidation for the field error, the domain for the invariant); reads never check. "Tomar" makes an incomplete customer without a document take the request's document and country together, and the customer merge fills document and country as a pair.

**Tech Stack:** .NET 10, FluentValidation, EF Core (Npgsql), xUnit v3, Testcontainers, PowerShell 5.1.

**Spec:** `docs/superpowers/specs/2026-11-08-buyer-document-type-by-country-design.md` (siblings: `../qep-frontend/docs/superpowers/specs/` and `../qep-storefront/docs/superpowers/specs/`, same filename; they ship after this repo).

## Global Constraints

- Codes, exactly: `NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID`. Table: `CO` → `NIT, CC, CE, PASAPORTE`; `ES` → `NIF, NIE, PASAPORTE`; any other country → `TAX_ID, PASAPORTE`. Country is trimmed and upper-cased; types compare trimmed and case-insensitively.
- The catalogue lives in `BuildingBlocks.Domain` (folder `Identification`, never `Identity`). No new module reference: `StoreLayerTests`, `BuyersLayerTests`, `CustomersLayerTests` and `QuotationsLayerTests` stay unchanged and green.
- New enum members in Customers and Quotations are exactly `Nif`, `Nie`, `TaxId` (they become stored and reported values).
- New error codes, exactly: `store.order_request.document_type_not_allowed`, `store.buyer.document_type_not_allowed`, `customers.customer.document_type_not_allowed`, `quotations.party.document_type_not_allowed`. Existing codes keep their meaning.
- Messages: Store and Buyers keep «El tipo de documento no es válido.» and add «El tipo de documento no es válido para el país.»; Customers and Quotations add "The identification type is not allowed for the country." (English, like their neighbours).
- Field keys do not change: `Billing.IdentificationType` (Store, Buyers), `IdentificationType` (Customers), `Parties.Billing.IdentificationType` (quotations), `Billing.Party.IdentificationType` (order edits).
- The per-country rule runs only when the code is known and the country has a valid shape, so a bad country is reported on the country field alone.
- Checks run on writes only: never in an EF constructor, never in the `BuyerBillingData` record constructor. No migration, no database check constraint.
- Out of scope, unchanged: Shopify code list, Pos quick-create list (four codes), Suppliers enum, number format (required, max 32), shipping blocks (no document), complete customers during "Tomar".
- Code comments follow the neighbouring comments (Spanish, *tú*). Messages that reach a person in Store/Buyers are Colombian Spanish with *tú*.
- Process: TDD with literal RED and GREEN output pasted in the task log. PowerShell commands only. Integration tests one project at a time, filtered, with `-- xUnit.MaxParallelThreads=4`, after `$env:Seed__Enabled = "false"`. If `Api.exe` is running and locks the build, tell the developer; do not stop a process you did not launch.
- Git: work on `feature/document-type-by-country` (from `develop`). Conventional commits, no AI attribution, no `Co-Authored-By` trailer. `docs/estado.md` is **not** edited on this branch (it is updated on `develop` at merge).

## Review Focus

1. **Case and whitespace variants** (`" nif "`, `"es"`, `" es "`) must be accepted wherever the canonical form is, by the validator and by the domain alike — tests in Tasks 1, 3 and 8.
2. **Legacy non-conforming rows** (`ES` + `CC` in a customer, a party or a `jsonb` profile) must still read with 200 and only fail when someone saves them — tests in Tasks 3, 7 and 10.
3. **A malformed country with a known code** (`COL` + `NIF`, `ESP` + `NIF`) must give exactly one error, on the country, never a second one on the type — tests in Tasks 3, 7 and 8.
4. **An incomplete customer with a phone but no country** (Shopify import with no country, merges) taken for a foreign request must take the request's country and keep its raw phone (no country was replaced) — test in Task 4.
5. **A signup event for an existing unverified account whose profile is now invalid** must still re-register the account and resend verification, dropping the profile, never failing the message — test in Task 10.

---

## File map

| File | Responsibility | Task |
| --- | --- | --- |
| `src/BuildingBlocks/BuildingBlocks.Domain/Identification/DocumentTypesByCountry.cs` (new) | The seven codes and the per-country table | 1 |
| `src/Modules/Customers/Modules.Customers.Domain/IdentificationType.cs` | Enum + parser widened; `IsAllowedFor` | 2, 3 |
| `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs`, `GetCustomerImportTemplate.cs`, `src/Bootstrapper/ServiceContractsCustomerProvisioning.cs` | Colombian-only entry points pinned to `AllowedFor("CO")` | 2 |
| `src/Modules/Customers/Modules.Customers.Domain/Customer.cs`, `CustomersErrorCodes.cs`, `Modules.Customers.Application/CustomerWriteRules.cs`, `src/Bootstrapper/ShopifyCustomerDirectory.cs` | Customer per-country rule | 3 |
| `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs` | `MergeForStore`: document and country travel together | 4 |
| `src/Modules/Customers/Modules.Customers.Domain/CustomerMerge.cs` | Merge plan fills document and country as a pair | 5 |
| `src/Modules/Quotations/Modules.Quotations.Domain/PartyFiscalTypes.cs`, `QuotationPartyDetails.cs`, `Modules.Quotations.Application/QuotationEdits.cs`, `OrderEdits.cs`, `QuotationsDtos.cs` | Party enum widened; billing-party per-country rule | 6, 7 |
| `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs`, `src/Modules/Buyers/Modules.Buyers.Application/BuyerProfileValidator.cs` | Store and Buyers validators (move together: parity) | 8 |
| `src/Modules/Store/Modules.Store.Domain/StoreRequestBilling.cs`, `StoreErrorCodes.cs` | Store domain invariant | 9 |
| `src/Modules/Buyers/Modules.Buyers.Domain/BuyerProfile.cs`, `BuyerErrorCodes.cs`, `Modules.Buyers.Application/BuyerSignup.cs` | Buyer profile invariant; signup drops an invalid profile | 10 |
| `tests/Modules/Store/Modules.Store.IntegrationTests/StoreInboxRig.cs`, `StoreTakeApiTests.cs` | "Tomar" acceptance | 4, 11 |
| `CLAUDE.md` | Gotchas | 12 |

---

### Task 0: Branch and spec commit

- [ ] **Step 1: Check the branch now (not from the session snapshot)**

```powershell
git branch --show-current
git status --short
```

Expected: the spec (and this plan) appear as untracked under `docs/superpowers/`.

- [ ] **Step 2: Branch from an up-to-date `develop`**

```powershell
git switch develop; if ($?) { git pull --ff-only }
git switch -c feature/document-type-by-country
```

The untracked spec and plan travel with the working tree.

- [ ] **Step 3: The spec goes first, as its own commit**

```powershell
git add docs/superpowers/specs/2026-11-08-buyer-document-type-by-country-design.md docs/superpowers/plans/2026-11-08-buyer-document-type-by-country.md
git commit -m "docs(spec): buyer document type by country design and plan"
```

---

### Task 1: Shared catalogue in `BuildingBlocks.Domain`

**Files:**
- Create: `src/BuildingBlocks/BuildingBlocks.Domain/Identification/DocumentTypesByCountry.cs`
- Test: create `tests/Modules/Companies/Modules.Companies.UnitTests/DocumentTypesByCountryTests.cs` (Companies.Domain already references `BuildingBlocks.Domain`; no new test project)

**Interfaces:**
- Produces (namespace `BuildingBlocks.Domain.Identification`):
  - `public static class DocumentTypesByCountry`
  - `public static IReadOnlyList<string> All { get; }` — `["NIT", "CC", "CE", "PASAPORTE", "NIF", "NIE", "TAX_ID"]`, in this order
  - `public static IReadOnlyList<string> AllowedFor(string country)` — fixed order per country as in Global Constraints (a list, not a set: the import template renders it as a dropdown)
  - `public static bool IsKnown(string? type)`
  - `public static bool IsAllowed(string? type, string country)`
- Decision: `CompanyDocumentTypes.AllowedFor` is **not** changed to delegate (Companies behaviour stays byte-identical); the parity test ties the two.

- [ ] **Step 1: Write the failing tests**

```csharp
using BuildingBlocks.Domain.Identification;
using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

public sealed class DocumentTypesByCountryTests
{
    [Fact]
    public void AllHoldsTheSevenCompanyCodes() =>
        Assert.Equal(["NIT", "CC", "CE", "PASAPORTE", "NIF", "NIE", "TAX_ID"], DocumentTypesByCountry.All);

    [Theory]
    [InlineData("CO", new[] { "NIT", "CC", "CE", "PASAPORTE" })]
    [InlineData("co", new[] { "NIT", "CC", "CE", "PASAPORTE" })]
    [InlineData("ES", new[] { "NIF", "NIE", "PASAPORTE" })]
    [InlineData(" es ", new[] { "NIF", "NIE", "PASAPORTE" })]
    [InlineData("US", new[] { "TAX_ID", "PASAPORTE" })]
    [InlineData("MX", new[] { "TAX_ID", "PASAPORTE" })]
    public void AllowedForFollowsTheCompanyTable(string country, string[] expected) =>
        Assert.Equal(expected, DocumentTypesByCountry.AllowedFor(country));

    [Theory]
    [InlineData("XX")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownTypeIsNotKnown(string? type) => Assert.False(DocumentTypesByCountry.IsKnown(type));

    [Fact]
    public void IsKnownTrimsAndIgnoresCase() => Assert.True(DocumentTypesByCountry.IsKnown(" nif "));

    [Theory]
    [InlineData("cc", "CO", true)]
    [InlineData(" nif ", "es", true)]
    [InlineData("CC", "ES", false)]
    [InlineData("TAX_ID", "CO", false)]
    [InlineData(null, "CO", false)]
    public void IsAllowedChecksTheTypeAgainstTheCountry(string? type, string country, bool expected) =>
        Assert.Equal(expected, DocumentTypesByCountry.IsAllowed(type, country));

    [Theory]
    [InlineData("CO")]
    [InlineData("ES")]
    [InlineData("US")]
    [InlineData("co")]
    public void TheCompanyTableMatchesTheSharedOne(string country) =>
        Assert.True(CompanyDocumentTypes.AllowedFor(country).Select(type => type.ToWire()).ToHashSet()
            .SetEquals(DocumentTypesByCountry.AllowedFor(country)));

    [Fact]
    public void TheCompanyCodesAreTheSharedCodes() =>
        Assert.True(Enum.GetValues<CompanyDocumentType>().Select(type => type.ToWire()).ToHashSet()
            .SetEquals(DocumentTypesByCountry.All));
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --filter "FullyQualifiedName~DocumentTypesByCountryTests"
```

Expected: build error `CS0234: The type or namespace name 'Identification' does not exist in the namespace 'BuildingBlocks.Domain'`.

- [ ] **Step 3: Implement `DocumentTypesByCountry` in `src/BuildingBlocks/BuildingBlocks.Domain/Identification/DocumentTypesByCountry.cs`**

Static arrays per country; `IsKnown` / `IsAllowed` use `Trim()` and `StringComparer.OrdinalIgnoreCase`; `AllowedFor` switches on `country.Trim().ToUpperInvariant()`. The summary says it is the same table as `CompanyDocumentTypes.AllowedFor` and that the parity test lives in `Modules.Companies.UnitTests`.

- [ ] **Step 4: Run to verify it passes**

Same command. Expected: `Passed!` with 21 tests, 0 failed.

- [ ] **Step 5: Commit**

```powershell
git add src/BuildingBlocks/BuildingBlocks.Domain/Identification/DocumentTypesByCountry.cs tests/Modules/Companies/Modules.Companies.UnitTests/DocumentTypesByCountryTests.cs
git commit -m "feat(domain): add the shared document type by country catalogue"
```

---

### Task 2: Customers know the seven codes; Colombian-only entry points stay on `CO`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/IdentificationType.cs:14-62` (enum, `ByWireValue`, `ToWireValue`, `Parse` message built from `SupportedWireValues`, class comment)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs:72-78,150-160` (`ExcelCustomerRowRules`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/GetCustomerImportTemplate.cs:49`
- Modify: `src/Bootstrapper/ServiceContractsCustomerProvisioning.cs:71-73`
- Test: create `tests/Modules/Customers/Modules.Customers.UnitTests/IdentificationTypeParserTests.cs`; modify `ExcelCustomerRowRulesTests.cs`, `CustomerProvisioningTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/PosCustomerLookupTests.cs:97-104`; create `tests/Bootstrapper/Bootstrapper.UnitTests/ServiceContractsIdentificationTypesTests.cs`

**Interfaces:**
- Consumes: `DocumentTypesByCountry.All`, `DocumentTypesByCountry.AllowedFor("CO")` (Task 1).
- Produces: `IdentificationType.Nif`, `.Nie`, `.TaxId` (appended after `Pasaporte`); wire `NIF`, `NIE`, `TAX_ID`; `public static IReadOnlyList<string> ExcelCustomerRowRules.IdentificationTypes` (= `DocumentTypesByCountry.AllowedFor("CO")`), used by the row rule, its message and the template.

- [ ] **Step 1: Write the failing parser tests**

```csharp
public sealed class IdentificationTypeParserTests
{
    [Fact]
    public void TheSupportedValuesAreTheSharedCatalogue() =>
        Assert.True(IdentificationTypeParser.SupportedWireValues.ToHashSet(StringComparer.Ordinal).SetEquals(DocumentTypesByCountry.All));

    [Theory]
    [InlineData("NIT")] [InlineData("CC")] [InlineData("CE")] [InlineData("PASAPORTE")]
    [InlineData("NIF")] [InlineData("NIE")] [InlineData("TAX_ID")]
    public void EveryCodeRoundTripsInAnyCase(string code)
    {
        Assert.Equal(code, IdentificationTypeParser.Parse(code).ToWireValue());
        Assert.Equal(code, IdentificationTypeParser.Parse(code.ToLowerInvariant()).ToWireValue());
    }

    [Fact]
    public void TheMemberNamesAreTheStoredValues() =>
        Assert.Equal(["Nit", "Cc", "Ce", "Pasaporte", "Nif", "Nie", "TaxId"], Enum.GetNames<IdentificationType>());

    [Fact]
    public void AnUnknownCodeFailsAndTheMessageListsTheNewCodes()
    {
        var error = Assert.Throws<CustomersDomainException>(() => IdentificationTypeParser.Parse("XX"));
        Assert.Equal("customers.customer.identification_type_invalid", error.Code);
        Assert.Contains("NIF", error.Message);
    }
}
```

Add to `CustomerProvisioningTests`:

```csharp
[Fact]
public async Task CreateCompleteCreatesASpanishCustomerWithANif()
{
    var bed = NewBed();
    var data = Buyer() with { IdentificationType = "NIF", IdentificationNumber = "12345678Z", Country = "ES", CityId = null, CityName = "Madrid" };

    await bed.Provisioning.CreateCompleteAsync(Tenant, data, TestContext.Current.CancellationToken);

    var customer = Assert.Single(bed.Customers.Added);
    Assert.Equal(IdentificationType.Nif, customer.IdentificationType);
    Assert.Equal("ES", customer.Country);
}
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~IdentificationTypeParserTests|FullyQualifiedName~CreateCompleteCreatesASpanishCustomerWithANif"
```

Expected: build error `CS0117: 'IdentificationType' does not contain a definition for 'Nif'`.

- [ ] **Step 3: Widen `IdentificationType` and `IdentificationTypeParser`**

Append the three members, their wire codes in `ByWireValue` and `ToWireValue`; build the `Parse` message as `$"The identification type must be one of {string.Join(", ", SupportedWireValues)}."`; rewrite the class comment (seven codes, the per-country table lives in `DocumentTypesByCountry`). `ByWireValue` stays an explicit dictionary; the parity test ties it.

- [ ] **Step 4: Run to verify they pass**

Same command. Expected: PASS.

- [ ] **Step 5: Write the Colombian-only tests and see the widening turn them red**

In `ExcelCustomerRowRulesTests`:

```csharp
[Theory]
[InlineData("NIF")] [InlineData("NIE")] [InlineData("TAX_ID")]
public void ACodeOutsideColombiaFailsWithTheInvalidCode(string code)
{
    var result = validator.Validate(ValidRow(identificationType: code));
    Assert.Contains(result.Errors, error => error.ErrorCode == "customers.import.row.identification_type_invalid");
}

[Fact]
public void TheImportOffersOnlyTheColombianCodes() =>
    Assert.Equal(["NIT", "CC", "CE", "PASAPORTE"], ExcelCustomerRowRules.IdentificationTypes);
```

Replace the body of `PosCustomerLookupTests.TheIdentificationTypesMatchTheCustomersModule` (keep the name, update its comment):

```csharp
Assert.True(PosCustomerRules.IdentificationTypes.ToHashSet(StringComparer.OrdinalIgnoreCase)
    .SetEquals(DocumentTypesByCountry.AllowedFor("CO")));
Assert.All(PosCustomerRules.IdentificationTypes,
    type => Assert.Contains(type, IdentificationTypeParser.SupportedWireValues, StringComparer.OrdinalIgnoreCase));
```

New `ServiceContractsIdentificationTypesTests`:

```csharp
[Fact]
public void TheImportOffersOnlyTheColombianCodes() =>
    Assert.Equal(DocumentTypesByCountry.AllowedFor("CO"), new ServiceContractsIdentificationTypes().SupportedWireValues);
```

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~ExcelCustomerRowRulesTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PosCustomerLookupTests|FullyQualifiedName~ServiceContractsIdentificationTypesTests"
```

Expected: Customers — build error `CS0117: 'ExcelCustomerRowRules' does not contain a definition for 'IdentificationTypes'`; Bootstrapper — the Pos parity assertion fails (`Assert.True() Failure`) and the service-contract test fails with `Assert.Equal() Failure` (seven values against four).

- [ ] **Step 6: Pin the three entry points to `AllowedFor("CO")`**

`ExcelCustomerRowRules.IdentificationTypes => DocumentTypesByCountry.AllowedFor("CO")`; `IsSupportedIdentificationType` checks membership in it (trimmed, `OrdinalIgnoreCase`) instead of `Parse`; the message joins `IdentificationTypes`. `GetCustomerImportTemplateHandler` passes `ExcelCustomerRowRules.IdentificationTypes` to `templateBuilder.Build`. `ServiceContractsIdentificationTypes.SupportedWireValues => DocumentTypesByCountry.AllowedFor("CO")`; update its summary.

- [ ] **Step 7: Run both whole projects**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
```

Expected: PASS, 0 failed.

- [ ] **Step 8: Commit**

```powershell
git add src/Modules/Customers/Modules.Customers.Domain/IdentificationType.cs src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs src/Modules/Customers/Modules.Customers.Application/GetCustomerImportTemplate.cs src/Bootstrapper/ServiceContractsCustomerProvisioning.cs tests/Modules/Customers/Modules.Customers.UnitTests tests/Bootstrapper/Bootstrapper.UnitTests
git commit -m "feat(customers): accept the seven document types and keep Colombian imports on CO"
```

---

### Task 3: Customers enforce type-allowed-for-country

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/IdentificationType.cs` (add `IsAllowedFor`)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomersErrorCodes.cs` (add `DocumentTypeNotAllowed`)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs:54-69` (constructor), `:386-424` (`CreateFromRegister`), `:432-460` (`Complete`), `:859-888` (`Update`), new private check next to `EnsurePersonTypeMatches` (`:1238`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs:87-92` (second rule)
- Modify: `src/Bootstrapper/ShopifyCustomerDirectory.cs:22-24` (`NotCompletableCodes`)
- Test: create `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerDocumentCountryTests.cs` (domain) and `CustomerDocumentCountryRulesTests.cs` (validator); modify `CustomerProvisioningTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/ShopifyAdaptersTests.cs`, `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyImportFlowTests.cs`, `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerWriteApiTests.cs`, `CustomersApiHarness.cs`; sweep (Step 7)

**Interfaces:**
- Consumes: `DocumentTypesByCountry.IsKnown/IsAllowed` (Task 1); `IdentificationType.Nif/Nie/TaxId` (Task 2).
- Produces: `public static bool IsAllowedFor(this IdentificationType type, string country)` in `IdentificationTypeParser`; `CustomersErrorCodes.DocumentTypeNotAllowed = "customers.customer.document_type_not_allowed"`; private `Customer.EnsureDocumentAllowedForCountry(IdentificationType type, string country)` throwing `CustomersDomainException(DocumentTypeNotAllowed, "The identification type is not allowed for the country.", { ["IdentificationType"] = [same message] })`.

- [ ] **Step 1: Write the failing domain tests**

`CustomerDocumentCountryTests` copies the `Colombian()`, `Spanish()`, `Commercial()` and `Create(...)` helpers of `CustomerPersonTypeTests`, and adds `const string NotAllowed = "customers.customer.document_type_not_allowed"`:

```csharp
[Fact]
public void CreateRejectsATypeTheCountryDoesNotAllow()
{
    var error = Assert.Throws<CustomersDomainException>(() => Create(IdentificationType.Cc, null, Spanish()));
    Assert.Equal(NotAllowed, error.Code);
    Assert.Equal(["IdentificationType"], error.FieldErrors.Keys);
}

[Fact]
public void CreateAcceptsASpanishNif() =>
    Assert.Equal(IdentificationType.Nif, Create(IdentificationType.Nif, null, Spanish()).IdentificationType);

[Fact]
public void CompleteRejectsAndLeavesTheCustomerIncomplete()
{
    var incomplete = Customer.CreateIncomplete(CustomerId.New(), TenantId, "Alguien", "+34600111222", "ES", "bsuid-1", Now, new FakeNormalizer());

    Assert.Throws<CustomersDomainException>(() => incomplete.Complete(
        "CLI08000001", "Laura", null, null, Identification(IdentificationType.Cc, "1020304050"), null, Spanish(), Commercial(), Now.AddHours(1)));

    Assert.False(incomplete.IsComplete);
    Assert.Null(incomplete.IdentificationType);
}

[Fact]
public void UpdateRejectsAndAssignsNothing()
{
    var customer = Create(IdentificationType.Cc, null);
    var version = customer.Version;

    Assert.Throws<CustomersDomainException>(() => customer.Update(
        "Otro nombre", null, Identification(IdentificationType.Cc, "1020304050"), null, Spanish(), Commercial(), "CLI", Now.AddHours(1)));

    Assert.Equal("Verde Esencial", customer.Name);
    Assert.Equal("CO", customer.Country);
    Assert.Equal(version, customer.Version);
}

[Fact]
public void AColombianLegalEntityWithANifIsACountryProblemFirst() =>
    Assert.Equal(NotAllowed, Assert.Throws<CustomersDomainException>(() => Create(IdentificationType.Nif, PersonType.Juridica)).Code);

[Fact]
public void TheRegisterOnlyTakesColombianTypes() =>
    Assert.Equal(NotAllowed, Assert.Throws<CustomersDomainException>(() => Customer.CreateFromRegister(
        CustomerId.New(), TenantId, "Laura", Identification(IdentificationType.Nif, "12345678Z"), "3001234567", Now, new FakeNormalizer())).Code);
```

- [ ] **Step 2: Write the failing validator, provisioning and Shopify tests**

`CustomerDocumentCountryRulesTests` (uses the public `CreateCustomerValidator`):

```csharp
private static readonly CreateCustomerValidator Validator = new();

private static CreateCustomerCommand Command(string type, string country) =>
    new(CustomerWriteDoubles.Tenant, "Laura Pérez", null, type, "X1234567", "310 935 2187", "laura@verde.co", "Calle 10",
        country, country.Trim().Equals("CO", StringComparison.OrdinalIgnoreCase) ? CustomerWriteDoubles.CityId : null,
        country.Trim().Equals("CO", StringComparison.OrdinalIgnoreCase) ? null : "Madrid", null, false, false);

[Theory]
[InlineData("NIF", "ES")] [InlineData("NIE", "ES")] [InlineData("TAX_ID", "US")]
[InlineData("CC", "CO")] [InlineData("PASAPORTE", "ES")] [InlineData(" nif ", "es")]
public void AnAllowedPairIsValid(string type, string country) => Assert.True(Validator.Validate(Command(type, country)).IsValid);

[Theory]
[InlineData("NIF", "CO")] [InlineData("CC", "ES")] [InlineData("NIT", "US")] [InlineData("TAX_ID", "CO")]
public void ATypeTheCountryDoesNotAllowFailsOnTheTypeOnly(string type, string country)
{
    var errors = Validator.Validate(Command(type, country)).Errors;
    Assert.Equal(["IdentificationType"], errors.Select(error => error.PropertyName).Distinct());
    Assert.Equal("The identification type is not allowed for the country.", Assert.Single(errors).ErrorMessage);
}

[Fact]
public void AMalformedCountryIsReportedOnTheCountryAlone()
{
    var fields = Validator.Validate(Command("NIF", "COL")).Errors.Select(error => error.PropertyName).ToList();
    Assert.Contains("Country", fields);
    Assert.DoesNotContain("IdentificationType", fields);
}

[Fact]
public void AnUnknownTypeKeepsTheListMessage() =>
    Assert.Contains("NIF", Assert.Single(Validator.Validate(Command("XX", "CO")).Errors, error => error.PropertyName == "IdentificationType").ErrorMessage);
```

In `CustomerProvisioningTests`:

```csharp
[Fact]
public async Task CreateCompleteRejectsAColombianTypeAbroad()
{
    var bed = NewBed();
    var data = Buyer() with { Country = "US", CityId = null, CityName = "Miami" };

    var error = await Assert.ThrowsAsync<ValidationException>(() => bed.Provisioning.CreateCompleteAsync(Tenant, data, TestContext.Current.CancellationToken));

    Assert.Contains(error.Errors, failure => failure.PropertyName == "IdentificationType");
    Assert.Empty(bed.Customers.Added);
}
```

In `ShopifyAdaptersTests` (pattern of `CustomerDirectoryCreateCompleteThatCannotCompleteThrowsItsOwnException`):

```csharp
[Fact]
public async Task CustomerDirectoryTreatsADocumentNotAllowedForTheCountryAsNotCompletable()
{
    var provisioning = new FakeCustomerProvisioning
    {
        OnComplete = () => throw new CustomersDomainException("customers.customer.document_type_not_allowed", "No.")
    };
    var directory = new ShopifyCustomerDirectory(new FakeCustomerRepository(), new FakeCustomerMergeStore(), provisioning, new PassThroughPhones());

    var error = await Assert.ThrowsAsync<ShopifyCustomerNotCompletableException>(() => directory.CreateCompleteAsync(TenantId, CompleteCustomer(), Ct));

    Assert.Equal("customers.customer.document_type_not_allowed", error.Reason);
}
```

Shopify end to end (§6.4.5), in `tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyImportFlowTests.cs` (pattern of `ImportWithoutDocumentIncompleteAndPendingOrder`). The unit tests of `ShopifyCustomerResolver` and of the directory use a fake provisioning, so only this test can go red:

```csharp
[Fact]
public async Task ImportWithAColombianDocumentShippingAbroadCreatesAnIncompleteCustomer()
// flow.DeliverAsync("orders/create", "wh-1", Fixture("taxes-included", order => {
//     var address = order["shipping_address"]!; address["country_code"] = "US"; address["country"] = "United States";
//     address["city"] = "Miami"; address["province"] = null; address["province_code"] = null; }));
// → mirror import_state "Imported"; customer completeness "Incomplete";
//   string_agg(w ->> 'code', ',') of the mirror warnings contains "customer_incomplete"
```

- [ ] **Step 3: Run to verify they fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerDocumentCountry|FullyQualifiedName~CreateCompleteRejectsAColombianTypeAbroad"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~CustomerDirectoryTreatsADocumentNotAllowedForTheCountryAsNotCompletable"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests --filter "FullyQualifiedName~ImportWithAColombianDocumentShippingAbroadCreatesAnIncompleteCustomer" -- xUnit.MaxParallelThreads=4
```

Expected: `Assert.Throws() Failure: No exception was thrown` (domain), `Assert.True() Failure` / `Assert.Equal() Failure` (validator accepts `ES` + `CC`), `ThrowsAsync … No exception was thrown` (provisioning), `Assert.Throws() Failure: Exception type was not an exact match … Actual: typeof(CustomersDomainException)` (Shopify directory), and `Assert.Equal() Failure … Expected: Incomplete Actual: Complete` (Shopify flow).

- [ ] **Step 4: Implement the rule**

- `IsAllowedFor` → `DocumentTypesByCountry.IsAllowed(type.ToWireValue(), country)`.
- `Customer`: call `EnsureDocumentAllowedForCountry` **after** the contact is normalized and its location validated and **before** `EnsurePersonTypeMatches`, in the constructor (country `contact.Normalized().Country`), `Complete` and `Update`; in `CreateFromRegister` call it with `normalizedIdentification.Type` and `ColombiaCountryCode` before building the customer. Never in the EF constructor (`:49-52`). Its summary says why it runs first (a `CO` legal entity with `NIF` is a country problem before a NIT problem).
- `CustomerWriteRules`: after the known-code rule, `RuleFor(c => c.IdentificationType).Must((c, type) => DocumentTypesByCountry.IsAllowed(type, c.Country)).WithMessage("The identification type is not allowed for the country.").When(c => DocumentTypesByCountry.IsKnown(c.IdentificationType) && c.Country is { Length: CustomerContactInfo.CountryLength } country && country.All(char.IsAsciiLetter))`.
- `ShopifyCustomerDirectory.NotCompletableCodes` gets `"customers.customer.document_type_not_allowed"`; update the comment above it ("los tres códigos").

- [ ] **Step 5: Run to verify they pass**

Same commands. Expected: PASS.

- [ ] **Step 6: Write the failing Customers API tests**

In `CustomersApiHarness`: change the `NewForeignCustomerBody` default `identificationType` from `"NIT"` to `"NIF"`, and add, next to `MarkMergedAsync`:

```csharp
public static async Task ForceIdentificationAsync(string connectionString, Guid customerId, string country, string? cityName, string identificationType)
// UPDATE customers.customers SET country = @country, city_id = NULL, city_name = @cityName, identification_type = @type WHERE id = @id
```

In `CustomerWriteApiTests` (pattern of `CreateAForeignCustomerStoresTheHandwrittenCityAndNoDivipolaCity`):

```csharp
[Fact]
public async Task ASpanishNifRoundTripsThroughPostGetAndPut()
// POST NewForeignCustomerBody(classification.Id, identificationNumber: "12345678Z") → 201;
// GET → IdentificationType == "NIF"; PUT the same body → 200 and IdentificationType == "NIF".

[Fact]
public async Task AColombianTypeForASpanishCustomerIsA422OnIdentificationType()
// POST NewForeignCustomerBody(classification.Id, identificationType: "CC") → 422,
// problem code "validation.failed", await ValidationFieldsAsync(response) == ["IdentificationType"].

[Fact]
public async Task ANonConformingStoredCustomerStillReadsAndIsFixedOnItsNextSave()
// CreateCustomerAsync (CO + NIT) → ForceIdentificationAsync(cs, id, "ES", "Madrid", "Cc");
// GET → 200 with IdentificationType "CC"; PUT NewForeignCustomerBody(identificationType: "CC") → 422 on IdentificationType;
// PUT NewForeignCustomerBody() (NIF) → 200.
```

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerWriteApiTests" -- xUnit.MaxParallelThreads=4
```

Expected after Step 4 is in place: the three new tests PASS (they pin behaviour already driven RED by Steps 1-3; record the output). Record any other failure for Step 7.

- [ ] **Step 7: Sweep the fixtures that encoded "any type for any country"**

Rule: a fixture whose test is not about the document type takes `PASAPORTE` (valid everywhere) or the country's own code; a test that asserted the old acceptance is rewritten to the new rule. Known breakers:
- `CustomerPersonTypeTests.AForeignJuridicaCustomerMayKeepACc` → `AForeignJuridicaCustomerMayKeepANif` with `IdentificationType.Nif`.
- `CustomerTests.CreateForeign` and `UpdateCanMoveACustomerAbroad` (default `Identification()` is `Nit`) → pass `Identification(IdentificationType.Pasaporte, "X1234567")`.
- `CustomerWriteHandlersTests.AForeignCustomerOfAColombianTenantSavesWithoutPersonType` (`:288`) → `IdentificationType = "NIF"`.
- Customers integration foreign tests are fixed by the harness default (Step 6).

Find the rest with:

```powershell
rg -n '"(ES|US|MX)"' tests/Modules/Customers tests/Bootstrapper --glob "*.cs"
```

- [ ] **Step 8: Run the projects**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests --filter "FullyQualifiedName~ShopifyImportFlowTests" -- xUnit.MaxParallelThreads=4
```

Expected: PASS, 0 failed.

- [ ] **Step 9: Commit**

```powershell
git add src/Modules/Customers src/Bootstrapper/ShopifyCustomerDirectory.cs tests/Modules/Customers tests/Bootstrapper/Bootstrapper.UnitTests tests/Modules/Shopify/Modules.Shopify.IntegrationTests/ShopifyImportFlowTests.cs
git commit -m "feat(customers): enforce the document type allowed for the customer country"
```

---

### Task 4: "Tomar" — an incomplete customer without a document takes the request's document and country

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs:124-131` (interface comment), `:374-403` (`MergeForStore` and its comment)
- Modify: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerProvisioningTests.cs` (`NewBed` gains `IPhoneNumberNormalizer? phones = null`, default `new PlusNormalizer()`)
- Modify: `tests/Modules/Store/Modules.Store.IntegrationTests/StoreInboxRig.cs:288-315` (`SeedIncompleteAsync` gains `string? country = null`, inserted into `country`), `StoreTakeApiTests.cs`

**Interfaces:**
- Consumes: Task 3's strict `CustomerWriteRules` (that is what makes the old merge fail).
- Produces: `MergeForStore` rule, used by Task 11:
  - `hasDocument` → document and country from the customer (`Keep(customer.Country, data.Country)`), as today.
  - no document → document **and** `data.Country`.
  - "country replaced" = no document **and** `customer.Country` is not null **and** differs from `data.Country` (trimmed, case-insensitive). Only then: address = `data.Address`, and phone = `customer.PhoneE164` when the customer's phone is kept and `PhoneE164` is not null. Otherwise address and phone are `Keep(...)` as today.

- [ ] **Step 1: Write the failing unit tests** (use `new LibPhoneNumberNormalizer()` from `Modules.Customers.Infrastructure.Phones`; it is visible to the test project)

```csharp
private static CompleteCustomerData SpanishBuyer() =>
    Buyer() with { IdentificationType = "NIF", IdentificationNumber = "12345678Z", Country = "ES", CityId = null, CityName = "Madrid", Phone = "+34 612 34 56 78", Address = "Calle Mayor 1" };

[Fact]
public async Task CompleteFromStoreWithoutDocumentTakesTheRequestDocumentAndCountry()
{
    var phones = new LibPhoneNumberNormalizer();
    var customer = Customer.CreateIncomplete(CustomerId.New(), Tenant, "Pedro", "+573101112233", "CO", "CO.1", Now, phones);
    var bed = NewBed(customer, phones: phones);

    await bed.Provisioning.CompleteFromStoreAsync(Tenant, customer.Id.Value, SpanishBuyer(), Guid.CreateVersion7(), TestContext.Current.CancellationToken);

    Assert.True(customer.IsComplete);
    Assert.Equal((IdentificationType.Nif, "12345678Z", "ES", "Madrid"), (customer.IdentificationType, customer.IdentificationNumber, customer.Country, customer.CityName));
    Assert.Equal("Calle Mayor 1", customer.Address);
}

[Fact]
public async Task CompleteFromStoreAbroadKeepsANationalPhoneAsItsE164()
{
    var phones = new LibPhoneNumberNormalizer();
    var customer = Customer.CreateIncompleteFromExternalSale(CustomerId.New(), Tenant, "Laura", "3001234567", null, "CO", Now, phones);
    var bed = NewBed(customer, phones: phones);

    await bed.Provisioning.CompleteFromStoreAsync(Tenant, customer.Id.Value, SpanishBuyer(), Guid.CreateVersion7(), TestContext.Current.CancellationToken);

    Assert.Equal(("+573001234567", "+573001234567"), (customer.Phone, customer.PhoneE164));
}

[Fact]
public async Task CompleteFromStoreWithoutCountryKeepsTheRawPhone()   // Review Focus 4
{
    var phones = new LibPhoneNumberNormalizer();
    var customer = Customer.CreateIncompleteFromExternalSale(CustomerId.New(), Tenant, "Laura", "612345678", null, null, Now, phones);
    var bed = NewBed(customer, phones: phones);

    await bed.Provisioning.CompleteFromStoreAsync(Tenant, customer.Id.Value, SpanishBuyer(), Guid.CreateVersion7(), TestContext.Current.CancellationToken);

    Assert.Equal(("612345678", "ES", IdentificationType.Nif), (customer.Phone, customer.Country, customer.IdentificationType));
}

[Fact]
public async Task CompleteFromStoreWithItsOwnDocumentKeepsItsDocumentAndCountry()
{
    var customer = Customer.CreateFromRegister(CustomerId.New(), Tenant, "Laura", new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" }, "3001234567", Now, new PlusNormalizer());
    var bed = NewBed(customer);

    await bed.Provisioning.CompleteFromStoreAsync(Tenant, customer.Id.Value, Buyer() with { IdentificationNumber = "999" }, Guid.CreateVersion7(), TestContext.Current.CancellationToken);

    Assert.Equal((IdentificationType.Cc, "1020304050", "CO"), (customer.IdentificationType, customer.IdentificationNumber, customer.Country));
}
```

- [ ] **Step 2: Write the failing "Tomar" integration test**

Uses `PASAPORTE`, which today's Store already accepts for `ES`, so the RED is the merge and not the store:

```csharp
[Fact]
public async Task AnIncompleteCustomerWithoutDocumentTakesTheRequestCountryWithItsDocument()
{
    await using var rig = await StoreInboxRig.OpenAsync((_, body) => body["salesCountries"] = ColombiaAndSpain);
    var customerId = await rig.SeedIncompleteAsync("Pedro WhatsApp", phone: "+57 310 111 2233", phoneE164: "+573101112233", country: "CO");
    var request = await rig.SubmitAsync(billing =>
    {
        billing["identificationType"] = "PASAPORTE"; billing["identificationNumber"] = "X1234567";
        billing["country"] = "ES"; billing["departmentId"] = null; billing["cityId"] = null; billing["cityName"] = "Madrid";
        billing["phone"] = "+34600111222"; billing["address"] = "Calle Mayor 1";
    });

    await rig.TakeOkAsync(request, ExistingBody(customerId));

    var customer = await rig.CustomerAsync(customerId);
    Assert.Equal(("Complete", "Pasaporte", "ES", "Madrid"), (customer.Completeness, customer.IdentificationType, customer.Country, customer.CityName));
    Assert.Equal("+573101112233", customer.Phone);
}
```

- [ ] **Step 3: Run to verify they fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CompleteFromStore"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~AnIncompleteCustomerWithoutDocumentTakesTheRequestCountryWithItsDocument" -- xUnit.MaxParallelThreads=4
```

Expected: `FluentValidation.ValidationException : Validation failed: -- IdentificationType: The identification type is not allowed for the country.` (first unit test); `Assert.Equal() Failure` on `PhoneE164` (second); the integration test fails on `TakeOkAsync` with `422 validation.failed` (the customer stays `CO` and has no `CityId`). The third and fourth unit tests may already pass; record it.

- [ ] **Step 4: Implement the new `MergeForStore` rule** (Interfaces block above) and rewrite both comments: the document and the country come together, from the customer when it has a document and from the store otherwise; when the country is replaced, the address is the store's and the phone keeps its E.164 form computed with the old country.

- [ ] **Step 5: Run to verify they pass**

Same commands. Then the whole `StoreTakeApiTests` and `CustomerProvisioningIntegrationTests` classes:

```powershell
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreTakeApiTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerProvisioningIntegrationTests" -- xUnit.MaxParallelThreads=4
```

Expected: PASS. `AnIncompleteCustomerIsCompletedOnlyInItsBlankFields` and `CompletingNeverOverwritesAnExistingValue` keep passing (seeded with a null country: nothing is replaced).

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Customers/Modules.Customers.Application/CustomerProvisioning.cs tests/Modules/Customers/Modules.Customers.UnitTests/CustomerProvisioningTests.cs tests/Modules/Store/Modules.Store.IntegrationTests/StoreInboxRig.cs tests/Modules/Store/Modules.Store.IntegrationTests/StoreTakeApiTests.cs
git commit -m "feat(customers): take the request country with its document when completing from the store"
```

---

### Task 5: Customer merge fills document and country as a pair

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerMerge.cs:58-63`
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerMergePlanTests.cs`

**Interfaces:**
- Consumes: `IdentificationType.IsAllowedFor` (Task 3).
- Produces (incomplete survivor only):
  - `_identificationFrom` = first merged customer with a document whose type `IsAllowedFor(survivor.Country ?? merged.Country)`; a candidate is skipped when both countries are null (no public factory produces that, so it has no test).
  - `_countryFrom` = when the survivor has no country: `_identificationFrom` if set, otherwise the first merged with a country (as today).
  - `FilledFields` keeps following the two fields; `MarkMerged` keeps releasing the document only from `_identificationFrom`.

- [ ] **Step 1: Write the failing tests** (add a helper `WhatsAppFrom(string country, string phone, string bsuid)` beside `WhatsApp()`)

```csharp
[Fact]
public void ASurvivorWithoutCountryTakesTheDocumentAndTheCountryFromTheSameMerged()
{
    var survivor = Instagram();
    var spanish = WhatsAppFrom("ES", "+34600111222", "ES.1");
    var register = Register("1020");

    var plan = CustomerMergePlan.Create(survivor, [spanish, register]);
    plan.MarkMerged(Now);
    plan.FillSurvivor(Now);

    Assert.Equal((IdentificationType.Cc, "CO"), (survivor.IdentificationType, survivor.Country));
    Assert.Contains(CustomerMergeField.Identification, plan.FilledFields);
    Assert.Contains(CustomerMergeField.Country, plan.FilledFields);
}

[Fact]
public void ASurvivorNeverTakesADocumentItsCountryDoesNotAllow()
{
    var survivor = WhatsAppFrom("ES", "+34600111222", "ES.1");
    var register = Register("1020");

    var plan = CustomerMergePlan.Create(survivor, [register]);
    plan.MarkMerged(Now);
    plan.FillSurvivor(Now);

    Assert.DoesNotContain(CustomerMergeField.Identification, plan.FilledFields);
    Assert.Null(survivor.IdentificationType);
    Assert.Equal(IdentificationType.Cc, register.IdentificationType);
}
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerMergePlanTests"
```

Expected: `Assert.Equal() Failure … Expected: (Cc, "CO") Actual: (Cc, "ES")` and `Assert.DoesNotContain() Failure … Identification`.

- [ ] **Step 3: Implement the pair rule** in the constructor of `CustomerMergePlan` (Interfaces block), with a comment that a document never lands under a country that does not allow it.

- [ ] **Step 4: Run the whole class, then the merge preview**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerMerge"
```

Expected: PASS (existing `AnIncompleteSurvivorTakesTheDocumentOfAnIncompleteMerged` keeps passing: a `CO` WhatsApp survivor takes a `CO` register document).

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Customers/Modules.Customers.Domain/CustomerMerge.cs tests/Modules/Customers/Modules.Customers.UnitTests/CustomerMergePlanTests.cs
git commit -m "feat(customers): merge the document and the country of an incomplete survivor as a pair"
```

---

### Task 6: Quotation and order parties know the seven codes

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/PartyFiscalTypes.cs:14-100` (class comment, enum, dictionary, `ToWireValue`, message at `:96`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationEdits.cs:124`, `OrderEdits.cs:168` (messages built from `SupportedIdentificationTypes`)
- Modify comments: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs:224-225`, `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPartyDetails.cs:22-23`
- Test: create `tests/Modules/Quotations/Modules.Quotations.UnitTests/PartyFiscalTypesParserTests.cs`; modify `CreateQuotationValidatorTests.cs`, `SaveOrderEditsValidatorTests.cs`, `OrderStoreFactoryTests.cs`

**Interfaces:**
- Consumes: `DocumentTypesByCountry.All` (Task 1).
- Produces: `PartyIdentificationType.Nif`, `.Nie`, `.TaxId` (same names as Customers); wire `NIF`, `NIE`, `TAX_ID`.

- [ ] **Step 1: Write the failing tests**

`PartyFiscalTypesParserTests`: the same four tests as `IdentificationTypeParserTests` (Task 2), against `PartyFiscalTypesParser.SupportedIdentificationTypes`, `ParseIdentificationType(code)!.Value.ToWireValue()`, `Enum.GetNames<PartyIdentificationType>()` and the code `quotations.party.identification_type_invalid`.

`CreateQuotationValidatorTests` (key `Parties.Billing.IdentificationType`) and `SaveOrderEditsValidatorTests` (key `Billing.Party.IdentificationType`), each:

```csharp
private static QuotationPartyRequest Party(string type, string? country, string? cityName = null) =>
    new("Otra S.L.", "12345678Z", null, null, null, null, null, IdentificationType: type, Country: country, CityName: cityName);

[Theory]
[InlineData("NIF", "ES", "Madrid")] [InlineData("NIE", "ES", "Madrid")] [InlineData("TAX_ID", "US", "Miami")] [InlineData("CC", null, null)]
public async Task AnAllowedBillingPairIsAccepted(string type, string? country, string? cityName)
// result has no error whose PropertyName is the key

[Fact]
public async Task AnUnknownBillingTypeNamesTheNewCodes()
// Party("XX", null): the error at the key has a message containing "NIF"
```

(Orders use `_save.Validate(Command(billing: new OrderBillingEditRequest(Party(...))))` synchronously.)

`OrderStoreFactoryTests`:

```csharp
[Fact]
public void AStoreOrderKeepsAForeignTaxId()
// Billing() with { IdentificationType = PartyIdentificationType.TaxId, Country = "US", CityName = "Miami", DepartmentId = null, CityId = null }
// → Order.CreateFromStore(...) billing party IdentificationType == TaxId and Country == "US"
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~PartyFiscalTypesParserTests|FullyQualifiedName~CreateQuotationValidatorTests|FullyQualifiedName~SaveOrderEditsValidatorTests|FullyQualifiedName~OrderStoreFactoryTests"
```

Expected: build error `CS0117: 'PartyIdentificationType' does not contain a definition for 'Nif'`.

- [ ] **Step 3: Widen the enum and parser; build the three messages from `SupportedIdentificationTypes`; rewrite the four comments** (seven codes, per-country table in `DocumentTypesByCountry`, still duplicated from Customers on purpose).

- [ ] **Step 4: Run to verify they pass**

Same command, then the whole project:

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Quotations tests/Modules/Quotations/Modules.Quotations.UnitTests
git commit -m "feat(quotations): accept the seven document types on billing parties"
```

---

### Task 7: Billing parties enforce type-allowed-for-country

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/PartyFiscalTypes.cs` (add `IsAllowedFor`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationPartyDetails.cs:76-106` (`Normalized`, new private check beside `EnsureLane`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationEdits.cs:121-125`, `OrderEdits.cs:165-169` (second rule)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderPartyTests.cs`, `CreateQuotationValidatorTests.cs`, `SaveOrderEditsValidatorTests.cs`; integration `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/StoreOrderGatewayTests.cs`, `QuotationPartiesApiTests.cs`, `OrderEditsApiTests.cs`

**Interfaces:**
- Consumes: Task 6 enum; `DocumentTypesByCountry.IsAllowed`.
- Produces: `public static bool IsAllowedFor(this PartyIdentificationType type, string? country)` (blank → `"CO"`); domain code `quotations.party.document_type_not_allowed` with field errors `{fieldPrefix}.IdentificationType` (prefix is `Parties.Billing` for both quotation and order parties).

- [ ] **Step 1: Write the failing unit tests**

`OrderPartyTests`:

```csharp
[Fact]
public void ABillingPartyWithATypeItsCountryDoesNotAllowIsRejected()
{
    var error = Assert.Throws<QuotationsDomainException>(() => Party(
        Contact() with { Country = "ES", CityName = "Madrid", IdentificationType = PartyIdentificationType.Cc, IdentificationNumber = "1" }, QuotationPartyRole.Billing));
    Assert.Equal("quotations.party.document_type_not_allowed", error.Code);
    Assert.Contains("Parties.Billing.IdentificationType", error.FieldErrors.Keys);
}

[Fact]
public void ABlankCountryIsColombiaForTheDocument() =>
    Assert.Throws<QuotationsDomainException>(() => Party(Contact() with { IdentificationType = PartyIdentificationType.Nif, IdentificationNumber = "1" }, QuotationPartyRole.Billing));

[Fact]
public void AShippingPartyIsNeverCheckedBecauseItHasNoDocument() =>
    Assert.Null(Party(Contact() with { Country = "ES", CityName = "Madrid", IdentificationType = PartyIdentificationType.Cc }).IdentificationType);

[Fact]
public void ABillingPartyKeepsASpanishNif() =>
    Assert.Equal(PartyIdentificationType.Nif, Party(Contact() with { Country = "ES", CityName = "Madrid", IdentificationType = PartyIdentificationType.Nif, IdentificationNumber = "1" }, QuotationPartyRole.Billing).IdentificationType);
```

Validators (both files, with the Task 6 `Party` helper):

```csharp
[Theory]
[InlineData("CC", "ES", "Madrid")] [InlineData("NIF", null, null)] [InlineData("TAX_ID", "CO", null)]
public async Task ABillingTypeItsCountryDoesNotAllowIsAFieldError(string type, string? country, string? cityName)
// exactly one error at the key, message "The identification type is not allowed for the country."

[Fact]
public async Task AMalformedCountryLeavesTheTypeAlone()
// Party("NIF", "ESP", "Madrid") → no error at the key
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderPartyTests|FullyQualifiedName~CreateQuotationValidatorTests|FullyQualifiedName~SaveOrderEditsValidatorTests"
```

Expected: `Assert.Throws() Failure: No exception was thrown` and `Assert.Single() Failure: The collection was empty`.

- [ ] **Step 3: Implement**

- `QuotationPartyDetails.Normalized`: after `EnsureLane`, if `IdentificationType is { } type && !type.IsAllowedFor(country)` throw the new code with `$"{fieldPrefix}.IdentificationType"`, same shape as `LaneInvalid`.
- Validators: second rule on the same property, `.Must((x, type) => PartyFiscalTypesParser.ParseIdentificationType(type)!.Value.IsAllowedFor(countryOf(x)))`, `.When` the type is in `SupportedIdentificationTypes` **and** the trimmed country is blank or two ASCII letters. Message: "The identification type is not allowed for the country.". (`CreateOrder` reuses `QuotationPartiesRequestValidator`, so manual order creation gets it too.)

- [ ] **Step 4: Run to verify they pass**

Same command, then the whole `Modules.Quotations.UnitTests` project. Expected: PASS.

- [ ] **Step 5: Write the integration tests and sweep**

`StoreOrderGatewayTests`:

```csharp
[Fact]
public async Task AForeignTaxIdBillingPartyRoundTrips()
// Draft(..., ForeignParty() with { Country = "US", CityName = "Miami", IdentificationType = "TAX_ID", IdentificationNumber = "12-3456789" })
// → LoadOrderAsync: billing party (PartyIdentificationType.TaxId, "US")

[Fact]
public async Task AColombianTypeAbroadIsRejectedAndCreatesNoOrder()
// ForeignParty() with { IdentificationType = "CC", IdentificationNumber = "1020304050" }
// → Assert.ThrowsAsync<QuotationsDomainException>, Code "quotations.party.document_type_not_allowed"; OrderRowsAsync == 0
```

`QuotationPartiesApiTests` (pattern of `AForeignPartyWithDivipolaIdsIsRejectedWithItsField`): `ASpanishBillingPartyWithACcIsA422OnItsType` (`ForeignBilling() with { IdentificationType = "CC" }` → 422, `Problem.Errors` contains `Parties.Billing.IdentificationType`) and `ASpanishBillingPartyWithANifRoundTrips` (200; saved and fetched billing `IdentificationType == "NIF"`). `OrderEditsApiTests`: `AnOrderBillingPartyWithACcAbroadIsA422OnItsType` (key `Billing.Party.IdentificationType`).

Sweep: `QuotationPartiesApiTests.ForeignBilling()` and `OrderEditsApiTests.TheOrderEditorAcceptsAForeignShippingParty` use `CE` with `ES` → `NIE` (the number `X1234567L` is already NIE-shaped).

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~StoreOrderGatewayTests|FullyQualifiedName~QuotationPartiesApiTests|FullyQualifiedName~OrderEditsApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: PASS (record it; these pin the behaviour Steps 1-4 drove RED).

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(quotations): enforce the document type allowed for the billing party country"
```

---

### Task 8: Store and Buyers validators (they move together)

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs:79` (remove list), `:125-128` (two rules)
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/BuyerProfileValidator.cs:25` (remove list), `:59-62` (two rules); update the class comment line about copied rules
- Test: `tests/Modules/Store/Modules.Store.UnitTests/SubmitOrderRequestValidatorTests.cs`, `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerProfileValidatorTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/BuyerParityTests.cs:121-131`; sweep in `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderRequestApiTests.cs`, `StoreTakeApiTests.cs`

**Interfaces:**
- Consumes: `DocumentTypesByCountry.IsKnown/IsAllowed`.
- Produces: in both `BillingValidator`s, rule 1 `NotEmpty().Must(type => type is null || DocumentTypesByCountry.IsKnown(type)).WithMessage("El tipo de documento no es válido.")`; rule 2 `Must((b, type) => DocumentTypesByCountry.IsAllowed(type, b.Country!)).WithMessage("El tipo de documento no es válido para el país.").When(b => DocumentTypesByCountry.IsKnown(b.IdentificationType) && !string.IsNullOrWhiteSpace(b.Country) && CountryShape(b.Country))`.

- [ ] **Step 1: Write the failing tests**

`SubmitOrderRequestValidatorTests` (and the same matrix in `BuyerProfileValidatorTests` with key `Billing.IdentificationType` on `new BuyerProfileDto(billing, null, true)`):

```csharp
// Colombia keeps its ids; any other country writes its city.
private static PublicBillingInput Abroad(string type, string country) =>
    country.Trim().Equals("CO", StringComparison.OrdinalIgnoreCase)
        ? Billing() with { IdentificationType = type, Country = country }
        : Billing() with { IdentificationType = type, Country = country, DepartmentId = null, CityId = null, CityName = "Madrid" };

[Theory]
[InlineData("NIF", "ES")] [InlineData("NIE", "ES")] [InlineData("TAX_ID", "US")] [InlineData(" nif ", " es ")]
public void AnAllowedForeignPairIsValid(string type, string country) => Assert.Empty(Errors(Body(Abroad(type, country))));

[Fact]
public void AColombianCcIsValid() => Assert.Empty(Errors(Body()));

[Theory]
[InlineData("CC", "ES")] [InlineData("NIT", "US")] [InlineData("TAX_ID", "CO")]
public void ATypeTheBillingCountryDoesNotAllowFailsOnTheTypeOnly(string type, string country) =>
    Assert.Equal(["Billing.IdentificationType"], Errors(Body(Abroad(type, country))));

[Fact]
public void ANifInColombiaFailsWithTheCountryMessage()
{
    var result = new PublicOrderRequestBodyValidator().Validate(Body(Billing() with { IdentificationType = "NIF" }));
    Assert.Equal("El tipo de documento no es válido para el país.", Assert.Single(result.Errors).ErrorMessage);
}

[Fact]
public void AMalformedCountryIsReportedOnTheCountryAlone()
{
    var errors = Errors(Body(Abroad("NIF", "COL")));
    Assert.Contains("Billing.Country", errors);
    Assert.DoesNotContain("Billing.IdentificationType", errors);
}

[Fact]
public void AnUnknownTypeKeepsItsMessage() =>
    Assert.Equal("El tipo de documento no es válido.",
        Assert.Single(new PublicOrderRequestBodyValidator().Validate(Body(Billing() with { IdentificationType = "XX" })).Errors).ErrorMessage);
```

`BuyerParityTests.BillingCases`: add

```csharp
{ "valid ES with NIF", Co() with { IdentificationType = "NIF", Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" }, true },
{ "valid US with TAX_ID", Co() with { IdentificationType = "TAX_ID", Country = "US", DepartmentId = null, CityId = null, CityName = "Miami" }, true },
{ "ES with CC", Co() with { Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" }, false },
{ "CO with NIF", Co() with { IdentificationType = "NIF" }, false },
{ "lowercase nif with es", Co() with { IdentificationType = " nif ", Country = "es", DepartmentId = null, CityId = null, CityName = "Madrid" }, true },
```

and change the existing `"valid ES with city name"` case to carry `IdentificationType = "PASAPORTE"` (it encoded `CC` with `ES`).

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter "FullyQualifiedName~SubmitOrderRequestValidatorTests"
dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests --filter "FullyQualifiedName~BuyerProfileValidatorTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~BuyerParityTests"
```

Expected: `Assert.Empty() Failure … ["Billing.IdentificationType"]` on `NIF`; `Assert.Equal() Failure` on `ES` + `CC` (accepted); parity `"valid ES with NIF: expected valid, profile has [Billing.IdentificationType]"`.

- [ ] **Step 3: Implement both validators** (Interfaces block). Sanity check of parity: change only Store first and re-run `BuyerParityTests` once; it must go red on `"ES with CC"` (`profile [] vs order [Billing.IdentificationType]`). Paste that output, then change Buyers.

- [ ] **Step 4: Sweep the unit fixtures** — `SubmitOrderRequestValidatorTests.AForeignBlockWithACityNameIsValid` and `AForeignBlockWithIdsIsLeftToTheHandler` use `CC` with `ES` → add `IdentificationType = "NIF"`; check `BuyerProfileValidatorTests` for the same pattern.

- [ ] **Step 5: Run to verify they pass**

Same three commands, then the whole three projects. Expected: PASS.

- [ ] **Step 6: Sweep the Store integration fixtures** (the validator now runs before the country-sold check, so `ES` + `CC` turns into a type error):
- `PublicOrderRequestApiTests.ABillingCountryTheStoreDoesNotSellToIs422`, `AForeignBlockWithDivipolaIdsIs422`, `AForeignBillingWithACityTextIsAccepted` → billing `identificationType = "PASAPORTE"` (keeps each test about what it tests).
- `StoreTakeApiTests.AForeignBillingCreatesAForeignCustomerAndAddress` → `billing["identificationType"] = "NIF"`, `billing["identificationNumber"] = "12345678Z"`.

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicOrderRequestApiTests|FullyQualifiedName~StoreTakeApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/Modules/Store/Modules.Store.Application/SubmitOrderRequest.cs src/Modules/Buyers/Modules.Buyers.Application/BuyerProfileValidator.cs tests/Modules/Store tests/Modules/Buyers/Modules.Buyers.UnitTests tests/Bootstrapper/Bootstrapper.UnitTests/BuyerParityTests.cs
git commit -m "feat(store): validate the billing document type against the billing country"
```

---

### Task 9: Store domain invariant

**Files:**
- Modify: `src/Modules/Store/Modules.Store.Domain/StoreErrorCodes.cs:33-47` (add `DocumentTypeNotAllowed`)
- Modify: `src/Modules/Store/Modules.Store.Domain/StoreRequestBilling.cs:6,13,29-53` (comment, remove list, constructor)
- Test: `tests/Modules/Store/Modules.Store.UnitTests/StoreOrderRequestTests.cs`; integration `tests/Modules/Store/Modules.Store.IntegrationTests/StorePersistenceTests.cs`, `PublicOrderRequestApiTests.cs`

**Interfaces:**
- Produces: `StoreErrorCodes.DocumentTypeNotAllowed = "store.order_request.document_type_not_allowed"`. Constructor order: `place` null check first (`ArgumentNullException.ThrowIfNull(place)`), known-code check (`validation.failed`, «El tipo de documento no es válido.»), then `!DocumentTypesByCountry.IsAllowed(type, place.Country)` → `StoreDomainException(DocumentTypeNotAllowed, "El tipo de documento no es válido para el país.")`. The private EF constructor is untouched.

- [ ] **Step 1: Write the failing tests**

```csharp
private static StorePlace Spain() => new("ES", null, null, null, "Madrid");

[Fact]
public void ABillingDocumentMustBeAllowedForItsCountry()
{
    Assert.Equal(
        StoreErrorCodes.DocumentTypeNotAllowed,
        CodeOf(() => _ = new StoreRequestBilling("Ana", "CC", "1", "612345678", "+34612345678", "a@b.es", "Calle Mayor 1", Spain())));
    Assert.Equal("NIF", new StoreRequestBilling("Ana", " nif ", "1", "612345678", "+34612345678", "a@b.es", "Calle Mayor 1", Spain()).IdentificationType);
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet test tests/Modules/Store/Modules.Store.UnitTests --filter "FullyQualifiedName~StoreOrderRequestTests"
```

Expected: build error `CS0117: 'StoreErrorCodes' does not contain a definition for 'DocumentTypeNotAllowed'`, then after adding the constant `Assert.Equal() Failure … Actual: validation.failed`.

- [ ] **Step 3: Implement** (Interfaces block) and rewrite the comment at `:6`: the store, the customer and the order party share the seven codes and the same per-country table (`DocumentTypesByCountry`).

- [ ] **Step 4: Run to verify it passes**, then the whole `Modules.Store.UnitTests`. Expected: PASS.

- [ ] **Step 5: Integration tests and sweep**

- `StorePersistenceTests.Billing(StorePlace? place = null)` gains `string identificationType = "CC"`; `AForeignBillingRoundTripsWithItsCityText` passes `"NIF"`. New `ATaxIdRoundTripsThroughTheBillingColumn`: `Billing(new StorePlace("US", null, null, null, "Miami"), "TAX_ID")` saved and reloaded → `found.Billing.IdentificationType == "TAX_ID"`.
- `PublicOrderRequestApiTests` (rig with `ColombiaAndSpain`): `ASpanishNifIsAccepted` → 201 and `LoadAsync(...).Billing.IdentificationType == "NIF"`; `AColombianTypeForSpainIs422AndWritesNothing` → 422 `validation.failed` with `errors["Billing.IdentificationType"]`, no `store.order_requests` row, and no number consumed (assert as `ASubmissionInACurrencyOutsideTheStoreWritesNothing` does).

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StorePersistenceTests|FullyQualifiedName~PublicOrderRequestApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Store/Modules.Store.Domain tests/Modules/Store
git commit -m "feat(store): reject an order request document not allowed for its billing country"
```

---

### Task 10: Buyer profile invariant and signup

**Files:**
- Modify: `src/Modules/Buyers/Modules.Buyers.Domain/BuyerErrorCodes.cs` (add `DocumentTypeNotAllowed`)
- Modify: `src/Modules/Buyers/Modules.Buyers.Domain/BuyerProfile.cs:39-46`
- Modify: `src/Modules/Buyers/Modules.Buyers.Application/BuyerSignup.cs:23-31,61-100` (new `ILogger<BuyerSignup> logger` constructor parameter, last)
- Test: create `tests/Modules/Buyers/Modules.Buyers.UnitTests/BuyerProfileTests.cs`; modify `BuyerSignupTests.cs` (`:150`), `BuyersTestDoubles.cs:43` (pass `NullLogger<BuyerSignup>.Instance`); integration `tests/Modules/Buyers/Modules.Buyers.IntegrationTests/BuyerAccountApiTests.cs`, `tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderBuyerAccountApiTests.cs`

**Interfaces:**
- Produces: `BuyerErrorCodes.DocumentTypeNotAllowed = "store.buyer.document_type_not_allowed"`; `public static bool AcceptsDocument(BuyerBillingData billing)` on `BuyerProfile` (`DocumentTypesByCountry.IsAllowed(billing.IdentificationType, billing.Country)`); `Create` and `Replace` throw `BuyerDomainException(DocumentTypeNotAllowed, "El tipo de documento no es válido para el país.")` before assigning. The `BuyerBillingData` record gets no check.
- Signup decision: at the top of `SignUpOnceAsync`, a profile that fails `AcceptsDocument(profile.WithNormalizedCodes().ToData().Billing)` is treated as `null` for the whole attempt, and logged with `LogWarning` carrying only the type and the country. Consequence (by the existing `null` semantics): for an existing unverified account the old profile is cleared.

- [ ] **Step 1: Write the failing tests**

`BuyerProfileTests`:

```csharp
private static BuyerProfileData Data(string type, string country) =>
    new(new BuyerBillingData("Ana", type, "X1", "612345678", "ana@correo.es", "Calle Mayor 1", country, null, null, "Madrid"), null, true);

[Fact]
public void CreateRejectsATypeTheCountryDoesNotAllow() =>
    Assert.Equal("store.buyer.document_type_not_allowed",
        Assert.Throws<BuyerDomainException>(() => BuyerProfile.Create(Guid.CreateVersion7(), Data("CC", "ES"), DateTimeOffset.UnixEpoch)).Code);

[Fact]
public void ReplaceRejectsAndKeepsTheSavedProfile()
{
    var profile = BuyerProfile.Create(Guid.CreateVersion7(), Data("NIF", "ES"), DateTimeOffset.UnixEpoch);
    Assert.Throws<BuyerDomainException>(() => profile.Replace(Data("CC", "ES"), DateTimeOffset.UnixEpoch.AddDays(1)));
    Assert.Equal("NIF", profile.Data.Billing.IdentificationType);
}

[Fact]
public void ALegacyStoredPairStillDeserializes() =>
    Assert.Equal("CC", JsonSerializer.Deserialize<BuyerProfileData>(JsonSerializer.Serialize(Data("CC", "ES"), JsonSerializerOptions.Web), JsonSerializerOptions.Web)!.Billing.IdentificationType);
```

`BuyerSignupTests` (with `Spanish(BuyerProfileDto p) => p with { Billing = p.Billing with { Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" } }`, which keeps `CC`):

```csharp
[Fact]
public async Task ASignupWithAProfileTheCountryDoesNotAllowCreatesTheAccountWithoutProfile()
// SignUpAsync(Request("hash-1", Spanish(Profile("Ana")))) does not throw; one account; world.SavedProfiles is empty

[Fact]
public async Task AnUnverifiedEmailWithADisallowedProfileStillReRegistersAndClearsTheOldProfile()   // Review Focus 5
// first SignUpAsync(Request("hash-1", Profile("Primero"))); then SignUpAsync(Request("hash-2", Spanish(Profile("Segundo"))))
// → no throw, world.SavedProfiles empty, and the same verification assertions as AnUnverifiedEmailReplacesPasswordAndProfileAndReissuesVerification
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests --filter "FullyQualifiedName~BuyerProfileTests|FullyQualifiedName~BuyerSignupTests"
```

Expected: `Assert.Throws() Failure: No exception was thrown` (profile) and, after the invariant exists but before the signup change, `BuyerDomainException : El tipo de documento no es válido para el país.` thrown from `SignUpAsync`.

- [ ] **Step 3: Implement the invariant, then the signup drop** (Interfaces block). Update both test construction sites for the new logger parameter.

- [ ] **Step 4: Run to verify they pass**, then the whole `Modules.Buyers.UnitTests` and `Bootstrapper.UnitTests`. Expected: PASS.

- [ ] **Step 5: Integration tests**

`BuyerAccountApiTests`: `Profile(...)` gains `string identificationType = "CC", string country = "CO"` (non-`CO` → `departmentId`/`cityId` null, `cityName = "Madrid"`).
- `UpdateProfileStoresASpanishNif` → 200; stored `data->'billing'->>'identificationType'` is `NIF`.
- `UpdateProfileWithAColombianTypeForSpainIs422` → 422 `validation.failed`, `errors` contains `Billing.IdentificationType`.
- `ALegacyProfileStillReads` → PUT a valid `CO` profile, then `await api.ScalarAsync<int>("UPDATE buyers.profiles SET data = jsonb_set(jsonb_set(data, '{billing,country}', '\"ES\"'), '{billing,identificationType}', '\"CC\"') RETURNING 1")`; `GET /session` → 200 with billing `identificationType` `CC`.

`PublicOrderBuyerAccountApiTests`: `OpenRigAsync` gains `string[]? salesCountries = null` (sets `body["salesCountries"]`).
- `CreateAccountStoresASpanishNieProfile` (pattern of `CreateAccountCreatesAnUnverifiedAccountWithTheOrderBlocks`, rig with `CO`, `ES`, billing `ES` + `NIE` + `Madrid`) → `buyers.profiles` billing type `NIE`.
- `UpdateProfileReplacesItWithASpanishNie` (pattern of `UpdateProfileReplacesTheProfileWithTheOrderBlocks`) → saved type `NIE`.

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --filter "FullyQualifiedName~BuyerAccountApiTests|FullyQualifiedName~BuyerSignupRequestedWorkerTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~PublicOrderBuyerAccountApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Modules/Buyers tests/Modules/Buyers tests/Modules/Store/Modules.Store.IntegrationTests/PublicOrderBuyerAccountApiTests.cs
git commit -m "feat(buyers): enforce the profile document type for its country and drop it on signup"
```

---

### Task 11: "Tomar" acceptance for the three new codes

**Files:**
- Modify: `tests/Modules/Store/Modules.Store.IntegrationTests/StoreInboxRig.cs` (`CreateCompleteCustomerAsync` gains `string identificationType = "CC", string country = "CO", string? cityName = null`; `cityId` is `City` only for `CO`; new `Task<string?> TextAsync(string sql, params (string Name, object? Value)[] parameters)` beside `CountAsync`)
- Test: `tests/Modules/Store/Modules.Store.IntegrationTests/StoreTakeApiTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2-10. No production code changes here.
- TDD note: every behaviour below was driven RED→GREEN by Tasks 3, 4, 6, 7, 8 and 9 (goal 3's RED is Task 4 Step 3). These tests are the end-to-end proof the spec asks for (§10.2); their expected first run is PASS, and that literal output is the evidence.

- [ ] **Step 1: Write the tests** (store selling `["CO", "ES", "US"]`; billing tweak sets type, number, country, null ids, city name, a local phone and an address)

```csharp
[Theory]
[InlineData("ES", "NIF", "Nif", "12345678Z", "Madrid", "+34600111222")]
[InlineData("ES", "NIE", "Nie", "X1234567L", "Madrid", "+34600111222")]
[InlineData("US", "TAX_ID", "TaxId", "12-3456789", "Miami", "+13055550100")]
public async Task TakingANewCustomerWithAForeignDocumentKeepsItEverywhere(string country, string wire, string member, string number, string city, string phone)
// TakeOkAsync(request, NewCustomerBody) → customer (member, country);
// rig.TextAsync("SELECT identification_type || '|' || country FROM quotations.order_parties WHERE order_id = @id AND role = 'Billing'", ("id", orderId)) == $"{member}|{country}";
// Party(await rig.OrderAsync(orderId), "Billing")["identificationType"] == wire; (await rig.StateAsync(request)).Status == StoreOrderRequestStatus.Converted

[Fact]
public async Task TakingAnIncompleteCustomerWithoutDocumentGivesItTheNifAndSpain()
// SeedIncompleteAsync(..., country: "CO") + ES/NIF request → customer ("Complete", "Nif", "ES")

[Fact]
public async Task TakingACompleteColombianCustomerForASpanishRequestLeavesItAlone()
// CreateCompleteCustomerAsync("Laura", "1020304050", "3105550001") + ES/NIF request, ExistingBody
// → customer ("Cc", "CO") and same Version as before; billing party "Nif|ES"

[Fact]
public async Task TakingTheCustomerThatAlreadyHasTheNifCreatesNoSecondCustomer()
// CreateCompleteCustomerAsync("Laura", "12345678Z", "+34600111222", identificationType: "NIF", country: "ES", cityName: "Madrid")
// + ES/NIF/12345678Z request, ExistingBody → 200; await rig.CountAsync("customers.customers") == 1

[Fact]
public async Task TheCandidatesOfANifRequestIncludeTheCustomerWithThatDocument()
// same seeded customer; GetJsonAsync($"/{request}/customer-candidates") → candidate with its id and matchedBy containing "Document"
```

- [ ] **Step 2: Run**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --filter "FullyQualifiedName~StoreTakeApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: PASS for the whole class. A failure here is a gap in an earlier task: fix it there with its own RED test, not here.

- [ ] **Step 3: Commit**

```powershell
git add tests/Modules/Store/Modules.Store.IntegrationTests/StoreInboxRig.cs tests/Modules/Store/Modules.Store.IntegrationTests/StoreTakeApiTests.cs
git commit -m "test(store): take order requests with NIF, NIE and TAX_ID end to end"
```

---

### Task 12: No migration, architecture, gotchas, full verification

**Files:**
- Modify: `CLAUDE.md` (§ Gotchas, entry at `:744-746` and new entries)

- [ ] **Step 1: Prove no migration is needed**

```powershell
dotnet ef migrations has-pending-model-changes --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext
dotnet ef migrations has-pending-model-changes --project src/Modules/Quotations/Modules.Quotations.Infrastructure --context QuotationsDbContext
```

Expected (paste literally): `No changes have been made to the model since the last migration.` for both. Anything else stops the task: the spec (§6.7) says no migration.

- [ ] **Step 2: Architecture tests, unchanged**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~StoreLayerTests|FullyQualifiedName~BuyersLayerTests|FullyQualifiedName~CustomersLayerTests|FullyQualifiedName~QuotationsLayerTests"
```

Expected: PASS with no edits to those files.

- [ ] **Step 3: Update `CLAUDE.md` § Gotchas**
- `BuyerParityTests` entry: the document-type rule is no longer a copy; Store and Buyers both read `DocumentTypesByCountry` (`BuildingBlocks.Domain`), and the parity cases cover the per-country rule.
- New entry: the per-country document table lives once in `DocumentTypesByCountry`; Companies keeps its own typed table tied by `DocumentTypesByCountryTests`; Customers (`IdentificationTypeParser`) and Quotations (`PartyFiscalTypesParser`) are tied by their parity tests. Customers and billing parties check the type against **their own** country on every write (a stored non-conforming row reads, but cannot be saved unchanged); a blank party country is `CO`.
- New entry: `PosCustomerLookupTests.TheIdentificationTypesMatchTheCustomersModule` compares Pos with `AllowedFor("CO")`, as do the customer Excel import and the service-contract import; widening Customers does not widen them.
- New entry: `MergeForStore` takes the request's country together with its document when the customer has no document (address from the request and phone as its old E.164 when the country is replaced); `CustomerMergePlan` fills document and country as a pair.

- [ ] **Step 4: Full verification** (README § Verificación, then the affected integration projects one at a time)

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes
dotnet build --no-restore
dotnet test tests/Modules/Companies/Modules.Companies.UnitTests --no-build
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --no-build
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --no-build
dotnet test tests/Modules/Store/Modules.Store.UnitTests --no-build
dotnet test tests/Modules/Buyers/Modules.Buyers.UnitTests --no-build
dotnet test tests/Modules/Shopify/Modules.Shopify.UnitTests --no-build
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --no-build
dotnet test tests/ArchitectureTests/ArchitectureTests --no-build
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --no-build -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --no-build -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests --no-build -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Buyers/Modules.Buyers.IntegrationTests --no-build -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests --no-build -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --no-build -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests --no-build -- xUnit.MaxParallelThreads=4
```

Expected: every command exits 0.

- [ ] **Step 5: Commit**

```powershell
git add CLAUDE.md
git commit -m "docs: record the shared document type table and the per-country rule in gotchas"
```

`docs/estado.md` stays untouched here; it is updated on `develop` in the merge commit or the `docs:` commit right after it.

---

## Pre-deploy note (owner, optional, not a task)

Before the backend reaches production the owner may run this **read-only** check (spec §8). It counts, and prints no personal data. Zero rows confirms the "all customers are Colombian" assumption; rows mean those records must be corrected, or accepted as fixed on their next edit.

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

Deployment order stays backend → frontend → storefront (spec §9).
