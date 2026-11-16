# PDF payment account toggle — backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Workspace rule (`qep/CLAUDE.md`): tasks run in 2 blocks, one implementer per block, one review per block.

**Goal:** Per-tenant, per-document switch that prints or hides the payment account lines in the quotation/order PDF, and a payment notice that no longer depends on the account.

**Architecture:** A new value `PdfPaymentAccount` inside `PdfContent`, stored in its own table `quotations.pdf_payment_account`, read/written by the existing `GET`/`PUT …/quotations/settings/pdf-content`. `QuotationPdfSectionsBuilder` turns it into `QuotationPdfSections.ShowPaymentAccount`, which `quotation.typ` reads; the fingerprint includes it.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, Typst (`qcode-pdf`), xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-16-pdf-payment-account-design.md` (builds on `2026-11-16-pdf-content-design.md`).

## Global Constraints

- Default without a row: `Enabled = false, InQuotation = true, InOrder = true` (owner 2026-11-16).
- Only `origen-botanico` is seeded on: `enabled = true, in_quotation = true, in_order = true`.
- Hidden account = exactly three lines gone: «Cuenta para el pago», bank name, «Número: … (currency)». Issuer header, «Forma de pago», «Contraentrega» untouched.
- Payment notice prints by its own flags only (no billing-account guard).
- `PUT` without `paymentAccount` → 422.
- Comments explain why, in Spanish like the surrounding code; identifiers in English. Conventional commits, no AI attribution.
- Integration tests filtered by name while iterating; never two integration runs at once (check `Get-Process testhost, dotnet`).

## Review Focus

1. **Cached quotation PDFs printed with the account before deploy.** After deploy most tenants are off; the cached PDF must regenerate. The fingerprint must change for every tenant at deploy → always append the account line (Task 2 test `Fingerprint_changes_for_empty_content_after_adding_the_account`).
2. **Payment box empty.** No payment method, no cash on delivery, account hidden → no empty bordered box (Task 3 template test).
3. **Notice alone.** Notice on, document without billing account → notice prints without the box (Task 2 builder test + Task 3 template test).
4. **Toggling the other document's flag** must not regenerate a quotation PDF (Task 2 fingerprint test).
5. **Audit** of a PUT that only flips the account records `PaymentAccount.Enabled` etc. (Task 1 handler test).

---

## Block 1 — Contract and persistence

### Task 1: Domain value, DTO, validator, handler, endpoint

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/PdfContent.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/PdfContentSettings.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationSettingsEndpoints.cs:69-97`
- Modify: every `new PdfContent(` / `PdfContent.Create(` caller (compile errors list them; `PdfContentStore.cs`, tests)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/PdfContentTests.cs`, `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PdfContentApiTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PdfPaymentAccount(bool Enabled, bool InQuotation, bool InOrder)` with `public bool PrintsOn(bool isOrder)` (same rule as `PdfSection.PrintsOn`) and `public static PdfPaymentAccount Empty { get; } = new(false, true, true);` — in `PdfContent.cs`.
  - `PdfContent(PdfSection PaymentNotice, PdfSection Terms, PdfSection DeliveryProcess, PdfPaymentAccount PaymentAccount)`; `PdfContent.Empty` uses `PdfPaymentAccount.Empty`; `Create(paymentNotice, terms, deliveryProcess, paymentAccount)` (null-check it). `All` stays the three sections.
  - `public sealed record PdfPaymentAccountDto(bool Enabled, bool InQuotation, bool InOrder);`
  - `PdfContentDto(..., PdfSectionDto DeliveryProcess, PdfPaymentAccountDto PaymentAccount, IReadOnlyList<QuotationPdfVariable> Variables)` — `Variables` stays last.
  - `UpdatePdfContentCommand(..., PdfSectionDto? DeliveryProcess, PdfPaymentAccountDto? PaymentAccount)`; `UpdatePdfContentRequest` gains `PdfPaymentAccountDto? PaymentAccount` last.
  - `IPdfContentStore` unchanged in shape (Task 2 implements the new field).

- [ ] **Step 1: Write failing domain tests** in `PdfContentTests.cs`:
  `PaymentAccount_prints_only_when_enabled_and_flag_of_document_is_on` (theory over the 8 combinations of `Enabled/InQuotation/InOrder` × `isOrder`, asserting `Enabled && (isOrder ? InOrder : InQuotation)`), and `Empty_payment_account_is_off_for_both_documents` (`Assert.False(PdfContent.Empty.PaymentAccount.Enabled)`, `InQuotation`/`InOrder` true).
- [ ] **Step 2: Write failing API tests** in `PdfContentApiTests.cs`:
  `Get_returns_payment_account_off_for_a_tenant_without_row` (`paymentAccount` = `{enabled:false,inQuotation:true,inOrder:true}`);
  `Put_without_payment_account_is_422` (errors contain key `PaymentAccount`);
  `Put_saves_and_returns_payment_account` (send `{enabled:true,inQuotation:false,inOrder:true}`, `GET` returns the same);
  `Put_that_only_flips_the_account_audits_its_fields` (audit details keys = `PaymentAccount.Enabled`, follow the existing audit test in that file).
- [ ] **Step 3: Run build to see RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests` → compile errors on `PdfPaymentAccount`. (No containers for RED.)
- [ ] **Step 4: Implement** the Interfaces above. Validator: `RuleFor(c => c.PaymentAccount).NotNull();`. Handler: `PdfContent.Create(paymentNotice!, terms!, deliveryProcess!, ToDomain(command.PaymentAccount!))`. `PdfContentMapping.ToDto` maps it; `ChangedFields` appends, after the sections loop, `PaymentAccount.Enabled|InQuotation|InOrder` when each differs. Endpoint passes `request.PaymentAccount`. Store keeps compiling by returning `PdfPaymentAccount.Empty` for now (Task 2 replaces it).
- [ ] **Step 5: GREEN** — unit: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~PdfContentTests"`; integration (after Task 2 lands the table): `--filter "FullyQualifiedName~PdfContentApiTests"`.
- [ ] **Step 6: Commit** — `feat(quotations): add payment account switch to the PDF content contract`

### Task 2: Table, migration with seed, store

**Files:**
- Create: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/PdfPaymentAccountRow.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/QuotationsDbContext.cs` (DbSet + `ConfigurePdfPaymentAccount` next to `ConfigurePdfSections`, ~l.1119)
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/PdfContentStore.cs`
- Create: migration `AddPdfPaymentAccount` (`dotnet ef migrations add AddPdfPaymentAccount` with the module's usual project/startup flags)
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/PdfContentMigrationTests.cs`

**Interfaces:**
- Consumes: `PdfPaymentAccount` (Task 1).
- Produces: `internal DbSet<PdfPaymentAccountRow> PdfPaymentAccounts`; table `quotations.pdf_payment_account` (`tenant_id uuid PK`, `enabled bool`, `in_quotation bool`, `in_order bool`, `updated_at timestamptz`), no FK to tenancy.

- [ ] **Step 1: Failing tests** in `PdfContentMigrationTests.cs`: `Migration_seeds_payment_account_on_for_origen_botanico_only` (origen-botanico row `true,true,true`; another tenant has no row → store reads `Empty`), following the existing seed test in that file. In `PdfContentApiTests` the Task 1 round-trip test covers the upsert.
- [ ] **Step 2: RED** — build fails on `PdfPaymentAccountRow`.
- [ ] **Step 3: Implement.** Store `GetAsync`: one more `AsNoTracking` read by tenant; absent → `PdfPaymentAccount.Empty`. `SaveAsync`: after the sections loop, `INSERT … ON CONFLICT (tenant_id) DO UPDATE SET enabled, in_quotation, in_order, updated_at` (same transaction guard). Migration `Up`: `CreateTable`, then the seed with the same `DO $$ … to_regclass('tenancy.tenants') … WHERE t.slug = 'origen-botanico' ON CONFLICT DO NOTHING` guard as `AddPdfSections`. `Down`: drop table.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.IntegrationTests --filter "FullyQualifiedName~PdfContentMigrationTests|FullyQualifiedName~PdfContentApiTests"`.
- [ ] **Step 5: Commit** — `feat(quotations): persist the PDF payment account switch`

**Block 1 review**, then Block 2.

## Block 2 — PDF

### Task 3: Builder, fingerprint, template

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationPdfSections.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Infrastructure/Pdf/quotation.typ:179-183, 272-310`
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationPdfSectionsTests.cs`, `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationTemplateTests.cs`

**Interfaces:**
- Consumes: `PdfContent.PaymentAccount.PrintsOn(bool)`.
- Produces: `QuotationPdfSections(QuotationPdfSection? PaymentNotice, QuotationPdfSection? Terms, QuotationPdfSection? DeliveryProcess, bool ShowPaymentAccount)` → JSON `data.sections.showPaymentAccount`.

- [ ] **Step 1: Failing builder tests:**
  - Replace the assertion at `QuotationPdfSectionsTests.cs:53` with `Payment_notice_prints_without_billing_account` (`Assert.NotNull(Build(content, Document() with { BillingAccount = null }, Reader).PaymentNotice)`).
  - `Show_payment_account_follows_the_flag_of_the_document` (quotation-only account → `true` for `Document()`, `false` for `Order()`).
  - `Fingerprint_changes_when_the_account_flag_of_that_document_changes` and `Fingerprint_ignores_the_account_flag_of_the_other_document`.
  - `Fingerprint_changes_for_empty_content_after_adding_the_account`: `Assert.NotEqual(<SHA-256 hex of "">, Fingerprint(PdfContent.Empty, Quotation))` — replaces the current empty-content expectation at l.190.
- [ ] **Step 2: Failing template tests** (`QuotationTemplateTests`, compile with `AssertCompilesAsync` like l.131, and assert on the extracted text the way the file already does): account hidden → no «Número:» and no «Cuenta para el pago»; account hidden + no payment method + no cash on delivery → no payment box; notice set + `BillingAccount = null` → notice title present.
- [ ] **Step 3: RED** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~QuotationPdfSectionsTests|FullyQualifiedName~QuotationTemplateTests"`.
- [ ] **Step 4: Implement.**
  - Builder: drop the `BillingAccount is null` guard and its comment; pass `content.PaymentAccount.PrintsOn(isOrder)`.
  - Fingerprint: after the sections, always append the line `PaymentAccount|{true|false}` (lowercase, `PrintsOn(isOrder)`), joined with `\n`; update the XML doc. Always-present on purpose: every cached PDF regenerates once at deploy.
  - Template: `#let mostrar-cuenta = data.sections != none and data.sections.showPaymentAccount`; box condition `data.paymentMethod != none or data.cashOnDelivery or (data.billingAccount != none and mostrar-cuenta)`; account lines inside `#if data.billingAccount != none and mostrar-cuenta`; move the `#if aviso != none [...]` block out of the box `#if`, right after it, keeping `#v(12pt)` spacing for both cases. Update the comment at l.300-301 (no longer tied to the account).
- [ ] **Step 5: GREEN** — same filter, then the whole `Modules.Quotations.UnitTests` project once.
- [ ] **Step 6: Commit** — `feat(quotations): hide the payment account in the PDF by tenant setting`

**Block 2 review.** Before merge to `develop`: `Modules.Quotations.UnitTests` and `Modules.Quotations.IntegrationTests` (with `-- xUnit.MaxParallelThreads=4`), only if the owner asks for the full integration run (memory: integración completa sólo si la pide); `ArchitectureTests`. Update `docs/estado.md` on `develop` at merge.
