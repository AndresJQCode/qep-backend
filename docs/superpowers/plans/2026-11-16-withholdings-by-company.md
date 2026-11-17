# Withholdings from the billing company alone (preview) — Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Workspace rules (`../CLAUDE.md` of the workspace) override the skill: one implementer per block, one review per block.

**Goal:** The four preview endpoints accept `billingCompanyId` and, without a bank account, answer the withholdings that company produces.

**Architecture:** A company-only preview resolves a *pending* `QuotationBillingAccount` (company, country, city, tenant default currency, empty bank fields, `IsPending = true`) and runs the unchanged pipeline. The pending account is never persisted and never answered; readiness treats it as absent.

**Tech Stack:** .NET 10, EF Core, FluentValidation, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-16-withholdings-by-company-design.md`

## Global Constraints

- Body field: `billingCompanyId` (JSON), `Guid? BillingCompanyId` (C#). Only on the four preview requests/queries; never on a write command.
- `billingAccount` present ⇒ `billingCompanyId` ignored. Both absent ⇒ today's behaviour unchanged.
- Unknown / other-tenant company ⇒ 422 `quotation.billing.company_not_found` (existing `QuotationBillingAccountVerifier.CompanyNotFoundCode`).
- Response: billing block (`billingAccount` / `QuotationBillingResponse`) is `null` when the account is pending; currency = tenant default currency.
- No migration. `IsPending` is ignored by EF in both owned mappings (`QuotationsDbContext.cs:183` area and `:656` area).
- Integration tests filtered by name while iterating (`--filter "FullyQualifiedName~BillingCompanyPreview"`); never two integration runs at once; RED by build failure needs no containers.

## Review Focus

- A company in a country with municipal types (CO, with a city): ReteICA rows and `operationCity` must appear, equal to the full-account preview of the same company. → Task 1 test `NewQuotationPreview_WithCompanyOnly_PaintsTheSameWithholdingsAsWithItsAccount`.
- Company-only preview must not flip `canBeConvertedToOrder` to `true`. → Task 1 test `NewQuotationPreview_WithCompanyOnly_IsNotConvertible`.
- `billingCompanyId` plus a full `billingAccount` of *another* company: the account wins. → Task 1 test `NewQuotationPreview_WithAccountAndCompanyId_UsesTheAccount`.
- Order edit with `billingCompanyId` of a different company than the saved one: withholdings follow the new company, response billing null. → Task 2 test `OrderEditsPreview_WithCompanyOnly_ResolvesAgainstThatCompany`.
- Save paths reject nothing new and accept nothing new: a `PUT …/save-all` with `billingCompanyId` behaves as without it (field unknown to the write request). Covered by not adding the field; no test.

---

### Task 1 (block A): Pending account + quotation previews

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationBillingAccount.cs` (add `IsPending`; `Normalized()` skips bank/number requirements when pending)
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/Quotation.cs` (`CanBeConvertedToOrder`, `Billing is null ? …` at :801 and any `BillingAccount is not null` readiness check: pending counts as absent)
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationBillingAccountResolver.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationEdits.cs` (`IQuotationEdits.BillingCompanyId`, default `null`)
- Modify: `PreviewNewQuotation.cs`, `PreviewQuotation.cs`, `QuotationDraftBuilder.cs:61`, `QuotationEditsApplication.cs:58`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationResponseComposer.cs:378` (`ToBillingResponse` → `null` when pending; DTO carries `IsPending`)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` (both preview request records + mapping)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (`Ignore(IsPending)` in both owned configs)
- Test: create `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/BillingCompanyPreviewApiTests.cs`; unit test in `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationBillingAccountTests.cs` (create if absent)

**Interfaces:**
- Produces: `public bool IsPending { get; init; }` on `QuotationBillingAccount`; `QuotationBillingAccountDto.IsPending` (default `false`).
- Produces: `QuotationBillingAccountResolver.ResolveAsync(IQuotationCompanyLookup lookup, Guid tenantId, QuotationBillingAccountRequest? request, Guid? billingCompanyId, string defaultCurrency, CancellationToken ct) : Task<QuotationBillingAccount?>` — existing overload kept for the write paths (`IExternalOrderWriter`, `IStoreOrderGateway`).
- Produces: `Guid? IQuotationEdits.BillingCompanyId => null;` (default interface member), overridden by `PreviewNewQuotationQuery` and `PreviewQuotationQuery` (`Guid? BillingCompanyId = null` positional parameter, last).

- [ ] **Step 1: Write failing tests** in `BillingCompanyPreviewApiTests` (seed: a CO company with a city and municipal rate, a customer agent there, a bank account in COP; tenant default COP):
  - `NewQuotationPreview_WithCompanyOnly_PaintsTheSameWithholdingsAsWithItsAccount`: preview A with `billingCompanyId`, preview B with the full account. Assert A `billingAccount` is `null`; A `withholdingBreakdown`, municipal rows, `operationCity`, `billingCompanyCity` equal B's; A `currency == "COP"`.
  - `NewQuotationPreview_WithCompanyOnly_IsNotConvertible`: `canBeConvertedToOrder == false` with otherwise-complete body.
  - `NewQuotationPreview_WithUnknownCompany_Is422`: random Guid ⇒ 422, `code == "quotation.billing.company_not_found"`.
  - `NewQuotationPreview_WithAccountAndCompanyId_UsesTheAccount`: account of company X + `billingCompanyId` of company Y ⇒ `billingAccount.companyId == X`.
  - `QuotationEditsPreview_WithCompanyOnly_PaintsItsWithholdings`: `POST /quotations/{id}/preview` same assertions as the first test.
  - Unit: `Normalized_WhenPending_AcceptsEmptyBankFields` and `Normalized_WhenNotPending_StillRequiresAccountNumber` (code `quotation.billing.account_number_required`).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.IntegrationTests` fails (unknown `BillingCompanyId` / `IsPending`). Keep the output.
- [ ] **Step 3: Implement** per Interfaces. The resolver with `request` null and `billingCompanyId` set looks the company up (`lookup.FindAsync`), throws `CompanyNotFoundCode` if null, returns `new QuotationBillingAccount { CompanyId, BankName = "", AccountNumber = "", Currency = defaultCurrency, Country = company.Country, CityId = company.CityId, IsPending = true }`. In `QuotationDraftBuilder`, read the default currency before resolving.
- [ ] **Step 4: GREEN** — unit tests, then `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~BillingCompanyPreview"`; then `--filter "FullyQualifiedName~QuotationMunicipalWithholdingsApiTests|FullyQualifiedName~QuotationOneStepCreationApiTests|FullyQualifiedName~QuotationEditsApiTests"` once at block end. Also `dotnet test tests/ArchitectureTests` (check path with `Test-Path` first).
- [ ] **Step 5: Commit** `feat(quotations): preview withholdings from the billing company alone`

### Task 2 (block B): Order previews

**Files:**
- Modify: `PreviewOrder.cs`, `OrderDraftBuilder.cs:115`, `PreviewOrderEdits.cs`, `OrderEdits.cs:177`, `OrderResponseComposer.cs` (billing null when pending), `Order.cs` (any «has an account» readiness the response exposes: pending = absent)
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/OrderEndpoints.cs` (`PreviewOrderAsync` :422, `PreviewOrderEditsAsync` :527 request records + mapping)
- Test: `BillingCompanyPreviewApiTests.cs` (same class)

**Interfaces:**
- Consumes: the Task 1 resolver overload and `IsPending`.
- Produces: `Guid? BillingCompanyId = null` on `PreviewOrderQuery` and `PreviewOrderEditsQuery`.

- [ ] **Step 1: Failing tests:**
  - `NewOrderPreview_WithCompanyOnly_PaintsTheSameWithholdingsAsWithItsAccount` (`POST /orders/preview`; same assertions as Task 1's first test on `OrderDetail`).
  - `OrderEditsPreview_WithCompanyOnly_ResolvesAgainstThatCompany`: order saved with company X's account; preview with `billingCompanyId` = company Y (CO, other city) ⇒ withholdings/municipal rows of Y, response billing account `null`.
  - `OrderEditsPreview_WithoutCompanyOrAccount_KeepsTheSavedAccount`: no `billingAccount`, no `billingCompanyId` ⇒ response billing account is X's (today's behaviour).
- [ ] **Step 2: RED** by build failure; keep output.
- [ ] **Step 3: Implement** threading per Interfaces.
- [ ] **Step 4: GREEN** — `--filter "FullyQualifiedName~BillingCompanyPreview"`, then once `--filter "FullyQualifiedName~OrderMunicipalWithholdingsApiTests|FullyQualifiedName~OrderEditsApiTests|FullyQualifiedName~ManualOrderApiTests"`.
- [ ] **Step 5: Commit** `feat(orders): preview withholdings from the billing company alone`
