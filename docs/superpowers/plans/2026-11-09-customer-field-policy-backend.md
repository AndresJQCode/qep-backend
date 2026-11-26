# Customer Field Policy (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the customer document (type + number) and the customer city optional and configurable per tenant, in two moments ("on customer save" and "on billing"), defaulting to today's behaviour (everything required), and drop the Colombia-only condition of the person type requirement.

**Architecture:** The policy is a value on `Tenant` (Tenancy.Domain), persisted as four `bool NOT NULL DEFAULT true` columns and read by other modules only through `ITenantDirectory.GetRegionalSettingsAsync`. The aggregate `Customer` stops deciding whether document and city are mandatory; a new Application class per moment decides it from the policy: `CustomerFieldPolicyRequirement` (Customers, on save), `BilledCustomerFieldsRequired` (Quotations, on order approval) and an inline gate in `CreatePosSaleHandler` (Pos). Database checks are relaxed and a new pair check keeps "both or neither".

**Tech Stack:** .NET 10, EF Core (Npgsql), FluentValidation, xUnit v3, Testcontainers, PowerShell 5.1.

**Spec:** `docs/superpowers/specs/2026-11-09-customer-field-policy-backend-design.md` (sibling: `../qep-frontend/docs/superpowers/specs/2026-11-09-customer-field-policy-frontend-design.md`; the backend ships first).

## Global Constraints

- Policy shape, exactly: `FieldRequirement(bool OnCustomerSave, bool OnBilling)` with `IsConsistent => !OnCustomerSave || OnBilling`; `CustomerFieldPolicy(FieldRequirement Document, FieldRequirement City)`; `CustomerFieldPolicy.Default` = all four `true`. `(OnCustomerSave: true, OnBilling: false)` is rejected with `tenancy.settings.customer_field_policy.invalid`.
- Columns, exactly: `customer_document_required_on_save`, `customer_document_required_on_billing`, `customer_city_required_on_save`, `customer_city_required_on_billing`, all `boolean NOT NULL DEFAULT true`. No backfill.
- JSON shape, exactly (settings GET/PUT and `/auth/me`): `"customerFieldPolicy": { "document": { "onCustomerSave": true, "onBilling": true }, "city": { "onCustomerSave": true, "onBilling": true } }`. In the PUT it is optional (absent/null keeps the stored one).
- New error codes, exactly: `customers.customer.identification_required`, `customers.customer.identification_incomplete`, `customers.import.row.identification_incomplete`, `order.order.billed_document_required`, `order.order.shipping_city_required`, `tenancy.settings.customer_field_policy.invalid`. Reused with a new owner: `customers.customer.city_required`, `customers.customer.city_name_required`, `pos.sale.customer_identification_missing`. Unchanged: `customers.customer.identification_number_required` (number blank **with** a type), `quotation.billing.identification_required`, `order.order.billed_person_type_required`.
- `missingOn` extension value for the two new order codes is `"Customer"`.
- `Juridica ⇒ NIT` stays Colombian only (`Customer.EnsurePersonTypeMatches`, `BillingPartyFiscalRules`); no per-country company-document catalogue.
- `CustomerProvisioning.CreateCompleteAsync`, `CompleteFromStoreAsync`, `CompleteExternalSaleAsync` and Store "Tomar" do **not** apply the policy. Name, phone and country stay mandatory.
- Layering: Application never references EF Core or Npgsql; DB error translation stays in Infrastructure (`CustomersUnitOfWork`, by index name). `CustomersLayerTests`, `PosLayerTests`, `QuotationsLayerTests`, `TenancyLayerTests` stay green unchanged.
- Migrations via the design factory, one per module touched, in this order: Tenancy `AddTenantCustomerFieldPolicy` → Customers `RelaxCustomerCompleteFields` → Pos `AllowNullPosSaleCustomerIdentification`:
  `dotnet ef migrations add <Name> --project src/Modules/<Module>/Modules.<Module>.Infrastructure --context <Module>DbContext -o Persistence/Migrations`
- Process: strict TDD, RED before GREEN, with the literal failing and passing output pasted in the task log. Commands for the developer in PowerShell only.
- Integration tests use Testcontainers (one Postgres per test class): run **per project, one at a time**, filtered to the touched module while iterating, always with `-- xUnit.MaxParallelThreads=4`, after `$env:Seed__Enabled = "false"`. The whole integration suite runs once, at the end (Task 13), still one project at a time.
- If `Api.exe` is running, `dotnet build`, `dotnet test` and `dotnet ef` fail with a locked file: tell the developer and wait. Never stop a process you did not launch.
- Git: branch `feature/customer-field-policy` (worktree `qep-backend-worktrees/customer-field-policy`). Run `git branch --show-current` right before every commit. Conventional commits, **no AI attribution, no `Co-Authored-By` trailer**. `docs/estado.md` is **not** edited on this branch.
- Code comments follow the neighbouring ones (Spanish, *tú*); English exception messages like their neighbours.

## Review Focus

1. **Whitespace-only document fields** (`identificationType: "  "`, `identificationNumber: "  "`) must be read as "no document" (not as a half pair, not as an unknown type) by the validator, the policy class and `CustomerMapping.ToIdentification` — tests in Tasks 6 and 7.
2. **A PUT that resends the same document with different case or padding** (`" cc "`, `"1020304050 "`) on a complete customer must count as *unchanged*, so a re-enabled `Document.OnCustomerSave` does not block it — test in Task 6.
3. **Two complete customers without document in one tenant**, created by the form and by an import file with two such rows, must coexist: no `identification_taken`, no `duplicate_in_file` — tests in Tasks 7 and 11.
4. **The POS sale list filtered by document** when some sales have `customer_identification_number = NULL` must still answer 200 and simply not match those rows — test in Task 9.
5. **A settings PUT that resends the stored policy unchanged** must not bump `version` nor emit `tenancy.settings.updated` (the ETag stays) — test in Task 2.

## Spec gaps settled in this plan

Each one is an orchestrator decision, reversible by the owner; the task that owns it repeats it in a code comment.

- **G1 (C# constraint).** §6.1 asks for `TenantRegionalSettings(... CustomerFieldPolicy CustomerFieldPolicy = CustomerFieldPolicy.Default)`: a record parameter default must be a compile-time constant. Settled: `CustomerFieldPolicy? CustomerFieldPolicy = null` plus `public CustomerFieldPolicy EffectiveCustomerFieldPolicy => CustomerFieldPolicy ?? Domain.CustomerFieldPolicy.Default`. Consumers read `settings?.EffectiveCustomerFieldPolicy ?? CustomerFieldPolicy.Default`.
- **G2.** `Tenant.UpdateSettings` gains `CustomerFieldPolicy? customerFieldPolicy = null` as the **last** parameter (after `occurredAt`), so its ~20 existing callers compile unchanged.
- **G3.** "If it comes, the four flags come" (§6.7): the PUT body binds to nullable flags (`FieldRequirementInput(bool? OnCustomerSave, bool? OnBilling)`); a present policy with a missing flag is `validation.failed` with keys `CustomerFieldPolicy.Document.OnCustomerSave` etc.
- **G4.** A Colombian customer saved without city has no DIVIPOLA department: its CUC uses `CucFormatter.ForeignDepartmentCode` (`"00"`) and it is born without address-book row (as a foreign one).
- **G5.** Only `null` means "no city". `CityId = Guid.Empty` stays a broken id: rejected by the validator (`NotEqual(Guid.Empty)`) and by `EnsureValidLocation` with `city_required`.
- **G6.** Field errors of the moved city codes: `{ CityId: [code] }` in `CO`, `{ CityName: [code] }` elsewhere (the form marks the visible input, as the validator did).
- **G7.** The POS selector (`ICustomerRepository.SearchSelectableAsync`) today requires a document. Settled: active **and** (with document **or** `Completeness == Complete`): complete customers without document become selectable; WhatsApp incompletes stay out (reason of deviation B1).
- **G8.** Pos.Application cannot reference Customers, so the quick-create codes are thrown by a new `PosCustomerIdentificationException : DomainException, IHasFieldErrors` in Pos.Application, with the Customers code strings and the same `errors` keys.
- **G9.** Import: department and city travel as a pair. Both empty is accepted when `City.OnCustomerSave` is `false`; one without the other keeps `department_required` / `city_required`. Rows without document skip the in-file dedupe and the existing-identification lookup.
- **G10.** §10.1 asks for a `SessionEstablisher` unit test; there is no unit seam (it is covered only through `AuthSessionApiTests`). Settled: integration tests in `AuthSessionApiTests`.
- **G11.** `BilledCustomerFieldsRequired`: a customer whose `Country` is null is treated as `CO` (same rule as `CustomerWriteRules.IsColombian`); a customer the lookup cannot resolve counts as missing both fields.
- **G12.** `PosSale.Create` with a customer stores the customer's number even when it is `null`; `PosFinalConsumer.IdentificationNumber` only when there is no customer (today `customer?.IdentificationNumber ?? FinalConsumer` would give a customer the final consumer's number).
- **G13.** `HasDefaultValue(true)` on a `bool` makes EF skip `false` on INSERT (sentinel). Harmless here because a `Tenant` is always born with `CustomerFieldPolicy.Default`; precedent `IdentityDbContext` `message_sound`.
- **G14.** §6.2 gives the rule the signature `(directory, tenantId, command, Customer? current, ct)`, but `UpdateCustomerHandler` calls the person-type rule **before** loading the customer (`UpdateCustomer.cs:55`). Settled: the field-policy call goes after `repository.FindAsync` + `EnsureNotMerged()`; the person-type call stays where it is.

---

## File map

| File | Responsibility | Task |
| --- | --- | --- |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/CustomerFieldPolicy.cs` (new), `Tenant.cs` | Policy value, stored on the tenant, consistency rule | 1 |
| `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs`, `TenantDirectory.cs`, migration | Four columns; regional settings carry the policy | 2 |
| `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs`, `UpdateTenantSettings.cs`, `TenantSettingsDto.cs`, `TenantMappings.cs`, `CustomerFieldPolicyInput.cs` (new) | Settings contract | 2 |
| `src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs` | GET/PUT body | 2 |
| `src/Modules/Tenancy/Modules.Tenancy.Domain/PersonTypeRequirement.cs`, `Customers.Application/CustomerPersonTypeRequirement.cs`, `Quotations.Application/BilledPersonTypeRequired.cs` | Person type for every country | 3 |
| `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` | Nullable identification; location only normalizes | 4 |
| `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs`, migration | Relaxed complete check; pair check | 5 |
| `src/Modules/Customers/Modules.Customers.Application/CustomerFieldPolicyRequirement.cs` (new), `Modules.Customers.Domain/CustomersErrorCodes.cs`, `CustomerMapping.cs`, `CustomerProvisioning.cs` (`RegisterCustomerData`) | Save-moment rule; nullable document in the command contract | 6 |
| `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs`, `CreateCustomer.cs`, `UpdateCustomer.cs` | Write path wired to the policy | 7 |
| `src/Modules/Quotations/Modules.Quotations.Application/BilledCustomerFieldsRequired.cs` (new), `ApproveOrder.cs` | Billing gate on approval | 8 |
| `src/Modules/Pos/Modules.Pos.Domain/PosSale.cs`, `PosSaleInputs.cs`, `Modules.Pos.Application/CreatePosSale.cs`, `PosDtos.cs`, `Modules.Pos.Infrastructure/Persistence/PosDbContext.cs`, migration | Sale with a customer without document | 9 |
| `src/Modules/Pos/Modules.Pos.Application/PosCustomers.cs`, `IPosCustomerLookup.cs`, `PosDtos.cs`, `src/Bootstrapper/PosCustomerLookup.cs`, `Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` | Quick create and selector | 10 |
| `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs` | Excel import by policy | 11 |
| `src/Api/AuthSessionEndpoints.cs`, `src/Api/SessionEstablisher.cs` | `/auth/me` exposure | 12 |

---

### Task 0: Branch check and plan commit

- [ ] **Step 1: Check the branch now**

```powershell
git branch --show-current
git status --short
```

Expected: `feature/customer-field-policy`; the plan appears untracked under `docs/superpowers/plans/` (the spec is already committed in `49a7cb98`).

- [ ] **Step 2: Commit the plan**

```powershell
git add docs/superpowers/plans/2026-11-09-customer-field-policy-backend.md
git commit -m "docs(plan): customer field policy backend plan"
```

---

### Task 1: The policy value on `Tenant`

**Files:**
- Create: `src/Modules/Tenancy/Modules.Tenancy.Domain/CustomerFieldPolicy.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs` (property after `PersonTypeRequirement` ~L75; `UpdateSettings` L156-213)
- Test: create `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/CustomerFieldPolicyTests.cs`; modify `TenantTests.cs`

**Interfaces:**
- Produces (namespace `Modules.Tenancy.Domain`):
  - `public sealed record FieldRequirement(bool OnCustomerSave, bool OnBilling)` with `public static readonly FieldRequirement Always` and `public bool IsConsistent`
  - `public sealed record CustomerFieldPolicy(FieldRequirement Document, FieldRequirement City)` with `public static readonly CustomerFieldPolicy Default`, `public bool IsConsistent` (both fields consistent) and `public const string InvalidCode = "tenancy.settings.customer_field_policy.invalid"`
  - `Tenant.CustomerFieldPolicy { get; private set; } = CustomerFieldPolicy.Default`
  - `Tenant.UpdateSettings(..., DateTimeOffset occurredAt, CustomerFieldPolicy? customerFieldPolicy = null)` (G2)

- [ ] **Step 1: Write the failing tests**

```csharp
// CustomerFieldPolicyTests.cs
[Theory]
[InlineData(true, false, false)]
[InlineData(true, true, true)]
[InlineData(false, true, true)]
[InlineData(false, false, true)]
public void SavingImpliesBilling(bool onSave, bool onBilling, bool expected) =>
    Assert.Equal(expected, new FieldRequirement(onSave, onBilling).IsConsistent);

[Fact]
public void TheDefaultRequiresEverythingInBothMoments() =>
    Assert.Equal(new CustomerFieldPolicy(new(true, true), new(true, true)), CustomerFieldPolicy.Default);

// TenantTests.cs
[Fact] public void ANewTenantStartsWithTheDefaultPolicy()            // tenant.CustomerFieldPolicy == Default
[Fact] public void UpdateSettingsChangesThePolicyAndTracksIt()       // returns true, Version + 1,
                                                                     // event ChangedFields contains "CustomerFieldPolicy"
[Fact] public void UpdateSettingsWithANullPolicyKeepsTheStoredOne()  // set relaxed, then null → still relaxed
[Fact] public void UpdateSettingsWithTheSamePolicyChangesNothing()   // returns false, Version unchanged
[Fact] public void AnInconsistentPolicyIsRejectedAndChangesNothing()
{
    // City (true, false) → TenantDomainException.Code == "tenancy.settings.customer_field_policy.invalid";
    // tenant.CustomerFieldPolicy still Default, Version unchanged.
}
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~CustomerFieldPolicyTests|FullyQualifiedName~TenantTests"
```

Expected: build error `CS0246: The type or namespace name 'FieldRequirement' could not be found`.

- [ ] **Step 3: Implement**

`CustomerFieldPolicy.cs` exactly as spec §6.1 plus `IsConsistent` and `InvalidCode`. In `UpdateSettings`: resolve `var validatedPolicy = customerFieldPolicy ?? CustomerFieldPolicy;`, throw `new TenantDomainException(CustomerFieldPolicy.InvalidCode, "A field required on customer save must also be required on billing.")` when `!validatedPolicy.IsConsistent` **before** any assignment, `TrackChange(nameof(CustomerFieldPolicy), CustomerFieldPolicy.ToString(), validatedPolicy.ToString(), changedFields)`, and assign with the rest.

- [ ] **Step 4: Run to verify they pass**

Same command. Expected: `Passed!` with 0 failed (the whole `Modules.Tenancy.UnitTests` project is green too: `dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests`).

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Tenancy/Modules.Tenancy.Domain/CustomerFieldPolicy.cs src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/CustomerFieldPolicyTests.cs tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantTests.cs
git commit -m "feat(tenancy): add the tenant customer field policy"
```

---

### Task 2: Persistence and the settings contract

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs` (tenant mapping ~L80), `TenantDirectory.cs` (`GetRegionalSettingsAsync`)
- Create: migration `AddTenantCustomerFieldPolicy` under `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs` (`TenantRegionalSettings` L42-53), `UpdateTenantSettings.cs`, `TenantSettingsDto.cs`, `TenantMappings.cs`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Application/CustomerFieldPolicyInput.cs`
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Api/TenantSettingsEndpoints.cs` (`UpdateTenantSettingsRequest` L208, `TenantSettingsResponse` L247, `SettingsResult`, the `UpdateTenantSettingsCommand` call L141-155)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/UpdateTenantSettingsHandlerTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantSettingsApiTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces:
  - `TenantRegionalSettings(..., string Country = "CO", PersonTypeRequirement PersonTypeRequirement = Never, CustomerFieldPolicy? CustomerFieldPolicy = null)` and `public CustomerFieldPolicy EffectiveCustomerFieldPolicy` (G1)
  - `public sealed record CustomerFieldPolicyInput(FieldRequirementInput? Document, FieldRequirementInput? City)`, `public sealed record FieldRequirementInput(bool? OnCustomerSave, bool? OnBilling)`, `public CustomerFieldPolicy ToDomain()` (Application; also the PUT body type)
  - `UpdateTenantSettingsCommand(..., string? PersonTypeRequirement = null, CustomerFieldPolicyInput? CustomerFieldPolicy = null)`
  - `TenantSettingsDto(..., string PersonTypeRequirement, CustomerFieldPolicy CustomerFieldPolicy)`
  - Api: `UpdateTenantSettingsRequest(..., string? PersonTypeRequirement = null, CustomerFieldPolicyInput? CustomerFieldPolicy = null)`; `TenantSettingsResponse(..., string PersonTypeRequirement, CustomerFieldPolicyResponse CustomerFieldPolicy)`; `public sealed record CustomerFieldPolicyResponse(FieldRequirementResponse Document, FieldRequirementResponse City)` with `static From(CustomerFieldPolicy)`; `public sealed record FieldRequirementResponse(bool OnCustomerSave, bool OnBilling)`

- [ ] **Step 1: Write the failing tests**

```csharp
// UpdateTenantSettingsHandlerTests.cs
[Fact] public async Task APresentPolicyIsSavedAndReturned()           // dto.CustomerFieldPolicy == relaxed policy
[Fact] public async Task AnAbsentPolicyKeepsTheStoredOne()             // CustomerFieldPolicy = null → unchanged
[Fact] public async Task APolicyWithAMissingFlagIsAValidationFailure()
{
    // City = new(OnCustomerSave: true, OnBilling: null) → ValidationException,
    // Errors contain PropertyName "CustomerFieldPolicy.City.OnBilling"
}
[Fact] public async Task AnInconsistentPolicyIsTheDomainCode()         // TenantDomainException "tenancy.settings.customer_field_policy.invalid"

// TenantSettingsApiTests.cs (integration)
[Fact] public async Task CustomerFieldPolicyRoundTripsAndDefaultsToRequired()
{
    // GET → customerFieldPolicy.document.onCustomerSave == true (the four true);
    // PUT with If-Match and { document: { onCustomerSave: false, onBilling: true }, city: { onCustomerSave: false, onBilling: false } }
    // → 200, new ETag; GET again returns the same four values.
}
[Fact] public async Task PutWithoutCustomerFieldPolicyKeepsTheStoredValue()
[Fact] public async Task PutWithAnInconsistentPolicyIsA422WithTheDomainCode()   // city (true,false) → 422, code tenancy.settings.customer_field_policy.invalid
[Fact] public async Task PutResendingTheStoredPolicyDoesNotBumpTheVersion()     // Review Focus 5: same ETag, no new tenancy.settings.updated outbox row
[Fact] public async Task TheRegionalSettingsCarryThePolicy()
{
    // after the PUT, scope.GetRequiredService<ITenantDirectory>().GetRegionalSettingsAsync(...)
    //   .EffectiveCustomerFieldPolicy equals the relaxed policy
}
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~UpdateTenantSettingsHandlerTests"
```

Expected: build error `CS1739` / `CS0246` on `CustomerFieldPolicyInput`.

- [ ] **Step 3: Implement the contract**

Validator: `RuleFor(c => c.CustomerFieldPolicy!.Document).NotNull()` and the same for `City`, then `NotNull()` on each of the four flags, all `.When(c => c.CustomerFieldPolicy is not null)` (G3). Handler passes `command.CustomerFieldPolicy?.ToDomain()` as the last argument of `UpdateSettings`. `TenantMappings.ToSettingsDto` fills `tenant.CustomerFieldPolicy`.

- [ ] **Step 4: Map the columns and generate the migration**

Owned type: `tenant.OwnsOne(t => t.CustomerFieldPolicy, policy => { policy.OwnsOne(p => p.Document, …); policy.OwnsOne(p => p.City, …); })` with the four exact column names from Global Constraints, `.IsRequired().HasDefaultValue(true)` on each (G13), and `Navigation(...).IsRequired()` at both levels so EF does not treat the owned value as optional. `TenantDirectory.GetRegionalSettingsAsync` projects `tenant.CustomerFieldPolicy`.

```powershell
dotnet ef migrations add AddTenantCustomerFieldPolicy --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
```

Check the generated `Up`: four `AddColumn<bool>` on `tenancy.tenants` with `nullable: false, defaultValue: true`, nothing else.

- [ ] **Step 5: Run to verify they pass**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~TenantSettingsApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: both `Passed!`, 0 failed.

- [ ] **Step 6: Commit**

```powershell
git branch --show-current
git add src/Modules/Tenancy tests/Modules/Tenancy
git commit -m "feat(tenancy): persist and expose the customer field policy in settings"
```

---

### Task 3: Person type for every country

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/PersonTypeRequirement.cs` (`Effective` L29; remove `IsColombian` L24 once unused), `src/Modules/Tenancy/Modules.Tenancy.Domain/Tenant.cs` (doc comment L69-74), `src/Modules/Tenancy/Modules.Tenancy.Application/ITenantDirectory.cs` (doc of `EffectivePersonTypeRequirement`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerPersonTypeRequirement.cs` (early `IsColombian` return L22-27), `src/Modules/Quotations/Modules.Quotations.Application/BilledPersonTypeRequired.cs` (both `IsColombian` checks), `src/Modules/Quotations/Modules.Quotations.Domain/BillingPartyFiscalRules.cs` (comment L9 only)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantTests.cs` (L147-161), `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteHandlersTests.cs`, `tests/Modules/Quotations/Modules.Quotations.UnitTests/ApproveOrderHandlerTests.cs`, `BillingPersonTypeRequirementTests.cs`, `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs` (L299-312)

**Interfaces:**
- Produces: `PersonTypeRequirementPolicy.Effective(string? tenantCountry, PersonTypeRequirement stored) == stored` for every country (signature unchanged, so `SessionEstablisher` and both modules compile untouched). `PersonTypeRequirementPolicy.IsColombian` no longer exists.

- [ ] **Step 1: Rewrite the tests that pin the old rule (they become the RED)**

```csharp
// TenantTests.cs — replaces EffectiveIsNeverOutsideColombia and IsColombianIgnoresCase
[Theory]
[InlineData("CO", PersonTypeRequirement.OnCustomerSave)]
[InlineData("ES", PersonTypeRequirement.OnCustomerSave)]
[InlineData(null, PersonTypeRequirement.OnOrderApproval)]
public void EffectiveIsTheStoredRequirementInEveryCountry(string? country, PersonTypeRequirement stored) =>
    Assert.Equal(stored, PersonTypeRequirementPolicy.Effective(country, stored));

// CustomerWriteHandlersTests.cs — replace AForeignCustomerOfAColombianTenantSavesWithoutPersonType and
// ANonColombianTenantWithOnCustomerSaveCreatesWithoutPersonType with:
[Fact] public async Task OnCustomerSaveRejectsAForeignCustomerWithoutPersonType()      // ES/NIF/Madrid → PersonTypeRequired, FieldErrors ["PersonType"]
[Fact] public async Task ANonColombianTenantWithOnCustomerSaveRequiresThePersonType()  // TenantDirectory.With("ES", OnCustomerSave) → PersonTypeRequired

// ApproveOrderHandlerTests.cs — replace AForeignCustomerApprovesWithoutPersonType and
// ANonColombianTenantWithOnOrderApprovalApprovesWithoutPersonType with:
[Fact] public async Task OnOrderApprovalRejectsAForeignCustomerWithoutPersonType()        // missingOn "Customer"
[Fact] public async Task ANonColombianTenantWithOnOrderApprovalRequiresThePersonType()   // BilledPersonTypeRequiredException

// BillingPersonTypeRequirementTests.cs — replace ANonColombianTenantIsNotChecked with:
[Fact] public async Task ANonColombianTenantIsChecked()   // ES + OnCustomerSave + billing row without person type → quotation.billing.person_type_required

// AuthSessionApiTests.cs (integration) — replace ANonColombianTenantPublishesNever with:
[Fact] public async Task ANonColombianTenantPublishesTheStoredRequirement()  // ES + OnOrderApproval → "OnOrderApproval"
```

Leave `Customer.EnsurePersonTypeMatches` tests as they are (NIT rule stays Colombian) and add to `CustomerPersonTypeTests.cs`: `[Fact] public void AForeignJuridicaWithANieIsAccepted()` (ES, `Juridica`, `Nie` → no exception).

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests --filter "FullyQualifiedName~EffectiveIsTheStoredRequirementInEveryCountry"
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerWriteHandlersTests|FullyQualifiedName~CustomerPersonTypeTests"
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~ApproveOrderHandlerTests|FullyQualifiedName~BillingPersonTypeRequirementTests"
```

Expected: `Assert.Equal() Failure: Expected OnCustomerSave Actual Never` in Tenancy; `Assert.Throws() Failure: No exception was thrown` in Customers and Quotations (four tests). `AForeignJuridicaWithANieIsAccepted` passes already (regression guard).

- [ ] **Step 3: Implement**

`Effective` returns `stored`. Delete the `IsColombian` early return in `CustomerPersonTypeRequirement` (keep "person type already given → return" before reading the tenant) and both `IsColombian` checks in `BilledPersonTypeRequired` (the `country` local and the tenant-country fallback go too). Then `rg "PersonTypeRequirementPolicy.IsColombian" src tests` must be empty; delete `IsColombian` (keep `ColombiaCountryCode` only if `rg "PersonTypeRequirementPolicy.ColombiaCountryCode" src` still finds a use). Rewrite the touched doc comments to say the requirement applies in every country.

- [ ] **Step 4: Run to verify they pass**

The three unit commands of Step 2 with the filter removed (whole projects), then:

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthSessionApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: all `Passed!`.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Tenancy src/Modules/Customers/Modules.Customers.Application/CustomerPersonTypeRequirement.cs src/Modules/Quotations/Modules.Quotations.Application/BilledPersonTypeRequired.cs src/Modules/Quotations/Modules.Quotations.Domain/BillingPartyFiscalRules.cs tests/Modules/Tenancy tests/Modules/Customers/Modules.Customers.UnitTests tests/Modules/Quotations/Modules.Quotations.UnitTests
git commit -m "feat(customers): require the person type in every country"
```

---

### Task 4: `Customer` accepts no identification and no city

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (private ctor L54-108, `Create` L317, `CreateFromRegister` L388, `Complete` L435, `Update` L863, `Assign(CustomerIdentification)` L937, `EnsureValidLocation` L1215, `EnsureDocumentAllowedForCountry` L1245, `EnsurePersonTypeMatches` L1265)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerTests.cs`, `CustomerCompletenessTests.cs`, `CustomerDomainFixtures.cs`

**Interfaces:**
- Produces: `Customer.Create(..., CustomerIdentification? identification, ...)`, `Customer.Complete(..., CustomerIdentification? identification, ...)`, `Customer.Update(..., CustomerIdentification? identification, ...)`, `Customer.CreateFromRegister(..., CustomerIdentification? identification, ...)`. With `null`, `IdentificationType` and `IdentificationNumber` are `null`; `EnsureDocumentAllowedForCountry` and `EnsurePersonTypeMatches` run only with an identification. `EnsureValidLocation` never throws for a missing city; it still throws `city_required` for `CityId == Guid.Empty` (G5) and still clears the lane that does not apply. Existing callers compile unchanged (non-null converts to nullable).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void CreateWithoutIdentificationLeavesBothFieldsNullAndIsComplete()
// identification: null → IdentificationType null, IdentificationNumber null, Identification null,
// Completeness == Complete, IsComplete true

[Fact] public void CompleteWithoutIdentificationLeavesBothFieldsNull()
[Fact] public void UpdateWithoutIdentificationClearsADocumentItHad()
[Fact] public void CreateFromRegisterWithoutIdentificationIsIncompleteWithoutDocument()
[Fact] public void AJuridicaWithoutIdentificationIsAccepted()            // CO + Juridica + null → no exception

// Replace AColombianCustomerStillRequiresItsCityId:
[Fact] public void AColombianCustomerWithoutCityIdHasNoCity()           // CityId null, CityName null, no exception
// Replace the AForeignCustomerRequiresTheCityInWriting theory (null, "", "   "):
[Theory, InlineData(null), InlineData(""), InlineData("   ")]
public void AForeignCustomerWithoutCityNameHasNoCity(string? cityName)  // CityId null, CityName null
```

`ContactInfoRejectsAnEmptyCityId` and `UpdateLeavesTheCustomerUntouchedWhenTheCityIsRejected` stay as they are (G5). Add an optional `CustomerIdentification? identification` overload to `CustomerDomainFixtures.Create` only if a test needs it.

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerTests|FullyQualifiedName~CustomerCompletenessTests"
```

Expected: `System.ArgumentNullException` / `NullReferenceException` for the `null` identification cases and `CustomersDomainException: customers.customer.city_required` for the city cases.

- [ ] **Step 3: Implement**

Make the four signatures and the private ctor take `CustomerIdentification?`; normalize with `identification?.Normalized()`; drop `ArgumentNullException.ThrowIfNull(identification)` in `Complete` and `CreateFromRegister`; `Assign(CustomerIdentification?)` writes `null` to both columns. In `EnsureValidLocation`: CO → `(country, cityId is { } id ? (id == Guid.Empty ? throw city_required : id) : null, null)`; elsewhere → `(country, null, IsNullOrWhiteSpace(cityName) ? null : cityName)`. Update its doc comment: the obligation now lives in `CustomerFieldPolicyRequirement` (Application) because it depends on the tenant.

- [ ] **Step 4: Run to verify they pass**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
```

Expected: `Passed!`, 0 failed (no handler test changes: the validator still requires document and city).

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Customers/Modules.Customers.Domain/Customer.cs tests/Modules/Customers/Modules.Customers.UnitTests
git commit -m "feat(customers): let a customer exist without document or city"
```

---

### Task 5: Relax the complete-fields check and add the pair check

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (`CompleteFieldsCheck` and its summary L36-42, check constraints ~L74-75, the `identification_type` mapping ~L107 and the comment at L138 that cites `CK_customers_complete_fields`)
- Create: migration `RelaxCustomerCompleteFields` under `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/Migrations/`
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCompletenessMigrationTests.cs`

**Interfaces:**
- Produces: `CustomersDbContext.CompleteFieldsCheck == "completeness = 'Incomplete' OR (cuc IS NOT NULL AND country IS NOT NULL)"`; `public const string IdentificationPairCheck = "(identification_type IS NULL) = (identification_number IS NULL)"` registered as `CK_customers_identification_pair`.

- [ ] **Step 1: Write the failing integration tests**

```csharp
[Fact] public async Task ACompleteCustomerWithoutIdentificationIsAccepted()
// raw UPDATE of a complete row: SET identification_type = NULL, identification_number = NULL → succeeds

[Fact] public async Task HalfAnIdentificationIsRejectedByThePairCheck()
// raw UPDATE SET identification_number = NULL (type kept) → PostgresException,
// SqlState "23514", ConstraintName "CK_customers_identification_pair"

[Fact] public async Task TwoCustomersWithoutIdentificationCoexistInATenant()
// both rows with NULL pair, same tenant, merged_into_id NULL → no 23505 on IX_customers_tenant_identification
```

- [ ] **Step 2: Run to verify they fail**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerCompletenessMigrationTests" -- xUnit.MaxParallelThreads=4
```

Expected: `23514 ... violates check constraint "CK_customers_complete_fields"` on the first and third; the second fails because the violated constraint is `CK_customers_complete_fields`, not the pair check.

- [ ] **Step 3: Implement and generate the migration**

```powershell
dotnet ef migrations add RelaxCustomerCompleteFields --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations
```

Check `Up`: drop + re-add `CK_customers_complete_fields`, add `CK_customers_identification_pair`; no index change. Rewrite the `CompleteFieldsCheck` summary and the comments that say the identification is mandatory on a complete row (it no longer is; the policy decides it in Application).

- [ ] **Step 4: Run to verify they pass**

Same command. Expected: `Passed!`.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Customers/Modules.Customers.Infrastructure/Persistence tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCompletenessMigrationTests.cs
git commit -m "feat(customers): allow complete customers without identification in the database"
```

---

### Task 6: `CustomerFieldPolicyRequirement` (save moment)

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/CustomerFieldPolicyRequirement.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomersErrorCodes.cs`, `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs` (`ICustomerWriteCommand.IdentificationType`/`IdentificationNumber` become `string?`; `CreateCustomerCommand`/`UpdateCustomerCommand` follow), `CustomerMapping.cs` (`ToIdentification` L162-172), `CustomerProvisioning.cs` (`RegisterCustomerData` L39 → nullable document; its two `ToIdentification` calls L501, L561 compile unchanged)
- Test: create `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerFieldPolicyRequirementTests.cs`; modify `CustomerWriteDoubles.cs`

**Interfaces:**
- Consumes: `TenantRegionalSettings.EffectiveCustomerFieldPolicy` (Task 2), `Customer` with nullable identification (Task 4).
- Produces:
  - `CustomersErrorCodes.IdentificationRequired = "customers.customer.identification_required"`, `IdentificationIncomplete = "customers.customer.identification_incomplete"`, `CityRequired = "customers.customer.city_required"`, `CityNameRequired = "customers.customer.city_name_required"`
  - `internal static class CustomerFieldPolicyRequirement` with `public static Task EnsureSatisfiedAsync(ITenantDirectory directory, Guid tenantId, ICustomerWriteCommand command, Customer? current, CancellationToken cancellationToken)` — throws `CustomersDomainException` with field errors, never `ValidationException`.
  - `CustomerMapping.ToIdentification(string? identificationType, string? identificationNumber) -> CustomerIdentification?` — `null` when both are null/whitespace; otherwise as today (unknown type → `IdentificationTypeParser` error, blank number with a type → `identification_number_required` from `Normalized()`). It moves here and not in Task 7 because `TreatWarningsAsErrors` turns the nullable `ICustomerWriteCommand` fields into `CS8604` at every caller.
  - `RegisterCustomerData(string Name, string? IdentificationType, string? IdentificationNumber, string? Phone, string? Email = null)`
  - Test double: `CustomerWriteDoubles.TenantDirectory.WithPolicy(CustomerFieldPolicy policy)`.

Rules, in this order:
1. **Shape (always, no tenant read):** a document field is "present" when not null/whitespace. Type without number → `IdentificationIncomplete`, `errors = { IdentificationNumber: [code] }`; number without type → `{ IdentificationType: [code] }`.
2. **When obligation runs:** `current` null or `!current.IsComplete` → both fields evaluated. Otherwise the document is evaluated only if the pair changed (type compared after `IdentificationTypeParser.Parse` when present, number compared trimmed and ordinal; present ↔ absent is a change), and the city only if country (trimmed, case-insensitive), `CityId` or `CityName` (trimmed) differ from `current`.
3. `Document.OnCustomerSave` and no document → `IdentificationRequired`, `errors = { IdentificationType: [code], IdentificationNumber: [code] }`.
4. `City.OnCustomerSave`: country `CO` (trimmed, case-insensitive) and `CityId` null → `CityRequired`, `{ CityId: [code] }`; other country and `CityName` blank → `CityNameRequired`, `{ CityName: [code] }` (G6).
5. Read the tenant only when step 2 left something to evaluate; `null` settings → `CustomerFieldPolicy.Default`.

- [ ] **Step 1: Write the failing tests**

Use `CreateCustomerCommand` / `UpdateCustomerCommand` records as `ICustomerWriteCommand`, and `CustomerDomainFixtures` for `current`.

```csharp
[Fact] public async Task ATypeWithoutNumberIsIncompleteOnTheNumberOnly()       // errors keys == ["IdentificationNumber"], code IdentificationIncomplete
[Fact] public async Task ANumberWithoutTypeIsIncompleteOnTheTypeOnly()         // keys == ["IdentificationType"]
[Fact] public async Task TheShapeRuleRunsEvenWithARelaxedPolicy()              // Document (false,false) + type only → IdentificationIncomplete
[Fact] public async Task WhitespaceOnlyFieldsAreNoDocument()                   // Review Focus 1: "  "/"  " + relaxed → passes; + Default → IdentificationRequired
[Fact] public async Task NoDocumentWithOnCustomerSaveIsRequiredOnBothFields()  // keys == ["IdentificationNumber", "IdentificationType"] (ordered)

[Theory]  // matrix: Document.OnCustomerSave × City.OnCustomerSave × {with, without data} × {"CO", "ES"}
[MemberData(nameof(PolicyMatrix))]
public async Task TheSaveFlagsDecide(bool documentOnSave, bool cityOnSave, bool withData, string country, string? expectedCode)

[Fact] public async Task WithoutRegionalSettingsTheDefaultRules()              // Regional = null + no document → IdentificationRequired
[Fact] public async Task ColombiaWithoutCityIdIsCityRequiredOnCityId()         // keys ["CityId"]
[Fact] public async Task AbroadWithoutCityNameIsCityNameRequiredOnCityName()   // keys ["CityName"]

// moment of the rule (Document and City OnCustomerSave = true)
[Fact] public async Task CreatingWithoutDocumentFails()                        // current: null
[Fact] public async Task CompletingAnIncompleteWithoutDocumentFails()          // current: incomplete customer
[Fact] public async Task APutThatLeavesAMissingDocumentUntouchedPasses()       // current: complete, no document; command: no document
[Fact] public async Task APutThatClearsAnExistingDocumentFails()               // IdentificationRequired
[Fact] public async Task APutThatChangesTheNumberWithoutTypeIsIncomplete()     // IdentificationIncomplete
[Fact] public async Task ResendingTheSameDocumentWithOtherCaseOrPaddingIsUnchanged()  // Review Focus 2: current CC/"1020304050", command " cc "/"1020304050 " with Document required, current missing city untouched → passes
[Fact] public async Task APutThatLeavesAMissingCityUntouchedPasses()
[Fact] public async Task APutThatClearsTheCityFails()                          // CityRequired
[Fact] public async Task ChangingTheCountryCountsAsACityChange()               // current CO without city, command ES without CityName → CityNameRequired
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerFieldPolicyRequirementTests"
```

Expected: build error `CS0103: The name 'CustomerFieldPolicyRequirement' does not exist in the current context`.

- [ ] **Step 3: Implement `EnsureSatisfiedAsync` with the rules above**

With the nullable command fields and `ToIdentification` above, everything compiles (the `Customer` methods accept `null` since Task 4) and behaviour does not change: `CustomerWriteRules` still has `NotEmpty` on both fields until Task 7. The class is not wired yet. Add `[Fact] public void ToIdentificationIsNullWhenBothFieldsAreBlank()` (`null`/`"  "` pairs) to the same test file.

- [ ] **Step 4: Run to verify they pass**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
```

Expected: `Passed!`, 0 failed.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Customers tests/Modules/Customers/Modules.Customers.UnitTests
git commit -m "feat(customers): add the save-moment customer field policy rule"
```

---

### Task 7: Wire the policy into create, update and complete

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerWriteRules.cs` (`IdentificationNumber`/`IdentificationType` rules, `CityId`/`CityName` rules L115-123), `CreateCustomer.cs` (L55-110), `UpdateCustomer.cs` (L50-120 and the `Update` branch)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteHandlersTests.cs`, `CustomerDocumentCountryRulesTests.cs`; `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomersApiHarness.cs`, `CustomerWriteApiTests.cs`, `CustomerCompletenessApiTests.cs`

**Interfaces:**
- Consumes: Task 6 `CustomerFieldPolicyRequirement.EnsureSatisfiedAsync` and `CustomerMapping.ToIdentification` (nullable).
- Produces:
  - `CustomerWriteRules`: no `NotEmpty` on `IdentificationType`/`IdentificationNumber` (keep `MaximumLength`, the parse rule and `DocumentTypesByCountry.IsAllowed` only when a type is present); no required city rule; `RuleFor(c => c.CityId).NotEqual(Guid.Empty)` (G5); `CityName` keeps `MaximumLength`.
  - `CustomersApiHarness.SetTenantCustomerFieldPolicyAsync(QepApiFactory factory, bool documentOnSave, bool documentOnBilling, bool cityOnSave, bool cityOnBilling, string tenantId = TenantId)` — raw `UPDATE tenancy.tenants SET customer_document_required_on_save = …` like `SetTenantPersonTypeRequirementAsync` (L104).

Handlers: `CreateCustomerHandler` calls `CustomerFieldPolicyRequirement.EnsureSatisfiedAsync(tenantDirectory, command.TenantId, command, current: null, ct)` right after the person type rule. `UpdateCustomerHandler` makes its call after `repository.FindAsync` + `EnsureNotMerged()` and passes the loaded customer (G14). Both: the DIVIPOLA lookup runs only when `isColombian && command.CityId is { } cityId`; with no city the CUC uses `CucFormatter.ForeignDepartmentCode` and no address-book row is seeded (G4).

- [ ] **Step 1: Update the tests that pin the old validator (part of the RED)**

- `CustomerWriteApiTests` L363-388 (the "everything invalid" body): drop the `IdentificationNumber` and `CityId` assertions only if they no longer appear; `identificationType = "DNI"` still reports `IdentificationType`, `cityId = Guid.Empty` still reports `CityId` (G5).
- `AForeignCustomerWithoutACityNameMarksThatField` / `AColombianCustomerWithoutACityIdMarksThatField` (~L1035-1083): now 422 with `code == "customers.customer.city_name_required"` / `"customers.customer.city_required"` and `errors` keys `["CityName"]` / `["CityId"]` (G6).
- `CustomerCompletenessApiTests` L135-146: completing with an empty number and a type is now `identification_incomplete` with `errors` `["IdentificationNumber"]`; the customer stays incomplete.

- [ ] **Step 2: Write the failing tests**

```csharp
// CustomerDocumentCountryRulesTests.cs (validator, unit)
[Fact] public void BothDocumentFieldsEmptyIsNotAValidatorError()          // null/null and "  "/"  " → no IdentificationType/IdentificationNumber failure
[Fact] public void AColombianBodyWithoutCityIdIsNotAValidatorError()      // Country "CO", CityId null → no CityId failure
[Fact] public void AnEmptyCityIdIsStillAValidatorError()                  // Guid.Empty → CityId failure (G5)

// CustomerWriteHandlersTests.cs (unit)
[Fact] public async Task ARelaxedTenantCreatesACustomerWithoutDocumentOrCity()
// WithPolicy(Document (false,true), City (false,true)); command: IdentificationType/Number null, CityId null
// → Added customer IsComplete, IdentificationNumber null, CityId null, Cuc ends with "00" + 6 digits (G4), no "city" call
[Fact] public async Task TheDefaultPolicyRejectsACreateWithoutDocumentBeforeTheCuc()  // IdentificationRequired, no "cuc" call
[Fact] public async Task AnUpdateReadsTheCustomerBeforeThePolicy()                    // complete customer without document + Default + name change → saved

// CustomerWriteApiTests.cs (integration)
[Fact] public async Task ACustomerWithoutDocumentGetsACucWhenThePolicyAllowsIt()      // 201, isComplete true, cuc not null, identificationType null
[Fact] public async Task TwoCustomersWithoutDocumentCoexist()                         // Review Focus 3: second POST also 201
[Fact] public async Task ATypeWithoutNumberIsA422OnTheNumber()                         // code identification_incomplete, errors ["IdentificationNumber"]
[Fact] public async Task TheDefaultPolicyStillRequiresTheDocument()                    // code identification_required, errors both fields
[Fact] public async Task ReenablingThePolicyDoesNotBlockEditingAnOldCustomer()
// relaxed → create without document and city; set Default; PUT changing only the name → 200;
// PUT clearing a city it had → 422 city_required

// CustomerCompletenessApiTests.cs (integration)
[Fact] public async Task CompletingAWhatsAppCustomerWithoutCityPassesWhenTheCityIsOptional()  // City (false, *) → 200, isComplete true
```

- [ ] **Step 3: Run to verify they fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerWriteHandlersTests|FullyQualifiedName~CustomerDocumentCountryRulesTests"
```

Expected: `ValidationException` (`'Identification Number' must not be empty.`) instead of a saved customer.

- [ ] **Step 4: Implement the validator, mapping and handler changes above**

- [ ] **Step 5: Run to verify they pass**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerWriteApiTests|FullyQualifiedName~CustomerCompletenessApiTests|FullyQualifiedName~CustomerAddressesApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: `Passed!` in both.

- [ ] **Step 6: Commit**

```powershell
git branch --show-current
git add src/Modules/Customers tests/Modules/Customers
git commit -m "feat(customers): apply the tenant field policy when saving a customer"
```

---

### Task 8: Billing gate on order approval

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Application/BilledCustomerFieldsRequired.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/ApproveOrder.cs` (L53, inside `if (order.Status == OrderStatus.Pending)`)
- Test: create `tests/Modules/Quotations/Modules.Quotations.UnitTests/BilledCustomerFieldsRequiredTests.cs`; modify `QuotationsTestDoubles.cs` (`OrderBuilders.Pending` ~L1227 gains `ShippingDetails? shipping = null`); create `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/BilledCustomerFieldsApiTests.cs`; add `SetTenantCustomerFieldPolicyAsync` to `QuotationsApiHarness.cs` (same SQL as Task 7)

**Interfaces:**
- Consumes: `OrderBilledParty.SourceFor`, `IQuotationCustomerLookup.FindAsync`, `QuotationCustomerRef.IdentificationType/IdentificationNumber/Country/CityId/CityName`, `Order.IsStorePickup`, `Order.ShippingDetails`, `ShippingDetails.None`, `Order.Shipping`, `TenantRegionalSettings.EffectiveCustomerFieldPolicy`.
- Produces:
  - `public sealed class BilledCustomerFieldRequiredException(string code, string message) : DomainException, IHasProblemExtensions` with `ProblemExtensions = { ["missingOn"] = "Customer" }`
  - `internal static class BilledCustomerFieldsRequired` with `const string DocumentCode = "order.order.billed_document_required"`, `const string ShippingCityCode = "order.order.shipping_city_required"` and `public static Task EnsureSatisfiedAsync(ITenantDirectory tenantDirectory, IQuotationCustomerLookup customerLookup, Guid tenantId, Order order, CancellationToken cancellationToken)`

Rules: source `FinalConsumer` or `BillingParty` → return without reading the tenant. Source `Customer`: read policy (`null` → Default); if neither `Document.OnBilling` nor `City.OnBilling` → return without reading the customer. Document: `OnBilling` and (`IdentificationType` or `IdentificationNumber` null/blank, or customer not found) → `DocumentCode`. City: `OnBilling` and `!order.IsStorePickup` and `order.ShippingDetails != ShippingDetails.None` and `order.Shipping is null` and the customer lacks `CityId` (country `CO` or null, G11) / `CityName` (other country) → `ShippingCityCode`. Document is checked first. `ApproveOrderHandler` calls it right after `BilledPersonTypeRequired.EnsureSatisfiedAsync`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task ACustomerWithoutDocumentPassesWhenTheDocumentIsNotRequiredOnBilling()
[Fact] public async Task ACustomerWithoutDocumentIsRejectedWhenRequired()       // DocumentCode, ProblemExtensions["missingOn"] == "Customer"
[Fact] public async Task AFinalConsumerOrderIsNotChecked()
[Fact] public async Task ABillingRowIsNotChecked()
[Fact] public async Task AStorePickupWithoutCityPasses()                          // City (true,true), parties.IsStorePickup
[Fact] public async Task AnOrderWithoutFreightNeverAsksForTheCity()                // ShippingDetails.None
[Fact] public async Task AnOwnShippingRowIsNotCheckedAgainstTheCustomer()         // shipping party present, customer without city → passes
[Fact] public async Task FreightToTheCustomersAddressWithoutCityIsRejected()      // ShippingCityCode, missingOn "Customer"
[Fact] public async Task FreightToTheCustomersAddressWithCityPasses()
[Fact] public async Task AForeignCustomerNeedsItsCityName()                       // country "ES", CityName null → ShippingCityCode
[Fact] public async Task WithoutRegionalSettingsTheDefaultRules()                // Regional null + no document → DocumentCode

// ApproveOrderHandlerTests.cs
[Fact] public async Task ApproveRejectsACustomerWithoutDocumentAndLeavesTheOrderPending()  // status Pending, no audit

// BilledCustomerFieldsApiTests.cs (integration)
[Fact] public async Task ApprovingAnOrderOfACustomerWithoutDocumentFollowsThePolicy()
// relax Document → create customer without document → manual order → approve 200;
// second order, set Document.OnBilling true → approve 422, code order.order.billed_document_required, missingOn "Customer"
```

The non-`None` `ShippingDetails` in tests must pass `ShippingDetails.Validated` (a carrier with a freight amount, as `OrderShippingApiTests` builds them).

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~BilledCustomerFieldsRequiredTests|FullyQualifiedName~ApproveOrderHandlerTests"
```

Expected: build error `CS0103: The name 'BilledCustomerFieldsRequired' does not exist`.

- [ ] **Step 3: Implement the class and the call in `ApproveOrderHandler`**

- [ ] **Step 4: Run to verify they pass**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~BilledCustomerFieldsApiTests|FullyQualifiedName~OrderApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: `Passed!` in both.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Quotations tests/Modules/Quotations
git commit -m "feat(orders): require customer document and shipping city on approval by policy"
```

---

### Task 9: POS sale with a customer without document

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Domain/PosSaleInputs.cs` (`PosSaleCustomer` L32), `PosSale.cs` (L24, L47, L133), `src/Modules/Pos/Modules.Pos.Application/CreatePosSale.cs` (ctor L79-94, L165-171), `PosDtos.cs` (`PosCustomerResponse` L179), `src/Modules/Pos/Modules.Pos.Infrastructure/Persistence/PosDbContext.cs` (L110)
- Create: migration `AllowNullPosSaleCustomerIdentification`
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/CreatePosSaleHandlerTests.cs` (theory L241-260, `Handler` L38), `PosSaleTests.cs`, `PosTestDoubles.cs`; `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosApiHarness.cs`, `PosSaleApiTests.cs`

**Interfaces:**
- Consumes: `TenantRegionalSettings.EffectiveCustomerFieldPolicy`.
- Produces:
  - `PosSaleCustomer(Guid Id, string Name, string? IdentificationType, string? IdentificationNumber)`
  - `PosSale.CustomerIdentificationNumber` is `string?`; with a customer it is the customer's number (possibly `null`), without one `PosFinalConsumer.IdentificationNumber` (G12)
  - `PosCustomerResponse(string Name, string? IdentificationType, string? IdentificationNumber)`
  - `CreatePosSaleHandler` gains an `ITenantDirectory tenantDirectory` constructor parameter (after `customers`)
  - Test double `FakeTenantDirectory : ITenantDirectory` in `PosTestDoubles.cs` with `TenantRegionalSettings? Regional`; `PosApiHarness.SetTenantCustomerFieldPolicyAsync(...)` (same SQL as Task 7)

- [ ] **Step 1: Write the failing tests**

```csharp
// CreatePosSaleHandlerTests.cs — the "noDocument" InlineData of AnUnusableCustomerIsADefinitive422 moves to:
[Fact] public async Task ACustomerWithoutDocumentIsRejectedWhenTheDocumentIsRequiredOnBilling()  // pos.sale.customer_identification_missing
[Fact] public async Task ACustomerWithoutDocumentSellsWhenTheDocumentIsNotRequiredOnBilling()
// → sale.CustomerId set, CustomerIdentificationType null, CustomerIdentificationNumber null

// PosSaleTests.cs
[Fact] public void ACustomerWithoutDocumentNeverTakesTheFinalConsumerNumber()   // number null, not "222222222222"
[Fact] public void WithoutCustomerTheSaleKeepsTheFinalConsumerNumber()

// PosSaleApiTests.cs (integration)
[Fact] public async Task ASaleToACustomerWithoutDocumentFollowsThePolicy()       // relaxed → 201 with customer.identificationNumber null; Default → 422
[Fact] public async Task FilteringSalesByDocumentIgnoresSalesWithoutOne()        // Review Focus 4: list with document filter → 200, only the matching sale
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~CreatePosSaleHandlerTests|FullyQualifiedName~PosSaleTests"
```

Expected: build error on `FakeTenantDirectory` / the new `CreatePosSaleHandler` argument; after adding the double, `PosDomainException: pos.sale.customer_identification_missing` in the relaxed case.

- [ ] **Step 3: Implement**

In `CreatePosSaleHandler`, read the policy only when the customer lacks type or number, and throw `customer_identification_missing` only with `Document.OnBilling`. Then:

```powershell
dotnet ef migrations add AllowNullPosSaleCustomerIdentification --project src/Modules/Pos/Modules.Pos.Infrastructure --context PosDbContext -o Persistence/Migrations
```

Check `Up`: a single `AlterColumn` making `customer_identification_number` nullable.

- [ ] **Step 4: Run to verify they pass**

```powershell
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosSaleApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: `Passed!` in both.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Pos tests/Modules/Pos
git commit -m "feat(pos): sell to a customer without document when the policy allows it"
```

---

### Task 10: POS quick create and selector

**Files:**
- Modify: `src/Modules/Pos/Modules.Pos.Application/PosCustomers.cs` (validator L37-46, handler L64-90), `IPosCustomerLookup.cs` (`PosCustomerRef` L8, `NewPosCustomer` L19), `PosDtos.cs` (`PosCustomerOptionResponse` L186)
- Modify: `src/Bootstrapper/PosCustomerLookup.cs` (`ToRef` L75-81, `CreateFromRegisterAsync`), `src/Modules/Customers/Modules.Customers.Application/ICustomerRepository.cs` (doc of `SearchSelectableAsync` L37-42), `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs` (filter ~L233)
- Test: `tests/Modules/Pos/Modules.Pos.UnitTests/PosCustomerHandlersTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/PosCustomerLookupTests.cs`, `tests/Modules/Pos/Modules.Pos.IntegrationTests/PosCustomerApiTests.cs`

**Interfaces:**
- Produces:
  - `PosCustomerRef(Guid Id, string Name, string? IdentificationType, string? IdentificationNumber, bool IsComplete, bool IsActive)`; `PosCustomerOptionResponse` with the same two fields nullable
  - `NewPosCustomer(string Name, string? IdentificationType, string? IdentificationNumber, string? Phone)` (consumes Task 6's nullable `RegisterCustomerData`)
  - `public sealed class PosCustomerIdentificationException(string code, IReadOnlyDictionary<string, string[]> fieldErrors) : DomainException, IHasFieldErrors` (G8) with codes `"customers.customer.identification_required"` / `"customers.customer.identification_incomplete"` and the keys of Task 6
  - `CreatePosCustomerHandler` gains `ITenantDirectory tenantDirectory`
  - `SearchSelectableAsync` filter: `IsActive && ((IdentificationNumber != null && IdentificationType != null) || Completeness == CustomerCompleteness.Complete)` (G7)

Validator: type checked against `PosCustomerRules.IdentificationTypes` and number `MaximumLength` only when present; no `NotEmpty`. Handler: shape rule first (incomplete pair), then `Document.OnCustomerSave` (spec §6.4: the quick create is a "save customer"), then `FindByIdentificationAsync` only when a document is present. `PosCustomerLookup.CreateFromRegisterAsync` passes nulls through; `CustomerProvisioning.CreateFromRegisterAsync` already maps them with the nullable `CustomerMapping.ToIdentification` (Task 6).

- [ ] **Step 1: Write the failing tests**

```csharp
// PosCustomerHandlersTests.cs
[Fact] public async Task QuickCreateWithoutDocumentPassesWhenTheDocumentIsOptionalOnSave()  // no FindByIdentification call
[Fact] public async Task QuickCreateWithoutDocumentIsRequiredByDefault()   // PosCustomerIdentificationException code identification_required, keys both
[Fact] public async Task QuickCreateWithHalfADocumentIsIncomplete()        // type only → identification_incomplete, keys ["IdentificationNumber"]

// PosCustomerLookupTests.cs (Bootstrapper)
[Fact] public async Task SearchMapsACompleteCustomerWithoutDocumentToARefWithNulls()

// PosCustomerApiTests.cs (integration) — TheCashierSearchesOnlyActiveCustomersWithDocument becomes:
[Fact] public async Task TheCashierSearchesActiveCustomersWithDocumentOrComplete()
// the WhatsApp incomplete stays out; a complete customer without document (relaxed policy) is listed with null fields
[Fact] public async Task TheCashierQuickCreatesACustomerWithoutDocumentWhenAllowed()   // 201, identificationNumber null
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests --filter "FullyQualifiedName~PosCustomerHandlersTests"
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~PosCustomerLookupTests"
```

Expected: `ValidationException` (`'Identification Number' must not be empty.`) and `InvalidOperationException: Nullable object must have a value.` from `ToRef`.

- [ ] **Step 3: Implement as described in Interfaces**

- [ ] **Step 4: Run to verify they pass**

```powershell
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests --filter "FullyQualifiedName~PosCustomerApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: all `Passed!`.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Pos src/Bootstrapper/PosCustomerLookup.cs src/Modules/Customers/Modules.Customers.Application/ICustomerRepository.cs src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerRepository.cs tests/Modules/Pos tests/Bootstrapper/Bootstrapper.UnitTests/PosCustomerLookupTests.cs
git commit -m "feat(pos): quick-create and select customers without document by policy"
```

---

### Task 11: Excel import by policy

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs` (`ExcelCustomerRowRules` L35-165, `ImportCustomersHandler` ctor L167-178 and `ValidateRowsAsync` L272-370, `RemoveExistingIdentificationsAsync` L473, writes L596-660, `CandidateRow` L748, `ExcelCustomerRowDeduplicator` L780)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/ExcelCustomerRowRulesTests.cs`, `ExcelCustomerRowDeduplicatorTests.cs`; `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs`

**Interfaces:**
- Produces:
  - `public ExcelCustomerRowRules(CustomerFieldPolicy policy)` plus the parameterless ctor delegating to `CustomerFieldPolicy.Default` (DI keeps resolving `IValidator<ExcelCustomerRow>` with today's behaviour; the handler builds `new ExcelCustomerRowRules(policy)` per file, reading the policy once through a new `ITenantDirectory tenantDirectory` ctor parameter that replaces `IValidator<ExcelCustomerRow> rowValidator`)
  - New row code `customers.import.row.identification_incomplete` (type without number reported on `IdentificationNumber`, number without type on `IdentificationType`)
  - `CandidateRow.Type` → `IdentificationType?`, `Number` → `string?`, `City` → `CustomerCityRef?`

Rules: `identification_type_required` and `identification_number_required` only with `Document.OnCustomerSave` and both empty; half a pair is always `identification_incomplete`. Department/city as a pair (G9): both empty → error only with `City.OnCustomerSave`; one without the other keeps `department_required` / `city_required`. Rows without document are skipped by `ExcelCustomerRowDeduplicator.Partition` and `RemoveExistingIdentificationsAsync`; rows without city skip `FindCityByNameAsync` and take the `"00"` department in the CUC (G4).

- [ ] **Step 1: Write the failing tests**

```csharp
// ExcelCustomerRowRulesTests.cs
[Fact] public void ARowWithoutDocumentIsValidWhenTheDocumentIsOptional()
[Fact] public void ARowWithOnlyATypeIsIncomplete()               // code customers.import.row.identification_incomplete on IdentificationNumber
[Fact] public void ARowWithoutDepartmentAndCityIsValidWhenTheCityIsOptional()
[Fact] public void ARowWithACityButNoDepartmentIsStillRejected() // department_required even with City (false, *)
[Fact] public void TheDefaultPolicyKeepsTodaysRequiredCodes()    // new ExcelCustomerRowRules() → identification_type_required, city_required

// ExcelCustomerRowDeduplicatorTests.cs
[Fact] public void RowsWithoutDocumentAreNeverDuplicatesOfEachOther()

// CustomerStatusAndImportApiTests.cs (integration)
[Fact] public async Task AnImportWithTwoRowsWithoutDocumentCreatesBothWhenAllowed()   // Review Focus 3: 2 created, 0 errors
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~ExcelCustomerRowRulesTests|FullyQualifiedName~ExcelCustomerRowDeduplicatorTests"
```

Expected: build error `CS1729: 'ExcelCustomerRowRules' does not contain a constructor that takes 1 arguments`.

- [ ] **Step 3: Implement as described in Interfaces**

- [ ] **Step 4: Run to verify they pass**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerStatusAndImportApiTests|FullyQualifiedName~CustomerExportApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: `Passed!` in both (export unchanged: it already writes `""` for nulls).

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs tests/Modules/Customers
git commit -m "feat(customers): import customers without document or city by policy"
```

---

### Task 12: `/auth/me` publishes the policy

**Files:**
- Modify: `src/Api/AuthSessionEndpoints.cs` (`ActiveTenantResponse` L172-183 and its remarks), `src/Api/SessionEstablisher.cs` (L117-128)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs` (G10)

**Interfaces:**
- Consumes: `TenantRegionalSettings.EffectiveCustomerFieldPolicy` (Task 2).
- Produces: `public sealed record SessionCustomerFieldPolicyResponse(SessionFieldRequirementResponse Document, SessionFieldRequirementResponse City)`, `public sealed record SessionFieldRequirementResponse(bool OnCustomerSave, bool OnBilling)`, and `ActiveTenantResponse(..., IReadOnlyList<SessionPriceRoundingResponse> PriceRoundings, SessionCustomerFieldPolicyResponse? CustomerFieldPolicy)` — `null` when `regional` is `null`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task TheSessionCarriesTheDefaultCustomerFieldPolicy()
// LoginAndGetSeededTenantAsync → tenant.GetProperty("customerFieldPolicy")
//   .GetProperty("document").GetProperty("onCustomerSave").GetBoolean() == true (the four true)

[Fact] public async Task TheSessionCarriesTheStoredCustomerFieldPolicyToACashier()
// store Document (false,true) / City (false,false) via TenancyDbContext + UpdateSettings(..., customerFieldPolicy);
// log in a member without tenancy.settings.read (X-Permissions as in TheSessionCarriesThePriceRoundingOfEveryCurrency)
// → the four values as stored
```

- [ ] **Step 2: Run to verify they fail**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthSessionApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: `System.Collections.Generic.KeyNotFoundException: The given key was not present in the dictionary.` on `customerFieldPolicy`.

- [ ] **Step 3: Implement the two records and the composition in `SessionEstablisher` from `regional?.EffectiveCustomerFieldPolicy`**

- [ ] **Step 4: Run to verify they pass**

Same command. Expected: `Passed!`.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Api/AuthSessionEndpoints.cs src/Api/SessionEstablisher.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/AuthSessionApiTests.cs
git commit -m "feat(auth): publish the customer field policy in the session"
```

---

### Task 13: Whole-branch verification

- [ ] **Step 1: Build and architecture**

```powershell
dotnet build
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`; `CustomersLayerTests`, `PosLayerTests`, `QuotationsLayerTests`, `TenancyLayerTests` all pass.

- [ ] **Step 2: Every unit test project touched**

```powershell
dotnet test tests/Modules/Tenancy/Modules.Tenancy.UnitTests
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/Modules/Pos/Modules.Pos.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.UnitTests
dotnet test tests/Modules/Store/Modules.Store.UnitTests
```

Expected: every one `Passed!`, 0 failed.

- [ ] **Step 3: Integration, one project at a time (once)**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Pos/Modules.Pos.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Store/Modules.Store.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/ServiceContracts/Modules.ServiceContracts.IntegrationTests -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Shopify/Modules.Shopify.IntegrationTests -- xUnit.MaxParallelThreads=4
```

Run each only after the previous one finishes. Store covers §10.2 "Tomar with an incomplete customer without document completes it with the visitor's" (no regression expected; `CompleteFromStoreAsync` does not apply the policy). Expected: every one `Passed!`. A failure that also fails on `develop` is reported as pre-existing, not fixed here.

- [ ] **Step 4: Spec check**

Walk §7 (error codes) and §10 of the spec against the test names of Tasks 1-12 and paste the mapping in the task log; anything uncovered is a new failing test in the owning task, not a note.

- [ ] **Step 5: Final state**

```powershell
git branch --show-current
git status --short
git log --oneline develop..HEAD
```

Expected: `feature/customer-field-policy`, clean tree (only the developer's own untracked files, if any), one commit per task. `docs/estado.md` untouched (`git diff --stat develop..HEAD -- docs/estado.md` is empty).
