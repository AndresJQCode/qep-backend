# Marcar cotización como enviada — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `POST …/quotations/{id}/mark-sent` pasa una cotización a `Sent` sin canal de WhatsApp, con los mismos efectos de dominio que el envío.

**Architecture:** Handler nuevo en Application que repite la secuencia de `SendQuotationHandler` sin canal, destinatario, publicación del PDF ni registro de fallas. Valor nuevo `MarkedSent` al final de `QuotationHistoryEventType` (texto, sin migración). Endpoint nuevo junto a `/send`.

**Tech Stack:** .NET 10, minimal APIs, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-17-marcar-cotizacion-enviada-design.md`

## Global Constraints

- Worktree: `qep-backend-worktrees/marcar-cotizacion-enviada`, rama `feature/marcar-cotizacion-enviada`. Nada en el checkout de `develop`.
- TDD: RED antes que GREEN. Si el RED es de compilación, la evidencia es la salida del build (sin contenedores).
- Commits: conventional commits, **sin** `Co-Authored-By` ni atribución de IA.
- Pruebas: unitarias de lo tocado; integración filtrada por nombre. La suite de Quotations la corre el owner antes de mergear.
- Textos al usuario en español colombiano tuteando; contratos (`MarkedSent`, `quotation.quotation.marked_sent`) tal cual.
- `MarkedSent` va **al final** del enum (se guarda como texto, `MaxLength(50)`).

## Review Focus

1. **Cotización `Voided`/`Expired`/`Converted`** → `422 quotation.quotation.not_draft`, sin historial ni evento. (Task 1, test `MarkSentRejectsAClosedQuotation`.)
2. **Sin `ValidUntil` o sin cuenta de cobro** → 422 con el mismo código que `/send` (`valid_until_required`, `billing.account_required`); no puede ser una puerta trasera. (Task 2, `MarkSentWithoutValidUntilIsUnprocessable`.)
3. **Cliente inactivo** → mismo rechazo que `/send` (`QuotationCustomerEligibility.Ensure`). (Task 1, `MarkSentRejectsAnInactiveCustomer`.)
4. **Marcar dos veces** (desde `Sent`) → 200, segunda entrada `MarkedSent`, segundo evento `sent.v1` (Pipeline es idempotente). (Task 2, `MarkingAnAlreadySentQuotationAddsAnotherEntry`.)
5. **Rollout:** un pod viejo que lee `MarkedSent` falla en `Enum.Parse` al listar el historial (500 en `GET …/history` de esa cotización) hasta que no queden pods viejos. Sin prueba: se anota en el handoff del merge.

---

### Task 1: Dominio de historial + handler

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Domain/QuotationHistoryEventType.cs` (valor `MarkedSent` al final, con `<summary>`).
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/QuotationChangeSummary.cs` (método nuevo).
- Create: `src/Modules/Quotations/Modules.Quotations.Application/MarkQuotationSent.cs`.
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:~902` (registro junto a `SendQuotationHandler`).
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/MarkQuotationSentHandlerTests.cs` (dobles como en `SendQuotationHandlerTests.cs`).

**Interfaces:**
- Produces:
  - `public sealed record MarkQuotationSentCommand(Guid TenantId, Guid QuotationId) : ICommand<QuotationDto>;`
  - `public sealed class MarkQuotationSentHandler(IQuotationRepository, IQuotationsUnitOfWork, IQuotationAuditPublisher, IQuotationLifecycleEventPublisher, IQuotationPdfProvider, IQuotationCustomerLookup, IQuotationProductLookup, ITenantDirectory, IMembershipDirectory, IExecutionContext, IClock) : ICommandHandler<MarkQuotationSentCommand, QuotationDto>`
  - `public const string AuditAction = "quotation.quotation.marked_sent";` en el handler.
  - `QuotationChangeSummary.MarkedSent()` → `"Marcada como enviada por fuera de QEP."`

- [ ] **Step 1: Pruebas que fallan** en `MarkQuotationSentHandlerTests`:
  - `MarkSentMovesADraftToSentAndPublishesTheSentEvent`: borrador enviable → `Status == Sent`, `SentAt == clock.UtcNow`; una entrada de historial con `EventType == MarkedSent` y detalle `"Marcada como enviada por fuera de QEP."`; auditoría `quotation.quotation.marked_sent`; `PublishSent` llamado una vez; `pdfProvider.EnsureCurrentAsync` llamado una vez; `SaveChangesAsync` una vez.
  - `MarkSentRejectsAClosedQuotation` (`[Theory]` Voided/Expired/Converted) → `QuotationsDomainException` con `Code == "quotation.quotation.not_draft"`; sin historial, sin evento, sin PDF.
  - `MarkSentRejectsAnInactiveCustomer` → el mismo código que lanza `QuotationCustomerEligibility.Ensure` hoy; sin evento.
  - `MarkSentWithoutManagePermissionIsForbidden` → la excepción de `QuotationsAuthorization.EnsureAuthorized`.
- [ ] **Step 2: Build para ver el RED**
  Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests`
  Expected: FAIL `CS0246` (`MarkQuotationSentCommand` / `MarkQuotationSentHandler` no existen).
- [ ] **Step 3: Implementar.** Orden del handler, igual que `SendQuotationHandler` (L59-201) sin canal:
  autorizar `QuotationManage` → cargar o `QuotationNotFound.For` → `EnsureSendable()` → `BillingPersonTypeRequirement.EnsureForQuotationAsync` → `QuotationAdvisorResolver.ResolveAsync` → `customerLookup.FindAsync` + `QuotationCustomerEligibility.Ensure` → `QuotationItemProductLabel.ResolveMissingAsync` → `pdfProvider.EnsureCurrentAsync` → `quotation.Send(sentBy, now, products)` → `AddHistoryEntry(MarkedSent, QuotationChangeSummary.MarkedSent())` → auditoría → `lifecycleEvents.PublishSent` → `SaveChangesAsync` → `ToDto()`.
  Sin try/catch ni `IQuotationSendFailureLog`. `<summary>` del handler en español: por qué existe y que «enviada» aquí depende de lo que dice el asesor.
- [ ] **Step 4: Correr las unitarias**
  Run: `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~MarkQuotationSentHandlerTests"`
  Expected: PASS.
- [ ] **Step 5: Commit** `feat(quotations): mark a quotation as sent without a channel`

### Task 2: Endpoint + integración

**Files:**
- Modify: `src/Modules/Quotations/Modules.Quotations.Api/QuotationEndpoints.cs` (ruta tras `/send`, L152; método `MarkQuotationSentAsync` junto a `SendQuotationAsync`, L~500).
- Test: `tests/Modules/Quotations/Modules.Quotations.IntegrationTests/QuotationMarkSentApiTests.cs` (`IClassFixture<SharedQuotationsHost>`, `StartTestAsync()`, helpers de `QuotationsApiHarness`).

**Interfaces:**
- Consumes: `MarkQuotationSentCommand` (Task 1).
- Produces: `POST /api/v1/tenants/{tenantId}/quotations/{quotationId}/mark-sent`, sin cuerpo, `QuotationManage`, `Produces<QuotationResponse>()`, `ProducesProblem` 403/404/422. Responde con `composer.ComposeAsync`, como `/send`.

- [ ] **Step 1: Pruebas que fallan** en `QuotationMarkSentApiTests`:
  - `MarkSentMovesADraftToSent`: `CreateSendableQuotationAsync` → `POST …/mark-sent` → 200, `Status == "Sent"`, `SentAt` no nulo; `GET …/history` contiene `EventType == "MarkedSent"`.
  - `MarkingAnAlreadySentQuotationAddsAnotherEntry`: `CreateSentQuotationAsync` → 200; el historial tiene `Sent` y `MarkedSent`.
  - `MarkSentWithoutValidUntilIsUnprocessable`: borrador sin `ValidUntil` → 422 `quotation.quotation.valid_until_required`; sigue `Draft`.
  - `MarkSentAVoidedQuotationIsUnprocessable` → 422 `quotation.quotation.not_draft`.
  - `MarkSentForAnotherTenantIsForbidden` → 403 (mismo patrón que `VoidForAnotherTenantIsForbidden`).
  No se afirma el outbox en host compartido (los conteos sin tenant rompen la regla de `Shared<Modulo>Host`); el evento lo cubre la unitaria.
- [ ] **Step 2: Build para ver el RED**
  Run: `dotnet build tests/Modules/Quotations/Modules.Quotations.IntegrationTests`
  Expected: compila; las pruebas fallarían con 404 (ruta inexistente). Si el owner prefiere evidencia corrida: `--filter "FullyQualifiedName~QuotationMarkSentApiTests"` → FAIL 404.
- [ ] **Step 3: Implementar** la ruta y `MarkQuotationSentAsync(Guid tenantId, Guid quotationId, IRequestDispatcher dispatcher, <composer>, CancellationToken)` copiando la forma de `SendQuotationAsync` sin cuerpo. Comentario corto: por qué existe, distinto de `/send`.
- [ ] **Step 4: Verificar**
  Run: `dotnet build` de la solución (0 errores, 0 avisos). La clase de integración la corre el owner (`$env:Seed__Enabled = "false"`; `--filter "FullyQualifiedName~QuotationMarkSentApiTests"`).
- [ ] **Step 5: Commit** `feat(quotations): expose mark-sent endpoint`

---

Después del merge a `develop`: fila en `docs/estado.md` y nota de rollout (Review Focus 5). Frontend: spec propio en `qep-frontend`.
