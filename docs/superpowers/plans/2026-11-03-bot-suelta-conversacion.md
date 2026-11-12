# El bot suelta la conversación cuando una persona la toma Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cuando una persona resuelve, toma, transfiere, responde sin asignar o devuelve al asistente, la corrida viva del bot termina `Interrupted` en segundos, y borrar un chatbot sólo da 409 si el bot de verdad atiende.

**Architecture:** Mensajería encola una fila `kind = 'ReleaseRun'` en `messaging.automation_inbox` desde el handler, en el mismo guardado del cambio (D-3, D-6). El worker de Automations la reclama en orden FIFO por conversación y `RunProcessor` la atiende en una rama propia, bajo el candado, que sólo cierra (D-5). El borrado lista las corridas vivas y aplica `BotAttendance.IsAttending` con la conversación (D-7). Bootstrapper mapea el tipo entre los dos módulos.

**Tech Stack:** .NET 10, EF Core + Npgsql (SQL crudo en la cola), xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-03-bot-suelta-conversacion-design.md`

## Global Constraints

- TDD estricto: RED antes que GREEN, con la salida literal de los dos en el reporte de cada tarea.
- Por tarea se corren sólo las clases tocadas, con `--filter`; la suite completa corre una sola vez, en la última tarea.
- Antes de cualquier prueba de integración, en PowerShell: `$env:Seed__Enabled = "false"`. Los comandos van en PowerShell, sin `&&`.
- `Api.exe` o `dotnet Api.dll` corriendo bloquea el build: detenerlo primero.
- Migración: `dotnet ef migrations add AddAutomationInboxKind --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`.
- Columna: `kind varchar(16) NOT NULL DEFAULT 'Engine'`, `CHECK (kind IN ('Engine','ReleaseRun'))`. Las filas existentes quedan `Engine`.
- D-1: la corrida termina `Interrupted`, fila `Closed` de `run_units` con `RunCloseReasons.HumanTookOver` (`human_took_over`), outcome de la fila `interrupted`. Sin auditoría.
- D-5: cierra sólo si `run.StartedAt < item.CreatedAt` **y** (`conversation is null` o `BotAttendance.HumanTookOver(conversation, run)`). Si no, `skipped` y nada se guarda. Nunca llega a `DecideAsync`, nunca arranca ni envía, ignora el módulo apagado, y una excepción sube (nunca `FailManualStartAsync`).
- D-6: encola el **handler**, nunca el agregado, sólo si el asistente no estaba en `None` **antes** del cambio y hubo cambio (`changes.Count > 0`). Encolan: resolver, tomar, transferir, autoasignación (`TryAutoAssignAsync`) y devolver. **No** encolan: pausar (D-6a), continuar, iniciar, liberar, reabrir, leer, cliente, bloquear, el bot ni el eco.
- D-7: 409 `automations.chatbot.has_live_runs` sólo si alguna corrida viva atiende (`BotAttendance.IsAttending`); corte en la primera. Conversación inexistente no atiende. Mensaje sin cambio: «El chatbot tiene conversaciones en curso: pausa el chatbot y espera a que terminen.».
- D-9: sin cambio de contrato HTTP. Sin frontend.
- Todo lector de `messaging.automation_inbox` usa `LEFT JOIN` a `messages` y filtra por `kind`; «sin mensaje es manual» sólo vale dentro de `kind = 'Engine'`.
- Commits convencionales, en inglés, **sin `Co-Authored-By` ni ninguna atribución de IA**. Rama `fix/bot-suelta-conversacion`; nunca a `main`. `docs/estado.md` no se toca (las ramas de feature no lo editan).
- Textos y comentarios en español colombiano, tuteando; identificadores en inglés.

## Review Focus

1. **Una señal detrás de un entrante que arrancó una corrida en el mismo instante de la acción.** «Devolver» encola la señal con `CreatedAt = now`; si un entrante anterior sigue pendiente, se procesa primero, interrumpe la corrida vieja y arranca otra con `StartedAt` en el piso `AutomationResumedAt == now`. La señal tiene que dejarla viva: el corte es `>=`, no `>`. Prueba fijadora en Task 2: `AReleaseRowDoesNotCloseARunThatStartedAtItsInstant` (`run.StartedAt == item.CreatedAt` → `skipped`).
2. **Lectores que todavía deciden «manual» por «sin mensaje».** `HasPendingManualStartAsync` (el veredicto `StartPending`), el `catch` de `ProcessAsync` y los helpers de prueba `ManualOutcomesAsync` / `ManualRowsEverAsync` verían una señal como arranque manual: «Iniciar» se ofrecería como «arrancando…» o una señal que falla traspasaría la conversación. Fijadoras: Task 1 `PendingManualStartIgnoresReleaseRows`; Task 2 `AReleaseRowThatThrowsIsRetriedNotHandedOff`; Task 3 `APendingReleaseRowIsNotAPendingManualStart` (resumen después de tomar y devolver).
3. **El bot resolviendo o traspasando no puede encolar (anti-objetivo 4).** `Conversation.Resolve` también lo llama `Bootstrapper/AutomationConversations.cs`; si alguien mueve el encolado al agregado, cada flujo que termina deja una señal. Fijadora en Task 3: `TheBotResolvingOrHandingOffQueuesNoReleaseRow`.
4. **Autoasignación sobre una pausa `Manual` y sobre `None`.** El `UPDATE` de `TryAutoAssignAsync` no cambia el estado si ya está `Paused`; la señal tiene que salir igual (un asesor la tuvo, D-6a), y no salir con `None`. Fijadoras en Task 3: `AutoAssigningAManuallyPausedConversationQueuesTheSignal` y `AutoAssigningWithoutAssistantQueuesNothing`.
5. **Relojes distintos entre el pod de la API (`item.CreatedAt`) y el del worker (`run.StartedAt`).** Si el worker va adelantado, una corrida anterior a la acción puede quedar con `StartedAt >= CreatedAt` y la señal la salta. Es degradación aceptada, no bug, siempre que el camino perezoso siga intacto. Fijadora en Task 2: `ASkippedReleaseRowLeavesTheLazyPathIntact` (señal `skipped`, luego un entrante con la conversación tomada la interrumpe como hoy).

---

### Task 1: Columna `kind` y la cola de Mensajería

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IAutomationInbox.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInboxRecord.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInbox.cs` (`ClaimAsync` 31-46, `EnqueueManualStart` 120-131, `HasPendingManualStartAsync` 134-143)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` (`ConfigureAutomationInbox`, 535-552)
- Create: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/Migrations/<timestamp>_AddAutomationInboxKind.cs` (+ Designer y snapshot, generados)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AutomationInboxTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class AutomationInboxKinds
  {
      public const string Engine = "Engine";
      public const string ReleaseRun = "ReleaseRun";
  }
  public sealed record AutomationInboxItem(
      Guid MessageId, Guid TenantId, Guid ConnectionId, Guid ConversationId, bool Opened, DateTimeOffset CreatedAt, int Attempts,
      string Kind = AutomationInboxKinds.Engine);
  // IAutomationInbox, hermano de EnqueueManualStart: sólo agrega al contexto, id propio (Guid.CreateVersion7()), opened = false.
  void EnqueueReleaseRun(Guid tenantId, Guid connectionId, Guid conversationId, DateTimeOffset now);
  // AutomationInbox, para que ConversationRepository (Task 3) arme la misma fila sin duplicar el literal.
  internal static AutomationInboxRecord NewReleaseRun(Guid tenantId, Guid connectionId, Guid conversationId, DateTimeOffset now);
  ```
  `AutomationInboxRecord.Kind` (`string`, inicializado en `AutomationInboxKinds.Engine`). En el contexto: `HasColumnName("kind").HasMaxLength(16).HasDefaultValue(AutomationInboxKinds.Engine)` y `ToTable("automation_inbox", Schema, t => t.HasCheckConstraint("CK_automation_inbox_kind", "kind IN ('Engine','ReleaseRun')"))`.

- [ ] **Step 1: Escribir las pruebas que fallan** (en `AutomationInboxTests`, con la forma de `AManualStartRowIsClaimedInOrderAndIsNoInbound`)

  ```csharp
  [Fact] public async Task ClaimCarriesTheKind()
  // un entrante, una manual y una ReleaseRun en tres conversaciones distintas → tres reclamos
  Assert.Equal(["Engine", "Engine", "ReleaseRun"], claimed.OrderBy(i => i.CreatedAt).Select(i => i.Kind));

  [Fact] public async Task PendingManualStartIgnoresReleaseRows()
  // sólo una ReleaseRun pendiente en la conversación
  Assert.False(await inbox.HasPendingManualStartAsync(tenantId, conversationId, Ct));

  [Fact] public async Task AReleaseRowHoldsBackALaterInboundOfItsConversation()
  // ReleaseRun en t, entrante en t + 1 s de la misma conversación
  var first = Assert.Single(await inbox.ClaimAsync(now, 10, Ct));
  Assert.Equal(AutomationInboxKinds.ReleaseRun, first.Kind);
  // cerrarla con CompleteAsync(..., "skipped") → el siguiente reclamo devuelve el entrante

  [Fact] public async Task OldRowsAreEngineAndTheCheckRejectsAnUnknownKind()
  Assert.Equal("Engine", await ScalarAsync<string>(cs, "SELECT kind FROM messaging.automation_inbox WHERE message_id = @id", ("id", manualId)));
  // INSERT con kind = 'Other' → PostgresException SqlState "23514"
  ```
- [ ] **Step 2: Correr y ver el fallo**

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~AutomationInboxTests"
  ```
  Expected: error de compilación (`AutomationInboxKinds`, `Kind` y `EnqueueReleaseRun` no existen).
- [ ] **Step 3: Implementar**

  Contexto, record y migración (comando de Global Constraints; revisar que el `Up` sea un `AddColumn` con default y el CHECK, y que no toque nada más). `ClaimAsync` suma `inbox.kind AS "Kind"` al `RETURNING`. `HasPendingManualStartAsync` suma `AND inbox.kind = 'Engine'`. `EnqueueManualStart` deja `Kind = Engine` explícito. `EnqueueReleaseRun` = `dbContext.AutomationInbox.Add(NewReleaseRun(...))`. Actualizar los `<summary>` de `IAutomationInbox` (el «Ojo» del encabezado y `HasPendingManualStartAsync`): «sin mensaje es manual» sólo dentro de `Engine`.
- [ ] **Step 4: Correr y pasar** el mismo comando. Expected: PASS, también los casos previos de la clase.
- [ ] **Step 5: Commit** `feat(messaging): add a kind to the automation inbox for release signals`

### Task 2: La rama `ReleaseRun` de `RunProcessor` y el mapeo de Bootstrapper

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`InboundQueueItem`, 232-234)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs` (`catch` 75-88, `ProcessUnitAsync` 146-193)
- Modify: `src/Bootstrapper/AutomationInboundQueue.cs` (`ClaimAsync`, 13-16)
- Create: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.ReleaseRun.cs`
- Modify: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs` (dobles: `Bed.Unit`, `Conversations.Missing`, `Conversations.ReadFailure`)
- Modify: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorRecordingTests.cs`
- Modify: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.ManualStart.cs`
- Modify: `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs`

**Interfaces:**
- Consumes: `AutomationInboxItem.Kind`, `AutomationInboxKinds` (Task 1).
- Produces:
  ```csharp
  public sealed record InboundQueueItem(
      Guid MessageId, Guid TenantId, Guid ConnectionId, Guid ConversationId, bool Opened, DateTimeOffset CreatedAt, int Attempts,
      bool ReleaseRun = false);
  // RunProcessor, privado
  private async Task<string> ReleaseAsync(
      InboundQueueItem item, AutomationConversationState? conversation, ConversationRun? run, CancellationToken cancellationToken);
  ```
  Orden en `ProcessUnitAsync`: candado (`Locked`) → `FindLiveForUpdateAsync` → P2-R24 (`Duplicate`) → `conversations.GetAsync` → **`if (item.ReleaseRun) return await ReleaseAsync(...)`** → el resto, sin cambios. `ReleaseAsync` cierra con `clock.UtcNow` (sin el piso de `AutomationResumedAt`, que es para arrancar). En el `catch`: `if (item.ReleaseRun || await inbox.ReadAsync(...) is not null) throw;`. Bootstrapper: `ReleaseRun: item.Kind == AutomationInboxKinds.ReleaseRun`.

- [ ] **Step 1: Escribir las pruebas que fallan**

  Dobles: `Bed` guarda su `NoUnitOfWork` en `public NoUnitOfWork Unit` (como `ManualBed`); `Conversations` gana `public bool Missing { get; set; }` (`GetAsync` → `null`) y `public Exception? ReadFailure { get; set; }` (`GetAsync` lanza). Helper:
  ```csharp
  private static InboundQueueItem ReleaseItem(DateTimeOffset createdAt) =>
      new(Guid.CreateVersion7(), TenantId, ConnectionId, ConversationId, Opened: false, createdAt, Attempts: 1, ReleaseRun: true);
  // y en cada prueba: bed.Inbox.NotInbound.Add(item.MessageId) — la señal no tiene mensaje, como en producción
  ```
  En `RunProcessorTests.ReleaseRun.cs` (corrida `WaitingRun(created.AddMinutes(-1))` salvo que se diga otra cosa, `created = DateTimeOffset.UtcNow`):
  - `[Theory] AReleaseRowClosesTheLiveRunWhenAPersonTookOver(string state)` con `"resolved"` (`Resolved`, `None`), `"took"` / `"transferred"` / `"replied"` (`Open`, asesor, `Paused`, `AdvisorTook` / `AdvisorTransferred` / `AdvisorReplied`), `"resumed"` (`Open`, sin asesor, `Active`, `AutomationResumedAt = run.StartedAt.AddSeconds(30)`):
    ```csharp
    Assert.Equal(RunOutcomes.Interrupted, outcome);
    Assert.Equal([$"complete {item.MessageId} 1 interrupted"], bed.Inbox.Calls);
    Assert.Equal((RunStatus.Interrupted, (Guid?)item.MessageId), (run.Status, run.LastInboundMessageId));
    Assert.Equal(1, bed.Unit.Saves);
    Assert.Empty(bed.Messenger.Sent); Assert.Empty(bed.Audit.Actions); Assert.Empty(bed.Conversations.Handoffs);
    Assert.Equal((0, 0), (bed.Conversations.Started, bed.Conversations.Ended));
    Assert.Single(bed.Runs.Added);
    ```
  - `AReleaseRowClosesTheRunOfADeletedConversation` (`Missing = true`) → `interrupted`.
  - `AReleaseRowDoesNotCloseARunThatStartedAtItsInstant` (Review Focus 1): `WaitingRun(created)`, conversación resuelta → `skipped`, `run.Status == Waiting`, `bed.Unit.Saves == 0`, `Calls == [$"complete {id} 1 skipped"]`.
  - `AReleaseRowDoesNotCloseARunThatStartedAfterIt`: `WaitingRun(created.AddSeconds(1))` → igual que la anterior.
  - `AReleaseRowDoesNotCloseWhenThePersonUndidIt`: `Open`, sin asesor, `Active`, sin sello → `skipped`, `Waiting`, `Saves == 0`.
  - `AReleaseRowNeverStartsARun`: `Open`, sin asesor, `Active`, sin corrida, `trigger: NewConversation` → `skipped`, `Runs.Added` vacío, `Messenger.Sent` vacío, `Started == 0`.
  - `AReleaseRowIgnoresTheDisabledModule`: `moduleEnabled: false`, `Active` con `AutomationResumedAt` posterior → `interrupted`, `Ended == 0` (`StandDownAsync` no corrió).
  - `AReleaseRowWithoutLiveRunIsSkipped` → `skipped`, `Saves == 0`.
  - `AReleaseRowAlreadyAppliedIsDuplicate`: corrida `Interrupted` con `MarkInbound(item.MessageId)` como última → `duplicate`, `Conversations.Reads == 0`.
  - `AReleaseRowThatThrowsIsRetriedNotHandedOff`: `ReadFailure = new InvalidOperationException("boom")` → `Assert.ThrowsAsync<InvalidOperationException>`, `Handoffs` vacío, `Inbox.Calls` vacío.
  - `ALockedConversationReschedulesTheReleaseRow`: `lockBusy: true` → `"locked"`, `Calls == [$"retry {id} 1 00:00:05"]`, `Waiting`.
  - `ASkippedReleaseRowLeavesTheLazyPathIntact` (Review Focus 5): señal con `WaitingRun(created.AddSeconds(1))` → `skipped`; luego `ProcessAsync(Item with { MessageId = Guid.CreateVersion7(), Opened = false })` con la conversación `Paused`/`AdvisorTook` → `skipped` y `run.Status == Interrupted` (D-M15 de hoy).

  En `RunProcessorRecordingTests`: `AReleaseRowWritesTheClosedRowWithHumanTookOver`:
  ```csharp
  Assert.Equal(
      (TenantId, 2, RunUnitTriggers.Closed, (Guid?)item.MessageId, RunOutcomes.Interrupted, "[]", (string?)"Interrupted", (string?)RunCloseReasons.HumanTookOver),
      (closed.TenantId, closed.BaseStep, closed.Trigger, closed.InboundMessageId, closed.Outcome, closed.StepsJson, closed.ClosedStatus, closed.ClosedReason));
  Assert.Single(bed.Units.Saved);
  ```
  En `RunProcessorTests.ManualStart.cs`: `AManualRowStillStartsTheFlow` — fila `ReleaseRun: false` en `NotInbound`, conversación `Active` sin corrida → `started` y un envío (regresión de D-4).

  En `AutomationAdaptersTests`: `TheInboundQueueMapsTheKind` — doble de `IAutomationInbox` cuyo `ClaimAsync` devuelve una fila `Engine` y una `ReleaseRun`:
  ```csharp
  Assert.Equal([false, true], items.Select(i => i.ReleaseRun));
  ```
- [ ] **Step 2: Correr y ver el fallo**

  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~RunProcessorTests|FullyQualifiedName~RunProcessorRecordingTests"
  dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AutomationAdaptersTests"
  ```
  Expected: error de compilación (`ReleaseRun` no es parámetro de `InboundQueueItem`).
- [ ] **Step 3: Implementar** lo de **Interfaces**. El `<summary>` de `InboundQueueItem` dice qué es `ReleaseRun` (spec 2026-11-03 D-4). El comentario de la rama cita D-5 y por qué relee `HumanTookOver` en vez de confiar en la señal.
- [ ] **Step 4: Correr y pasar** los dos comandos. Expected: PASS, incluidas las pruebas previas de las tres clases.
- [ ] **Step 5: Commit** `feat(automations): close a live run when a release signal arrives`

### Task 3: Los handlers de Mensajería encolan la señal

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationLifecycle.cs` (`ResolveConversationHandler`, 133-191)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationAssignment.cs` (`TakeConversationHandler` 113-123, `TransferConversationHandler` 125-145)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationAutomation.cs` (`ResumeAutomationHandler`, 84-95). `PauseAutomationHandler`, `ContinueAutomationHandler`, `StartAutomationHandler` y `ReleaseConversationHandler` **no cambian**.
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ReleaseRunSignal.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/ConversationRepository.cs` (`TryAutoAssignAsync` 155-198, `AutoAssignRow` 219)
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationAutomationApiTests.cs`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.cs`, `EngineEndToEndTests.Lifecycle.cs`, `EngineEndToEndTests.ManualStart.cs`, `EngineEndToEndTests.Recovery.cs`, `EngineEndToEndTests.Continue.cs` (barrido de expectativas)

**Interfaces:**
- Consumes: `IAutomationInbox.EnqueueReleaseRun`, `AutomationInbox.NewReleaseRun` (Task 1).
- Produces:
  ```csharp
  // Messaging.Application. Una sola regla para los cuatro handlers (D-6): asistente distinto de None antes del cambio y algún cambio.
  public static class ReleaseRunSignal
  {
      public static void EnqueueIfAssisted(
          this IAutomationInbox inbox, Conversation conversation, ConversationAutomationStatus before, int changes, DateTimeOffset now);
  }
  ```
  Cada handler lee `var before = conversation.AutomationStatus;` **antes** de llamar al agregado (`Resolve` deja `None`, `Conversation.cs:285`), y llama `inbox.EnqueueIfAssisted(...)` antes de `steps.CommitAsync` / `unitOfWork.SaveChangesAsync`. Resolver pasa `changes: 1` (`EnsureCanResolve` ya lanzó si no había). `TryAutoAssignAsync`: `RETURNING` suma `automation_status <> 'None' AS "HadAssistant"` (el `UPDATE` sólo cambia `Active` por `Paused`, así que el valor nuevo dice lo mismo que el viejo); `AutoAssignRow(Guid ConnectionId, bool PausedNow, bool HadAssistant)`; si `HadAssistant`, `dbContext.AutomationInbox.Add(AutomationInbox.NewReleaseRun(tenantId, taken[0].ConnectionId, conversationId, at))` antes del `SaveChangesAsync` de esa transacción. `IConversationRepository` no cambia.

- [ ] **Step 1: Escribir las pruebas que fallan**

  En `ConversationAutomationApiTests` (el `ArrangeAsync` deja la conversación `Active`), con
  ```csharp
  private static Task<long> ReleaseRowsAsync(Fixture f) =>
      CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.automation_inbox WHERE kind = 'ReleaseRun' AND conversation_id = @id", ("id", f.ConversationId));
  ```
  - `ResolvingAnAssistedConversationQueuesTheSignal` → `1L`, y `created_at` de la fila igual al `occurred_at` del evento `Resolved`.
  - `TakingQueuesTheSignalAndTakingAgainDoesNot`: `take`, `take` otra vez (D-A2, 200 sin cambio) → `1L`.
  - `TransferringQueuesTheSignal`: `transfer` a `f.Beatriz` (`new { memberId = f.Beatriz }`) → `1L`.
  - `ResumingQueuesTheSignal`: `take`, `automation/resume` → `2L`.
  - `PausingDoesNotQueueTheReleaseSignal`: `automation/pause` → `0L` (D-6a).
  - `TakingAManuallyPausedConversationQueuesTheSignal`: `automation/pause`, `take` → `1L`.
  - `ActionsWithoutAssistantQueueNothing`: conversación a `None` por SQL (como `PausingTwiceIsAlreadyPausedAndANoneConversationIsNotAvailable`), `take`, `release`, `transfer`, `resolve` → `0L`.
  - `AutoAssigningQueuesTheSignalInItsTransaction`: copia de `SendingInAnActiveConversationAutoAssignsAndPausesIt` → `1L`.
  - `AutoAssigningAManuallyPausedConversationQueuesTheSignal` (Review Focus 4): `automation/pause`, enviar texto → `1L`.
  - `AutoAssigningWithoutAssistantQueuesNothing` (Review Focus 4): conversación a `None`, enviar texto → `0L`.

  En `EngineEndToEndTests.Lifecycle.cs`: `TheBotResolvingOrHandingOffQueuesNoReleaseRow` (Review Focus 3) — el flujo de `AResolvedConversationReopenedByTheCustomerStartsAgain` hasta `Resolved|None`, y el de `AGraphRejectionFailsTheRunAndHandsOff` → `0L` filas `kind = 'ReleaseRun'` en los dos.
  En `EngineEndToEndTests.ManualStart.cs`: `APendingReleaseRowIsNotAPendingManualStart` (Review Focus 2) — `take`, `automation/resume`, **sin** drenar el worker, `automation/start` → 200 (no el rechazo de `StartPending`) y una sola fila `kind = 'Engine'` sin mensaje.
- [ ] **Step 2: Correr y ver el fallo**

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationAutomationApiTests"
  ```
  Expected: FAIL en las que esperan `1L`/`2L` (`Assert.Equal() Failure: Expected 1, Actual 0`); las que esperan `0L` pasan ya, y es correcto: amarran lo que no debe cambiar.
- [ ] **Step 3: Implementar** lo de **Interfaces**. `ResolveConversationHandler` y `ResumeAutomationHandler` (y los de tomar y transferir) ganan `IAutomationInbox` por constructor; DI ya lo registra.
- [ ] **Step 4: Barrer las expectativas de Automations que cambian con la señal**

  Una acción humana sobre una conversación con asistente deja ahora una fila más en `automation_inbox`. Corregir, sin cambiar lo que cada prueba prueba:
  - `EngineEndToEndTests.OutcomesAsync` y todo `string_agg` sobre `automation_inbox`: filtrar `WHERE kind = 'Engine'` en el helper, y asertar la señal aparte donde importe. Afectadas conocidas: `AnAssignedConversationIsSkipped`, `AReopenedConversationInterruptsTheStaleRunAndStartsANewOne` (la corrida vieja la cierra ahora la señal; el `Interrupted,Waiting` no cambia), `TheSweepDoesNotUndoAHumanResume`.
  - `ManualOutcomesAsync` y `ManualRowsEverAsync` (`EngineEndToEndTests.ManualStart.cs:318-332`): sumar `AND inbox.kind = 'Engine'`.
  - Buscar el resto con `rg -n 'ConversationActionAsync\(f, "(take|resolve|transfer|automation/resume)"' tests/Modules/Automations` y `rg -n "automation_inbox" tests/Modules/Automations` (incluye `Recovery`, `Continue` y `ClasicFlowEndToEndTests`).

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~ConversationAutomationApiTests"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~EngineEndToEndTests"
  ```
  Expected: PASS las dos.
- [ ] **Step 5: Commit** `feat(messaging): queue a release signal when a person takes over the conversation`

### Task 4: Borrar sólo bloquea si el bot atiende

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`IConversationRunQueries`, 108-117)
- Modify: `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/ConversationRunQueries.cs` (38-41)
- Modify: `src/Modules/Automations/Modules.Automations.Application/Chatbots.cs` (`DeleteChatbotHandler`, 795-827)
- Create: `tests/Modules/Automations/Modules.Automations.UnitTests/ChatbotLiveRunsTests.cs`
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/AutomationsPersistenceTests.cs` (216-218)
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/ChatbotsApiTests.cs` (`DeleteWithLiveRunsIs409`, 1406-1430)
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.Lifecycle.cs`

**Interfaces:**
- Produces:
  ```csharp
  // IConversationRunQueries: reemplaza a HasLiveRunsAsync. Running o Waiting del chatbot, AsNoTracking, por started_at.
  Task<IReadOnlyList<ConversationRun>> ListLiveAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken);

  // Chatbots.cs. D-7: corta en la primera que atiende; una conversación inexistente no atiende.
  internal static class ChatbotLiveRuns
  {
      public static async Task<bool> AnyAttendingAsync(
          IConversationRunQueries runs, IAutomationConversations conversations, Guid tenantId, Guid chatbotId, CancellationToken cancellationToken);
  }
  ```
  `DeleteChatbotHandler` gana `IAutomationConversations conversations` y llama `ChatbotLiveRuns.AnyAttendingAsync`; el 409 y su mensaje no cambian. Su `<summary>` dice la regla nueva (spec 2026-11-03 D-7).

- [ ] **Step 1: Escribir las pruebas que fallan**

  `ChatbotLiveRunsTests` con dos dobles chicos (`IConversationRunQueries` cuyo `TodayAsync` lanza `NotSupportedException`; `IAutomationConversations` con un diccionario por conversación, `Reads`, y el resto `NotSupportedException`):
  - `[Theory] AWaitingRunNobodyAttendsDoesNotBlock(string state)` con `"resolved"`, `"taken"` (`Open`, asesor, `Paused`, `AdvisorTook`), `"pausedManual"` (`Open`, sin asesor, `Paused`, `Manual`), `"missing"` → `Assert.False(...)`.
  - `AWaitingRunTheBotAttendsBlocks`: `Open`, sin asesor, `Active` → `Assert.True(...)`.
  - `ARunningRunWithoutAPersonBlocks`: corrida `RunStatus.Running`, `Active` → `Assert.True(...)`.
  - `TheFirstAttendingRunStopsTheReads`: dos corridas que atienden → `Assert.True(...)` y `Assert.Equal(1, conversations.Reads)`.
  - `NoLiveRunsReadsNothing` → `Assert.False(...)` y `Reads == 0`.

  `AutomationsPersistenceTests`: el par de `HasLiveRunsAsync` pasa a
  ```csharp
  Assert.Empty(await queries.ListLiveAsync(tenantId, chatbotId, Ct));      // sólo Completed y HandedOff
  // INSERT de una Waiting
  Assert.Equal(RunStatus.Waiting, Assert.Single(await queries.ListLiveAsync(tenantId, chatbotId, Ct)).Status);
  ```
  `ChatbotsApiTests.DeleteWithLiveRunsIs409` se renombra `DeleteWithALiveRunOfAMissingConversationIs204`: misma siembra (la conversación no existe) → `NoContent`, `0L` chatbots con ese id.
  `EngineEndToEndTests.Lifecycle.cs`, con un helper `DeleteChatbotAsync(Fixture f)` (DELETE con `If-Match` de la versión que da el editor, como `ChatbotActionAsync`):
  - `DeletingWhileTheBotAttendsIs409`: `InboundAsync(f, "w1", "Hola")`, drenar → corrida `Waiting`; `pause` del chatbot; delete → `Conflict`, `automations.chatbot.has_live_runs`.
  - `DeletingAfterAnAdvisorTookItIs204`: igual, `take` sin drenar el worker → `NoContent` (la corrida sigue `Waiting`: D-7 no depende de la señal).
  - `DeletingWithAManualPauseIs204`: igual, `automation/pause` → `NoContent`, y la corrida sigue `Waiting` (D-6a).
- [ ] **Step 2: Correr y ver el fallo**

  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~ChatbotLiveRunsTests"
  ```
  Expected: error de compilación (`ChatbotLiveRuns` y `ListLiveAsync` no existen).
- [ ] **Step 3: Implementar** lo de **Interfaces**; borrar `HasLiveRunsAsync` del puerto y del adaptador.
- [ ] **Step 4: Correr y pasar**

  ```powershell
  dotnet test tests/Modules/Automations/Modules.Automations.UnitTests --filter "FullyQualifiedName~ChatbotLiveRunsTests"
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~AutomationsPersistenceTests|FullyQualifiedName~ChatbotsApiTests|FullyQualifiedName~EngineEndToEndTests"
  ```
  Expected: PASS.
- [ ] **Step 5: Commit** `fix(automations): block chatbot deletion only while the bot attends a conversation`

### Task 5: El incidente de punta a punta y la regresión de «Continuar»

**Files:**
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.Lifecycle.cs`
- Modify: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.Continue.cs`

**Interfaces:**
- Consumes: todo lo anterior. Sin código de producto nuevo: si una de estas pruebas falla, el arreglo va en la tarea dueña y se reabre su revisión.

- [ ] **Step 1: Escribir las pruebas**

  `AnAdvisorResolvingReleasesTheBotWithoutTheCustomerWriting` (Lifecycle): `InboundAsync(f, "w1", "Hola")`, drenar → `Waiting`; `resolve`; drenar:
  ```csharp
  Assert.Equal("Interrupted", await RunAsync(f, "status"));
  Assert.Equal("interrupted", await ScalarAsync<string>(f.ConnectionString, "SELECT outcome FROM messaging.automation_inbox WHERE kind = 'ReleaseRun'"));
  Assert.Equal(1L, await CountAsync(f.ConnectionString,
      "SELECT count(*) FROM automations.run_units u JOIN messaging.automation_inbox i ON i.message_id = u.inbound_message_id WHERE u.trigger = 'Closed' AND u.closed_reason = 'human_took_over' AND i.kind = 'ReleaseRun'"));
  Assert.Equal(sendsBefore, f.Meta.Sends().Count);
  // pause del chatbot, luego DeleteChatbotAsync(f) → NoContent
  ```
  `PausingKeepsTheRunWaitingAndContinueResumesThePendingStep` (Continue): `automation/pause` por la API, drenar → corrida `Waiting` y `0L` filas `ReleaseRun`; `ContinueAsync(f)` → 200 y `0L` filas `ReleaseRun`; el siguiente entrante avanza desde el paso pendiente (mismas aserciones del caso feliz de `Continue` que ya existe).
  `RunProcessorTests.Continue.cs` no cambia: pausar no encola (D-6a), así que el procesador nunca ve una señal en ese camino; lo que hay que amarrar es que no se encole, y eso es de integración.
- [ ] **Step 2: Correr**

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests --filter "FullyQualifiedName~EngineEndToEndTests"
  ```
  Expected: PASS. Para la evidencia RED, correr una vez la de Lifecycle con la línea `if (item.ReleaseRun) return ...` comentada en local: falla con `Assert.Equal() Failure: Expected "Interrupted", Actual "Waiting"`. Restaurar la línea antes de seguir.
- [ ] **Step 3: Commit** `test(automations): cover the stuck-run incident and the continue regression end to end`

### Task 6: Gotcha, suite completa y notas de despliegue

**Files:**
- Modify: `CLAUDE.md` (gotcha de la línea 551, «Una fila de `automation_inbox` sin mensaje es un arranque manual»)

- [ ] **Step 1: Reemplazar el gotcha** por el texto del spec §5, literal:

  > **Una fila de `automation_inbox` tiene tipo** (spec 2026-11-03). `ReleaseRun` es la señal de que una persona resolvió, tomó, transfirió o devolvió la conversación: `RunProcessor` sólo cierra la corrida viva (`Interrupted`, `human_took_over`) si arrancó antes de la fila y `HumanTookOver` sigue siendo verdadero, y nunca arranca ni avanza nada. Dentro de `Engine`, una fila sin mensaje sigue siendo un arranque manual: todo lector nuevo usa `LEFT JOIN` y filtra por `kind`. Una acción humana nueva sobre una conversación con asistente decide si encola la señal, en el handler y no en el agregado (el bot también resuelve).

  Conservar del gotcha viejo lo que sigue siendo cierto y el spec no repite: `run_units.inbound_message_id` y `conversation_runs.last_inbound_message_id` pueden apuntar a una fila sin mensaje (manual **o** señal), y el camino manual del motor es `ResumeAsync`.
- [ ] **Step 2: Suite completa, una vez** (detener `Api.exe` / `dotnet Api.dll` antes)

  ```powershell
  $env:Seed__Enabled = "false"
  dotnet build
  dotnet test
  ```
  Expected: verde. Las «Failed to connect» masivas son puertos agotados de Windows, no regresión: se reintenta sólo la clase afectada y se compara contra `develop`.
- [ ] **Step 3: Commit** `docs: describe the automation inbox kinds in CLAUDE.md`

#### Notas de despliegue (para el handoff, no son pasos de código)

- La migración `AddAutomationInboxKind` es aditiva y con default: el código viejo inserta y lee sin la columna. El backend va solo, sin frontend.
- Verificación en producción: filas `ReleaseRun` cerradas `interrupted` o `skipped` en `messaging.automation_inbox`; filas `Closed` con `human_took_over` en `automations.run_units` cuyo `inbound_message_id` es el id de la señal; el borrado del chatbot del tenant del incidente responde 204.
- **Rollback con cuidado.** El código viejo lee una `ReleaseRun` pendiente como arranque manual: con la conversación `Active` (tras «Devolver») arrancaría el flujo y mandaría el primer mensaje, y el resumen mostraría «arrancando…». Antes de revertir:

  ```sql
  UPDATE messaging.automation_inbox SET processed_at = now(), outcome = 'skipped'
   WHERE kind = 'ReleaseRun' AND processed_at IS NULL;
  ```

  La columna puede quedar: el código viejo no la lee.
- `docs/estado.md` se actualiza en `develop` al mergear, no en esta rama.
