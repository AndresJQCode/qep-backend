# Iniciar el bot desde el hilo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un asesor, desde el hilo, ponga al bot a mandar el primer mensaje del flujo sin esperar al cliente, y sepa al presionar si no se puede y por qué.

**Architecture:** El `POST …/automation/start` valida bajo el candado de la conversación, cierra la corrida muerta, pasa la conversación a `Active` y deja una fila en `automation_inbox`, todo antes de responder. El worker corre la fila por el camino que ya existe (`ProcessUnitAsync` → `ResumeAsync` → `StartRunAsync` → `EngineInput.Start`), con sus reintentos e idempotencia. La ruta vive en Messaging; cruza a Automations por un puerto con adaptador en Bootstrapper.

**Tech Stack:** .NET 10, EF Core + SQL crudo sobre PostgreSQL, xUnit, Testcontainers. React 19, TanStack Query, Vitest + Testing Library, bun.

**Spec:** `docs/superpowers/specs/2026-10-31-iniciar-bot-desde-el-hilo-design.md`. Depende de `docs/superpowers/specs/2026-10-26-cuando-toma-el-bot-la-conversacion-design.md`, ya en `develop`.

## Decisiones del builder (spec §7)

**1. La primera unidad corre en el worker; todo lo que D-1 a D-8 rechaza se decide en el request.** Una unidad no tiene tope útil: 25 reentradas × (10 s de HTTP + 20 s de espera de adjunto) son 750 s más 15 s por envío, contra los ~100 s del 524 de Cloudflare y el lease de 30 s. En el request, además, una respuesta del cliente reclamada mientras la unidad corre gastaría sus 5 intentos en `Locked` (unos 25 s) y se cerraría `failed`: en el worker no pasa, porque el reclamo sólo entrega la fila más vieja de cada conversación. El costo es que el POST responde antes del envío (el worker sondea cada 1 s).

| Falla posterior | Cómo se entera el asesor |
| --- | --- |
| Envío rechazado, conexión caída, flujo roto | `FailAsync` traspasa: `Paused Handoff`, evento `AutomationPaused` con su causa en el hilo y aviso a la bandeja. Existe hoy. |
| Excepción no prevista en la unidad | Una fila manual no reintenta: traspasa con `automations.run.manual_start_failed` y se cierra `run_failed` (Task 6). |
| El proceso muere a mitad de la unidad | La fila vuelve por lease con la misma corrida y los mismos `clientId`; sigue oculto el botón. |
| El chatbot se pausó o la ventana venció entre el POST y el worker | `EndAsync` deja `None` y avisa: el encabezado relee y muestra el botón deshabilitado con el motivo (D-11). |
| Un asesor tomó, pausó o resolvió en el medio | La fila sale `skipped`; lo que se ve es lo que esa persona hizo. |

**2. El id de la corrida sale del id de la fila, sin migración.** `automation_inbox.message_id` es PK sin FK a `messages` (`20261021215006_AddAutomationEngine`), así que una fila manual lleva un `Guid.CreateVersion7()` que no es ningún mensaje. `RunIdFor(item.MessageId)` y `LastInboundMessageId` funcionan igual: un reintento reusa corrida y `clientId`s, y P2-R24 lo cierra `duplicate`. Una fila es manual si `ReadAsync` devuelve `null`. **No hay migración.**

**3. La ruta es de Messaging; el cruce es `IAutomationManualStart`, en `Modules.Messaging.Application`, con adaptador `Bootstrapper/AutomationManualStart.cs` sobre `ManualRunStarter` (Automations).** El resumen y el permiso son de Messaging, y Automations no la referencia (`NoLayerReferencesMessagingIntegrationsCustomersOrIdentity`). Precedente: `IAutomationsChatbotNames`. `ArchitectureTests` no cambia.

**4. `HumanTookOver` pasa a `BotAttendance` (`Modules.Automations.Application/Engine/BotAttendance.cs`), pública.** La usan `RunProcessor` y `ManualRunStarter`; este último es el único lugar donde vive la decisión y sirve al botón y al POST.

**5. El evento es `ConversationEventType.AutomationStarted`, con el texto «{actor} inició el asistente.», en `utils/conversation-event.ts`.** Se persiste por nombre en `details` (jsonb) y no tiene CHECK. Va al final del enum. Un front viejo no se rompe: el tipo desconocido se lee como `type: null` (`types/messaging.ts`) y `describeConversationEvent` pinta «La conversación tuvo un cambio.», así que el backend puede salir primero.

**6. `ConversationSummary.AutomationStart` es `string?`: `Available`, `WindowClosed`, `NoChatbot` o `null` (no se ofrece).** Se calcula en `BuildOneAsync`, como `SharedPhoneCustomers`, y queda `null` en la lista: la bandeja no gasta nada. El detalle gasta una lectura de la conversación si ya hay asesor o está resuelta, y hasta tres más, cortas y por índice, si no (el estado del módulo está cacheado).

**7. Códigos.** Ningún status se arma a mano: todo es `MessagingDomainException` (422) o `RequestConcurrencyException` (412) y lo mapea `ApiExceptionHandler`.

| Caso | Código | Origen |
| --- | --- | --- |
| Resuelta | `messaging.conversation.not_open` | existe (`ConversationNotOpen`) |
| Contacto bloqueado | `messaging.contact.blocked` | existe (`ContactBlocked`) |
| Instagram | `messaging.channel_unsupported` | existe (`ChannelUnsupported`) |
| Fuera de ventana | `messaging.window_closed` | existe (`WindowClosed`) |
| Bot atendiendo, o arranque pendiente | `messaging.automation.already_active` | existe (`AutomationAlreadyActive`) |
| Módulo `automations` apagado | `messaging.automation.not_available` | existe (`AutomationNotAvailable`) |
| Con asesor | `messaging.automation.has_advisor` | **nuevo** (`AutomationHasAdvisor`) |
| Sin chatbot activo, o su versión no se puede leer | `messaging.automation.no_chatbot` | **nuevo** (`AutomationNoChatbot`) |
| Candado ocupado | `concurrency.conflict` (412) | existe; otra unidad está en la conversación |

No es HTTP, va en el traspaso: `AutomationsRunFailures.ManualStartFailed = "automations.run.manual_start_failed"`.

## Global Constraints

- Sin migración. `CK_conversations_automation_assignment` no cambia.
- Despliegue: backend primero, frontend después.
- Textos exactos (D-11): «Iniciar asistente», «Ya pasaron 24 horas desde el último mensaje del cliente», «Esta conexión no tiene un chatbot activo».
- `Conversation.StartAutomation` no cambia: sigue rechazando `Manual` y `Handoff`.
- Motivo escrito en el DTO (regla BFF de `CLAUDE.md`).
- Comentarios y textos en español colombiano, tuteando. Identificadores y códigos, tal cual.
- Pruebas por tarea: sólo las clases tocadas, en primer plano. Integración: `$env:Seed__Enabled = "false"`, y una corrida final filtrada por nombre. Nunca la suite completa: agota los puertos.
- Rutas de pruebas relativas a la raíz del worktree. `Api.exe` detenido antes de `dotnet build`, `dotnet test` o `ef`.
- Antes de cada commit: `git branch --show-current` en ese momento. Conventional commits, sin atribución ni trailers, aunque el entorno pida uno. Después de cada commit, revisa el mensaje con `git log -1 --format=%B`.
- Las ramas de feature no tocan `docs/estado.md`. No se mergea a `main`.
- `.cs` nuevos sin reescribir con `Set-Content` (rompe tildes). Rutas absolutas del worktree; `Set-Location` en cada llamada.

## Review Focus

1. **El reloj del worker va detrás del que atendió el POST.** La corrida no puede quedar con `StartedAt < AutomationResumedAt`, o el siguiente entrante la mata. Prueba: `ARunStartedBehindTheResumeClockStaysAlive` (Task 2).
2. **Conversación de WhatsApp sin `wa_id` (sólo BSUID).** `BuildOneAsync` sale temprano en ese caso y perdería el campo, aunque al contacto sí se le puede escribir. Prueba: `ASummaryWithoutWaIdStillCarriesTheStartState` (Task 5).
3. **Segundo clic o segundo asesor entre la respuesta del POST y el arranque de la corrida.** En ese lapso hay `Active` sin corrida, que D-7 daría por disponible. Prueba: `AStartWhilePendingIsRejectedAndTheResponseOffersNothing` (Task 5).
4. **El cliente escribe justo antes de presionar.** Su fila va delante de la manual y arranca el flujo; la manual no puede arrancarlo otra vez. Prueba: `AnInboundQueuedBeforeTheStartRunsOneFlow` (Task 6).
5. **La unidad lanza una excepción no prevista.** El lease reintentaría durante unos 30 minutos con el bot «activo» y mudo. Prueba: `AManualStartThatThrowsHandsOffInsteadOfRetrying` (Task 6).

---

## File Structure

| Archivo | Responsabilidad |
| --- | --- |
| `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs` | `StartAutomationByMember`: las reglas de D-1, D-5, D-8 y el evento |
| `src/Modules/Automations/Modules.Automations.Application/Engine/BotAttendance.cs` (nuevo) | La regla de D-7 |
| `src/Modules/Automations/Modules.Automations.Application/Engine/ManualRunStarter.cs` (nuevo) | Decidir (botón y POST), candado, cierre de la corrida muerta |
| `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs` | Piso de `now`; fila manual que lanza |
| `src/Modules/Messaging/Modules.Messaging.Application/IAutomationManualStart.cs` (nuevo) | Puerto hacia Automations y estados de D-13 |
| `src/Modules/Messaging/Modules.Messaging.Application/ConversationAutomation.cs` | `StartAutomationCommand` y su handler |
| `src/Bootstrapper/AutomationManualStart.cs` (nuevo) | Veredicto → excepción o estado |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInbox.cs` | Fila manual y su lectura |

# Parte 1: Backend

Worktree: `qep-backend-worktrees/bot-manual-hilo`, rama `feature/bot-manual-hilo`.

### Task 1: Dominio de la conversación

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs` (después de `ResumeAutomation`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationAutomationTests.cs`, `ConversationEventJsonTests.cs`

**Interfaces:**
- Produces: `ConversationEvent Conversation.StartAutomationByMember(Guid actorMemberId, Guid chatbotId, DateTimeOffset now)`; `ConversationEventType.AutomationStarted`; `MessagingAuditActions.AutomationStarted = "messaging.conversation.automation_started"`; `MessagingErrorCodes.AutomationHasAdvisor`, `MessagingErrorCodes.AutomationNoChatbot`.

- [ ] **Step 1: Escribe las pruebas que fallan**

En `ConversationAutomationTests`:

```csharp
[Theory] // Manual, Handoff, AdvisorTook, AdvisorReplied, AdvisorTransferred
public void StartByMemberClearsAnyPause(AutomationPauseReason reason)
// Status Active, ChatbotId, StartedAt = now, ResumedAt = now, los tres Paused* y PauseReason null, Step null, HistoryManaged false
// evento (AutomationStarted, Actor = miembro), Version + 1

[Fact] public void StartByMemberWorksFromNoneAndFromActive()
[Fact] public void StartByMemberRejectsAResolvedConversation()   // ConversationNotOpen
[Fact] public void StartByMemberRejectsAnAdvisor()               // AutomationHasAdvisor
[Fact] public void StartByMemberRejectsABlockedContact()         // ContactBlocked
[Fact] public void StartByMemberRejectsInstagram()               // ChannelUnsupported
[Fact] public void StartByMemberRejectsAClosedWindow()           // WindowClosed, también sin entrante nunca
[Theory] // Manual, Handoff
public void StartAutomationKeepsRejectingDeliberatePauses(AutomationPauseReason reason) // AutomationNotAvailable
```

En `ConversationEventJsonTests`: `AutomationStartedRoundTripsByName` (`details` con `"type":"AutomationStarted"` y el `actor`). En la misma clase, `ForEventNamesTheStartedAudit`.

- [ ] **Step 2: Corre y verifica que fallan**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests/Modules.Messaging.UnitTests.csproj --filter "FullyQualifiedName~ConversationAutomationTests|FullyQualifiedName~ConversationEventJsonTests"
```

Expected: no compila, falta `StartAutomationByMember`.

- [ ] **Step 3: Implementa**

Orden de los rechazos: `EnsureOpenForAutomation`, asesor, bloqueado, canal distinto de `WhatsApp`, `!IsWindowOpen(now)`. Después: `Active`, `AutomationStartedAt = now`, chatbot, limpia la pausa, `AutomationStep = null`, `AutomationResumedAt = now`, `HistoryManaged = false`, `Touch(now)`.

Dos motivos que van en el `<summary>`:
- `AutomationResumedAt = now` hace que una corrida vieja siga muerta aunque el guardado de Automations falle después de este commit. La corrida nueva sobrevive por el piso de `now` (Task 2).
- `HistoryManaged = false` porque es una acción del asesor: sin eso, una parte tardía del historial le pisa el estado (`CLAUDE.md`, coexistencia).

`AutomationStarted` va al final del enum. Suma la fila en `ForEvent`.

- [ ] **Step 4: Corre el comando del Step 2.** Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Messaging tests/Modules/Messaging/Modules.Messaging.UnitTests
git commit -m "feat(messaging): let a member start the assistant on a conversation"
```

### Task 2: La regla de D-7 y el piso de `now`

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/Engine/BotAttendance.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs:123,132,213,240-244`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`AutomationConversationState`)
- Modify: `src/Bootstrapper/AutomationConversations.cs:42-48`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/BotAttendanceTests.cs` (nuevo), `RunProcessorTests.cs`

**Interfaces:**
- Produces: `static bool BotAttendance.HumanTookOver(AutomationConversationState conversation, ConversationRun run)`; `static bool BotAttendance.IsAttending(AutomationConversationState conversation, ConversationRun? live)` = viva y no `HumanTookOver`.
- Produces: `AutomationConversationState` suma al final `string Channel = "WhatsApp"`, `bool Blocked = false`, `DateTimeOffset? WindowExpiresAt = null`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`BotAttendanceTests.IsAttending` como `[Theory]`: sin corrida → `false`; viva, `Active`, sin asesor, `AutomationResumedAt` nulo o anterior a `StartedAt` → `true`; igual a `StartedAt` → `true`; posterior → `false`; con asesor, `Resolved`, `Paused` o `None` → `false`; corrida terminada → `false`.

En `RunProcessorTests`:

```csharp
[Fact] // Review Focus 1
public async Task ARunStartedBehindTheResumeClockStaysAlive()
// estado Active sin corrida, AutomationResumedAt = reloj + 2 s
// ProcessAsync(Item) → run.StartedAt == resumedAt
// un segundo entrante → "advanced", run.Status != Interrupted
```

- [ ] **Step 2: Corre y verifica que fallan**

```powershell
dotnet test tests/Modules/Automations/Modules.Automations.UnitTests/Modules.Automations.UnitTests.csproj --filter "FullyQualifiedName~BotAttendanceTests|FullyQualifiedName~RunProcessorTests"
```

Expected: no compila, falta `BotAttendance`.

- [ ] **Step 3: Implementa**

Mueve `HumanTookOver` tal cual, con su `<summary>`. En `ProcessUnitAsync`, `now` es el mayor entre `clock.UtcNow` y `conversation?.AutomationResumedAt`. El motivo va en el comentario: el POST y el worker pueden ser pods distintos, y la corrida no puede nacer antes del regreso que la originó (P2-R25).

En `GetAsync` del adaptador: `row.Channel.ToString()`, `row.BlockedAt is not null`, `row.LastInboundAt?.Add(Conversation.WindowLength)`.

- [ ] **Step 4: Corre el comando del Step 2.** Expected: PASS, también las de P2-R25 que ya existían.

- [ ] **Step 5: Commit**

```powershell
git add src/Modules/Automations src/Bootstrapper/AutomationConversations.cs tests/Modules/Automations/Modules.Automations.UnitTests
git commit -m "refactor(automations): share the rule for a bot really attending"
```

### Task 3: La fila manual del inbox

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IAutomationInbox.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/AutomationInbox.cs`
- Modify: `src/Modules/Automations/Modules.Automations.Application/AutomationsPorts.cs` (`IAutomationInboundQueue`)
- Modify: `src/Bootstrapper/AutomationInboundQueue.cs`
- Modify (dobles): `RecordingInbox` en `RunProcessorTests.cs` y `RunProcessorRecordingTests.cs`; `StubAutomationInbox` en `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/AutomationInboxTests.cs`

**Interfaces:**
- Produces: `void IAutomationInbox.EnqueueManualStart(Guid tenantId, Guid connectionId, Guid conversationId, DateTimeOffset now)`. Sólo agrega al contexto: viaja en el `SaveChanges` del cambio.
- Produces: `Task<bool> HasPendingManualStartAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)` en `IAutomationInbox` y en `IAutomationInboundQueue`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Fact] public async Task AManualStartRowIsClaimedInOrderAndIsNoInbound()
// la fila manual se reclama detrás de un entrante anterior de la conversación
// opened = false, ReadInboundAsync(id) == null

[Fact] public async Task PendingManualStartIgnoresInboundRowsAndClosedRows()
// sólo entrantes pendientes → false; manual pendiente → true; CompleteAsync → false; otro tenant → false
```

- [ ] **Step 2: Corre y verifica que fallan**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests/Modules.Messaging.IntegrationTests.csproj --filter "FullyQualifiedName~AutomationInboxTests"
```

Expected: no compila.

- [ ] **Step 3: Implementa**

`EnqueueManualStart` agrega un `AutomationInboxRecord` con `MessageId = Guid.CreateVersion7()`, `Opened = false`, `CreatedAt = NextAttemptAt = now`. `HasPendingManualStartAsync`: `EXISTS` sobre `IX_automation_inbox_conversation`, con `processed_at IS NULL`, tenant, y `NOT EXISTS` en `messages` por id. El `<summary>` de `IAutomationInbox` explica que `message_id` no siempre es un mensaje.

Los dobles devuelven `false` y no hacen nada. Suma a `RecordingInbox` de `RunProcessorTests` un `public HashSet<Guid> NotInbound { get; } = []`: sus ids responden `null` en `ReadAsync`.

- [ ] **Step 4: Corre el comando del Step 2.** Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(messaging): queue a manual assistant start without an inbound"
```

### Task 4: `ManualRunStarter`

**Files:**
- Create: `src/Modules/Automations/Modules.Automations.Application/Engine/ManualRunStarter.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:1176`
- Modify: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.cs` (la clase pasa a `partial`; `NoUnitOfWork` suma un contador `Saves`)
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.ManualStart.cs` (nuevo; reusa `NoUnitOfWork`, `MemoryRuns`, `ServingChatbots`, `RecordingInbox`, `Conversations`, `ModuleStatus`, `FixedClock`)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

**Interfaces:**
- Consumes: `BotAttendance`, `AutomationConversationState` (Task 2), `HasPendingManualStartAsync` (Task 3).
- Produces:

```csharp
public enum ManualStartVerdict { Available, NotFound, NotOpen, HasAdvisor, Blocked, UnsupportedChannel, ModuleDisabled, BotAttending, StartPending, NoChatbot, WindowClosed, Busy }

public sealed class ManualRunStarter(
    IAutomationsUnitOfWork unitOfWork, IConversationRunRepository runs, IChatbotRepository chatbots, IAutomationInboundQueue inbox,
    IAutomationConversations conversations, IAutomationsModuleStatus modules, RunUnitWriter recorder, IClock clock)
{
    Task<ManualStartVerdict> EvaluateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
    /// <param name="commit">Recibe el chatbot; sólo corre con Available, bajo el candado.</param>
    Task<ManualStartVerdict> StartAsync(Guid tenantId, Guid conversationId, Func<Guid, CancellationToken, Task> commit, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Theory] // una fila por veredicto, en el orden de abajo
public async Task EvaluateAndStartGiveTheSameVerdict(...)
// StartAsync no llama a commit salvo Available

[Fact] public async Task ABusyLockIsBusyAndCommitsNothing()
[Fact] public async Task ABotAttendingRejectsAndLeavesItsRunAlive()           // Waiting, Active, sin regreso → BotAttending
[Fact] public async Task ADeadRunIsClosedBeforeTheConversationChanges()
// Paused Manual con corrida Waiting: dentro de commit, run.Status == Interrupted;
// el veredicto salió del estado de antes; una fila Closed "human_took_over"; Saves == 1
[Fact] public async Task AFailedCommitSavesNothing()                          // commit lanza concurrency.conflict → sube, Saves == 0
[Fact] public async Task AnUnreadableFlowIsNoChatbotAtPress()                 // StartAsync → NoChatbot, sin commit
[Fact] public async Task ALatestCompletedRunDoesNotBlockTheStart()            // D-6
```

En `CompositionRootTests`: `TheContainerBuildsTheManualRunStarter`.

- [ ] **Step 2: Corre y verifica que fallan**

```powershell
dotnet test tests/Modules/Automations/Modules.Automations.UnitTests/Modules.Automations.UnitTests.csproj --filter "FullyQualifiedName~RunProcessorTests"
```

Expected: no compila, falta `ManualRunStarter`.

- [ ] **Step 3: Implementa**

Un solo `DecideAsync` privado para los dos caminos, con este orden; lo barato primero, porque el resumen lo llama en cada detalle:

`null` → `NotFound`; no `Open` → `NotOpen`; asesor → `HasAdvisor`; bloqueado → `Blocked`; canal → `UnsupportedChannel`; módulo → `ModuleDisabled`; `BotAttendance.IsAttending` → `BotAttending`; fila pendiente → `StartPending`; `FindServingByConnectionAsync` sin `Status: Active` → `NoChatbot`; `WindowExpiresAt` ausente o `<= now` → `WindowClosed`; si no, `Available`.

`NoChatbot` va antes que la ventana porque es lo que el tenant puede arreglar.

`EvaluateAsync` no abre transacción: la corrida sale de `FindLatestAsync` con `IsLive` (a lo sumo hay una viva, y es la última).

`StartAsync`, en orden:
1. `BeginTransactionAsync`; `TryLockConversationAsync`, y `false` → `Busy`.
2. `FindLiveForUpdateAsync`, después `GetAsync`, `DecideAsync`.
3. Con `Available`, lee y parsea la versión publicada; si no se puede → `NoChatbot`.
4. Corrida viva: `Finish(Interrupted, null, now)` y `recorder.WriteClosed(run, null, RunCloseReasons.HumanTookOver, now)`.
5. `await commit(chatbotId, cancellationToken)`.
6. `SaveChangesAsync`.

El paso 4 va antes del 5 porque `HumanTookOver` se lee sobre el estado de antes. Si el commit de Mensajería lanza, la transacción se deshace sola y la corrida sigue como estaba. Si falla el guardado del paso 6, el worker la cierra igual: `AutomationResumedAt` quedó posterior a su `StartedAt`.

`AddScoped<ManualRunStarter>()` junto a `RunProcessor`.

- [ ] **Step 4: Corre el comando del Step 2, y este.** Expected: PASS.

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --filter "FullyQualifiedName~CompositionRootTests|FullyQualifiedName~AutomationsLayerTests"
```

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(automations): decide and guard a manual run start"
```

### Task 5: Endpoint y resumen

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/IAutomationManualStart.cs`
- Create: `src/Bootstrapper/AutomationManualStart.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationAutomation.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs:36-69`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs` (`ConversationSummary`)
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs:220-227,532`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:935,1146`
- Modify (dobles): `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:229`, `ConversationReadsTests.cs:78,106` (nuevo `FakeAutomationManualStart`, junto a `FakeChatbotNames`)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationReadsTests.cs`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.ManualStart.cs` (nuevo; parcial de `EngineEndToEndTests`, reusa `ArrangeAsync`, `ConversationActionAsync`, `InboundAsync`, `DrainAutomationsAsync`, `f.Meta`)

**Interfaces:**
- Consumes: `StartAutomationByMember` (Task 1), `EnqueueManualStart` (Task 3), `ManualRunStarter` (Task 4).
- Produces:

```csharp
public interface IAutomationManualStart
{
    /// <summary><c>null</c>: el encabezado no ofrece el botón.</summary>
    Task<string?> FindStateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
    Task StartAsync(Guid tenantId, Guid conversationId, Func<Guid, CancellationToken, Task> commit, CancellationToken cancellationToken);
}

public static class AutomationStartStates { Available, WindowClosed, NoChatbot } // constantes string
public sealed record StartAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;
// ConversationSummary: public string? AutomationStart { get; init; }
```

- [ ] **Step 1: Escribe las pruebas que fallan**

En `ConversationReadsTests`:

```csharp
[Fact] public async Task BuildOneCarriesTheStartState()
[Fact] public async Task BuildAsyncNeverAsksForTheStartState()      // FakeAutomationManualStart.Calls == 0: la bandeja no paga
[Fact] public async Task ASummaryWithoutWaIdStillCarriesTheStartState() // Review Focus 2: la salida temprana de BuildOneAsync
```

En `EngineEndToEndTests.ManualStart.cs`, cada prueba con `[Trait("Slice", "ManualStart")]`. Para partir de `None`, arregla con `ArrangeAsync(database, trigger: "Keyword", keywords: ["precio"])` e `InboundAsync(f, "w1", "hola")`:

```csharp
[Fact] public async Task AStartAnswersTheSummaryWithTheEventAndTheAudit()
// 200; automation.status Active; 1 evento AutomationStarted con actor = membresía del dueño;
// 1 audit messaging.conversation.automation_started con actor_id = el usuario; 1 fila manual pendiente

[Fact] public async Task WithoutIfMatchIs428AndAStaleVersionIs412()
[Fact] public async Task AnotherTenantAndAMissingPermissionAre403()  // nunca 404
[Fact] public async Task AnInboundAfterTheReadAnswers412AndEnqueuesNothing()
// lee version, InboundAsync sube la versión por SQL, POST con la vieja → 412, sin evento, sin fila manual

[Theory] // con asesor, resuelta, bloqueado, Instagram, sin chatbot activo, ventana cerrada
public async Task ARejectionNamesItsCodeAndSendsNothing(...)
// 422, code exacto de la tabla de decisión 7, sin evento, sin fila manual, f.Meta.Sends() sin cambio

[Fact] public async Task TheBotAttendingIsRejectedWithoutAMessage()  // already_active
[Fact] public async Task TheAutomationsModuleOffIsRejected()         // not_available; automationStart null

[Fact] // Review Focus 3
public async Task AStartWhilePendingIsRejectedAndTheResponseOffersNothing()
// el POST responde automationStart null; un segundo POST con la versión nueva → 422 already_active; 1 sola fila manual

[Fact] public async Task TheSummaryMatchesTheEngineOnAttendance()
// GET detalle: Available con None y con Paused Manual + corrida Waiting; null con corrida viva atendiendo;
// WindowClosed con last_inbound_at viejo; NoChatbot con el chatbot pausado; null con asesor, resuelta, bloqueada
```

- [ ] **Step 2: Corre y verifica que fallan**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests/Modules.Messaging.UnitTests.csproj --filter "FullyQualifiedName~ConversationReadsTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests/Modules.Automations.IntegrationTests.csproj --filter "Slice=ManualStart"
```

Expected: no compila, falta `IAutomationManualStart`.

- [ ] **Step 3: Implementa**

Ruta `/conversations/{conversationId:guid}/automation/start`, copiada de `automation/resume`, con las mismas respuestas y `RequireAuthorization(MessagingPermissions.ConversationManage)`.

`StartAutomationHandler(ConversationAssignmentSteps steps, IAutomationManualStart automations, IAutomationInbox inbox)`, con el orden de pausar:

1. `steps.LoadAsync` (tenant, permiso, 404, membresía).
2. `ConversationConcurrency.EnsureVersion`.
3. `automations.StartAsync` con un `commit` que llama `conversation.StartAutomationByMember(member, chatbotId, now)`, luego `inbox.EnqueueManualStart(...)` y luego `steps.CommitAsync(..., [change], now, ct)`. Fila, evento y auditoría van en un solo guardado.
4. Devuelve el resumen que armó `CommitAsync`.

El agregado se cargó antes del candado a propósito: cualquier cambio en el medio, también el `version + 1` por SQL de la ingesta, choca al guardar y sale 412. No hace falta un reintento.

`AutomationManualStart(ManualRunStarter starter)`:
- `StartAsync` traduce con un `switch` exhaustivo: la tabla de la decisión 7 a `MessagingDomainException`, y `Busy` a `RequestConcurrencyException("concurrency.conflict", …)`.
- `FindStateAsync` deja pasar `Available`, `WindowClosed` y `NoChatbot`; el resto es `null`.

Corre con el tracker del request a propósito: sólo lee y sostiene el candado; la unidad corre en el worker.

`BuildOneAsync` pide el estado **antes** de la salida temprana por canal o `wa_id`. El `<summary>` del campo en `ConversationSummary` da el motivo: la pantalla no conoce el chatbot de la conexión ni debe reimplementar la ventana, y `null` en la lista es a propósito.

Registra `ICommandHandler<StartAutomationCommand, ConversationSummary>` e `IAutomationManualStart`.

- [ ] **Step 4: Corre los comandos del Step 2.** Expected: PASS.

- [ ] **Step 5: Corre las pruebas de capas**

```powershell
dotnet test tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj --filter "FullyQualifiedName~MessagingLayerTests|FullyQualifiedName~AutomationsLayerTests"
```

Expected: PASS, sin tocar esos archivos.

- [ ] **Step 6: Commit**

```powershell
git add src tests
git commit -m "feat(messaging): add automation start endpoint and summary state"
```

### Task 6: La fila manual en el worker

**Files:**
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs:59-88`
- Modify: `src/Modules/Automations/Modules.Automations.Application/Engine/AutomationsRunFailures.cs`
- Test: `tests/Modules/Automations/Modules.Automations.UnitTests/RunProcessorTests.ManualStart.cs`
- Test: `tests/Modules/Automations/Modules.Automations.IntegrationTests/EngineEndToEndTests.ManualStart.cs`

**Interfaces:**
- Consumes: el endpoint de Task 5; `RecordingInbox.NotInbound` (Task 3).
- Produces: `AutomationsRunFailures.ManualStartFailed`, que el frontend pinta (Task 8).

- [ ] **Step 1: Escribe las pruebas que fallan**

Unitarias:

```csharp
[Fact] // Review Focus 5
public async Task AManualStartThatThrowsHandsOffInsteadOfRetrying()
// Item en NotInbound, conversación Active sin corrida, el mensajero lanza InvalidOperationException
// ProcessAsync → "run_failed"; 1 HandoffAsync con FlowFailed y Code = ManualStartFailed; inbox "complete … run_failed"

[Fact] public async Task AnInboundThatThrowsKeepsItsLeaseRetry()  // sin NotInbound: la excepción sube y no hay HandoffAsync
[Fact] public async Task AHandoffThatNoLongerAppliesStillClosesTheRow()   // HandoffAsync lanza DomainException → "run_failed"
```

De punta a punta, también con `[Trait("Slice", "ManualStart")]`:

```csharp
[Fact] public async Task AStartSendsTheFirstMessageFromTheWorker()
// sin asesor ni asistente: tras DrainAutomationsAsync, f.Meta.Sends() es bienvenida y botones; corrida Waiting; conversación Active;
// outcome "started"; AuditAsync("automations.run.started") == 1; run_units con trigger Start

[Fact] public async Task TheCustomerReplyContinuesTheStartedRun()
// TapAsync: misma corrida, Waiting en n3, sin Interrupted; ésta es la de AutomationResumedAt vs StartedAt

[Theory] // Manual, Handoff, AdvisorTook; cada una con y sin corrida Waiting
public async Task AStartOverAPauseClearsItAndClosesTheOldRun(...)
// la vieja Interrupted con su fila Closed; la nueva Waiting; pausa en null

[Fact] public async Task AStartAfterResumeClosesTheOldRunAndTheNextInboundFollowsTheNew()
[Fact] public async Task AStartOnActiveWithoutARunStarts()
[Fact] public async Task AStartAfterACompletedRunStartsAndTheTriggerStillDoesNot() // D-6: flowJson que termina sin resolver
[Fact] public async Task TwoStartsInARowSendOneFlow()
[Fact] public async Task TwoSimultaneousStartsSendOneFlow()
// Task.WhenAll con la misma versión: un 200, el otro 412 o 422; una sola fila manual; f.Meta.Sends() de un flujo

[Fact] // Review Focus 4
public async Task AnInboundQueuedBeforeTheStartRunsOneFlow()
// InboundAsync sin drenar la cola, POST con la versión nueva, DrainAutomationsAsync:
// una corrida, un flujo, y la fila manual cerrada sin envíos nuevos

[Fact] public async Task AReclaimedStartRowIsDuplicate()
// reabre la fila manual por SQL (ReopenInboxRowAsync busca por wamid y esta fila no tiene): outcome "duplicate", sin envíos nuevos
```

- [ ] **Step 2: Corre y verifica que fallan**

```powershell
dotnet test tests/Modules/Automations/Modules.Automations.UnitTests/Modules.Automations.UnitTests.csproj --filter "FullyQualifiedName~RunProcessorTests"
```

Expected: FAIL, la excepción sube de `ProcessAsync`. Las de punta a punta pueden pasar de una, porque amarran el camino que ya existe. Eso es correcto; si una falla, investígala antes de seguir.

- [ ] **Step 3: Implementa**

En `ProcessAsync`, un `catch` después del de `RequestConcurrencyException` que excluye `OperationCanceledException`:

- `inbox.ReadAsync` devuelve un entrante → relanza con `throw;` (se conserva el reintento por lease).
- Fila manual → `HandoffAsync` con `HandoffCause.FlowFailed(ManualStartFailed, null, null)` y `CancellationToken.None`; un `DomainException` ahí se ignora, porque una persona ya la tiene. Después `CompleteAsync` con `RunOutcomes.RunFailed`, y un log con ids y el nombre del error, sin el mensaje.

El comentario dice por qué no se reintenta: el lease llega a unos 30 minutos con el bot activo y mudo. Un mensaje puede haber salido antes de la excepción; por eso traspasa y no reabre el botón.

- [ ] **Step 4: Corre**

```powershell
dotnet test tests/Modules/Automations/Modules.Automations.UnitTests/Modules.Automations.UnitTests.csproj --filter "FullyQualifiedName~RunProcessorTests"
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests/Modules.Automations.IntegrationTests.csproj --filter "Slice=ManualStart"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(automations): hand off a manual start that fails unexpectedly"
```

### Task 7: Documentación y corrida final del backend

**Files:**
- Modify: `README.md:3268-3273` (el endpoint, sus códigos y `automationStart`)
- Modify: `CLAUDE.md` (gotchas)

- [ ] **Step 1: Documenta**

README: `automation/start`, sus respuestas, los códigos y que responde antes del envío.

`CLAUDE.md`, una entrada: «Una fila de `automation_inbox` sin mensaje es un arranque manual». Debe decir que `message_id` no siempre existe en `messages`, que `run_units.inbound_message_id` y `last_inbound_message_id` pueden apuntar a esa fila, que el camino es `ResumeAsync`, y que un `INNER JOIN` del inbox a `messages` la pierde.

- [ ] **Step 2: Corrida final, filtrada por nombre**

```powershell
$env:Seed__Enabled = "false"
dotnet test tests/Modules/Automations/Modules.Automations.IntegrationTests/Modules.Automations.IntegrationTests.csproj --filter "FullyQualifiedName~EngineEndToEndTests|FullyQualifiedName~RunUnits"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests/Modules.Messaging.IntegrationTests.csproj --filter "FullyQualifiedName~ConversationAutomationApiTests|FullyQualifiedName~AutomationInboxTests"
```

Expected: PASS. Si aparecen `Failed to connect` en lote, son puertos: repite por nombre, no es regresión.

- [ ] **Step 3: Commit**

```powershell
git add README.md CLAUDE.md
git commit -m "docs: document the manual assistant start"
```

# Parte 2: Frontend

Va en su propio worktree, desde `develop` de `qep-frontend` (589e0f4f o posterior), y se despliega después del backend. Lee antes el `CLAUDE.md` de ese repo. Rutas relativas a su raíz.

### Task 8: Contrato, evento y causa del traspaso

**Files:**
- Modify: `src/features/messaging/types/messaging.ts`
- Modify: `src/features/messaging/services/messaging.api.ts:577,1159,1273`
- Modify: `src/features/messaging/utils/conversation-event.ts`, `src/features/messaging/utils/handoff-cause.ts:147-163,231`
- Modify: `src/test/messaging.ts:102,661`
- Test: `src/features/messaging/services/messaging.api.test.ts`, `src/features/messaging/utils/conversation-event.test.ts`, `src/features/messaging/utils/handoff-cause.test.ts`

**Interfaces:**
- Produces: `type AutomationStartState = 'Available' | 'WindowClosed' | 'NoChatbot'`; `ConversationSummary.automationStart: AutomationStartState | null`; `AutomationTransition` suma `'start'`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```ts
it('posts to automation/start with the frozen version')
it.each(['Available', 'WindowClosed', 'NoChatbot'])('keeps automationStart %s')
it.each([undefined, null, 'SomethingNew'])('reads automationStart %s as null')
// describeAutomationFailure:
// 'messaging.automation.no_chatbot' → 'Esta conexión no tiene un chatbot activo.', shouldReload true
// 'messaging.automation.has_advisor' → 'Esta conversación ya tiene un asesor.', shouldReload true
// 'messaging.window_closed' → 'Ya pasaron 24 horas desde el último mensaje del cliente.', shouldReload true
// 'messaging.channel_unsupported' → 'Esta conversación no admite el asistente.', shouldReload false
// 'messaging.contact.blocked' → CONTACT_BLOCKED_FAILURE
it('names who started the assistant') // 'Camila inició el asistente.'
// handoff: 'automations.run.manual_start_failed' → frase 'el asistente no pudo arrancar.' y 'Motivo: el asistente no pudo arrancar.'
```

- [ ] **Step 2: Corre y verifica que fallan**

```powershell
bun run test --run src/features/messaging/services/messaging.api.test.ts src/features/messaging/utils/conversation-event.test.ts src/features/messaging/utils/handoff-cause.test.ts
```

Expected: FAIL.

- [ ] **Step 3: Implementa**

Suma `'AutomationStarted'` a `CONVERSATION_EVENT_TYPES`. `parseConversation` acepta sólo los tres valores; cualquier otro, `null`. `conversation()` del fixture trae `automationStart: null`, y el mock de `automation` responde también `/automation/start`.

- [ ] **Step 4: Corre el comando del Step 2.** Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src
git commit -m "feat(messaging): read the automation start state and event"
```

### Task 9: El botón

**Files:**
- Create: `src/features/messaging/utils/automation-start.ts`
- Modify: `src/features/messaging/hooks/use-conversation-automation.ts`
- Modify: `src/features/messaging/components/thread-header.tsx:84-86,231-257`
- Modify: `src/features/messaging/pages/conversation-thread-page.tsx:121-123,259-261`
- Modify: `src/routes/_authenticated/messaging/$conversationId.tsx:214-216`
- Test: `src/features/messaging/utils/automation-start.test.ts` (nuevo), `src/routes/_authenticated/messaging/$conversationId.test.tsx`

**Interfaces:**
- Consumes: `automationStart`, `transitionAutomation('start')` (Task 8).
- Produces:

```ts
export interface AutomationStartOffer { disabledReason: string | null; replacesResume: boolean }
export function automationStartOffer(conversation: ConversationSummary): AutomationStartOffer | null
// useConversationAutomation devuelve además `start`; ThreadHeader recibe `onStartAutomation`
```

- [ ] **Step 1: Escribe las pruebas que fallan**

`automationStartOffer`, como tabla:

| `automationStart` | `automation.status` | asesor | Resultado |
| --- | --- | --- | --- |
| `null` | cualquiera | cualquiera | `null` |
| `Available` | ninguno | no | `{ disabledReason: null, replacesResume: false }` |
| `Available` | `Paused` | no | `{ disabledReason: null, replacesResume: true }` |
| `WindowClosed` | `Paused` | no | reason «Ya pasaron 24 horas desde el último mensaje del cliente», `replacesResume: false` |
| `NoChatbot` | ninguno | no | reason «Esta conexión no tiene un chatbot activo», `replacesResume: false` |

En `$conversationId.test.tsx`:

```ts
it('offers only «Iniciar asistente» when it replaces the resume')
it('keeps «Devolver al asistente» beside a disabled start')    // WindowClosed + Paused, D-10
it('keeps «Devolver al asistente» when the start is hidden')   // automationStart null + Paused + asesor
it('shows the reason on a start it cannot run')                // title y aria-disabled; el clic no hace POST
it('hides the start from someone without manage')
it('posts automation/start with the version and repaints')     // relee el hilo
it('shows the failure of a rejected start')                    // 422 no_chatbot en la franja de automation.failure
```

- [ ] **Step 2: Corre y verifica que fallan**

```powershell
bun run test --run src/features/messaging/utils/automation-start.test.ts 'src/routes/_authenticated/messaging/$conversationId.test.tsx'
```

Expected: FAIL.

- [ ] **Step 3: Implementa**

Botón junto a «Pausar asistente», con la misma forma y `canManage`, antes de «Devolver al asistente». «Devolver» se pinta si cumple lo de hoy y `offer?.replacesResume` no es `true`.

Con motivo usa `aria-disabled` y no `disabled`: `Button` trae `disabled:pointer-events-none`, y el `title` nunca aparecería (mismo criterio de `contact-block`). Suma `className="aria-disabled:opacity-50"` y un `onClick` que no hace nada. Sin motivo, `disabled={isAutomating}`.

- [ ] **Step 4: Corre el comando del Step 2.** Expected: PASS.

- [ ] **Step 5: Verifica el tipado y las pruebas de mensajería**

```powershell
bun run build
bun run test --run src/features/messaging
```

Expected: build sin errores; las fallas, si las hay, son por nombre las mismas que en `develop` (el frontend no tiene CI de pruebas).

- [ ] **Step 6: Commit**

```powershell
git add src
git commit -m "feat(messaging): start the assistant from the thread header"
```

## Discrepancias con el spec

1. **§4.2 dice que `RunProcessor` no tiene un arranque sin entrante, y es sólo parcialmente cierto.** `ResumeAsync` ya arranca desde el primer bloque sin disparador ni D-12, y `StartRunAsync` sólo necesita un id: la fila no tiene que ser un mensaje, porque `automation_inbox.message_id` no tiene FK. El plan no agrega un camino al motor. Los pasos 1 a 3 de §4.2 corren en el request y el 4 en el worker, lo que §7 permite.
2. **§2 dice «de inmediato»: el POST responde antes del envío.** El mensaje sale con la pasada del worker (cada 1 s, y en serie con las demás filas de la réplica). Un fallo posterior no llega como error HTTP sino como la tabla de la decisión 1.
3. **§4.3 pide fijar `AutomationResumedAt` antes del `StartedAt`, pero eso solo no alcanza.** Con POST y worker en pods distintos, un reloj atrasado invertiría el orden. El plan lo fija con la hora del POST y pone un piso a `now` en `ProcessUnitAsync`.
4. **D-7 daría `Active` sin corrida por disponible también en el lapso entre el POST y el arranque.** El plan agrega `StartPending`: se oculta y responde `already_active`. Sin esto, un segundo clic escribiría otro evento, aunque no otro flujo. §6 pide `Active` sin corrida → arranca, y sigue valiendo cuando no hay fila pendiente.
5. **El spec no dice qué pasa con el módulo `automations` apagado.** El plan oculta el botón y responde `messaging.automation.not_available`: arrancar ahí terminaría en `StandDownAsync`, que deja `None` sin enviar nada. El owner puede preferir otro texto.
6. **§4.1 lista 412 sólo por versión; el plan también lo usa para el candado ocupado.** Otra unidad está en la conversación, y releer es lo que le sirve al asesor. No se abre un 409 nuevo.
7. **D-11 pide el motivo en el `title` de un botón deshabilitado, y con `disabled` no se ve.** `Button` trae `disabled:pointer-events-none`. El plan usa `aria-disabled`, como el bloqueo de contacto.
8. **El texto de D-11 no calza en una conversación sin entrantes.** Una abierta desde QEP o importada da `WindowClosed` y dice «Ya pasaron 24 horas desde el último mensaje del cliente», aunque el cliente nunca haya escrito. Se deja el texto del spec; cambiarlo es decisión del owner.
9. **§6 pide el rechazo con «error con su código»; un fallo de envío no lo da.** Responde 200 y traspasa con su causa en el hilo, porque ocurre después de la respuesta.

## Self-Review

**Cobertura del spec.** D-1, D-5, D-8: Tasks 1 y 4. D-2, D-6: Tasks 4 y 6. D-3: Tasks 1 y 6. D-4: Task 4. D-7: Tasks 2, 4 y 5. D-9: Task 5. D-10, D-11: Task 9. D-12: Tasks 1, 5 y 6. D-13: Task 5. §6 completo en Tasks 1, 4, 5, 6, 8 y 9. Sin huecos.

**Pasos.** Cada código nombra archivo y firma. Los cuerpos de `DecideAsync` y `StartAsync` van como orden, porque ese orden es la decisión.

**Tipos.** `ManualStartVerdict`, `IAutomationManualStart.StartAsync` y `EnqueueManualStart` se usan con la misma firma en todas las tareas. `AutomationStart` en C# es `automationStart` en TS.

**Review Focus.** Las cinco líneas tienen prueba en la tarea dueña: Tasks 2, 5, 5, 6 y 6.

**Proporción.** Casi todo son decisiones, nombres de prueba y aserciones. Los únicos bloques de código son firmas.
