# Company Contacts (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a customer (a person) be linked to another customer of the same tenant that acts as its company, and make the customers list, Messaging, Pipeline, the WhatsApp send of quotations/orders, the merge and the Excel import/export use that link.

**Architecture:** `Customer` gains a nullable self-reference `CompanyId` (one level only), set and cleared by a dedicated action `PUT /customers/{id}/company`; the regular `POST`/`PUT` never read or touch it. Other modules never reference Customers: they reach the link through one new Customers port, `ICustomerCompanyDirectory`, wrapped by the existing adapters in `src/Bootstrapper`. Everything is additive (nullable column, optional request fields, new response fields), so an old frontend keeps working and the backend ships first.

**Tech Stack:** .NET 10, EF Core (Npgsql, `pg_trgm`), FluentValidation, ClosedXML, xUnit v3, Testcontainers, PowerShell 5.1.

**Spec:** `docs/superpowers/specs/2026-11-10-contactos-de-empresa-backend-design.md` (sibling, shared contract only: `../qep-frontend/docs/superpowers/specs/2026-11-10-contactos-de-empresa-frontend-design.md`). This backend spec owns the HTTP contract. Executors read the spec together with this plan.

**Where the work happens:** worktree `..\qep-backend-worktrees\contactos-de-empresa`, branch `feature/contactos-de-empresa`, created from `develop` (Task 0). Commits go there only. **Never merge, never push, never touch `main` or `develop`.**

## Global Constraints

- **Names, exactly as the spec (§3.2, §4):** property `Customer.CompanyId` (`CustomerId?`), column `company_id`; route `PUT /api/v1/tenants/{tenantId:guid}/customers/{customerId:guid}/company`, body `{ "companyId": "<guid>" | null }`; query parameter `contacts` = `include` | `exclude`; response fields `businessName`, `personType`, `company` (`{ id, name, isComplete, isActive } | null`), `contactCount`, `contacts` (`[{ id, name, phone, email, isComplete, isActive }]`); send fields `contactId`, `threadConnectionId`; recipient value `Contact`; `clientContacts` (`[{ id, name, phone }]`); merge `Contacts` (move kind) and `companyConflict` (`bool`); import field `companyIdentificationNumber`, Excel header `Empresa`.
- **New error codes, exactly (all 422):** `customers.customer.company_not_found`, `customers.customer.company_inactive`, `customers.customer.company_is_self`, `customers.customer.company_is_contact`, `customers.customer.has_contacts`, `customers.customer.merge_company_conflict`, `quotation.quotation.contact_not_of_client`, `order.order.contact_not_of_client`. Import row codes named by this plan (G6): `customers.import.row.company_not_found`, `customers.import.row.company_ambiguous`, `customers.import.row.company_inactive`, `customers.import.row.company_is_self`, `customers.import.row.company_is_contact`, `customers.import.row.has_contacts`. Reused, unchanged: `{quotation|order}.whatsapp.recipient_missing`, `{quotation|order}.whatsapp.channel_required`, `…no_channel`, `…channel_already_set`.
- **New audit action:** `customers.customer.company_changed`, added to `CustomerAuditActions.cs`.
- **Permission:** the link action uses the existing `customers.customer.manage` (`CustomersPermissions.CustomerManage`). No new permission, no new policy.
- **Company label (A4):** `BusinessName` when it is not null/whitespace, else `Name`. Computed in the backend in every DTO that exposes a company.
- **One level only (A2):** a contact cannot have contacts; a customer with contacts cannot become a contact; no self-link; same tenant; the company must be active and not merged when linking. The company is **not** restricted to `PersonType.Juridica` (A3).
- **No data migration (A15):** `company_id` is born `NULL` everywhere. No backfill from `BusinessName`. No auto-linking by name similarity anywhere.
- **`POST /customers` and `PUT /customers/{id}` never read or write `CompanyId`** (A5). `CreateCustomerRequest`/`UpdateCustomerRequest` do not gain the field.
- **Unchanged on purpose (spec §7):** anomalies, reporting counts, loans and service-contracts name search, POS search (`SearchSelectableAsync`), campaign audiences, the inbox name search (`FindIdsByNameAsync`), `EnsureAsync`/`EnsureByPhoneAsync`/`ChangeConversationCustomer`, `POST /deals`.
- **Layering:** `Modules.Customers.Application` never references EF Core or Npgsql; Messaging, Pipeline and Quotations never reference Customers (adapters in `src/Bootstrapper`). Translating a DB error lives in Infrastructure (`CustomersUnitOfWork`), discriminating unique violations **by index name**, never only by `SqlState 23505`. `tests/ArchitectureTests` stays green unchanged.
- **Migration:** one, Customers only, additive, generated with the design factory:
  `dotnet ef migrations add AddCustomerCompany --project src/Modules/Customers/Modules.Customers.Infrastructure --context CustomersDbContext -o Persistence/Migrations`
- **TDD, RED before GREEN, with the literal failing and passing output pasted in the task log.** Unit tests run per task. **Integration tests (Testcontainers, one Postgres per test class) are written in the task that owns the behaviour, compiled there (`dotnet build <test project>`), and executed once, in Task 12**, per project, one project at a time, filtered to the touched classes, after `$env:Seed__Enabled = "false"`, always with `-- xUnit.MaxParallelThreads=4`. Never the whole solution in parallel. An integration failure found in Task 12 is fixed there, with that failing run as its RED.
- **`Api.exe`:** if it is running, `dotnet build`, `dotnet test` and `dotnet ef` fail with a locked file. **Never stop a process you did not start.** Record it in the task log, continue with whatever does not need a build (writing tests and code), and retry the build later; if it never unlocks, report it in the final summary.
- **Never print a secret.** `dotnet user-secrets list` and equivalents only masked or counted.
- **Commands for the developer are PowerShell** (no `&&`, `curl.exe`, env vars on their own line).
- **Git:** run `git branch --show-current` right before every commit; it must say `feature/contactos-de-empresa`. Conventional commits. **No AI attribution, no `Co-Authored-By` trailer.** `docs/estado.md` is **not** edited on this branch (Task 12 leaves the text for the merge).
- **The owner is not available while this runs.** Never invent an owner decision: where the spec leaves something open, apply the conservative default written in "Open points" and mark it `PENDING OWNER` in the task log and in the final summary.
- Code comments follow the neighbouring ones (Spanish, *tú*, explaining why); exception messages in English like their neighbours.

## Review Focus

1. **A search parameter that is empty or whitespace** (`?search=%20`, `?name=`) is not a search: contacts stay hidden. Only a parameter that produces a filter pattern un-hides them. — test in Task 4.
2. **Re-linking a contact to the company it already has** answers 200 with the same `version`, and publishes no audit record; unlinking a customer that has no company does the same. — tests in Tasks 1 and 3.
3. **An import file where row A names row B as its company and row B names row A**, and a row that names its own document: no row is linked half-way; each offending row gets its row error. — test in Task 7.
4. **`recipient: "Contact"` without `contactId`**, or with the id of a merged customer, or of the document's own client: 422 `…contact_not_of_client`, nothing sent, nothing persisted. — tests in Task 10.
5. **A company whose `BusinessName` is whitespace or null, and a company deactivated after its contacts were linked:** the `company` reference still travels, `name` falls back to `Name`, `isActive` is `false`, and no list or conversation query breaks. — tests in Tasks 2 and 4.

## Spec gaps settled in this plan

Each one is a plan decision, reversible by the owner; the owning task repeats it in a code comment. None changes what a person sees beyond what the spec already describes.

- **G1 (port).** One new port in `Modules.Customers.Application`, `ICustomerCompanyDirectory`, serves every cross-module read of the link (Messaging, Pipeline, Quotations) and the Customers DTOs. Spec §8 leaves the shape to the builder.
- **G2 (concurrency, spec §4.1).** The link handler opens a transaction (`ICustomersUnitOfWork.BeginTransactionAsync`, as the merge does) and locks the contact row and the company row `FOR UPDATE`, **ordered by id**, before checking the rules. Every link locks both rows, so A→B and B→A serialize, and so does C→A against A→B.
- **G3 (FK).** `company_id` references `customers.customers(id)` with `ON DELETE SET NULL`. There is no customer `DELETE` today (`CustomerEndpoints.cs:138`); this makes A17 true by construction if one is ever built.
- **G4 (POS and the shared query).** `CustomerRepository.FilteredQuery` is shared by the list, the export **and** `SearchSelectableAsync` (POS, `CustomerRepository.cs:288`). The contacts rule and the `BusinessName` match are parameters of `FilteredQuery`; `SearchSelectableAsync` passes the values that keep it exactly as today.
- **G5 (importer).** `ClosedXmlCustomerImporter` reads by header and rejects the whole file when any header of `CustomerImportColumns.Ordered` is missing (`ClosedXmlCustomerImporter.cs:39-46`). So `Empresa` is a new constant **outside** `Ordered`, read only when its header is present; a file without it is valid and reads as "no company". This closes the spec's open point on header vs position.
- **G6 (import row codes).** The spec asks for three kinds of row error and leaves the names: not found → `company_not_found`; ambiguous number → `company_ambiguous`; A2 broken → the same suffix as the domain code (`company_inactive`, `company_is_self`, `company_is_contact`, `has_contacts`), all under `customers.import.row.`, all with `field: "companyIdentificationNumber"`.
- **G7 (`contacts` binding).** The parameter arrives as text, like `isComplete`: an unknown value is 422 `validation.failed` with key `contacts`, not a 400 from binding. Matching is case-insensitive.
- **G8 (`Contact` without `contactId`).** The spec says "obligatorio" without naming a code. A missing `contactId` is treated as "not a contact of the client": `…contact_not_of_client`. No new code.
- **G9 (which deal belongs to whom, spec §4.5 and §9).** Verified: `CustomerOpenDealDto` and `CustomerClosedDealDto` (`CustomerSummary.cs`) carry no customer. Both gain `CustomerId` (`Guid`, JSON `customerId`) as their **last** parameter, so the strip can tell the company's deals from the person's.
- **G10 (`LastClosedDeal` on the union).** It stays a single deal: the most recently closed of the two customers.
- **G11 (triage, what stays).** The triage **decision** keeps the conversation's customer (the person) as its `customerId`; only the **deal** is created on the company. `TriageState.EvaluateAsync` is not changed by this plan (see Open points).
- **G12 (contacts cap).** `contacts` in the customer detail is capped at 100, ordered by name then id; `contactCount` is the full count.
- **G13 (inactive contact).** Linking does not call `EnsureActive` on the contact (same as notes and tags, which work on an inactive customer); it does call `EnsureNotMerged`. The task verifies the notes/tags precedent first and follows the code if it says otherwise.
- **G14 (merge counts).** The `Contacts` move count is the number of non-merged customers whose `company_id` is one of the selected merged ids, **excluding the survivor**.

## Open points and their conservative default

| Open point (spec §9 / §8) | Verified first in | Default if it cannot be resolved |
| --- | --- | --- |
| Does each deal of the summary say whose it is? | Done while planning: no. Task 9 adds `customerId` (G9). | — |
| Importer by header or by position? | Done while planning: by header (G5). Task 7 pins it with a test. | — |
| How the Automations module reads customers | Task 12, step 2 (`src/Bootstrapper/Automation*.cs`) | No change. Record what it reads in the final summary. `PENDING OWNER` only if it lists or counts customers in a way a contact would distort. |
| Does `TriageState.EvaluateAsync` look at the customer's open deals? | Task 9, step 1 | Leave it as is. A contact's conversation may stay "pending triage" while its company already has an open deal: record it as `PENDING OWNER`, do not change triage rules. |
| Inbox search by company name | — | Out of scope (spec §4.4). Not built. |
| Orders ↔ conversation link | — | None exists; nothing to build. |
| Ficha phone different from the thread's number (A20) | — | Known edge, not solved. Not built. |
| Names of codes / parameters (spec §8) | — | The names in Global Constraints are final for this plan; Task 12 lists them for the frontend. |

---

## File map

| File | Responsibility | Task |
| --- | --- | --- |
| `src/Modules/Customers/Modules.Customers.Domain/Customer.cs`, `CustomersErrorCodes.cs` | `CompanyId`, link/unlink rules, merge tombstone clears the link | 1 |
| `Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs`, migration `AddCustomerCompany`, `CustomerCompanyDirectory.cs` (new), `CustomerRepository.cs`; `Modules.Customers.Application/ICustomerCompanyDirectory.cs` (new), `ICustomerRepository.cs` | Column, FK, indexes; the port and its queries; the row lock | 2 |
| `Modules.Customers.Application/SetCustomerCompany.cs` (new), `CustomerAuditActions.cs`, `CustomersDtos.cs`, `CustomerMapping.cs`; `Modules.Customers.Api/CustomerEndpoints.cs` | Link action; `company` on the detail | 3 |
| `Modules.Customers.Application/ListCustomers.cs`, `ExportCustomers.cs`, `GetCustomer.cs`, `CustomersDtos.cs`; `CustomerRepository.cs`; `CustomerEndpoints.cs` | `contacts` filter, `BusinessName` search, list fields, `contacts`/`contactCount` on the detail | 4 |
| `Modules.Customers.Domain/CustomerMerge.cs`; `Modules.Customers.Application/CustomerMerge.cs`, `ICustomerMergeStore.cs`, `CustomersDtos.cs`; `Persistence/CustomerMergeStore.cs` | Merge rules, preview `Contacts` + `companyConflict` | 5, 6 |
| `Modules.Customers.Application/IExcelCustomerImporter.cs`, `ImportCustomers.cs`, `CustomersDtos.cs`, `ExportFailedCustomerRows.cs`; `Modules.Customers.Infrastructure/Excel/*` | `Empresa` column in import, failed rows, template, export | 7 |
| `Modules.Messaging.Application/MessagingDtos.cs`; `src/Bootstrapper/MessagingCustomerDirectory.cs` | `company` on the conversation's customer reference | 8 |
| `Modules.Pipeline.Application/PipelineDirectories.cs`, `Triage.cs`, `CustomerSummary.cs`; `src/Bootstrapper/PipelineCustomerDirectory.cs` | Triage deal on the company; summary accepts the company | 9 |
| `Modules.Quotations.Application/SendQuotation.cs`, `SendOrder.cs`, `WhatsAppChannelSelection.cs`, `QuotationsDtos.cs`, `OrdersDtos.cs`, `QuotationChangeSummary.cs`, `OrderChangeSummary.cs`, `IQuotationCustomerLookup.cs`; `Modules.Quotations.Api/QuotationEndpoints.cs`, `OrderEndpoints.cs`; `src/Bootstrapper/QuotationCustomerLookup.cs` | `Contact` recipient, `threadConnectionId` | 10 |
| `Modules.Quotations.Application/QuotationResponseComposer.cs`, `OrderResponseComposer.cs`, `QuotationsDtos.cs`, `OrdersDtos.cs` | `clientContacts` | 11 |

Test projects: `tests/Modules/<Module>/Modules.<Module>.UnitTests`, `tests/Modules/<Module>/Modules.<Module>.IntegrationTests`, `tests/Bootstrapper/Bootstrapper.UnitTests`, `tests/ArchitectureTests`.

---

### Task 0: Worktree, branch check, plan and spec commit

- [ ] **Step 1: Create the worktree from `develop`** (from the main checkout of `qep-backend`)

```powershell
git branch --show-current
git worktree add ..\qep-backend-worktrees\contactos-de-empresa -b feature/contactos-de-empresa develop
```

Expected: first line `develop` (if it says `main`, stop and report). Do not copy or link a `.codegraph/` folder from another checkout.

- [ ] **Step 2: Bring the spec and this plan into the worktree** (they are untracked in the main checkout, so the worktree does not have them)

```powershell
Copy-Item docs\superpowers\specs\2026-11-10-contactos-de-empresa-backend-design.md ..\qep-backend-worktrees\contactos-de-empresa\docs\superpowers\specs\
Copy-Item docs\superpowers\plans\2026-11-10-contactos-de-empresa-backend.md ..\qep-backend-worktrees\contactos-de-empresa\docs\superpowers\plans\
```

- [ ] **Step 3: Commit, inside the worktree**

```powershell
git branch --show-current
git add docs/superpowers/specs/2026-11-10-contactos-de-empresa-backend-design.md docs/superpowers/plans/2026-11-10-contactos-de-empresa-backend.md
git commit -m "docs(customers): company contacts backend spec and plan"
```

Expected: `feature/contactos-de-empresa`. Every later task runs inside the worktree.

- [ ] **Step 4: Baseline build**

```powershell
dotnet build tests/Modules/Customers/Modules.Customers.UnitTests
```

Expected: `Build succeeded`. A locked-file error means `Api.exe` is running: see Global Constraints.

---

### Task 1: The link on the `Customer` aggregate

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs` (property next to `MergedIntoId` ~L311; methods next to `EnsureNotMerged` ~L1054; `ApplyMergedInto` ~L1063)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomersErrorCodes.cs`
- Test: create `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerCompanyTests.cs` (use `CustomerDomainFixtures.cs` to build customers)

**Interfaces:**
- Produces (namespace `Modules.Customers.Domain`):
  - `public CustomerId? CompanyId { get; private set; }`
  - `public bool LinkToCompany(Customer company, bool hasContacts, DateTimeOffset occurredAt)` — returns `false` and changes nothing when `CompanyId` already equals `company.Id`; otherwise sets it, bumps `Version` and `UpdatedAt` the way `Deactivate` does, returns `true`.
  - `public bool UnlinkFromCompany(DateTimeOffset occurredAt)` — `false` when there is no company.
  - `CustomersErrorCodes.CompanyNotFound`, `CompanyInactive`, `CompanyIsSelf`, `CompanyIsContact`, `HasContacts`, `MergeCompanyConflict` with the strings of Global Constraints.
  - `ApplyMergedInto` sets `CompanyId = null` (spec §4.7, third rule).

- [ ] **Step 1: Read first.** Read `Customer.Deactivate` (L971), `EnsureNotMerged` (L1054) and `ApplyMergedInto` (L1063) to copy how they bump `Version`/`UpdatedAt` and throw `CustomersDomainException`. Verify G13: check whether the note and tag handlers call `EnsureActive`; if they do, add `EnsureActive()` to both new methods and say so in the task log.

- [ ] **Step 2: Write the failing tests** in `CustomerCompanyTests`:

```csharp
[Fact] public void LinkingSetsTheCompanyAndBumpsTheVersion()            // CompanyId == company.Id; Version == before + 1; returns true
[Fact] public void LinkingAnIncompleteCustomerKeepsItIncomplete()       // IsComplete stays false
[Fact] public void LinkingToTheSameCompanyAgainChangesNothing()         // returns false; Version unchanged
[Fact] public void LinkingToItselfIsRejected()                          // code == "customers.customer.company_is_self"
[Fact] public void LinkingToAMergedCompanyIsRejected()                  // "customers.customer.company_not_found"
[Fact] public void LinkingToAnInactiveCompanyIsRejected()               // "customers.customer.company_inactive"
[Fact] public void LinkingToACompanyThatIsAContactIsRejected()          // "customers.customer.company_is_contact"
[Fact] public void LinkingACustomerThatHasContactsIsRejected()          // hasContacts: true → "customers.customer.has_contacts"
[Fact] public void LinkingAMergedCustomerIsRejected()                   // CustomersErrorCodes.Merged
[Fact] public void UnlinkingClearsTheCompany()                          // CompanyId == null; returns true; Version + 1
[Fact] public void UnlinkingWithoutCompanyChangesNothing()              // returns false; Version unchanged
[Fact] public void AMergedCustomerLosesItsCompany()                     // after ApplyMergedInto (through CustomerMergePlan.MarkMerged): CompanyId == null
```

Each rejection asserts `Assert.Throws<CustomersDomainException>(…).Code`. A company from another tenant is rejected with `company_not_found` too (`company.TenantId != TenantId`) — add `LinkingToACompanyOfAnotherTenantIsRejected`.

- [ ] **Step 3: Run to see them fail**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerCompanyTests"
```

Expected: build error, `'Customer' does not contain a definition for 'LinkToCompany'`.

- [ ] **Step 4: Implement** the property, the two methods, the six constants and the `ApplyMergedInto` line. Rule order in `LinkToCompany`: `EnsureNotMerged()` → self → other tenant or merged company (`company_not_found`) → inactive → company is a contact → `hasContacts` → same company (no-op).

- [ ] **Step 5: Run to see them pass**, then the whole unit project:

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
```

Expected: `Passed!`, 0 failed.

- [ ] **Step 6: Commit**

```powershell
git branch --show-current
git add src/Modules/Customers/Modules.Customers.Domain tests/Modules/Customers/Modules.Customers.UnitTests/CustomerCompanyTests.cs
git commit -m "feat(customers): link a customer to a company customer in the aggregate"
```

---

### Task 2: Column, indexes, migration and the company directory

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomersDbContext.cs` (next to the `MergedIntoId` mapping and the indexes at L236-275)
- Create: migration `AddCustomerCompany` (generated; `CustomersDbContextModelSnapshot.cs` updates itself)
- Create: `src/Modules/Customers/Modules.Customers.Application/ICustomerCompanyDirectory.cs`
- Create: `src/Modules/Customers/Modules.Customers.Infrastructure/Persistence/CustomerCompanyDirectory.cs`
- Modify: `ICustomerRepository.cs`, `CustomerRepository.cs` (row lock), `CustomersInfrastructureExtensions.cs` (registration next to `ICustomerProvisioning`, L61)
- Test: create `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCompanyDirectoryTests.cs` and `CustomerCompanyMigrationTests.cs` (model them on `CustomerMergeStoreTests.cs` and `CustomerMergeMigrationTests.cs`); create `tests/Modules/Customers/Modules.Customers.UnitTests/FakeCustomerCompanyDirectory.cs`

**Interfaces:**
- Consumes: `Customer.CompanyId` (Task 1).
- Produces (namespace `Modules.Customers.Application`):

```csharp
/// La empresa de un cliente, como la necesita quien la muestra. Name ya es la etiqueta (A4): BusinessName ?? Name.
public sealed record CustomerCompanyRef(Guid Id, string Name, bool IsComplete, bool IsActive);

public sealed record CustomerContactRef(Guid Id, string Name, string? Phone, string? Email, bool IsComplete, bool IsActive);

public interface ICustomerCompanyDirectory
{
    /// Por id de contacto: sólo aparecen los que tienen empresa. Una consulta para toda la página.
    Task<IReadOnlyDictionary<Guid, CustomerCompanyRef>> FindCompaniesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// Contactos sin fusionar de una empresa, por nombre y luego id, hasta take.
    Task<IReadOnlyList<CustomerContactRef>> ListContactsAsync(
        Guid tenantId, Guid companyId, int take, CancellationToken cancellationToken);

    /// Por id de empresa: cuántos contactos sin fusionar tiene. Las que no tienen no aparecen.
    Task<IReadOnlyDictionary<Guid, int>> CountContactsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken);

    /// El contacto, sólo si existe en el tenant, no está fusionado y su empresa es companyId.
    Task<CustomerContactRef?> FindContactOfAsync(
        Guid tenantId, Guid companyId, Guid contactId, CancellationToken cancellationToken);
}
```

  - `ICustomerRepository.LockForCompanyLinkAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)` → `Task`: `SELECT id FROM customers.customers WHERE tenant_id = @tenant AND id = ANY(@ids) ORDER BY id FOR UPDATE` (raw SQL selecting only `id`; see the `FromSql` gotcha in `CLAUDE.md` — do not map an entity from it).
  - `FakeCustomerCompanyDirectory` (unit tests): an in-memory implementation fed with a list of `Customer`.
- Database: column `company_id uuid NULL`; FK to `customers.customers(id)` `ON DELETE SET NULL` (G3); index `IX_customers_tenant_company` on `(tenant_id, company_id)` with filter `company_id IS NOT NULL`; index `IX_customers_business_name_trgm` on `business_name`, method `gin`, operators `gin_trgm_ops` (for Task 4).

- [ ] **Step 1: Write the integration tests** (compiled now, executed in Task 12):

`CustomerCompanyMigrationTests`: `TheMigrationAddsANullableCompanyColumn` (every existing row has `company_id IS NULL` after migrating up from the previous migration and back to the latest — finish with `MigrateAsync(null)`, see `CLAUDE.md`); `TheIndexesExist` (`IX_customers_tenant_company`, `IX_customers_business_name_trgm` in `pg_indexes`).

`CustomerCompanyDirectoryTests`:

```csharp
[Fact] public async Task FindCompaniesReturnsOnlyCustomersThatHaveACompany()
[Fact] public async Task TheCompanyNameIsTheBusinessNameAndFallsBackToTheName()   // BusinessName null and "   " → Name (Review Focus 5)
[Fact] public async Task ADeactivatedCompanyStillTravelsWithIsActiveFalse()       // Review Focus 5
[Fact] public async Task ListContactsSkipsMergedCustomersAndOrdersByName()
[Fact] public async Task ListContactsHonoursTake()
[Fact] public async Task CountContactsCountsPerCompanyAndSkipsMerged()
[Fact] public async Task FindContactOfReturnsNullForACustomerOfAnotherCompanyOrTenant()
[Fact] public async Task NothingCrossesTenants()
```

- [ ] **Step 2: Map the column and indexes** in `CustomersDbContext` (same conversion as `MergedIntoId`), then generate the migration with the command of Global Constraints and name `AddCustomerCompany`. Open the generated file and check it only adds the column, the FK and the two indexes.

- [ ] **Step 3: Implement** `ICustomerCompanyDirectory`, `CustomerCompanyDirectory` (EF, `AsNoTracking`, every query filtered by `tenantId`), `LockForCompanyLinkAsync`, the DI registration and the fake.

- [ ] **Step 4: Build what cannot run yet**

```powershell
dotnet build tests/Modules/Customers/Modules.Customers.IntegrationTests
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~CustomersLayerTests"
```

Expected: build succeeded; unit and architecture tests `Passed!`.

- [ ] **Step 5: Commit**

```powershell
git branch --show-current
git add src/Modules/Customers tests/Modules/Customers
git commit -m "feat(customers): company_id column, indexes and company directory port"
```

---

### Task 3: `PUT /customers/{id}/company` and `company` on the detail

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Application/SetCustomerCompany.cs`
- Modify: `CustomerAuditActions.cs`, `CustomersDtos.cs` (`CustomerDto` L46, `CustomerResponse` L101, `CustomerMergeResponse` L484 — a parity test ties it to `CustomerResponse`), `CustomerMapping.cs` (`ToDto` L30, `ToDtoAsync` L129)
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs` (route next to `deactivate`/`activate` L126-145; `ToResponse` L565-590)
- Test: create `tests/Modules/Customers/Modules.Customers.UnitTests/SetCustomerCompanyHandlerTests.cs` (doubles from `CustomerWriteDoubles.cs`); create `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCompanyApiTests.cs` (harness `CustomersApiHarness.cs`)

**Interfaces:**
- Consumes: Task 1 methods; `ICustomerCompanyDirectory`, `LockForCompanyLinkAsync` (Task 2).
- Produces:
  - `public sealed record SetCustomerCompanyCommand(Guid TenantId, Guid CustomerId, Guid? CompanyId) : ICommand<CustomerDto>;` and `SetCustomerCompanyHandler`.
  - `public sealed record SetCustomerCompanyRequest(Guid? CompanyId);`
  - `public sealed record CustomerCompanyDto(Guid Id, string Name, bool IsComplete, bool IsActive);`
  - `CustomerDto` and `CustomerResponse` gain `CustomerCompanyDto? Company = null` as their last parameter (travels always, `null` included, no `JsonIgnore`, like `MergedInto`).
  - `CustomerMapping.ToDtoAsync` gains an `ICustomerCompanyDirectory` parameter; **every existing caller is updated** (find them with `rg "ToDtoAsync\(" src`), and their handlers gain the dependency. Unit-test doubles pass `FakeCustomerCompanyDirectory`.
  - `CustomerAuditActions.CompanyChanged = "customers.customer.company_changed"`.
  - HTTP: `PUT /{customerId:guid}/company`, `RequireAuthorization(CustomersPermissions.CustomerManage)`, answers `CustomerResponse`; no `If-Match`.

- [ ] **Step 1: Write the failing unit tests** in `SetCustomerCompanyHandlerTests`:

```csharp
[Fact] public async Task LinksAndReturnsTheCompanyInTheDto()                 // dto.Company == (company.Id, label, isComplete, isActive)
[Fact] public async Task CompanyIdNullUnlinks()                              // dto.Company == null
[Fact] public async Task WorksOnAnIncompleteCustomer()                       // dto.IsComplete == false
[Fact] public async Task TheCustomerMustExist()                              // CustomerNotFound (404 path)
[Fact] public async Task AnUnknownCompanyIsCompanyNotFound()                 // "customers.customer.company_not_found"
[Fact] public async Task ACustomerWithContactsCannotBecomeAContact()         // directory reports contacts → "customers.customer.has_contacts"
[Fact] public async Task AuthorizesBeforeReadingTheRepository()              // without customers.customer.manage: forbidden, repository never called
[Fact] public async Task PublishesTheAuditOnlyWhenTheLinkChanges()           // linking twice → one "customers.customer.company_changed" (Review Focus 2)
[Fact] public async Task LocksBothRowsBeforeCheckingTheRules()               // LockForCompanyLinkAsync called with {customer, company} before FindAsync of the company
```

- [ ] **Step 2: Run to see them fail** — `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~SetCustomerCompanyHandlerTests"`. Expected: build error, `SetCustomerCompanyCommand` not found.

- [ ] **Step 3: Implement the handler.** Order: authorize (`CustomersAuthorization.EnsureAuthorized`, as `DeactivateCustomerHandler`) → begin transaction → `LockForCompanyLinkAsync` → load the customer (404 `CustomerNotFound.For`) → if `CompanyId` is null: `UnlinkFromCompany`; else load the company (`company_not_found` when absent), ask `CountContactsAsync` for the customer, call `LinkToCompany` → audit only when the method returned `true` → `SaveChangesAsync` → commit → `ToDtoAsync`. Then the mapping (`company` resolved through `FindCompaniesAsync`), the request record, the endpoint and `ToResponse`.

- [ ] **Step 4: Write the integration tests** in `CustomerCompanyApiTests` (compiled now, executed in Task 12):

```csharp
[Fact] public async Task LinkingReturnsTheCustomerWithItsCompany()            // 200; body.company.id; GET /{id} shows it too
[Fact] public async Task UnlinkingReturnsCompanyNull()
[Fact] public async Task WithoutManagePermissionItIs403()                     // only X-Permissions: customers.customer.read
[Fact] public async Task AnUnknownCustomerIs404()
[Theory] /* company_not_found, company_inactive, company_is_self, company_is_contact, has_contacts */
         public async Task EachRuleAnswers422WithItsCode(string scenario, string code)
[Fact] public async Task TheRegularPutKeepsTheLink()                          // PUT /{id} with a full body on a contact → company unchanged (spec §4.2, mandatory)
[Fact] public async Task TheRegularPostIgnoresACompanyIdInTheBody()
[Fact] public async Task LinkingTwiceKeepsTheVersionAndWritesOneAuditRow()    // Review Focus 2
[Fact] public async Task TwoOppositeLinksAtOnceNeverLeaveTwoLevels()          // A→B and B→A in parallel: exactly one 200, the other 422 (G2)
[Fact] public async Task TheAuditRowIsWrittenInTheSameTransaction()
```

- [ ] **Step 5: Run unit tests and build the integration project**

```powershell
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
dotnet build tests/Modules/Customers/Modules.Customers.IntegrationTests
```

Expected: `Passed!`; build succeeded (other modules' test doubles that call `ToDtoAsync` compile too: `dotnet build` the solution once here).

- [ ] **Step 6: Commit** — `feat(customers): link and unlink a customer's company`.

---

### Task 4: List, search, export and the company's contacts

**Files:**
- Modify: `ListCustomers.cs` (`ListCustomersQuery`, `ListCustomersValidator`, handler, `CustomerPage`), `ExportCustomers.cs`, `GetCustomer.cs`, `CustomersDtos.cs` (`CustomerListItemResponse` L145, `CustomerResponse`), `ICustomerRepository.cs` (`SearchAsync` L31, `ListForExportAsync` L64)
- Modify: `Persistence/CustomerRepository.cs` (`FilteredQuery` L60, `SearchAsync`, `ListForExportAsync` L140, relevance order L232-273, `SearchSelectableAsync` L276-298)
- Modify: `CustomerEndpoints.cs` (`ListCustomersAsync` L188-201, export handler, `ToListItem` L592-606, `ToResponse`)
- Test: modify `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerWriteDoubles.cs` if the fake repository implements `SearchAsync`; create `tests/Modules/Customers/Modules.Customers.UnitTests/ListCustomersContactsTests.cs`; create `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerContactsListApiTests.cs`; extend `CustomerSelectableSearchTests.cs` and `CustomerExportApiTests.cs`

**Interfaces:**
- Consumes: `ICustomerCompanyDirectory` (Task 2), `CustomerCompanyDto` (Task 3).
- Produces:
  - `public enum CustomerContactsFilter { Default, Include, Exclude }` (Application). `ListCustomersQuery` gains `string? Contacts = null` as its last parameter; the export command gains the same.
  - `SearchAsync` and `ListForExportAsync` gain `bool includeContacts`. The handler computes it: `Include` → `true`; `Exclude` → `false`; `Default` → `true` only when at least one of `search`, `name`, `identificationNumber`, `cuc`, `phone` is not null/whitespace.
  - `FilteredQuery` gains `bool includeContacts` and `bool matchBusinessName`. `SearchSelectableAsync` passes `includeContacts: true, matchBusinessName: false` (G4: POS exactly as today).
  - `CustomerListItemResponse` gains, at the end: `string? BusinessName`, `string? PersonType`, `CustomerCompanyDto? Company`, `int ContactCount`.
  - `CustomerResponse` gains, at the end: `IReadOnlyList<CustomerContactDto> Contacts`, `int ContactCount`; `public sealed record CustomerContactDto(Guid Id, string Name, string? Phone, string? Email, bool IsComplete, bool IsActive);`. Cap: `CustomerContactsLimits.MaxInDetail = 100` (G12).
  - `CustomerDto` gains `int ContactCount = 0` and `IReadOnlyList<CustomerContactDto>? Contacts = null` (filled only by `GetCustomer` and by the handlers that answer the full ficha).
  - The list resolves `company` and `contactCount` for the whole page with **two** queries (`FindCompaniesAsync`, `CountContactsAsync`), never one per row.

- [ ] **Step 1: Write the failing unit tests** in `ListCustomersContactsTests` (handler with a recording fake repository):

```csharp
[Theory]
[InlineData(null, null, false)]            // no filter → contacts hidden
[InlineData(null, "acme", true)]           // search → shown
[InlineData(null, "   ", false)]           // whitespace search is not a search (Review Focus 1)
[InlineData("include", null, true)]
[InlineData("exclude", "acme", false)]     // the company picker
[InlineData("INCLUDE", null, true)]        // case-insensitive (G7)
public async Task IncludeContactsIsDecidedFromTheParameters(string? contacts, string? search, bool expected)

[Theory] /* name, identificationNumber, cuc, phone each alone → true */
public async Task AnySearchBoxUnhidesContacts(string box)

[Fact] public void AnUnknownContactsValueFailsValidationWithItsKey()      // "contacts" in the errors map
[Fact] public async Task ThePageCarriesCompanyAndContactCountPerItem()
```

- [ ] **Step 2: Run to see them fail** — `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~ListCustomersContactsTests"`. Expected: build error on `Contacts`.

- [ ] **Step 3: Implement.** In `FilteredQuery`: `if (!includeContacts) query = query.Where(c => c.CompanyId == null)`; when `matchBusinessName`, add `BusinessName` to the `search` OR and to the `name` box (both `ILIKE` with the same pattern), and add `TrigramsSimilarity(customer.BusinessName ?? string.Empty, term)` to the relevance sums at L264-271. Then the query, validator, handler, DTOs, `GetCustomer` (contacts + count through the directory) and the endpoint parameter `string? contacts = null`.

- [ ] **Step 4: Write the integration tests** in `CustomerContactsListApiTests` (compiled now, executed in Task 12):

```csharp
[Fact] public async Task ContactsAreHiddenByDefaultAndTheTotalAgrees()
[Fact] public async Task ASearchBringsThemBack()                               // one test per box: search, name, identificationNumber, cuc, phone
[Fact] public async Task ContactsExcludeWinsOverASearch()
[Fact] public async Task ContactsIncludeShowsThemWithoutASearch()
[Fact] public async Task SearchFindsACompanyByItsBusinessName()                // search=acme and name=acme
[Fact] public async Task ItemsCarryBusinessNamePersonTypeCompanyAndContactCount()
[Fact] public async Task TheCompanyDetailCarriesItsContactsOrderedByName()     // and contactCount; a contact's detail carries company and contacts: []
[Fact] public async Task TheDetailCapsContactsAndStillCountsThemAll()          // 101 contacts → 100 + contactCount 101
[Fact] public async Task TheExportFollowsTheSameContactsRule()                 // in CustomerExportApiTests
[Fact] public async Task ThePageRunsAConstantNumberOfQueries()                 // 1 vs 20 rows: same count (no N+1)
```

In `CustomerSelectableSearchTests`: `ThePosSearchStillIgnoresTheBusinessName` and `ThePosSearchStillReturnsContacts` (G4).

- [ ] **Step 5: Run unit tests, build integration** — same two commands as Task 3 step 5.

- [ ] **Step 6: Commit** — `feat(customers): hide contacts in the list, search by business name, contacts in the detail`.

---

### Task 5: Merge keeps the links

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Domain/CustomerMerge.cs` (`CustomerMergePlan`, L109-207)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerMerge.cs` (`MergeCustomersHandler`), `ICustomerMergeStore.cs`
- Modify: `Persistence/CustomerMergeStore.cs`
- Test: extend `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerMergePlanTests.cs`; extend `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergeStoreTests.cs` and `CustomerMergeApiTests.cs`

The link lives in the Customers schema, so it moves through `ICustomerMergeStore`, **not** `CustomerMergeReferences` (that adapter is for other modules' tables).

**Interfaces:**
- Consumes: `Customer.CompanyId`, `ApplyMergedInto` clearing it (Task 1), `CustomersErrorCodes.MergeCompanyConflict`.
- Produces:
  - `CustomerMergePlan.SurvivorKeepsCompany` (`bool`): the survivor has a `CompanyId` that is **not** one of the merged ids.
  - `CustomerMergePlan.HasCompanyConflict(int survivorContacts, int movedContacts)` → `SurvivorKeepsCompany && survivorContacts + movedContacts > 0`.
  - `CustomerMergePlan.FillSurvivor` clears the survivor's `CompanyId` when it points at a merged customer (internal setter on `Customer`, next to the `Fill*` helpers at L1089-1120).
  - `ICustomerMergeStore.CountContactsMovedAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken)` → `Task<int>` (G14).
  - `ICustomerMergeStore.RepointContactsAsync(Guid tenantId, Guid survivorId, IReadOnlyCollection<Guid> mergedIds, CancellationToken)` → `Task`: `company_id = survivor` for non-merged customers whose `company_id` is in `mergedIds` and whose id is not the survivor. Same style as `RepointMergedIntoAsync` (L39-51), same treatment of `version`/`updated_at` as that method.

- [ ] **Step 1: Write the failing unit tests** in `CustomerMergePlanTests`:

```csharp
[Fact] public void TheSurvivorLosesItsCompanyWhenThatCompanyIsMerged()        // FillSurvivor → survivor.CompanyId == null
[Fact] public void TheSurvivorKeepsACompanyThatIsNotMerged()
[Fact] public void AMergedContactOfTheSurvivorBecomesATombstoneWithoutCompany()
[Theory]
[InlineData(true, 0, 0, false)]    // keeps company, nobody arrives
[InlineData(true, 0, 2, true)]     // keeps company and would gain contacts
[InlineData(true, 1, 0, true)]     // defensive: already has contacts
[InlineData(false, 3, 2, false)]   // no company: gaining contacts is fine
public void TheConflictIsKeepingACompanyWhileHavingContacts(bool keeps, int own, int moved, bool expected)
```

- [ ] **Step 2: Run to see them fail** — `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests --filter "FullyQualifiedName~CustomerMergePlanTests"`.

- [ ] **Step 3: Implement** the plan members, the two store methods and the handler. In `MergeCustomersHandler`, right after `CustomerMergePlan.Create`: count the survivor's contacts (`ICustomerCompanyDirectory.CountContactsAsync`) and the moved ones; if `HasCompanyConflict`, throw `CustomersDomainException(CustomersErrorCodes.MergeCompanyConflict, …)` **before** `MarkMerged`. Call `RepointContactsAsync` after the first `SaveChangesAsync` (the one that writes the tombstones), next to `RepointMergedIntoAsync`, inside the same transaction.

- [ ] **Step 4: Write the integration tests** (compiled now, executed in Task 12). In `CustomerMergeStoreTests`: `RepointContactsMovesThemToTheSurvivor`, `RepointContactsSkipsTheSurvivorItself`, `CountContactsMovedExcludesTheSurvivorAndMergedOnes`. In `CustomerMergeApiTests`:

```csharp
[Fact] public async Task TheContactsOfAMergedCompanyMoveToTheSurvivor()
[Fact] public async Task MergingAContactIntoItsCompanyLeavesNoSelfLink()        // survivor = company, merged = its contact
[Fact] public async Task MergingACompanyIntoItsContactClearsTheSurvivorsLink()  // survivor = contact, merged = its company; the other contacts follow
[Fact] public async Task ASurvivorThatWouldKeepACompanyAndGainContactsIs422()   // "customers.customer.merge_company_conflict"; nothing merged
[Fact] public async Task NoCustomerIsLeftPointingAtAMergedCompanyOrTwoLevelsDeep()  // SQL assertion over the tenant after each scenario
[Fact] public async Task ALinkRacingAMergeNeverPointsAtATombstone()             // PUT …/company to a customer being merged, in parallel
```

- [ ] **Step 5: Run unit tests, build integration.**

- [ ] **Step 6: Commit** — `feat(customers): keep company links through a merge`.

---

### Task 6: Merge preview — `Contacts` and `companyConflict`

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerMerge.cs` (`CustomerMergeMoveKind` L15-25, `GetCustomerMergePreviewHandler` L64-112), `CustomersDtos.cs` (`CustomerMergePreviewResponse` L462-467)
- Test: create `tests/Modules/Customers/Modules.Customers.UnitTests/CustomerMergePreviewCompanyTests.cs`; extend `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerMergePreviewApiTests.cs`

**Interfaces:**
- Consumes: `CountContactsMovedAsync`, `HasCompanyConflict` (Task 5).
- Produces: `CustomerMergeMoveKind.Contacts` (last value; wire `"Contacts"`); `CustomerMergePreviewResponse` gains `bool CompanyConflict = false` as its last parameter. `Moves` carries nine entries, `Contacts` included **also when zero**.

- [ ] **Step 1: Write the failing unit tests:** `ThePreviewListsContactsEvenAtZero` (`Moves` has `Contacts` with `Count == 0`), `ThePreviewCountsTheContactsThatMove`, `ThePreviewFlagsTheCompanyConflictWithoutThrowing` (`CompanyConflict == true`, no exception).

- [ ] **Step 2: Run to see them fail** — `--filter "FullyQualifiedName~CustomerMergePreviewCompanyTests"`.

- [ ] **Step 3: Implement** in the preview handler (it never throws for the conflict: 200 with the field).

- [ ] **Step 4: Integration tests** in `CustomerMergePreviewApiTests`: `ThePreviewReportsContactsAndConflictAnd200`, and `ConfirmingAFlaggedSelectionIs422` (preview 200 with `companyConflict: true`, then `POST …/merge` of the same selection → 422). Any existing test that asserts "eight moves" is updated to nine.

- [ ] **Step 5: Run unit tests, build integration.**

- [ ] **Step 6: Commit** — `feat(customers): merge preview reports contacts and the company conflict`.

---

### Task 7: Excel — `Empresa` in import, failed rows, template and export

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/IExcelCustomerImporter.cs` (`CustomerImportColumns` L19-74, `ExcelCustomerRow` L89), `ImportCustomers.cs`, `CustomersDtos.cs` (`CustomerImportRowData` L253-266), `ExportFailedCustomerRows.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerImporter.cs` (L34-70), `ClosedXmlCustomerImportTemplateBuilder.cs`, `ClosedXmlCustomerExportBuilder.cs` (L29-44, L120-132), and the failed-rows builder next to them
- Test: create `tests/Modules/Customers/Modules.Customers.UnitTests/ImportCustomersCompanyTests.cs`; extend `tests/Modules/Customers/Modules.Customers.UnitTests/Excel/ClosedXmlCustomerImportTemplateBuilderTests.cs` and `ClosedXmlCustomerExportBuilderTests.cs`; extend `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs`

**Interfaces:**
- Consumes: Task 1 rules; `ICustomerRepository.FindExistingIdentificationsAsync`-style lookups already used by the import; `ICustomerCompanyDirectory.CountContactsAsync`.
- Produces:
  - `CustomerImportColumns.Company = "Empresa"`, **not** in `Ordered` (G5). `CustomerImportColumns.Optional = [Company]` for the builders, which write it as the **last** column.
  - `ExcelCustomerRow` gains `string? CompanyIdentificationNumber` (last parameter, `null` when the header is absent or the cell is empty).
  - `CustomerImportRowData` gains `string? CompanyIdentificationNumber = null` as its last parameter (A22). It travels in every `ImportRowError.RowData` and comes back in `ExportFailedCustomerRowsRequest`.
  - A repository lookup for the number alone: `ICustomerRepository.FindIdsByIdentificationNumberAsync(Guid tenantId, IReadOnlyCollection<string> numbers, CancellationToken)` → `Task<IReadOnlyDictionary<string, IReadOnlyList<CustomerId>>>` (non-merged customers; a number owned by two types gives two ids → ambiguous). Compare with the same normalization the import already uses for documents.
  - Row errors with `Field = "companyIdentificationNumber"` and the six codes of G6.

- [ ] **Step 1: Read first.** Read `ImportCustomers.cs` around L600-670 to see how a row resolves its existing customer and how `PersonType` is preserved, and how a row error is built (`ImportRowError`), so the new errors are built the same way.

- [ ] **Step 2: Write the failing unit tests** in `ImportCustomersCompanyTests` (handler with fakes, rows built in memory):

```csharp
[Fact] public async Task ARowIsLinkedToAnExistingCompanyByItsDocumentNumber()
[Fact] public async Task ARowIsLinkedToACompanyThatComesInTheSameFile()        // the company row may be above or below
[Fact] public async Task AnUnknownNumberIsARowError()                          // code "customers.import.row.company_not_found", field "companyIdentificationNumber", RowData.CompanyIdentificationNumber == the raw cell
[Fact] public async Task ANumberOwnedByTwoCustomersIsAmbiguous()               // "customers.import.row.company_ambiguous"
[Theory] /* company_inactive, company_is_self, company_is_contact, has_contacts */
         public async Task ARuleBrokenIsARowErrorWithTheMatchingSuffix(string scenario, string code)
[Fact] public async Task TwoRowsNamingEachOtherAreBothRejected()               // Review Focus 3: neither ends linked
[Fact] public async Task AnEmptyCompanyCellKeepsAnExistingLink()               // re-importing a contact without the value does not unlink
[Fact] public async Task AFailedCompanyLeavesTheRowUnwritten()                 // the customer of that row is neither created nor updated
```

- [ ] **Step 3: Run to see them fail** — `--filter "FullyQualifiedName~ImportCustomersCompanyTests"`.

- [ ] **Step 4: Implement.** In-file resolution first (by the normalized document number of the other rows; a row that resolves to itself is `company_is_self`; A-names-B while B-names-A makes both `company_is_contact`), then the tenant lookup. Link only rows that passed every other rule, in a second pass after the companies of the same file exist, inside the unit of work the import already uses for that row. Then the importer (read `Empresa` only if `columnByName` has it), the template, the failed-rows file and the export (the company's document number of each exported contact; one batched query through `FindCompaniesAsync` plus the companies' numbers — add the number to the batched read rather than a query per row).

- [ ] **Step 5: Extend the builder unit tests:** `TheTemplateEndsWithTheCompanyColumn`, `TheExportCarriesTheCompanyDocumentNumber`, `TheFailedRowsFileCarriesTheCompanyColumn`. **Integration** in `CustomerStatusAndImportApiTests` (compiled now, executed in Task 12): `AFileWithoutTheCompanyColumnIsStillValid` (G5), `ImportingLinksContactsToTheirCompany`, `ACompanyErrorComesBackWithFieldAndRowData`.

- [ ] **Step 6: Run unit tests, build integration.**

- [ ] **Step 7: Commit** — `feat(customers): company column in the Excel import, template and export`.

---

### Task 8: Messaging — the conversation's customer carries its company

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs` (`CustomerRefDto` L16)
- Modify: `src/Bootstrapper/MessagingCustomerDirectory.cs` (`MatchAsync` L25-28, `FindRefsAsync` L60-63)
- Test: create `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingCustomerDirectoryCompanyTests.cs` (model on `MessagingCustomerDirectoryEnsureByPhoneTests.cs`); extend `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationsApiTests.cs`

**Interfaces:**
- Consumes: `ICustomerCompanyDirectory.FindCompaniesAsync`.
- Produces (namespace `Modules.Messaging.Application`): `public sealed record CustomerCompanyRefDto(Guid Id, string Name, bool IsComplete, bool IsActive);` and `CustomerRefDto` gains `CustomerCompanyRefDto? Company = null` as its last parameter. JSON: `customer.company` on every conversation payload that already carries `customer`. `ListByPhoneAsync` and `ResolveSharedPhonesAsync` are **not** enriched (they are pickers of the shared-phone line, not the conversation's customer).

- [ ] **Step 1: Write the failing unit tests:** `FindRefsAddsTheCompanyOfEachContact`, `MatchAddsTheCompanyOfEachContact`, `ACustomerWithoutCompanyHasCompanyNull`, `OneDirectoryCallServesTheWholePage` (the fake counts calls: exactly one `FindCompaniesAsync` per `FindRefsAsync`).

- [ ] **Step 2: Run to see them fail** — `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~MessagingCustomerDirectoryCompanyTests"`.

- [ ] **Step 3: Implement** the record, the parameter and the two enrichments in the adapter (constructor gains `ICustomerCompanyDirectory`). `rg "new CustomerRefDto\(" src` — the other constructions keep compiling because the parameter is optional; ServiceContracts has its own unrelated `CustomerRefDto`, do not touch it.

- [ ] **Step 4: Integration** in `ConversationsApiTests` (compiled now, executed in Task 12): `TheConversationOfAContactCarriesItsCompany` (list and detail), `TheConversationOfAPersonWithoutCompanyCarriesCompanyNull`.

- [ ] **Step 5: Run** the Bootstrapper unit project and `dotnet test tests/ArchitectureTests` (Messaging must still not reference Customers).

- [ ] **Step 6: Commit** — `feat(messaging): expose the company of a conversation's customer`.

---

### Task 9: Pipeline — triage on the company, summary accepts the company

**Files:**
- Modify: `src/Modules/Pipeline/Modules.Pipeline.Application/PipelineDirectories.cs` (`IPipelineCustomerDirectory` L26-36), `Triage.cs` (L78-125), `CustomerSummary.cs` (L13-140)
- Modify: `src/Bootstrapper/PipelineCustomerDirectory.cs`
- Test: find the handlers' unit tests with `rg -l "DecideTriageHandler|GetCustomerDealsSummaryHandler" tests/Modules/Pipeline/Modules.Pipeline.UnitTests` and extend them (create `TriageCompanyTests.cs` / `CustomerSummaryCompanyTests.cs` there if none exists, reusing that project's fakes); extend `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/TriageApiTests.cs` and `CustomerSummaryApiTests.cs` (harness `PipelineApiHarness.cs`)

**Interfaces:**
- Consumes: `ICustomerCompanyDirectory.FindCompaniesAsync`.
- Produces:
  - `IPipelineCustomerDirectory.FindCompanyIdAsync(Guid tenantId, Guid customerId, CancellationToken)` → `Task<Guid?>`: the company of that customer, already passed through `ResolveSurvivorAsync`; `null` when it has none.
  - `CustomerOpenDealDto` and `CustomerClosedDealDto` gain `Guid CustomerId` as their last parameter (G9; JSON `customerId`).
  - `CustomerDealsSummaryDto` keeps its shape; `CustomerId` echoes the requested id.

- [ ] **Step 1: Read first.** Read `TriageState.EvaluateAsync` (`Triage.cs:30`) and `TriageRules`. Write in the task log whether "pending" depends on the customer's open deals. Do **not** change it (G11, Open points); if a contact's conversation would stay pending while its company has an open deal, log it as `PENDING OWNER`.

- [ ] **Step 2: Write the failing unit tests:**

```csharp
// Triage
[Fact] public async Task CreatingFromAContactsConversationPutsTheDealOnTheCompany()   // deal.CustomerId == companyId; OriginConversationId == conversation.Id
[Fact] public async Task TheDecisionKeepsTheConversationsCustomer()                   // TriageDecision.CustomerId == personId (G11)
[Fact] public async Task TheDefaultTitleIsTheCompanysName()
[Fact] public async Task APersonWithoutCompanyBehavesAsToday()
// Summary
[Fact] public async Task TheCompanyIdWithItsContactsConversationReturnsBothSetsOfDeals()  // no duplicates; each deal says its customerId
[Fact] public async Task ThePersonsIdReturnsOnlyThePersonsDeals()
[Fact] public async Task WithoutConversationOnlyTheRequestedCustomersDeals()              // even when it is a company
[Fact] public async Task AnUnrelatedCustomerWithThatConversationIsNotFound()              // PipelineNotFound.Conversation
[Fact] public async Task TheLastClosedDealIsTheMostRecentOfTheTwo()                       // G10
[Fact] public async Task TriageStatusLooksAtTheWholeSet()                                 // an open deal of the company makes customerHasOpenDeal true
```

- [ ] **Step 3: Run to see them fail** — `dotnet test tests/Modules/Pipeline/Modules.Pipeline.UnitTests --filter "FullyQualifiedName~Triage|FullyQualifiedName~CustomerSummary"`.

- [ ] **Step 4: Implement.** Triage: after `customerId = conversation.CustomerId!.Value`, resolve `dealCustomerId = await customers.FindCompanyIdAsync(…) ?? customerId`; the `DealDraft` and the default title use `dealCustomerId`; `TriageDecision.Created/Dismissed` keep `customerId`. Summary: when `ConversationId` is given, load the conversation **before** listing deals; valid when `conversation.CustomerId == query.CustomerId` or `FindCompanyIdAsync(conversation.CustomerId) == query.CustomerId`, else `PipelineNotFound.Conversation`. In the second case list open and last-closed for both ids, de-duplicate by deal id, keep today's order (the repository's order per customer, company first), and pass the conversation's own customer to the unchanged `TriageState` call. Adapter: `FindCompanyIdAsync` through the directory plus `mergeStore.FindSurvivorIdAsync`.

- [ ] **Step 5: Integration** (compiled now, executed in Task 12). `TriageApiTests`: `TriageOnAContactsConversationCreatesTheDealOnTheCompany`. `CustomerSummaryApiTests`: `TheCompanySummaryFromItsContactsConversationIs200WithBothSets`, `AnUnrelatedCustomerIs404`, `ThePersonsOwnSummaryIsUnchanged`.

- [ ] **Step 6: Run** the Pipeline unit project, the Bootstrapper unit project and `dotnet test tests/ArchitectureTests`.

- [ ] **Step 7: Commit** — `feat(pipeline): deals of a contact's conversation belong to its company`.

---

### Task 10: Send a quotation or an order to a contact, through the thread's number

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/WhatsAppChannelSelection.cs`, `SendQuotation.cs` (command L18-19, enum L23-27, handler L76-79 and L137-159, `ResolveRecipient` L272-308), `SendOrder.cs` (L16-17, L77-80, L103, L142, L209-243), `QuotationsDtos.cs` (`SendQuotationRequest` L439-442), `OrdersDtos.cs` (`SendOrderRequest` L134), `QuotationChangeSummary.cs` (L49-51), `OrderChangeSummary.cs` (L74), `IQuotationCustomerLookup.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` (L486), `OrderEndpoints.cs` (L290); `src/Bootstrapper/QuotationCustomerLookup.cs`
- Test: extend `tests/Modules/Quotations/Modules.Quotations.UnitTests/WhatsAppChannelSelectionTests.cs`, `SendQuotationHandlerTests.cs`, `SendOrderHandlerTests.cs`; extend `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationSendVoidApiTests.cs`, `OrderSendApiTests.cs`, `WhatsAppChannelApiTests.cs` (doubles in `WhatsAppTestDoubles.cs`)

**Interfaces:**
- Consumes: `ICustomerCompanyDirectory.FindContactOfAsync` (through the adapter).
- Produces:
  - `QuotationRecipient.Contact` (third value).
  - `SendQuotationCommand(Guid TenantId, Guid QuotationId, string? Recipient = null, Guid? ConnectionId = null, Guid? ContactId = null, Guid? ThreadConnectionId = null)`; `SendOrderCommand` the same with `OrderId`. `SendQuotationRequest` and `SendOrderRequest` gain `Guid? ContactId = null, Guid? ThreadConnectionId = null` at the end.
  - `public sealed record QuotationClientContact(Guid Id, string Name, string? Phone);`
  - `IQuotationCustomerLookup.FindContactAsync(Guid tenantId, Guid clientId, Guid contactId, CancellationToken)` → `Task<QuotationClientContact?>` (null unless it is a non-merged contact of `clientId`).
  - `IQuotationCustomerLookup.ListContactsAsync(Guid tenantId, Guid clientId, CancellationToken)` → `Task<IReadOnlyList<QuotationClientContact>>` (used by Task 11; capped at 100).
  - `WhatsAppChannelSelection.Resolve(available, saved, requested, document, Guid? thread = null)`: when `thread` is one of `available`, returns `new ResolvedWhatsAppChannel(thatChannel, Remember: false, PreviousChoice: null)` **before** any other rule; when `available` is empty it is still `no_channel`; when `thread` is not available, the result is exactly today's.
  - `QuotationChangeSummary.Sent(string? contactName = null)` / `Resent(string? contactName = null)` and `OrderChangeSummary.Sent(QuotationRecipient recipient, string? contactName = null)`: with a contact the text names the person (Spanish, *tú*-neutral, like the neighbours: «Enviada a {nombre} con su PDF.»).

- [ ] **Step 1: Write the failing unit tests.**

`WhatsAppChannelSelectionTests`:

```csharp
[Fact] public void AnAvailableThreadConnectionWinsOverASavedChoiceWithoutRemembering()   // saved = A, thread = B, both available → B, Remember false, PreviousChoice null
[Fact] public void AnAvailableThreadConnectionSkipsChannelRequired()                     // two available, none saved, requested null, thread = B → B, Remember false
[Fact] public void AThreadConnectionThatIsNotAvailableFallsBackToTodaysRules()           // theory over: saved; single; several-none-chosen → channel_required; requested ≠ saved → channel_already_set
[Fact] public void NoChannelStaysNoChannelEvenWithAThreadConnection()
```

`SendQuotationHandlerTests` and `SendOrderHandlerTests` (same list in each, with the document's prefix):

```csharp
[Fact] public async Task ContactSendsToTheContactsPhoneAndName()            // message.ToPhone, FullName from the contact; message.CustomerId == contactId
[Fact] public async Task ContactOfAnotherClientIsRejected()                 // "quotation.quotation.contact_not_of_client" / "order.order.contact_not_of_client"; sender never called
[Fact] public async Task ContactWithoutContactIdIsRejected()                // same code (G8, Review Focus 4)
[Fact] public async Task ContactWithoutPhoneIsRecipientMissing()            // existing "{prefix}.whatsapp.recipient_missing"
[Fact] public async Task EligibilityIsStillCheckedOnTheClient()             // inactive client + valid contact → today's eligibility error
[Fact] public async Task ContactIdIsIgnoredWithCustomerAndBilling()
[Fact] public async Task TheThreadConnectionDoesNotTouchTheSavedChoice()    // channelChoices never written; no channel-choice audit
[Fact] public async Task TheHistoryNamesTheContact()
```

- [ ] **Step 2: Run to see them fail**

```powershell
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~WhatsAppChannelSelectionTests|FullyQualifiedName~SendQuotationHandlerTests|FullyQualifiedName~SendOrderHandlerTests"
```

- [ ] **Step 3: Implement.** `Resolve` first. In both handlers pass `command.ThreadConnectionId` as `thread`; when the resolved channel came from the thread, the "remember the choice" branch must not run (it already cannot: `Remember` is `false`) — verify in `SendOrder.cs:165-180` and the quotation equivalent that nothing else writes `channelChoices`. `ResolveRecipient` becomes async for `Contact`: `customerLookup.FindContactAsync(tenant, document.ClientId, contactId)`; null or missing id → `contact_not_of_client`; the contact with an empty phone flows to the sender, which already raises `recipient_missing` (`WhatsAppSenderRouter.cs:19,37`) — confirm by reading it, and raise it in the handler if the router does not. `CustomerId` of the message: the client for `Customer`, the contact for `Contact`, `null` for `Billing`. Then requests, endpoints and the adapter.

- [ ] **Step 4: Integration** (compiled now, executed in Task 12). In `QuotationSendVoidApiTests` and `OrderSendApiTests`: `SendingToAContactDeliversToThatContact`, `AContactOfAnotherClientIs422AndNothingIsSent`, `AContactWithoutPhoneIs422RecipientMissing`. In `WhatsAppChannelApiTests`: `SendingThroughTheThreadNumberKeepsTheTenantsChoice` (saved A, send with `threadConnectionId: B` → sent through B; `GET …/whatsapp-channels` still says A), `AThreadNumberThatCannotSendFallsBackToChannelRequired`, `…ToNoChannel`.

- [ ] **Step 5: Run** the Quotations unit project, the Bootstrapper unit project and `dotnet test tests/ArchitectureTests`.

- [ ] **Step 6: Commit** — `feat(quotations): send a document to a contact through the thread's number`.

---

### Task 11: `clientContacts` on the quotation and the order detail

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationsDtos.cs` (`QuotationResponse` L507), `QuotationResponseComposer.cs`, `OrdersDtos.cs` (`OrderDetailDto` L76, `OrderDetailResponse` L300), `OrderResponseComposer.cs`
- Test: extend `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderResponseComposerTests.cs`; create `QuotationResponseComposerClientContactsTests.cs` in the same project (reuse the fakes `OrderResponseComposerTests` uses); extend the integration classes of Task 10

**Interfaces:**
- Consumes: `IQuotationCustomerLookup.ListContactsAsync`, `QuotationClientContact` (Task 10).
- Produces: `public sealed record ClientContactDto(Guid Id, string Name, string? Phone);`; `QuotationResponse` and `OrderDetailResponse` (and `OrderDetailDto`) gain `IReadOnlyList<ClientContactDto> ClientContacts` — always present, `[]` when the client has no contacts. JSON `clientContacts`. `QuotationResponse` is the HTTP contract, not `QuotationDto`: the field is added in the composer.

- [ ] **Step 1: Read first.** Open both composers and note which endpoints build a `QuotationResponse` / `OrderDetailResponse` for a **single** document (detail, create, edit, preview, send, convert) and which build lists. `clientContacts` is filled only for single documents; list rows carry `[]` so a page never fans out one query per row.

- [ ] **Step 2: Write the failing unit tests:** `TheQuotationResponseCarriesTheClientsContacts`, `AClientWithoutContactsCarriesAnEmptyList`, `ContactsWithoutPhoneTravelWithPhoneNull`, and the same three for the order detail.

- [ ] **Step 3: Run to see them fail** — `--filter "FullyQualifiedName~ComposerClientContacts|FullyQualifiedName~OrderResponseComposerTests"`.

- [ ] **Step 4: Implement** in the two composers and the records.

- [ ] **Step 5: Integration** (compiled now, executed in Task 12): `TheQuotationDetailListsTheClientsContacts`, `TheOrderDetailListsTheClientsContacts`.

- [ ] **Step 6: Run** the Quotations unit project.

- [ ] **Step 7: Commit** — `feat(quotations): expose the client's contacts for the send dialog`.

---

### Task 12: Integration run, architecture, contract note

- [ ] **Step 1: Architecture and full unit pass**

```powershell
dotnet test tests/ArchitectureTests
dotnet test tests/Modules/Customers/Modules.Customers.UnitTests
dotnet test tests/Modules/Pipeline/Modules.Pipeline.UnitTests
dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests
dotnet test tests/Bootstrapper/Bootstrapper.UnitTests
```

Expected: every one `Passed!`, 0 failed. Run them one at a time.

- [ ] **Step 2: Automations (open point).** `rg -n "Customer" src/Bootstrapper/Automation*.cs`. Write in the task log which customer fields the module reads and whether it lists or counts customers. No code change. Mark `PENDING OWNER` only if a contact would distort a list or a count.

- [ ] **Step 3: Sweep raw request bodies.** `rg -n "CustomerMergeMoveKind|Moves\b.*8|\"moves\"" tests` and `rg -n "new CustomerListItemResponse\(|new CustomerResponse\(" tests` — fix any test that asserts the old shape (eight moves, positional records) before the long run.

- [ ] **Step 4: Integration tests, one project at a time, filtered** (Docker must be running; each class starts its own Postgres):

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerCompany|FullyQualifiedName~CustomerContactsListApiTests|FullyQualifiedName~CustomerMerge|FullyQualifiedName~CustomerExportApiTests|FullyQualifiedName~CustomerStatusAndImportApiTests|FullyQualifiedName~CustomerSelectableSearchTests|FullyQualifiedName~CustomerWriteApiTests|FullyQualifiedName~CustomerApiTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationsApiTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests --filter "FullyQualifiedName~TriageApiTests|FullyQualifiedName~CustomerSummaryApiTests" -- xUnit.MaxParallelThreads=4
dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~QuotationSendVoidApiTests|FullyQualifiedName~OrderSendApiTests|FullyQualifiedName~WhatsAppChannelApiTests" -- xUnit.MaxParallelThreads=4
```

Expected: each `Passed!`. Wait for one to finish before starting the next. If a run is cut, look for an orphan `Modules.*.IntegrationTests.exe` **that this session started** (match by the worktree path) before retrying; never stop anything else. A failure is fixed here: paste the failing output (RED), fix, paste the passing output (GREEN), commit as `fix(<module>): …`.

- [ ] **Step 5: Contract note for the frontend.** Append to the task log, verbatim, the final names: route and body of the link action; `contacts` values; every new response field with its JSON name and type (`businessName`, `personType`, `company`, `contactCount`, `contacts`, `clientContacts`, `customer.company` in conversations, `customerId` on summary deals, `Contacts` move kind, `companyConflict`, `companyIdentificationNumber`); the request fields `contactId` and `threadConnectionId`; and every code of Global Constraints. If any name differs from the spec, say which and why.

- [ ] **Step 6: Final state**

```powershell
git branch --show-current
git status --short
git log --oneline develop..HEAD
```

Expected: `feature/contactos-de-empresa`, clean tree, one commit per task. **Do not merge and do not push.**

- [ ] **Step 7: Text for `docs/estado.md`** — not edited on this branch (repo rule: it is updated on `develop`, at merge time). Whoever merges adds this row and registry line, replacing the hash:

> **Contactos de empresa (backend)** — Construido. `Customer.CompanyId` (`company_id`, migración `AddCustomerCompany`), `PUT /customers/{id}/company`, lista con `contacts=include|exclude` y búsqueda por razón social, `company`/`contacts`/`contactCount` en la ficha, fusión con `Contacts` y `companyConflict`, columna «Empresa» en Excel, `customer.company` en conversaciones, triage y resumen de negocios por empresa, destinatario `Contact` y `threadConnectionId` en el envío de cotización y pedido, `clientContacts`. Spec `2026-11-10-contactos-de-empresa-backend-design.md`. Despliegue: backend primero. Pendiente: prueba manual del owner y las decisiones marcadas `PENDING OWNER` en el resumen de la rama. Merge `<hash>`.

---

## Self-review (done while writing)

- **Spec coverage:** §4.1 → Tasks 1-3; §4.2 → Task 3; §4.3 → Task 4; §4.4 → Task 8; §4.5 → Task 9; §4.6 → Tasks 10-11; §4.7 → Tasks 5-6; §4.8 → Task 7; §5 → Global Constraints and Task 12; §6 → the test lists of each task; §7 → "Unchanged on purpose" plus the POS guard tests of Task 4; §9 → Open points table.
- **Type consistency:** `CustomerCompanyRef`/`CustomerContactRef` (port) → `CustomerCompanyDto`/`CustomerContactDto` (Customers HTTP) → `CustomerCompanyRefDto` (Messaging) → `QuotationClientContact`/`ClientContactDto` (Quotations). `FindCompanyIdAsync` exists only on `IPipelineCustomerDirectory`; `FindContactAsync`/`ListContactsAsync` only on `IQuotationCustomerLookup`; `FindContactOfAsync` only on `ICustomerCompanyDirectory`.
- **Known trade-off:** integration tests have no per-task RED (they run once, in Task 12, by the owner's standing rule about Testcontainers load); the unit tests of each task carry the RED/GREEN evidence, and Task 12 is where an integration-only defect surfaces.
