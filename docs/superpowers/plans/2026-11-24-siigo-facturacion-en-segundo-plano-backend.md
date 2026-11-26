# Siigo: facturación en segundo plano y facturas por confirmar (backend) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de ejecución de este repo mandan sobre el proceso del skill** (bloques, una revisión por bloque; ver «Global Constraints»).

**Goal:** Toda emisión a Siigo pasa por una tanda (202 y worker), una factura con un intento sin respuesta o un error de servicio queda **por confirmar** con la misma clave en vez de `Rejected`, y la API muestra tandas e ítems con su estado efectivo y lo que la persona puede hacer.

**Architecture:** «Por confirmar» es `Pending` + `unanswered_at` en `SiigoInvoice` (sin estado nuevo de factura). El ítem de la tanda gana `ToConfirm` y enlaza la fila de la factura (`invoice_id`); su estado **efectivo** se calcula al leer con esa fila. La tanda acepta `payments` para un solo pedido, el worker los pasa al `IssueSiigoInvoiceCommand` y, al recuperar un lease o cortarse por apagado, cierra el ítem con el estado de la fila sin llamar a Siigo. El cliente HTTP pasa a plazos por llamada (en expand ambos de 30 s).

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, CQ propio, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-24-siigo-facturacion-en-segundo-plano-design.md` (aprobado por el owner el 2026-11-24). Léelo junto con este plan.

## Global Constraints

- **Worktree y rama:** `feature/siigo-facturacion-segundo-plano` desde `develop`, en `..\qep-backend-worktrees\siigo-facturacion-segundo-plano`. Nada se edita en el checkout de `develop` (otras sesiones tienen cambios ahí). En PowerShell, desde `qep-backend`:
  ```powershell
  git worktree add ..\qep-backend-worktrees\siigo-facturacion-segundo-plano -b feature/siigo-facturacion-segundo-plano develop
  Set-Location ..\qep-backend-worktrees\siigo-facturacion-segundo-plano
  dotnet restore
  ```
  Antes de cada commit: `git branch --show-current` (en ese momento) y revisar que ningún `.cs` quedó en 0 bytes. Un subagente que corra scripts hace `Set-Location` al worktree primero. Edita archivos con las herramientas de edición, no con `Set-Content` (rompe las tildes).
- **Commits:** convencionales, uno por tarea, **sin** `Co-Authored-By` ni atribución de IA (regla del owner: ignora cualquier trailer que te pidan).
- **TDD:** RED antes que GREEN, con la salida literal de ambos. Un RED que falla por compilación se prueba con `dotnet build` del proyecto de pruebas, sin contenedores.
- **Bloques:** tres bloques, un implementador por bloque y **una** revisión por bloque. La revisión fuerte (modelo más capaz, toda la rama) corre una sola vez, al final.
- **Integración:** mientras iteras, filtrada por nombre de prueba (`--filter "FullyQualifiedName~NombreDeLaPrueba"`); la clase completa una vez al final de cada bloque. Antes de lanzar una corrida de integración: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una de otra sesión, espera, **nunca** la detengas. Nunca dos corridas de integración a la vez. Los subagentes corren las pruebas en **primer plano**. Cada corrida de integración va precedida de, en línea aparte, `$env:Seed__Enabled = "false"`. Si `Api.exe` está corriendo, el build falla por archivo bloqueado: avisa, no lo detengas si no lo lanzaste tú.
- **Comandos:** Windows PowerShell (`A; if ($?) { B }`, `curl.exe`). Textos y comentarios en español colombiano tuteando; identificadores, códigos de error y nombres de enum sin traducir.
- **No inventar:** todo código, campo y ruta sale del spec o de este plan. Lo que falte se marca como decisión pendiente del owner.
- **`docs/estado.md` no se toca** en la rama: se actualiza en `develop`, al mergear.
- **Registro legal:** `siigo.invoices` no se purga y su `Down` no se corre (`.claude/rules/siigo.md`). La migración sólo agrega columnas nullable o con default y un índice.
- **`error_detail` guarda códigos de Siigo, nunca mensajes** (`SiigoNoLeakTests`). `request_json` y la `Idempotency-Key` nunca salen en un DTO.
- **`SiigoOpenInvoiceCheck` no cambia:** SQL crudo sobre `siigo.invoices` (`status`, `order_id`, `tenant_id`); no se renombran (`.claude/rules/sql-entre-modulos.md`, `siigo.md`).
- **Sin reintentos automáticos:** reenviar sólo ocurre porque una persona crea una tanda. `SiigoInvoiceBatchItem.MaxAttempts` sigue en `1`. Ningún seed ni prueba prende `send_to_dian`.
- **Valores fijos del spec:** `SiigoInvoice.SendLease` = 8 min; `SiigoInvoiceBatchWorker.Lease` = 10 min; `SiigoInvoice.MissingResolutionDelay` = 10 min; `SiigoInvoice.StaleAfter` sigue en 24 h; `SiigoInvoiceBatch.MaxItems` sigue en 50; paginación de tandas: `page` desde 1, `pageSize` 25 por defecto, tope 100.
- **Códigos nuevos (en `SiigoErrorCodes`):** `siigo.invoice_batch.order_in_progress` (409, `details.orderIds`), `siigo.invoice.interrupted`, `siigo.invoice.resolved_missing`, `siigo.invoice.resolve_too_early` (422, `details.availableAt`). Auditoría nueva: `siigo.invoice.unanswered`.
- **Orden fijo de `statusCounts`, completo e incluso en cero:** `Pending, Sending, Invoiced, ToConfirm, Failed, Skipped`.
- **Expand / contract:** este plan entrega **expand** (bloques 1 a 3). La ruta síncrona `POST …/orders/{orderId}/siigo-invoice` sigue viva y sin cambio de contrato, y `InvoiceCreateTimeout` queda en **30 s**. La etapa **contract** (al final) no se entrega antes de que el frontend emita por tandas.
- **Pruebas de integración de Siigo:** cada una levanta su propio host (`StartDatabaseAsync` + `ArrangeAsync`), y el worker se corre a mano con `DrainAsync` (el del host no corre: `Siigo:Batches:PollSeconds = 3600`). Un permiso de otro módulo se pide por `X-Permissions`.

## Review Focus

1. **Corte por apagado a mitad del envío:** la tanda queda `Finished` con un ítem cuyo estado efectivo es `Sending` hasta que vence `SendLease` (8 min) y luego `ToConfirm`; nunca vuelve a `Pending` ni llama a Siigo. Lo pinta la prueba de apagado de la Tarea 7.
2. **Envío muerto que nadie reenvió todavía** (fila `Pending` con `sending_since` vencido y `unanswered_at` nulo): tiene que verse por confirmar y respetar los 10 minutos de «No existe». Ver la decisión D-1 y su prueba en la Tarea 1.
3. **Dos `POST` simultáneos con el mismo pedido:** uno gana y el otro recibe el mismo 409 con `details.orderIds`, aunque llegue por el `23505` del índice. Prueba en la Tarea 6.
4. **Rechazo de datos en un reenvío** después de un intento sin respuesta: la fila sigue `Pending` y el pedido sigue bloqueado; no se libera ni se arma otra clave. Pruebas en la Tarea 4.
5. **Tanda con un pedido de otro tenant o inexistente:** `orderNumber` y `customerName` en `null`, sin 500 ni fuga. Prueba en la Tarea 8.

---

## Bloque 1 — Por confirmar en la fila de la factura (Tareas 1 a 5, un implementador)

### Task 1: Dominio de la factura por confirmar

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoInvoice.cs`, `src/Modules/Siigo/Modules.Siigo.Domain/SiigoErrorCodes.cs`
- Create: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoResolveTooEarlyException.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoInvoiceTests.cs`, create `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoInvoiceAwaitingConfirmationTests.cs`

**Interfaces:**
- Produces (en `SiigoErrorCodes`): `InvoiceBatchOrderInProgress`, `InvoiceInterrupted`, `InvoiceResolvedMissing`, `InvoiceResolveTooEarly`, con los valores de «Global Constraints».
- Produces (en `SiigoInvoice`):
  - `DateTimeOffset? UnansweredAt` y `DateTimeOffset? LastUnansweredAt` (setters privados).
  - `SendLease` = `TimeSpan.FromMinutes(8)`, con el comentario corregido (cubre el peor caso de un pedido: auth, buscar y crear cliente, auth, factura y flete). `public static readonly TimeSpan MissingResolutionDelay = TimeSpan.FromMinutes(10)`.
  - `bool MarkUnanswered(string errorCode, string? errorDetail, DateTimeOffset now)`: fija `ErrorCode`/`ErrorDetail` (recortados), `UnansweredAt ??= now`, `LastUnansweredAt = now`, `SendingSince = null`, `UpdatedAt = now`. Devuelve `true` sólo si `UnansweredAt` pasó de nulo a valor.
  - `bool RecordRejection(string errorCode, string errorDetail, decimal? expectedPaymentsTotal, bool serviceOutage, DateTimeOffset now)`: con `serviceOutage` → `MarkUnanswered(errorCode, errorDetail, now)`; con `UnansweredAt` puesto → la fila **sigue** `Pending` con `ErrorCode`/`ErrorDetail`, suelta `SendingSince` y **no** mueve `LastUnansweredAt` (Siigo contestó); si no → `MarkRejected` de hoy. Devuelve `true` sólo si quedó `Rejected`.
  - `bool RecoverInterruptedSend(DateTimeOffset now)`: si `Pending`, `SendingSince` no nulo y `!IsBeingSent(now)` → `UnansweredAt ??= now`, `LastUnansweredAt = now`, `ErrorCode = InvoiceInterrupted`, `ErrorDetail = null`, `SendingSince = null`. Devuelve `true` sólo si `UnansweredAt` pasó de nulo a valor.
  - `bool IsAwaitingConfirmation(DateTimeOffset now)`, `bool IsResolvable(DateTimeOffset now)` (`Unknown`, o `Pending` y `!IsBeingSent(now)`), `bool CanResend(DateTimeOffset now)` (`Pending`, `!IsBeingSent(now)`, `now - CreatedAt <= StaleAfter`), `DateTimeOffset? MissingAvailableAt(DateTimeOffset now)` (`null` si no es resoluble o si ya se puede).
  - `ResolveExists(string number, DateTimeOffset now)` y `ResolveMissing(DateTimeOffset now)` cambian `EnsureUnknown` por `EnsureResolvable(now)` (422 `state_invalid` si no). `ResolveMissing` lanza `SiigoResolveTooEarlyException` si `MissingAvailableAt(now)` no es nulo.
  - `MarkTransient` se conserva tal cual (lo usan la de flete que espera y la D-2).
- Produces: `public sealed class SiigoResolveTooEarlyException(DateTimeOffset availableAt) : DomainException(SiigoErrorCodes.InvoiceResolveTooEarly, …), IHasProblemExtensions` con `AvailableAt` y `ProblemExtensions["details"] = new { availableAt }` (422 por el mapeo central de `DomainException`).

> **D-1 (aprobada por el owner, 2026-11-24):** el spec define `IsAwaitingConfirmation` como `Pending ∧ UnansweredAt ≠ null ∧ ¬IsBeingSent` y ancla los 10 minutos en `LastUnansweredAt`. Un envío muerto que nadie reenvió (proceso caído o corte por apagado, §4) tiene `sending_since` vencido y `unanswered_at` nulo hasta el próximo reenvío, así que no se vería por confirmar y «No existe» no esperaría. **El plan propone**, salvo que el owner diga otra cosa: `IsAwaitingConfirmation = Pending ∧ (UnansweredAt ≠ null ∨ SendingSince ≠ null) ∧ ¬IsBeingSent(now)` y anclar `MissingAvailableAt` en `SendingSince ?? LastUnansweredAt`. Confírmalo antes de empezar el bloque.

- [ ] **Step 1: Pruebas que fallan** en `SiigoInvoiceAwaitingConfirmationTests` (fila de `PendingFor` como en `SiigoInvoiceTests.NewPending`):
  - `AnUnansweredSendKeepsTheFirstMomentAndMovesTheLast`: `BeginSend(Now)`, `MarkUnanswered("siigo.invoice.timeout", null, Now)` → `true`; `BeginSend(Now+1m)`, `MarkUnanswered("siigo.invoice.siigo_service", "documents_service", Now+1m)` → `false`; `UnansweredAt == Now`, `LastUnansweredAt == Now+1m`, `Status == Pending`, `IsBeingSent(Now+1m) == false`.
  - `ARejectionAfterAnUnansweredAttemptStaysPending`: con `UnansweredAt` puesto, `RecordRejection("siigo.invoice.rejected", "invalid_reference", null, serviceOutage: false, Now+2m)` → `false`; `Status == Pending`, `ErrorCode == "siigo.invoice.rejected"`, `ErrorDetail == "invalid_reference"`, `LastUnansweredAt` sin cambio, `IsLive`.
  - `AServiceOutageOnTheFirstAttemptAwaitsConfirmation`: fila sin `UnansweredAt`, `RecordRejection("siigo.invoice.siigo_service", "documents_service", null, true, Now)` → `false`, `Pending`, `UnansweredAt == Now`.
  - `ADataRejectionOnTheFirstAttemptIsStillRejected`: `RecordRejection(…, false, Now)` → `true`, `Status == Rejected`, `IsLive == false`.
  - `AnExpiredSendWithoutOutcomeIsRecoveredAsUnanswered`: `BeginSend(Now)`; `RecoverInterruptedSend(Now + SendLease + 1s)` → `true`, `ErrorCode == "siigo.invoice.interrupted"`, `SendingSince == null`; otra con `RecoverInterruptedSend(Now + 1m)` (dentro del lease) → `false` y nada cambia.
  - `MissingWaitsTenMinutesAfterTheLastUnansweredAttemptAndExistsDoesNot`: `MarkUnanswered(…, Now)`; `ResolveMissing(Now+9m)` lanza `SiigoResolveTooEarlyException` con `AvailableAt == Now+10m` y `Code == "siigo.invoice.resolve_too_early"`; `ResolveMissing(Now+10m)` deja `Failed`; en otra fila igual, `ResolveExists("FV-1-9", Now+1m)` deja `Created`.
  - `AnUnknownResolvesMissingAtOnce`: `Pending` vencida (`ExpireIfStale(Now+25h)`) → `ResolveMissing(Now+25h)` deja `Failed`.
  - `ADeadSendThatNobodyResentAwaitsConfirmationAndWaitsTenMinutes` (D-1): `BeginSend(Now)`; a `Now + SendLease + 1s`: `IsAwaitingConfirmation == true`, `MissingAvailableAt == Now + 10m`.
  - `OnlyAPendingNotBeingSentAndNotStaleCanBeResent`: `CanResend` falso con `IsBeingSent`, con `Created` y con `now - CreatedAt > StaleAfter`; verdadero tras `MarkUnanswered`.
  - En `SiigoInvoiceTests`: `OnlyAnUnknownCanBeResolved` pasa a `OnlyAnUnknownOrAPendingNotBeingSentCanBeResolved` (la `Pending` de la prueba hace `BeginSend(Now)` y sigue lanzando `state_invalid`; `Created` y `Rejected` también; una `Pending` sin envío en curso se resuelve). `TheSendLeaseIsEightMinutes`: `Assert.Equal(TimeSpan.FromMinutes(8), SiigoInvoice.SendLease)`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`. Esperado: no compila (`MarkUnanswered`, `RecordRejection`, `SiigoResolveTooEarlyException` no existen). Pega la salida.
- [ ] **Step 3: Implementar** los miembros de «Interfaces» en `SiigoInvoice`, los cuatro códigos y la excepción. El XML doc de la clase dice que «por confirmar» es `Pending` + `UnansweredAt`.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoInvoiceTests|FullyQualifiedName~SiigoInvoiceAwaitingConfirmationTests|FullyQualifiedName~SiigoPendingReasonTests"`. Esperado: verde.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): keep an unanswered invoice pending to confirm instead of rejecting it"`.

### Task 2: Ítem de la tanda: `ToConfirm`, factura enlazada y estado efectivo

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoInvoiceBatchItemStatus.cs`, `src/Modules/Siigo/Modules.Siigo.Domain/SiigoInvoiceBatch.cs`
- Create: `src/Modules/Siigo/Modules.Siigo.Domain/SiigoBatchItemState.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoInvoiceBatchTests.cs`, create `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoBatchItemEffectiveStateTests.cs`

**Interfaces:**
- Consumes: `SiigoInvoice.IsBeingSent`, `CanResend`, `IsResolvable`, `MissingAvailableAt` (Task 1).
- Produces:
  - `SiigoInvoiceBatchItemStatus.ToConfirm`, **al final** del enum (se guarda por nombre). Se corrige el XML doc del enum.
  - `SiigoInvoiceBatch.Create(Guid id, Guid tenantId, Guid requestedBy, IReadOnlyList<Guid> orderIds, DateTimeOffset now, string? paymentsJson = null)`: `paymentsJson` sólo con un pedido (si no, `ArgumentException`: la forma la frena antes el validador) y se guarda en ese ítem.
  - `SiigoInvoiceBatchItem`: `Guid? InvoiceId`, `string? PaymentsJson`, `bool OrderMarkFailed`; `IsSettled` incluye `ToConfirm`.
  - `MarkInvoiced(string number, DateTimeOffset now, Guid? invoiceId = null, bool orderMarkFailed = false)`, `MarkFailed(string errorCode, DateTimeOffset now, Guid? invoiceId = null)`, `MarkToConfirm(Guid invoiceId, string? errorCode, DateTimeOffset now)`. Las tres no hacen nada si el ítem ya está cerrado, como hoy.
  - `SiigoBatchItemState SiigoInvoiceBatchItem.Effective(SiigoInvoice? invoice, DateTimeOffset now)` y `public sealed record SiigoBatchItemState(SiigoInvoiceBatchItemStatus Status, string? ErrorCode, string? InvoiceNumber, bool CanResend, bool CanResolve, DateTimeOffset? MissingAvailableAt)` con `public static IReadOnlyList<SiigoInvoiceBatchItemStatus> CountOrder` = `Pending, Sending, Invoiced, ToConfirm, Failed, Skipped`.
  - Regla de `Effective` (tabla §2 del spec): sólo un guardado `ToConfirm` con `invoice.Id == InvoiceId` mira la fila: `Pending` e `IsBeingSent` → `Sending`; `Pending` o `Unknown` → `ToConfirm` con `ErrorCode` de la fila, `CanResend = invoice.CanResend(now)`, `CanResolve = invoice.IsResolvable(now)`, `MissingAvailableAt = invoice.MissingAvailableAt(now)`; `Created`/`Stamped`/`StampRejected` → `Invoiced` con `Number` de la fila; `Failed` → `Failed` con `siigo.invoice.resolved_missing`; `Rejected` → `Failed` con `ErrorCode` de la fila. Cualquier otro guardado, o sin fila: lo guardado y los tres `can*`/hora en falso/nulo.
- [ ] **Step 1: Pruebas que fallan:**
  - `SiigoBatchItemEffectiveStateTests`: una prueba por fila de la tabla (`AToConfirmWhoseInvoiceIsBeingSentLooksSending`, `…PendingStaysToConfirmWithTheRowCodeAndItsActions`, `…CreatedLooksInvoicedWithTheRowNumber`, `…ResolvedMissingLooksFailedWithResolvedMissing`, `…RejectedLooksFailedWithTheRowCode`, `AnyOtherStoredStatusIsShownAsStored`, `AnInvoiceOfAnotherIdIsIgnored`) y `TheCountOrderIsFixed` (`Assert.Equal([Pending, Sending, Invoiced, ToConfirm, Failed, Skipped], SiigoBatchItemState.CountOrder)`).
  - `SiigoInvoiceBatchTests`: `AToConfirmItemIsSettledAndClosesTheBatch`; `TheItemKeepsItsInvoiceAndTheOrderMarkWarning` (`MarkInvoiced("FV-1-1", Now, id, true)` → `InvoiceId == id`, `OrderMarkFailed`); `PaymentsTravelOnlyWithASingleOrder` (`Create(…, [a], Now, "[…]")` guarda `PaymentsJson`; con `[a, b]` lanza `ArgumentException`). Las existentes siguen compilando (parámetros opcionales al final).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`. Esperado: no compila (`ToConfirm`, `Effective`, `MarkToConfirm`).
- [ ] **Step 3: Implementar** lo de «Interfaces».
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoInvoiceBatchTests|FullyQualifiedName~SiigoBatchItemEffectiveStateTests"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): add the to-confirm batch item and its effective state"`.

### Task 3: Persistencia, repositorios y migración expand

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/SiigoDbContext.cs`, `SiigoUnitOfWork.cs`, `SiigoInvoiceRepository.cs`, `SiigoInvoiceBatchRepository.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/SiigoPorts.cs` (`ISiigoInvoiceRepository`), `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceBatches.cs` (`ISiigoInvoiceBatchRepository`)
- Create: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Persistence/Migrations/<timestamp>_AddSiigoBackgroundInvoicing.cs` (+ `.Designer.cs` y snapshot, generados)
- Modify (dobles): `tests/Modules/Siigo/Modules.Siigo.UnitTests/SendSiigoInvoiceWhatsAppHandlerTests.cs` (`StubInvoices`)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoDbContextMappingTests.cs`, `SiigoUnitOfWorkTranslationTests.cs`; create `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoBackgroundInvoicingMigrationTests.cs`

**Interfaces:**
- Consumes: miembros nuevos de Task 1 y Task 2.
- Produces:
  - Columnas: `siigo.invoices.unanswered_at`, `last_unanswered_at` (`timestamptz NULL`); `siigo.invoice_batch_items.invoice_id` (`uuid NULL`, sin FK), `payments_json` (`text NULL`), `order_mark_failed` (`boolean NOT NULL DEFAULT false`).
  - `internal const string SiigoDbContext.InvoiceBatchItemsTenantOrderOpenIndex = "IX_invoice_batch_items_tenant_order_open"`: único sobre `(tenant_id, order_id)` con `HasFilter("status IN ('Pending', 'Sending')")`.
  - `SiigoUnitOfWork.Translate`: un `23505` de **ese** índice (por nombre) → `ResourceConflictException(SiigoErrorCodes.InvoiceBatchOrderInProgress, …)`.
  - `ISiigoInvoiceRepository.ListByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken)` → `IReadOnlyList<SiigoInvoice>` (vacía con ids vacíos; filtra por tenant).
  - `ISiigoInvoiceBatchRepository`: `Task<IReadOnlyList<Guid>> ListOpenOrderIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken)` (sin tracking; ítems `Pending`/`Sending`), `Task<Guid?> FindOpenBatchIdAsync(Guid tenantId, Guid orderId, CancellationToken)`, `Task<(IReadOnlyList<SiigoInvoiceBatch> Items, int Total)> SearchAsync(Guid tenantId, int page, int pageSize, CancellationToken)` (con ítems, `CreatedAt` descendente y luego `Id` descendente).
  - En la migración, dos `internal const string` probables por separado: `OpenOrderGuardSql` (un `DO $$ … RAISE EXCEPTION … $$` si hay más de un ítem `Pending`/`Sending` por `(tenant_id, order_id)`; el mensaje dice que el owner decide cuál cerrar) y `BackfillSql` (idempotente: `UPDATE siigo.invoices SET unanswered_at = updated_at, last_unanswered_at = updated_at WHERE status = 'Pending' AND unanswered_at IS NULL AND (error_code IN ('siigo.invoice.timeout','siigo.invoice.network','siigo.invoice.rate_limited','siigo.invoice.unavailable','siigo.invoice.siigo_service') OR sending_since IS NOT NULL)`). Orden en `Up`: columnas → `OpenOrderGuardSql` → índice → `BackfillSql`.
- [ ] **Step 1: Pruebas que fallan:**
  - `SiigoDbContextMappingTests.TheBackgroundInvoicingColumnsAndTheOpenOrderIndexAreMapped`: columnas con sus nombres y nulabilidad, `order_mark_failed` con default `false`, índice único con el nombre y el filtro de arriba.
  - `SiigoUnitOfWorkTranslationTests.AnOpenItemOfTheSameOrderIsOrderInProgress` (con el helper `UniqueViolation` del archivo).
  - `SiigoBackgroundInvoicingMigrationTests` (base migrada del harness): `TheBackfillMarksOnlyPendingRowsThatWentOutAndRunsTwiceTheSame` (siembra por SQL una `Pending` con `error_code = 'siigo.invoice.timeout'`, una `Pending` con `sending_since`, una `Pending` sin código ni envío y una `Rejected` con `siigo_service`; corre `BackfillSql` dos veces; sólo las dos primeras tienen `unanswered_at = last_unanswered_at = updated_at`) y `TheOpenOrderGuardRefusesDuplicatesAndPassesWithoutThem` (`DROP INDEX siigo."IX_invoice_batch_items_tenant_order_open"`, dos ítems `Pending` del mismo pedido → `OpenOrderGuardSql` lanza `PostgresException` con `SqlState == "P0001"`; sin duplicados no lanza).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests` y `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests`. Esperado: no compilan.
- [ ] **Step 3: Implementar** mapeo, traducción y repositorios; actualiza `StubInvoices` (`ListByIdsAsync` filtra sus filas).
- [ ] **Step 4: Generar la migración** — `dotnet ef migrations add AddSiigoBackgroundInvoicing --project src/Modules/Siigo/Modules.Siigo.Infrastructure --context SiigoDbContext`. Agrega a mano las dos constantes y sus `migrationBuilder.Sql` en el orden de arriba. Revisa que `Up` sólo agregue columnas, el índice y el SQL; `Down` queda generado pero **no se corre** (registro legal): déjalo con un comentario que lo diga.
- [ ] **Step 5: GREEN** — unitarias: `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoDbContextMappingTests|FullyQualifiedName~SiigoUnitOfWorkTranslationTests"` (incluye `TheModelHasNoChangesPendingAMigration`). Integración, en primer plano: `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoBackgroundInvoicingMigrationTests"`.
- [ ] **Step 6: Commit** — `git commit -m "feat(siigo): persist unanswered attempts and link batch items to their invoice"`.

### Task 4: `IssueSiigoInvoiceHandler` deja por confirmar en vez de rechazar

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/IssueSiigoInvoice.cs`, `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceSupport.cs`, `src/Modules/Siigo/Modules.Siigo.Application/SiigoAuditActions.cs`
- Modify: `.claude/rules/siigo.md` (viñeta de errores de servicio: ahora la factura queda por confirmar, no `Rejected`)
- Create: `tests/Modules/Siigo/Modules.Siigo.UnitTests/IssueSiigoInvoiceHandlerTests.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceApiTests.cs`

**Interfaces:**
- Consumes: `MarkUnanswered`, `RecordRejection`, `RecoverInterruptedSend` (Task 1).
- Produces:
  - `SiigoAuditActions.InvoiceUnanswered = "siigo.invoice.unanswered"`.
  - `SiigoInvoiceSupport.LeftQep(string transientReason)`: `false` sólo para `partner_id_missing` y `auth_without_token` (la factura no salió; ver D-2).
  - `SiigoInvoiceSupport.Audit(…, string? outcome = null, string? fromStatus = null)`: el payload suma `fromStatus`.
  - Comportamiento: en `ApplyAsync`, la rama `Rejected` llama a `RecordRejection(ErrorCodeOf, ErrorDetailOf, total, SiigoApiErrors.IsServiceUnavailable(errors), now)`; si quedó `Rejected` audita `InvoiceRejected` y descarta la de flete hermana `Pending` (como hoy); si no, audita `InvoiceUnanswered` y **no** toca la de flete. La rama transitoria: `LeftQep(reason)` → `MarkUnanswered(TransientCodeOf(reason), null, now)` (audita `InvoiceUnanswered` si devuelve `true`); si no → `MarkTransient(TransientCodeOf(reason), now)`. En `ResumeAsync`, antes de `BeginSend`, cada fila de `toSend` llama a `RecoverInterruptedSend(now)` y audita `InvoiceUnanswered` si devuelve `true`. El XML doc del handler se actualiza.

> **D-2 (aprobada por el owner, 2026-11-24):** el spec lista los motivos que dejan por confirmar (`timeout`, `network`, `http_5xx`, `http_408`, `http_3xx`, `http_429`, `invalid_response` y errores de servicio) pero no dice nada de `partner_id_missing` ni `auth_without_token`, que el cliente también devuelve como transitorios y en los que la factura nunca salió. El plan los deja `Pending` **sin** `UnansweredAt` (código `siigo.invoice.unavailable`). Además, un `timeout`/`network` de `/auth` llega igual que uno de la factura: el plan lo trata como sin respuesta (lo seguro ante duplicados).

- [ ] **Step 1: Pruebas que fallan** en `IssueSiigoInvoiceHandlerTests`, con dobles privados del archivo (copiando el estilo de `SendSiigoInvoiceWhatsAppHandlerTests`): contexto con `siigo.invoice.issue`, módulos prendidos, membresía fija, conexión activa, repositorio en memoria sembrado con filas `Pending` de `PendingFor` (camino de reanudación: no se arma factura), unidad de trabajo con transacción de mentira, `ISiigoApi` por `DispatchProxy` con un guion de `SiigoInvoiceOutcome` y las claves que recibió, auditoría que anota, reloj mutable y `ISiigoOrderSource` que sólo responde `GetVersionAsync`/`MarkInvoicedAsync`.
  - `ATimeoutThenADocumentsServiceOnTheResendStaysPendingWithTheSameKey` (el caso de producción): guion `[Transient("timeout"), Rejected([documents_service])]`, dos `HandleAsync` → `Pending`, `UnansweredAt == t1`, `LastUnansweredAt == t2`, `ErrorCode == "siigo.invoice.siigo_service"`, `ErrorDetail == "documents_service"`, las dos claves iguales, exactamente una auditoría `siigo.invoice.unanswered`.
  - `ATimeoutThenADataRejectionIsNotRejected`: `[Transient("timeout"), Rejected([invalid_reference])]` → `Pending`, `ErrorCode == "siigo.invoice.rejected"`, dos auditorías `unanswered`, ninguna `rejected`.
  - `ADataRejectionOnTheFirstAttemptIsRejected`: `[Rejected([invalid_reference])]` → `Rejected`, auditoría `siigo.invoice.rejected`.
  - `AServiceOutageOnTheFirstAttemptAwaitsConfirmation` y `ATooManyRequestsAwaitsConfirmation` (`http_429` → `siigo.invoice.rate_limited`, `UnansweredAt` puesto).
  - `AMissingPartnerIdIsNotAnUnansweredAttempt` (D-2): `[Transient("partner_id_missing")]` → `UnansweredAt == null`, `ErrorCode == "siigo.invoice.unavailable"`.
  - `AResumeOfASendThatDiedIsMarkedUnansweredBeforeSendingAgain`: fila con `BeginSend(Now - 9 min)`, guion `[Created]` → `Created`, `UnansweredAt == Now`, una auditoría `unanswered` y una `created`.
  - `TheFreightIsDiscardedOnlyWhenTheOrderInvoiceIsReallyRejected`: filas `Order` y `Freight` `Pending`; con la del pedido ya sin respuesta y `[Rejected([invalid_reference])]` → la de flete sigue `Pending`; sin intento previo → la de flete `Failed` con `siigo.invoice.order_rejected`.
  - En `SiigoInvoiceApiTests`: `ADocumentsServiceOnTheFirstAttemptBlocksTheManualMarkUntilResolved` (Siigo contesta 400 `documents_service` → `Pending`; `POST …/orders/{id}/invoice` responde 409 `order.order.external_invoice_pending`) y `APendingWhoseSendLeaseExpired…` cambia `'3 minutes'` por `'9 minutes'` (3 minutos ya caen dentro del lease de 8).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`. Esperado: no compila (`InvoiceUnanswered`). Luego `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~IssueSiigoInvoiceHandlerTests"` falla en los asserts de estado.
- [ ] **Step 3: Implementar** lo de «Interfaces» y la viñeta de `siigo.md`.
- [ ] **Step 4: GREEN** — unitarias del filtro anterior más `SiigoServiceErrorTests|SiigoPendingReasonTests`. Integración en primer plano: `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~ADocumentsServiceOnTheFirstAttemptBlocksTheManualMarkUntilResolved|FullyQualifiedName~APendingWhoseSendLeaseExpired|FullyQualifiedName~ATimeoutLeavesItPending|FullyQualifiedName~TheRealStatusAdapterSeesOpenInvoices"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): hold service errors and resends after an unanswered attempt as pending to confirm"`.

### Task 5: Resolver sin esperar 24 h y lo que ve el pedido

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/ResolveSiigoInvoice.cs`, `SiigoInvoiceDto.cs`, `GetSiigoInvoices.cs`, `StampSiigoInvoice.cs`, `IssueSiigoInvoice.cs`
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceApiTests.cs`

**Interfaces:**
- Consumes: `IsResolvable`, `IsAwaitingConfirmation`, `CanResend`, `MissingAvailableAt`, `SiigoResolveTooEarlyException` (Task 1); `Audit(…, fromStatus)` (Task 4).
- Produces:
  - `SiigoInvoiceDto` suma al final `bool AwaitingConfirmation = false, bool CanResend = false, bool CanResolve = false, DateTimeOffset? MissingAvailableAt = null`, y `From(SiigoInvoice invoice, DateTimeOffset now, bool orderMarkFailed = false, bool canSendWhatsApp = false)`: `AwaitingConfirmation = IsAwaitingConfirmation(now)`, `CanResend = CanResend(now)`, `CanResolve = Status == Unknown || IsAwaitingConfirmation(now)` (D-3), `MissingAvailableAt = CanResolve ? MissingAvailableAt(now) : null`. Los cuatro llamadores pasan `clock.UtcNow`. El XML doc explica por qué existen (la pantalla no ve `SendingSince` ni el reloj de 24 h).
  - `ResolveSiigoInvoiceHandler`: guarda `fromStatus` antes de resolver y lo audita; al resolver `missing` la del pedido, descarta cada de flete `Pending` sin `UnansweredAt` con `MarkFailed("siigo.invoice.order_rejected", "The order invoice was declared missing, so the freight invoice was not sent.", now)` en el mismo guardado. XML doc: acepta `Unknown` o `Pending` que no se esté mandando.

> **D-3 (aprobada por el owner, 2026-11-24):** el spec no define `canResolve`/`canResend` de `SiigoInvoiceDto` más allá de «fila resoluble». §3 permite resolver una `Pending` que nunca salió (la de flete que espera); ofrecer «Existe/No existe» ahí confunde. El plan limita `canResolve` a `Unknown` o por confirmar; el endpoint sigue aceptando lo que §3 permite.

- [ ] **Step 1: Pruebas que fallan** en `SiigoInvoiceApiTests`:
  - `APendingAwaitingConfirmationIsResolvedAsExistsWithoutWaitingADay`: Siigo 503 → `POST`; `resolve` `exists` con `FV-1-777` → `Created` y el pedido marcado con ese número.
  - `MissingBeforeTenMinutesIs422WithAvailableAtAndAfterThemFreesTheOrder`: tras el 503, `resolve` `missing` → 422 `siigo.invoice.resolve_too_early` con `details.availableAt`; `UPDATE siigo.invoices SET last_unanswered_at = now() - interval '11 minutes'` (y `sending_since = NULL`) → `Failed` y un nuevo `POST` crea otra factura.
  - `APendingBeingSentCannotBeResolved`: `UPDATE … SET sending_since = now()` → 422 `siigo.invoice.state_invalid`.
  - `ResolvingTheOrderAsMissingDiscardsTheFreightThatNeverLeft` (`ArrangeAsync(database, freightSeparate: true, freight: true)`): 503 en la del pedido, tiempo corrido 11 min por SQL, `missing` → la de flete `Failed` con `siigo.invoice.order_rejected` y el pedido libre.
  - `TheListSaysWhichPendingAwaitsConfirmationAndWhatCanBeDone`: `GET …/siigo-invoice` tras el 503 → `awaitingConfirmation`, `canResend`, `canResolve` en `true` y `missingAvailableAt` presente; con `sending_since = now()` los tres en `false` y `missingAvailableAt` nulo.
  - `TheResolutionAuditCarriesWhereItCameFrom`: `audit.entries` de `siigo.invoice.resolved` lleva `"fromStatus":"Pending"`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests` (no compila si usas los nombres nuevos del DTO desde la prueba) o, si compila, la corrida filtrada falla en los asserts. Pega la salida.
- [ ] **Step 3: Implementar** lo de «Interfaces».
- [ ] **Step 4: GREEN** — `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~APendingAwaitingConfirmationIsResolved|FullyQualifiedName~MissingBeforeTenMinutes|FullyQualifiedName~APendingBeingSentCannotBeResolved|FullyQualifiedName~ResolvingTheOrderAsMissing|FullyQualifiedName~TheListSaysWhichPending|FullyQualifiedName~TheResolutionAuditCarries|FullyQualifiedName~AnUnknownResolved"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): resolve a pending invoice to confirm without waiting a day"`.

### Cierre del bloque 1

- [ ] `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests` completo y `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~Siigo"`.
- [ ] Una corrida de integración por clase completa, en fila: `SiigoInvoiceApiTests`, `SiigoBackgroundInvoicingMigrationTests`, `SiigoInvoiceBatchApiTests` (verifica que el dominio nuevo no la rompió; sus cambios de comportamiento llegan en el bloque 2).
- [ ] Una revisión del bloque (lente `review-reliability`) sobre el diff del bloque.

---

## Bloque 2 — Tandas: formas de pago, worker y lectura BFF (Tareas 6 a 8, un implementador)

### Task 6: La tanda acepta formas de pago, choca con una abierta y el pedido la encuentra

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceBatches.cs`, `IssueSiigoInvoice.cs` (reglas de la forma de pago compartidas), `SiigoInvoiceAvailability.cs`
- Create: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoBatchPayments.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs` (`SiigoInvoiceBatchRequest`, `CreateInvoiceBatchAsync`)
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceBatchApiTests.cs`, `SiigoInvoiceApiTests.cs`

**Interfaces:**
- Consumes: `SiigoInvoiceBatch.Create(…, paymentsJson)` (Task 2); `ListOpenOrderIdsAsync`, `FindOpenBatchIdAsync`, traducción del índice (Task 3).
- Produces:
  - `internal static class SiigoPaymentChoiceRules { public static void Configure(InlineValidator<SiigoPaymentChoice> payment) }` con las tres reglas de hoy; `IssueSiigoInvoiceValidator` usa `RuleForEach(…).ChildRules(SiigoPaymentChoiceRules.Configure)` (mismas claves de error).
  - `CreateSiigoInvoiceBatchCommand(Guid TenantId, IReadOnlyList<Guid>? OrderIds, IReadOnlyList<SiigoPaymentChoice>? Payments = null)`; el validador suma `payments` con las mismas reglas por elemento y «con `payments` no vacío, exactamente un `orderId`» (422 `validation.failed`, clave `payments`).
  - `public static class SiigoBatchPayments { string? Serialize(IReadOnlyList<SiigoPaymentChoice>? payments); IReadOnlyList<SiigoPaymentChoice>? Deserialize(string? json); }` (lista vacía o nula → `null`).
  - `public sealed class SiigoInvoiceBatchOrderInProgressException(IReadOnlyList<Guid> orderIds) : ResourceConflictException(SiigoErrorCodes.InvoiceBatchOrderInProgress, …), IHasProblemExtensions` con `details = new { orderIds }`.
  - `CreateSiigoInvoiceBatchHandler`: después de validar, `ListOpenOrderIdsAsync` y, si hay, lanza esa excepción; al guardar, un `ResourceConflictException` con código `InvoiceBatchOrderInProgress` (la carrera por el índice) se reemplaza por la excepción con los ids que devuelva una nueva `ListOpenOrderIdsAsync`. La auditoría `siigo.invoice_batch.requested` lleva `{ orders, withPayments }`.
  - `SiigoInvoiceAvailabilityDto(bool Available, string? Reason, Guid? QueuedBatchId = null)`: `FindOpenBatchIdAsync` en todas las respuestas del handler.
  - `SiigoInvoiceBatchRequest(IReadOnlyList<Guid>? OrderIds, IReadOnlyList<SiigoPaymentChoiceRequest>? Payments = null)`; el endpoint mapea `ProofId ?? Guid.Empty` como `IssueInvoiceAsync`. El 202 documenta además `ProducesProblem(409)`.
- [ ] **Step 1: Pruebas que fallan** en `SiigoInvoiceBatchApiTests`:
  - `ABatchWithPaymentsIsAcceptedAndThePaymentsReachTheIssue`: `payments` con un comprobante ajeno al pedido → 202; tras `DrainAsync`, el ítem `Skipped` con `siigo.invoice.payments_mismatch` y `InvoiceCalls` vacío (sin `payments` el mismo pedido se factura: lo cubre `ABatchIsAccepted…`).
  - `PaymentsWithTwoOrdersIs422OnPayments`: `errors.payments` presente y ninguna tanda creada; un elemento sin `siigoPaymentTypeId` también es 422.
  - `AnOrderAlreadyInAnOpenBatchIs409WithItsIds`: segundo `POST` con el mismo pedido antes de drenar → 409 `siigo.invoice_batch.order_in_progress` y `details.orderIds == [orderId]`; tras drenar, otro `POST` es 202.
  - `TwoSimultaneousBatchesOfTheSameOrderCreateOne`: dos `POST` con `Task.WhenAll` → uno 202 y otro 409 con `details.orderIds`; una sola tanda en la base.
  - `TheBatchRequestAuditSaysWhetherItCarriedPayments`: `audit.entries` lleva `"withPayments":true`.
  - En `SiigoInvoiceApiTests`: `TheAvailabilityPointsToTheOpenBatchOfTheOrder` (`queuedBatchId` igual al id de la tanda antes de drenar; `null` después).
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests` (no compila si la prueba usa tipos nuevos) o corrida filtrada en rojo. Pega la salida.
- [ ] **Step 3: Implementar** lo de «Interfaces».
- [ ] **Step 4: GREEN** — `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~ABatchWithPayments|FullyQualifiedName~PaymentsWithTwoOrders|FullyQualifiedName~AnOrderAlreadyInAnOpenBatch|FullyQualifiedName~TwoSimultaneousBatches|FullyQualifiedName~TheBatchRequestAudit|FullyQualifiedName~TheAvailabilityPointsToTheOpenBatch|FullyQualifiedName~ABadBodyIs422"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): accept payments in a single-order batch and refuse an order already queued"`.

### Task 7: El ejecutor y el worker cierran el ítem con la factura

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceBatches.cs` (`SiigoBatchItemOutcome`, `ISiigoBatchItemInvoker`)
- Modify: `src/Bootstrapper/SiigoBatchItemInvoker.cs`
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Batches/SiigoInvoiceBatchWorker.cs`
- Modify: `.claude/rules/siigo.md` (viñeta de la tanda: lease de 10 min, recuperación por el estado de la fila, `ToConfirm`, `payments`, 409)
- Test: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceBatchApiTests.cs`

**Interfaces:**
- Consumes: `MarkInvoiced/MarkFailed/MarkToConfirm`, `PaymentsJson` (Task 2); `SiigoBatchPayments.Deserialize` (Task 6); `ISiigoInvoiceRepository.ListByOrderAsync`.
- Produces:
  - `SiigoBatchItemOutcome.Invoiced(string Number, Guid? InvoiceId = null, bool OrderMarkFailed = false)`, `Failed(string ErrorCode, Guid? InvoiceId = null)` y nuevo `ToConfirm(Guid InvoiceId, string? ErrorCode)`.
  - `ISiigoBatchItemInvoker.InvokeAsync(Guid tenantId, Guid requestedBy, Guid orderId, IReadOnlyList<SiigoPaymentChoice>? payments, CancellationToken)`; el adaptador pasa `payments` al `IssueSiigoInvoiceCommand`.
  - `SiigoBatchItemInvoker.Map`: `Created`/`Stamped`/`StampRejected` → `Invoiced(Number, Id, OrderMarkFailed)`; `Rejected`/`Failed` → `Failed(ErrorCode ?? "siigo.invoice.rejected", Id)`; `Pending`/`Unknown` → `ToConfirm(Id, ErrorCode)`; sin fila del pedido → `Failed(SiigoErrorCodes.Unavailable)`.
  - `SiigoInvoiceBatchWorker.Lease` = `TimeSpan.FromMinutes(10)` (comentario: mayor que `SiigoInvoice.SendLease`).
  - `private Task<SiigoBatchItemOutcome> RecoverAsync(Guid tenantId, Guid orderId, CancellationToken)`: la fila `Order` viva más nueva del pedido: `Pending`/`Unknown` → `ToConfirm(row.Id, row.ErrorCode ?? SiigoErrorCodes.InvoiceInterrupted)`; creada → `Invoiced(row.Number ?? "", row.Id)`; ninguna → `Failed(SiigoErrorCodes.InvoiceInterrupted)`. **Nunca llama a Siigo.** La usa el ítem recuperado (`Attempts > MaxAttempts`, en lugar del `Failed(siigo.unavailable)` de hoy) y el corte por apagado.
  - Corte por apagado: en `ProcessAsync`, un `OperationCanceledException` con el token cancelado durante `InvokeAsync` cierra el ítem con `RecoverAsync` y `SettleAsync` usando `CancellationToken.None` (un guardado corto) y relanza. `SettleAsync` aplica `ToConfirm` con `MarkToConfirm`.
- [ ] **Step 1: Pruebas que fallan** en `SiigoInvoiceBatchApiTests`:
  - `ASiigoOutageFailsTheOrderAtOnce…` pasa a `ASiigoOutageLeavesTheOrderToConfirmAndNothingIsScheduledForLater`: 503 → ítem `ToConfirm` con `siigo.invoice.unavailable` e `invoiceId` igual al id de la fila; tanda `Finished`; un solo envío tras dos `DrainAsync`.
  - `AnOrderWhoseSendWasCutShort…` pasa a `AnOrderCutShortBeforeItsInvoiceExistedIsFailedAsInterrupted`: `Failed` con `siigo.invoice.interrupted`, sin llamadas.
  - `ARecoveredItemTakesThePendingInvoiceAndNeverCallsSiigo`: el pedido ya tiene una `Pending` por el `POST` síncrono con 503; tanda; `UPDATE siigo.invoice_batch_items SET status = 'Sending', attempts = 1, lease_until = now() - interval '1 minute'`; `DrainAsync` → `ToConfirm` con el id de esa fila; una sola llamada a `/v1/invoices` en toda la prueba.
  - `ARecoveredItemWhoseInvoiceWasCreatedIsInvoiced`: igual con la fila `Created` → `Invoiced` con su número.
  - `AShutdownDuringTheSendClosesTheItemWithoutWaitingForTheLease`: `world.Siigo.OnRequest("/v1/invoices", _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); })`; `await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.DrainAsync(cts.Token))`; en la base el ítem queda `ToConfirm` con `invoice_id` y `lease_until` nulo, la tanda con `finished_at`; el `GET` muestra el ítem `Sending` (la fila sigue en envío) y, con `UPDATE siigo.invoices SET sending_since = now() - interval '9 minutes'`, `ToConfirm`.
  - `AFirstAttemptDataRejectionFailsWithTheInvoiceId`: amplía `ASiigoRejectionFailsOnlyThatOrderWithItsCode` para exigir `invoiceId`.
  - `ABatchOfAnOrderWhoseInvoiceWasNotMarkedMarksItWithoutCallingSiigo` (H-1 del spec del frontend, aprobada 2026-11-24; reemplaza a
    «Generar» sobre la ruta síncrona, que se borra en contract): fila `Created` con el pedido sin marcar (la marca falló en el primer
    envío por una marca manual puesta en medio, como en `AnOrderMarkedByHandAfterTheSendReturnsTheCreatedRowWithAWarningAndNeverA422`,
    y después se quita) → tanda de ese pedido (202, no 422) → ítem `Invoiced` sin `orderMarkFailed`, pedido marcado con el número y una
    sola llamada a `/v1/invoices` en toda la prueba. Si ya pasa en RED es la garantía que faltaba: déjala y anótalo.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests` y corrida filtrada en rojo (los asserts de `ToConfirm`). Pega la salida.
- [ ] **Step 3: Implementar** lo de «Interfaces» y la viñeta de `siigo.md`.
- [ ] **Step 4: GREEN** — `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoInvoiceBatchApiTests"` (en este punto, la clase completa ya es el filtro útil). Además `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~Siigo"` y `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRoot|FullyQualifiedName~Siigo"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): settle batch items from the invoice row and never resend a recovered one"`.

### Task 8: Lista y detalle de tandas (BFF)

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Application/Invoicing/SiigoInvoiceBatches.cs` (DTO, `GetSiigoInvoiceBatchHandler`, lista nueva), `src/Modules/Siigo/Modules.Siigo.Application/SiigoPorts.cs` (`ISiigoOrderSource`, `SiigoOrderLabel`)
- Modify: `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs` (`GET /` en el grupo `batches`)
- Modify: `src/Bootstrapper/SiigoOrderSource.cs`
- Modify: `src/Modules/Quotations/Modules.Quotations.Application/IExternalOrderInvoicing.cs`, `ExternalOrderInvoicingService.cs`, `IOrderRepository.cs`; `src/Modules/Quotations/Modules.Quotations.Infrastructure/Persistence/OrderRepository.cs`
- Modify (dobles): `tests/Modules/Quotations/Modules.Quotations.UnitTests/QuotationsTestDoubles.cs` (`StubOrderListRepository`), `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs` (`FakeInvoicing`)
- Test: `tests/Modules/Quotations/Modules.Quotations.UnitTests/OrderExternalInvoicingTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/SiigoOrderSourceTests.cs`, `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoiceBatchApiTests.cs`, `SiigoNoLeakTests.cs`

**Interfaces:**
- Consumes: `Effective`, `CountOrder` (Task 2); `ListByIdsAsync`, `SearchAsync` (Task 3).
- Produces:
  - Quotations: `public sealed record OrderNumberAndClient(string OrderNumber, Guid ClientId)` e `IOrderRepository.FindNumbersAndClientsAsync(Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken)` → `IReadOnlyDictionary<OrderId, OrderNumberAndClient>` (una consulta, sin tracking, como `FindOrderNumbersAsync`); `public sealed record ExternalInvoicingOrderLabel(string OrderNumber, string? CustomerName)` e `IExternalOrderInvoicing.ListLabelsAsync(Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken)` → `IReadOnlyDictionary<Guid, ExternalInvoicingOrderLabel>`; el nombre sale de `IQuotationCustomerLookup.FindNamesAsync`, igual que `ClientName` de `/orders` (`OrderListing.ToListItemsAsync`). Un id de otro tenant no aparece.
  - Siigo: `public sealed record SiigoOrderLabel(string OrderNumber, string? CustomerName)` e `ISiigoOrderSource.ListLabelsAsync(Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken)` → `IReadOnlyDictionary<Guid, SiigoOrderLabel>`; `SiigoOrderSource` sólo traduce.
  - DTOs: `SiigoInvoiceBatchStatusCountDto(string Status, int Count)`; `SiigoInvoiceBatchItemDto(Guid OrderId, int Position, string? OrderNumber, string? CustomerName, string Status, string? ErrorCode, IReadOnlyList<string> SiigoErrors, Guid? InvoiceId, string? InvoiceNumber, IReadOnlyList<string> Missing, bool OrderMarkFailed, bool CanResend, bool CanResolve, DateTimeOffset? MissingAvailableAt)`; `SiigoInvoiceBatchDto` conserva `Id, Status, Total, Invoiced, Skipped, Failed, CreatedAt, FinishedAt, Items` y suma `StatusCounts` (todo desde el estado efectivo); `SiigoInvoiceBatchSummaryDto(Guid Id, string Status, int Total, IReadOnlyList<SiigoInvoiceBatchStatusCountDto> StatusCounts, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt)`; `SiigoInvoiceBatchPageDto(IReadOnlyList<SiigoInvoiceBatchSummaryDto> Items, int Total, int Page, int PageSize)`. `SiigoErrors` son los códigos de `ErrorDetail` de la fila enlazada (partido por `"; "`, cada uno hasta el primer `':'`), vacío sin fila (D-4). El XML doc de cada DTO dice el porqué BFF (conteos completos en cero, `can*` porque la pantalla no ve `SendingSince` ni el reloj).
  - `SiigoInvoiceBatchDto.From(SiigoInvoiceBatch batch, IReadOnlyDictionary<Guid, SiigoInvoice> invoices, IReadOnlyDictionary<Guid, SiigoOrderLabel> labels, DateTimeOffset now)`; el `POST` (Task 6) y el `GET` por id la usan, con `IClock`.
  - `ListSiigoInvoiceBatchesQuery(Guid TenantId, string? Page, string? PageSize) : IQuery<SiigoInvoiceBatchPageDto>`, su validador (enteros, misma regla y mensajes que `ListSiigoCallFailuresValidator`, reutilizando `ListSiigoCallFailuresQueryParser.TryInt` y `SiigoCallFailurePaging`) y su handler (`SiigoInvoiceSupport.AuthorizeAsync`: 403, nunca 404; todas las tandas del tenant).
  - Endpoint: `batches.MapGet("/", ListInvoiceBatchesAsync)` con `page` y `pageSize` como texto, `Produces<SiigoInvoiceBatchPageDto>()`, 403 y 422.

> **D-4:** el spec dice que `siigoErrors` lleva «sólo códigos de Siigo» sin fijar el tipo; el plan lo entrega como lista de códigos para que la pantalla no parta texto.

- [ ] **Step 1: Pruebas que fallan:**
  - `OrderExternalInvoicingTests.ListLabelsReturnsTheNumberAndCustomerNameOfTheTenantOrdersOnly` y `SiigoOrderSourceTests.TheLabelsTravelByOrderId`.
  - `SiigoInvoiceBatchApiTests`: `TheDetailCarriesTheOrderNumberTheCustomerAndFullCounts` (`orderNumber == world.OrderNumber`, `customerName == "Verde Esencial S.A.S."`, `statusCounts` con los seis estados en el orden fijo, ceros incluidos); `AForeignOrderHasNoNumberNorCustomer`; `TheListPagesNewestFirstWithFullCounts` (tres tandas, `pageSize=2`: `total == 3`, dos ítems, la más nueva primero, seis conteos por tanda); `ABadPageIs422WithItsField` (`page=x` → `errors.page`); `TheListIsTheTenantsAndNeedsThePermission` (otro cliente sin `siigo.invoice.issue` → 403); `AResolutionFromTheOrderShowsInEveryBatchThatTouchedTheInvoice` (tanda con 503 → `ToConfirm`; 11 min por SQL; `resolve missing` → el detalle dice `Failed` con `siigo.invoice.resolved_missing`); `AResendFromAnotherBatchShowsAsInvoicedInTheFirst` (primera tanda con 503 → `ToConfirm`; segunda tanda con Siigo respondiendo creado → la primera muestra `Invoiced` con el número, y `canResend`/`canResolve` en `false`).
  - `SiigoNoLeakTests.TheBatchRoutesNeverCarryTheRequestTheKeyOrSiigoMessages`: Siigo contesta 400 `{"Errors":[{"Code":"documents_service","Message":"CLIENTE-SENTINEL"}]}`; `POST` de la tanda, `DrainAsync`, `GET` lista, `GET` detalle, `GET …/siigo-invoice`; ningún cuerpo contiene `SENTINEL`, `requestJson`, `request_json` ni la `idempotency_key` leída de `siigo.invoices`; `siigoErrors == ["documents_service"]`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Quotations/Modules.Quotations.UnitTests`, `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests` y `dotnet build tests/Modules/Siigo/Modules.Siigo.IntegrationTests`. Esperado: no compilan (`ListLabelsAsync`, DTOs nuevos).
- [ ] **Step 3: Implementar** lo de «Interfaces».
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Quotations/Modules.Quotations.UnitTests --filter "FullyQualifiedName~OrderExternalInvoicingTests"`, `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SiigoOrderSourceTests"` y, en primer plano, `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~TheDetailCarries|FullyQualifiedName~AForeignOrderHasNo|FullyQualifiedName~TheListPages|FullyQualifiedName~ABadPageIs422|FullyQualifiedName~TheListIsTheTenants|FullyQualifiedName~AResolutionFromTheOrderShows|FullyQualifiedName~AResendFromAnotherBatch|FullyQualifiedName~TheBatchRoutesNeverCarry"`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo): list invoice batches and show each item with its effective state"`.

### Cierre del bloque 2

- [ ] Unitarias completas de `Modules.Siigo.UnitTests` y `Bootstrapper.UnitTests`; `Modules.Quotations.UnitTests` filtrado por `OrderExternalInvoicing|StubOrderList`; `ArchitectureTests` con `Siigo|CompositionRoot|Quotations`.
- [ ] Clases completas, una a la vez: `SiigoInvoiceBatchApiTests`, `SiigoInvoiceApiTests`, `SiigoNoLeakTests`, `SiigoInvoiceWhatsAppApiTests`. Y `OrderExternalInvoicingApiTests` (Quotations) por tocar el puerto.
- [ ] Una revisión del bloque (lente `review-reliability`; el diff toca estado, worker y pruebas).

---

## Bloque 3 — Plazos por llamada en el cliente HTTP (Task 9, un implementador)

### Task 9: Plazo propio por llamada y presupuesto de leases

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoApiClient.cs`
- Modify: `.claude/rules/siigo.md` (el cliente ya no corta con `HttpClient.Timeout`: plazo por llamada)
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoApiClientTests.cs`; create `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoTimeoutBudgetTests.cs`

**Interfaces:**
- Consumes: `SiigoInvoice.SendLease` (Task 1), `SiigoInvoiceBatchWorker.Lease` (Task 7).
- Produces:
  - `internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30)` e `internal static readonly TimeSpan InvoiceCreateTimeout = TimeSpan.FromSeconds(30)` en `SiigoApiClient` (**30 s en expand**; contract lo sube a 120 s). Sin configuración.
  - `internal sealed record SiigoApiTimeouts(TimeSpan Default, TimeSpan InvoiceCreate)` con `public static SiigoApiTimeouts Production => new(DefaultTimeout, InvoiceCreateTimeout)`; parámetro opcional al final del constructor, `SiigoApiTimeouts? timeouts = null` (como `failures` y `metrics`), para que la prueba inyecte plazos cortos.
  - `ConfigureClient` pone `client.Timeout = Timeout.InfiniteTimeSpan`. `CallAsync`/`SendAsync` reciben el plazo; `SendAsync` usa `CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` + `CancelAfter(plazo)` para el envío **y** la lectura del cuerpo. `POST v1/invoices` usa `InvoiceCreate`; todo lo demás (incluido el `/auth` previo a la factura) usa `Default`. El `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` sigue mirando el token **de quien llama** y sigue devolviendo `timeout`.
  - `SiigoHttpStub.Hang(string path)`: el stub espera `Task.Delay(Timeout.Infinite, cancellationToken)` en esa ruta.
- [ ] **Step 1: Pruebas que fallan:**
  - `SiigoApiClientTests.TheInvoiceCreationUsesItsOwnDeadline`: cliente con `new SiigoApiTimeouts(Default: TimeSpan.FromMinutes(10), InvoiceCreate: TimeSpan.FromMilliseconds(50))`, `/auth` responde y `Hang("/v1/invoices")` → `Transient("timeout")`, y el `CancellationToken` de la prueba no se canceló.
  - `SiigoApiClientTests.AReadUsesTheDefaultDeadline`: `new SiigoApiTimeouts(TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(10))`, `Hang("/v1/document-types")` → `ServiceUnavailableException` con `siigo.unavailable`.
  - `SiigoApiClientTests.TheClientItselfNeverTimesOut`: `ConfigureClient(new HttpClient())` deja `Timeout == Timeout.InfiniteTimeSpan`.
  - `SiigoApiClientTests.ACallerCancellationIsNotATimeout`: token de la prueba cancelado durante `Hang` → lanza `OperationCanceledException`, no devuelve `timeout`.
  - `SiigoTimeoutBudgetTests.EveryCallOfAnOrderFitsInsideTheSendLeaseAndTheSendLeaseInsideTheWorkerLease`: `DefaultTimeout * 4 + InvoiceCreateTimeout * 2 < SiigoInvoice.SendLease` y `SiigoInvoice.SendLease < SiigoInvoiceBatchWorker.Lease`; `TheProductionDeadlinesAreThirtySecondsInExpand`: `Production == (30 s, 30 s)`.
- [ ] **Step 2: RED** — `dotnet build tests/Modules/Siigo/Modules.Siigo.UnitTests`. Esperado: no compila (`SiigoApiTimeouts`, `Hang`).
- [ ] **Step 3: Implementar** lo de «Interfaces» y la viñeta de `siigo.md`.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests` completo (todas las de `SiigoApiClientTests` siguen verdes, incluida `ATimeoutIsTransient`). Integración en primer plano: `$env:Seed__Enabled = "false"` y `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoNoLeakTests|FullyQualifiedName~SiigoCallFailureLogTests"` (el cliente real con el doble).
- [ ] **Step 5: Commit** — `git commit -m "refactor(siigo): give each Siigo call its own deadline"`.

### Cierre del bloque 3

- [ ] `dotnet test tests/ArchitectureTests/ArchitectureTests` filtrado por `Siigo|ShutdownBudget`.
- [ ] Una revisión del bloque (lente `review-resilience`: plazos, cancelación y apagado).

---

## Revisión final y merge a `develop`

- [ ] **Revisión fuerte, una sola vez:** modelo más capaz sobre toda la rama contra el spec y este plan. Debe mirar en particular las decisiones D-1 a D-4 y el «Review Focus».
- [ ] **Antes de mergear:** `.\scripts\test-feature.ps1` en verde, **leyendo sus avisos** (dice qué clases de integración no corrió). Como el cambio toca `Bootstrapper` y Quotations, corre además, de a una: `OrderExternalInvoicingApiTests` y las clases de Siigo que el reporte no cubra.
- [ ] **Merge a `develop`** (nunca a `main`). En `develop`, en el mismo merge o en un commit `docs:` inmediato, actualiza `docs/estado.md` (fila de Siigo y Registro) con la evidencia (commits y clases).
- [ ] **Antes del despliegue (owner):** en producción, verifica que no haya ítems abiertos duplicados; si los hay, la migración se detiene a propósito y el owner decide cuál cerrar:
  ```sql
  SELECT tenant_id, order_id, count(*) FROM siigo.invoice_batch_items
  WHERE status IN ('Pending', 'Sending') GROUP BY tenant_id, order_id HAVING count(*) > 1;
  ```
- [ ] **Rollout:** backend expand a producción y el frontend **el mismo día** (spec, «Rollout»): el frontend publicado pinta una `Pending` como «Enviando…» y no conoce `ToConfirm`. El frontend no puede ir antes: el backend viejo ignora `payments` en silencio.

---

## Etapa contract — no se entrega antes del frontend que emite por tandas

Se empieza sólo cuando el frontend con tandas (también desde el pedido, con `payments`) ya está en producción. Rama propia desde `develop` (`feature/siigo-quitar-emision-sincrona`), en su worktree, con las mismas reglas de arriba.

### Task 10: Quitar la emisión síncrona y subir la creación a 120 s

**Files:**
- Modify: `src/Modules/Siigo/Modules.Siigo.Api/SiigoEndpoints.cs` (quitar `invoice.MapPost("/", IssueInvoiceAsync)`, `IssueInvoiceAsync` y `SiigoIssueInvoiceRequest`; `SiigoPaymentChoiceRequest` se queda: lo usa la tanda)
- Modify: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoApiClient.cs` (`InvoiceCreateTimeout = TimeSpan.FromSeconds(120)`)
- Modify: `tests/Modules/Siigo/Modules.Siigo.IntegrationTests/SiigoInvoicingHarness.cs` (helper nuevo), `SiigoInvoiceApiTests.cs`, `SiigoNoLeakTests.cs`, `SiigoInvoiceWhatsAppApiTests.cs`
- Modify: `.claude/rules/siigo.md`
- Test: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoTimeoutBudgetTests.cs`, `SiigoInvoiceApiTests.cs`

**Interfaces:**
- Produces: `SiigoInvoicingHarness.IssueThroughBatchAsync(InvoicingWorld world, object? payments = null)`: crea una tanda de un pedido, corre `DrainAsync` y devuelve el `GET …/siigo-invoice` (o el ítem de la tanda cuando el pedido salió `Skipped`/`Failed`).

> **Brecha del spec (ver informe):** el spec dice «contract, sin migración: quitar la ruta», pero unas 57 llamadas de pruebas de integración (`SiigoInvoiceApiTests`, `SiigoNoLeakTests`, `SiigoInvoiceWhatsAppApiTests`) facturan por esa ruta. Las que esperan un 422/409 síncrono del `POST` (`not_ready`, `connection_unavailable`, asesor sin vínculo, 403) pasan a verificar el resultado del ítem de la tanda; el 403 por permiso o módulo ya lo cubre `WithoutThePermissionOrWithTheModuleOffTheBatchRoutesAre403`.

- [ ] **Step 1: Pruebas que fallan:** `TheSynchronousIssueRouteNoLongerExists` (`POST …/siigo-invoice` → 404 o 405) y `TheProductionDeadlinesAreThirtyAndOneHundredTwentySeconds` (reemplaza a la de expand: `Production == (30 s, 120 s)`); la relación de `SiigoTimeoutBudgetTests` debe seguir cumpliéndose (30·4 + 120·2 = 360 s < 8 min < 10 min).
- [ ] **Step 2: RED** — corrida filtrada de esas dos (unitaria e integración). Pega la salida.
- [ ] **Step 3: Implementar:** quitar la ruta, subir la constante, agregar el helper y migrar los llamadores de las tres clases.
- [ ] **Step 4: GREEN** — unitarias de Siigo completas; integración, de a una clase: `SiigoInvoiceApiTests`, `SiigoNoLeakTests`, `SiigoInvoiceWhatsAppApiTests`, `SiigoInvoiceBatchApiTests`.
- [ ] **Step 5: Commit** — `git commit -m "feat(siigo)!: remove the synchronous invoice route and wait up to 120 s for Siigo"`.
- [ ] **Cierre:** una revisión (lente `review-risk`: se quita un contrato), `.\scripts\test-feature.ps1` en verde, merge a `develop` y `docs/estado.md` en `develop`.
