# Agente IA que agenda citas — Plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Las reglas de `qep/CLAUDE.md` («Presupuesto de pruebas y ejecución de planes») mandan sobre el proceso del skill:** tres bloques, un implementador por bloque, una revisión por bloque y una revisión fuerte (modelo más capaz, toda la rama) al final.

**Goal:** que el agente IA reserve, liste, cancele y cambie de hora las citas del cliente de la conversación contra la misma Agenda del back office, con las reglas aplicadas por el backend; más el historial `byChannel` y el remitente por defecto del recordatorio que el owner sumó al cerrar las preguntas.

**Architecture:** Scheduling separa de sus handlers el núcleo de disponibilidad, reserva, cancelación y cambio de hora (`AvailabilitySearch`, `AppointmentBooking`, `AppointmentCancellation`, `AppointmentRescheduling`, con un `SchedulingActor` que dice quién escribe) y expone `IChannelScheduling`, el único camino sin sesión para operar la Agenda. Ai declara su puerto `IAiScheduling` y seis herramientas (tres simuladas en el playground); el adaptador `Bootstrapper/AiScheduling.cs` traduce excepciones a códigos y resuelve el cliente de la conversación con `ConversationCustomerResolver`. Una migración expand agrega `appointment_events.by_channel` y `settings.reminder_sender_member_id`.

**Tech Stack:** .NET 10, EF Core + Npgsql (migración expand-only), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-23-agente-ia-agenda-design.md` (aprobado; D-1 a D-19). Contexto: `2026-10-29-agentes-ia-design.md` (§7, §8, D-A16, D-A30), `2026-11-21-agenda-citas-design.md` y su plan `plans/2026-11-21-agenda-citas-backend.md` (correcciones 17, 22c, 34). Ante discrepancia, gana el código.

## Dónde se trabaja

Worktree `qep-backend-worktrees/agenda-agente-ia`, rama `feature/agenda-agente-ia`. Nunca en `main` ni `develop` hasta el merge del Bloque 3. Antes de tocar un archivo y antes de cada commit: `git branch --show-current` debe decir `feature/agenda-agente-ia`. Antes de tocar un módulo, abre su regla (`.claude/rules/ai-copiloto.md`, `messaging.md`, `customers.md`, `build-y-despliegue.md`, `pruebas-y-entorno-local.md`, `sql-entre-modulos.md`).

## Global Constraints

- Copy en español colombiano tuteando, sin voseo; identificadores, comentarios y nombres de archivo en inglés. Códigos, enums y rutas no se traducen.
- Conventional commits, **sin** atribución de IA ni `Co-Authored-By`. Un commit por tarea.
- TDD: RED antes que GREEN con evidencia literal. RED por compilación = salida de `dotnet build` del proyecto de prueba, **sin contenedores**. La integración corre en GREEN.
- Mientras se itera, la integración se filtra por nombre de prueba: `dotnet test <proyecto> --filter "FullyQualifiedName~<Prueba>" -- xUnit.MaxParallelThreads=4`. El proyecto completo corre **una vez al final de cada bloque** (paso «Cierre del bloque»).
- **Nunca dos corridas de integración a la vez.** Antes de cada corrida de Testcontainers: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay uno de otra sesión, se espera, nunca se detiene. `Api.exe` corriendo bloquea el build: se avisa. Pruebas en primer plano.
- Comandos en PowerShell (`A; if ($?) { B }`, nunca `&&`).
- Capas: Ai no referencia Scheduling, Messaging ni Customers; Scheduling no referencia Ai ni Messaging (`AiLayerTests`, `SchedulingLayerTests`). Todo cruce por puerto con adaptador en `src/Bootstrapper`. `Application` sin EF Core ni Npgsql.
- Las respuestas HTTP de la Agenda **no cambian** salvo `AppointmentEventDto.ByChannel` y `SettingsDto.ReminderSenderMemberId|ReminderSenderMemberName` (+ `SettingsRequest.ReminderSenderMemberId`).
- Migración expand-only, generada con `dotnet ef migrations add AddAppointmentEventByChannelAndReminderSender --project src/Modules/Scheduling/Modules.Scheduling.Infrastructure --context SchedulingDbContext -o Persistence/Migrations`; el snapshot se commitea con ella; no se aplica a mano.
- Registro a mano: lo de Scheduling junto a `AvailabilityLoader` (`QepServiceCollectionExtensions.cs:825`); herramientas en `AiInfrastructureExtensions.cs:57-61`; el adaptador junto a `IAiQuotationDrafts` (`QepServiceCollectionExtensions.cs:2004`).
- Topes del modelo (spec §3): todas las herramientas `strict`, todo en `required`; con el agente completo: 12 herramientas y 8 uniones `["string","null"]`, sin opcionales.
- El tenant y el cliente nunca salen de un argumento del modelo (D-A30). Horas al modelo: local del tenant, `yyyy-MM-ddTHH:mm`, una `timeZone` por resultado (D-10).
- `docs/estado.md` **no** se toca en la rama: se actualiza en `develop` al mergear (Bloque 3).

## Review Focus

1. **Reserva repetida por un trabajo retomado o una carrera con la misma llave.** Esperado: una sola cita y el resultado describe la guardada (`alreadyBooked: true`), aunque la segunda llamada pida otra hora. Pruebas: `ResumedJobFindsTheBookingItAlreadyMade` y `AFailedBookingDoesNotConsumeAnOrdinal` (Task 8), `SameKeyInParallelBooksOnceAndBothDescribeIt` (Task 5).
2. **Cita de otro cliente** (id adivinado o copiado del historial, cliente fusionado, playground con un id real ajeno). Esperado: `appointment_not_found`, igual que uno inexistente, sin escribir. Pruebas: `AnotherCustomersAppointmentIsNotFound` (Task 5), `SimulatedCancelOfAnotherCustomersAppointmentIsNotFound` (Task 9), `RereadsTheConversationCustomerOnEveryCall` (Task 10).
3. **Saltarse el plazo moviendo la cita en vez de cancelarla.** Esperado: el cambio de hora por chat respeta `clientCancelCutoffHours` y exige `bookableByChannel`, y termina con traspaso `appointment_change_blocked`. Pruebas: `RescheduleInsideTheCutoffIsInvalidTransition`, `RescheduleOfANonChannelServiceIsInvalidTransition` (Task 5), `ChangeBlockedHandsOffToTheConfiguredDestination` (Task 9).
4. **Contacto sin ficha de cliente** (conversación vieja, ingesta que falló, Instagram). Esperado: reservar crea y cuelga la ficha con el resolvedor; sin identidad, traspaso `appointment_customer_unavailable`; listar responde vacío; cancelar y mover responden `appointment_not_found`. Pruebas: `BookCreatesAndLinksTheCustomerWhenMissing`, `BookWithoutChannelIdentityHandsOff`, `ListWithoutCustomerIsEmpty`, `CancelWithoutCustomerIsNotFound` (Task 10).
5. **Dos escrituras en una respuesta del modelo** (reservar y cancelar a la vez, o dos reservas). Esperado: corre sólo la primera; la segunda vuelve `one_write_per_response` y no consume ordinal. Pruebas: `WritesCoverTheThreeSchedulingWrites` (Task 7), `ASecondSchedulingWriteInOneResponseIsRejected` (Task 8).

---

## Mapa de archivos

| Archivo | Responsabilidad |
| --- | --- |
| `Scheduling.Domain/Appointment.cs`, `AppointmentEvent.cs` | `byChannel` en `Create`, `Cancel`, `Reschedule` → `AppointmentEvent.ByChannel` |
| `Scheduling.Domain/SchedulingSettings.cs` | `ReminderSenderMemberId` |
| `Scheduling.Domain/SchedulingErrorCodes.cs` | `ResourceNotChoosable` |
| `Scheduling.Application/SchedulingActor.cs` (nuevo) | Quién escribe |
| `Scheduling.Application/AvailabilitySearch.cs`, `AppointmentBooking.cs`, `AppointmentCancellation.cs`, `AppointmentRescheduling.cs` (nuevos) | Núcleo sin autorización |
| `Scheduling.Application/ChannelScheduling.cs` (nuevo) | `IChannelScheduling`, sus registros y la implementación |
| `Scheduling.Infrastructure/Persistence/*` | Columnas, migración, `PublishByAgent`, `ListUpcomingByCustomerAsync` |
| `Scheduling.Infrastructure/Reminders/AppointmentReminderProcessor.cs` | Remitente por defecto |
| `Ai.Domain/AgentConfig.cs`, `AgentConfigValidator.cs` | `Tools.Scheduling`, `AgentSchedulingReadiness`, dos códigos |
| `Ai.Application/AgentSchedulingTools.cs` (nuevo) | Puerto, registros, argumentos y las nueve clases de herramienta |
| `Ai.Application/AgentTools.cs`, `AgentHandoff.cs`, `AgentTurnRunner.cs`, `AgentPromptBuilder.cs`, `AgentEditorBuilder.cs`, `AgentDtos.cs`, `AgentActivity.cs` | Contrato común, ordinal, prompt, editor, actividad |
| `Ai.Infrastructure/Persistence/AgentActivityReader.cs`, `AiInfrastructureExtensions.cs` | Conteo y registro |
| `Bootstrapper/AiScheduling.cs` (nuevo) | Adaptador `IAiScheduling` → `IChannelScheduling` |

---

## Bloque 1 — Scheduling (Tasks 1 a 5)

Un implementador. Al terminar: «Cierre del bloque 1» y una revisión del bloque.

### Task 1: columnas `by_channel` y `reminder_sender_member_id` (dominio y persistencia)

**Files:**
- Modify: `src/Modules/Scheduling/Modules.Scheduling.Domain/AppointmentEvent.cs`, `Appointment.cs:70-167`, `SchedulingSettings.cs`
- Modify: `src/Modules/Scheduling/Modules.Scheduling.Infrastructure/Persistence/SchedulingDbContext.cs:294-326`
- Create: migración `AddAppointmentEventByChannelAndReminderSender` (+ snapshot)
- Modify: `src/Modules/Scheduling/Modules.Scheduling.Application/AppointmentDtos.cs:29`, `AppointmentComposer.cs:52`
- Test: `tests/Modules/Scheduling/Modules.Scheduling.UnitTests/AppointmentTests.cs`, `SchedulingSettingsTests.cs`

**Interfaces:**
- Produces:
  - `AppointmentEvent.ByChannel { get; }` (`bool`); `AppointmentEvent.Create(AppointmentId, AppointmentAction, Guid? actorMemberId, string? detail, DateTimeOffset at, bool byChannel = false)`.
  - `Appointment.Create(…, Guid? actorMemberId, DateTimeOffset now, string? clientRequestId = null, bool byChannel = false)`; `Appointment.Cancel(string? reason, Guid? actorMemberId, DateTimeOffset now, bool byChannel = false)`; `Appointment.Reschedule(ResourceId, SessionId, DateTimeOffset startsAt, DateTimeOffset endsAt, Guid? actorMemberId, DateTimeOffset now, bool byChannel = false)`. `RecordReminderSent`/`RecordReminderSkipped` siempre `false`.
  - `SchedulingSettings.ReminderSenderMemberId { get; }` (`Guid?`); `SchedulingSettings.Update(ConfirmationMode, int, int, int, bool reminderEnabled, int reminderOffsetHours, Guid? reminderConnectionId, Guid? reminderSenderMemberId, DateTimeOffset now)` — guarda el remitente siempre.
  - `AppointmentEventDto(string Action, DateTimeOffset At, string? ActorName, string? Detail, bool ByChannel)`.
  - EF: `by_channel` `boolean NOT NULL DEFAULT false` (`HasDefaultValue(false)`), `reminder_sender_member_id uuid NULL`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// AppointmentTests
[Fact] public void ChannelWritesMarkTheirHistoryEntry()
{   var a = Booked(byChannel: true); a.Reschedule(res, ses, s2, e2, null, now, byChannel: true); a.Cancel(null, null, now, byChannel: true);
    Assert.All(a.Events, e => Assert.True(e.ByChannel)); }
[Fact] public void WritesWithoutTheFlagAreNotByChannel()   // Create/Confirm/Cancel sin el parámetro
    => Assert.All(BookedConfirmedAndCancelled().Events, e => Assert.False(e.ByChannel));
[Fact] public void ReminderEntriesAreNeverByChannel()      // RecordReminderSent sobre una cita creada con byChannel: true
    => Assert.False(a.Events.Last().ByChannel);
// SchedulingSettingsTests
[Fact] public void TheReminderSenderIsKeptWithTheReminderOff()
{   settings.Update(Manual, 60, 30, 24, reminderEnabled: false, 24, null, senderId, now);
    Assert.Equal(senderId, settings.ReminderSenderMemberId); Assert.Null(settings.ReminderConnectionId); }
```

- [ ] **Step 2: RED por compilación** — `dotnet build tests/Modules/Scheduling/Modules.Scheduling.UnitTests`. Esperado: `CS1739`/`CS1501` (parámetro `byChannel`/`reminderSenderMemberId` inexistente).
- [ ] **Step 3: Implementa** el dominio, el mapeo EF, `AppointmentEventDto.ByChannel` (lo llena `AppointmentComposer` con `entry.ByChannel`), y ajusta la única llamada a `Update` (`UpdateSettingsHandler`, pasa `row.ReminderSenderMemberId` hasta la Task 2). Genera la migración; revisa que `Up` sólo tenga dos `AddColumn` (`defaultValue: false` en la booleana) y `Down` sus `DropColumn`.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Scheduling/Modules.Scheduling.UnitTests --filter "FullyQualifiedName~AppointmentTests|FullyQualifiedName~SchedulingSettingsTests"`. Esperado: PASS.
- [ ] **Step 5: Commit** — `feat(scheduling): record channel writes and the reminder sender column`

### Task 2: remitente por defecto del recordatorio (reglas y envío)

**Files:**
- Modify: `src/Modules/Scheduling/Modules.Scheduling.Application/SchedulingRequests.cs:58`, `SchedulingDtos.cs:59`, `SchedulingComposer.cs` (`Settings`), `Settings.cs` (`GetSettingsHandler`, `UpdateSettingsHandler`)
- Modify: `src/Modules/Scheduling/Modules.Scheduling.Infrastructure/Reminders/AppointmentReminderProcessor.cs:302,350-372`
- Test: `tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests/SchedulingSettingsApiTests.cs`, `AppointmentReminderWorkerTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SchedulingSettings.ReminderSenderMemberId`, `Update(…, reminderSenderMemberId, now)`); `ISchedulingMemberDirectory.FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken)` → `SchedulingMemberRef(Guid MemberId, string DisplayName, bool IsActive)`.
- Produces:
  - `SettingsRequest(string? ConfirmationMode, int? MinNoticeMinutes, int? HorizonDays, int? ClientCancelCutoffHours, bool? ReminderEnabled, int? ReminderOffsetHours, Guid? ReminderConnectionId, Guid? ReminderSenderMemberId)`.
  - `SettingsDto(string ConfirmationMode, int MinNoticeMinutes, int HorizonDays, int ClientCancelCutoffHours, bool ReminderEnabled, int ReminderOffsetHours, Guid? ReminderConnectionId, Guid? ReminderSenderMemberId, string? ReminderSenderMemberName, ReminderTemplateStateDto? ReminderTemplate, long Version)`. El `<param>` explica por qué viaja el nombre (BFF: la pantalla no consulta Personas) y que un suspendido sigue nombrado.
  - `SchedulingComposer.Settings(SchedulingSettings row, ReminderTemplateStateDto? template, string? senderName)`.
  - `internal static class ReminderSenderName { Task<string?> ResolveAsync(ISchedulingMemberDirectory, Guid tenantId, SchedulingSettings row, CancellationToken) }` en `Settings.cs`: `null` sin id; el `DisplayName` del directorio aunque `IsActive` sea falso; `null` si el directorio no lo devuelve.
  - `UpdateSettingsHandler` gana `ISchedulingMemberDirectory members`. Con valor no activo/ausente: `SchedulingFieldErrors.Throw("reminderSenderMemberId", "Elige un miembro activo de la organización.")`, **antes** de `row.Update`. Cambio → `"reminderSenderMemberId"` en `changed`.
  - `SenderOfAsync(IServiceProvider, Appointment, Guid? resourceMemberId, Guid? defaultSenderMemberId, CancellationToken)`: el remitente por defecto se agrega **al final** de los candidatos; el filtro de activos no cambia.

- [ ] **Step 1: Escribe las pruebas que fallan** (integración; RED por compilación)

```csharp
// SchedulingSettingsApiTests
[Fact] ReminderSenderRoundTripsWithItsName()          // PUT con id de un miembro activo → 200; GET: reminderSenderMemberId == id, reminderSenderMemberName == su DisplayName
[Fact] ReminderSenderIsStoredWithTheReminderOff()     // reminderEnabled: false + id → GET lo devuelve
[Fact] ASuspendedSenderIsStillNamed()                 // guarda activo, suspende la membresía, GET → mismo id y mismo nombre
[Fact] AnInactiveSenderIs422WithItsField()            // PUT con miembro suspendido → 422, errors contiene "reminderSenderMemberId"
[Fact] ASenderFromAnotherTenantIs422WithItsField()    // id de membresía de otro tenant → 422, mismo campo
[Fact] NullClearsTheSender()                          // PUT sin el campo → GET: los dos null
[Fact] ChangingTheSenderIsAudited()                   // audit.entries de scheduling.settings.updated incluye "reminderSenderMemberId"
// AppointmentReminderWorkerTests
[Fact] WithoutResourceMemberOrActorTheDefaultSenderSends()   // cita de canal (actor nulo), recurso sin miembro, remitente activo → sender == remitente, sin ReminderSkipped
[Fact] TheResourceMemberWinsOverTheDefaultSender()
[Fact] ASuspendedDefaultSenderStillSkipsWithNoSenderMember()  // ReminderSkippedReason == "no_sender_member"
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests`. Esperado: `CS7036`/`CS1739` por el campo nuevo de `SettingsRequest`.
- [ ] **Step 3: Implementa** las firmas de arriba.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests --filter "FullyQualifiedName~ReminderSender|FullyQualifiedName~DefaultSender|FullyQualifiedName~SenderIs422|FullyQualifiedName~SuspendedSender|FullyQualifiedName~NullClearsTheSender|FullyQualifiedName~ChangingTheSenderIsAudited|FullyQualifiedName~TheResourceMemberWins" -- xUnit.MaxParallelThreads=4`. Esperado: PASS.
- [ ] **Step 5: Commit** — `feat(scheduling): default reminder sender in the scheduling rules`

### Task 3: extracción del núcleo (refactor sin cambio de comportamiento)

**Files:**
- Create: `src/Modules/Scheduling/Modules.Scheduling.Application/SchedulingActor.cs`, `AvailabilitySearch.cs`, `AppointmentBooking.cs`, `AppointmentCancellation.cs`, `AppointmentRescheduling.cs`
- Modify: `ISchedulingAuditPublisher.cs`, `AppointmentTransitions.cs`, `BookAppointment.cs`, `CancelAppointment.cs`, `RescheduleAppointment.cs`, `ListAvailability.cs`, `AppointmentStarts.cs`
- Modify: `src/Modules/Scheduling/Modules.Scheduling.Infrastructure/Persistence/SchedulingAuditPublisher.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:825` (registra los cuatro servicios, `AddScoped`)
- Test: red existente (abajo) y `tests/Modules/Scheduling/Modules.Scheduling.UnitTests/SchedulingActorTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SchedulingActor(Guid? HistoryMemberId, bool ByChannel, Guid AuditActorId, bool IsAgent) { public static SchedulingActor Human(Guid subjectId, Guid? memberId); public static SchedulingActor Agent(Guid agentId); }` — `Human` → `ByChannel: false, IsAgent: false`; `Agent` → `HistoryMemberId: null, ByChannel: true, IsAgent: true`.
  - `ISchedulingAuditPublisher.PublishByAgent(Guid tenantId, Guid agentId, string action, string resourceType, string resourceId, string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt)`; `SchedulingAuditPublisher` lo escribe con `AuditActorType.AiAgent` y `agentId` como actor.
  - `SchedulingAudit.Publish(ISchedulingAuditPublisher, SchedulingActor, Guid tenantId, string action, string resourceType, Guid resourceId, IReadOnlyCollection<string> changedFields, DateTimeOffset now)` — reemplaza la firma con `IExecutionContext`; `IsAgent` → `PublishByAgent`.
  - `AppointmentTransitions.ActorAsync(Guid tenantId, bool actsAsClient, CancellationToken) → Task<SchedulingActor>` (reemplaza `ActorAndNowAsync`; `actsAsClient` → `Human(subjectId, null)`); `SaveAsync(Guid tenantId, Appointment, SchedulingActor, string auditAction, IReadOnlyCollection<string> changedFields, DateTimeOffset now, CancellationToken)`.
  - `public sealed record AvailabilitySearchResult(TimeZoneInfo TimeZone, IReadOnlyList<AvailableSlot> Slots, IReadOnlyDictionary<ResourceId, string> ResourceNames);` `public sealed class AvailabilitySearch(AvailabilityLoader loader) { Task<AvailabilitySearchResult> SearchAsync(Guid tenantId, ResourceScope scope, Service service, DateOnly from, DateOnly to, ResourceId? onlyResource, CancellationToken) }`.
  - `public sealed record BookingInput(Guid TenantId, Guid ServiceId, Guid CustomerId, DateTimeOffset StartsAt, ResourceId? ResourceId, AppointmentSource Source, Guid? ConversationId, string? Note, string? ClientRequestId, bool RequireClientChoice);` `public sealed record BookingOutcome(Appointment Appointment, bool Replayed);` `public sealed class AppointmentBooking(…) { Task<BookingOutcome> BookAsync(BookingInput input, ResourceScope scope, SchedulingActor actor, CancellationToken) }` — desde la primera búsqueda por `clientRequestId` hasta el commit; la auditoría y el `Appointment.Create` usan `actor` (`HistoryMemberId`, `ByChannel`). `RequireClientChoice` queda sin efecto hasta la Task 5 (el handler pasa `false`).
  - `public sealed class AppointmentCancellation(…) { Task CancelAsync(Guid tenantId, Appointment appointment, string? reason, bool byClient, SchedulingActor actor, CancellationToken) }` — plazo, candado, `session.Release`, `appointment.Cancel(reason, actor.HistoryMemberId, now, actor.ByChannel)`, auditoría, guardado y commit.
  - `public sealed class AppointmentRescheduling(…) { Task RescheduleAsync(Guid tenantId, Appointment appointment, ResourceScope scope, ResourceId? resourceId, DateTimeOffset startsAt, SchedulingActor actor, bool byClient, bool requireClientChoice, CancellationToken) }` — desde el chequeo de estado; `byClient`/`requireClientChoice` sin efecto hasta la Task 5.
  - Los handlers HTTP quedan: autorizar, validar, alcance, `LoadAsync`, actor, núcleo, `AppointmentComposer.ComposeAsync`. El chequeo «la cita ya tiene ese horario» (422 `startsAt`) se queda en `RescheduleAppointmentHandler`, antes del núcleo.

- [ ] **Step 1: Escribe la prueba que falla**

```csharp
// SchedulingActorTests
[Fact] public void AnAgentWritesByChannelWithoutAMember()
{ var a = SchedulingActor.Agent(agentId); Assert.Null(a.HistoryMemberId); Assert.True(a.ByChannel); Assert.True(a.IsAgent); Assert.Equal(agentId, a.AuditActorId); }
[Fact] public void AHumanNeverWritesByChannel()
{ var h = SchedulingActor.Human(subjectId, null); Assert.False(h.ByChannel); Assert.False(h.IsAgent); Assert.Equal(subjectId, h.AuditActorId); }
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Scheduling/Modules.Scheduling.UnitTests`. Esperado: `CS0103 SchedulingActor`.
- [ ] **Step 3: Implementa** la extracción moviendo código (sin reescribir reglas). El orden dentro de cada núcleo es el mismo de hoy; los handlers conservan su autorización y su `If-Match`.
- [ ] **Step 4: GREEN unitarias** — `dotnet test tests/Modules/Scheduling/Modules.Scheduling.UnitTests`. Esperado: PASS. La red de integración (`SchedulingBookingApiTests`, `SchedulingAvailabilityApiTests`, `SchedulingAppointmentTransitionsApiTests`, `SchedulingAuditApiTests`) corre en el cierre del bloque, no acá.
- [ ] **Step 5: Commit** — `refactor(scheduling): extract booking, cancel, reschedule and search cores`

### Task 4: `IChannelScheduling`, lecturas

**Files:**
- Create: `src/Modules/Scheduling/Modules.Scheduling.Application/ChannelScheduling.cs`
- Modify: `SchedulingRepositories.cs` (`IAppointmentReadRepository`), `src/Modules/Scheduling/Modules.Scheduling.Infrastructure/Persistence/SchedulingRepositories.cs:319`, `SchedulingErrorCodes.cs`, `AppointmentCancellation.cs` (reglas), `AppointmentStarts.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:825` (`AddScoped<IChannelScheduling, ChannelScheduling>()`)
- Test: `tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests/SchedulingChannelServiceTests.cs` (nuevo, sobre `SchedulingApiHarness`), `tests/Modules/Scheduling/Modules.Scheduling.UnitTests/AppointmentCancellationRulesTests.cs`, `SchedulingErrorCodesTests.cs`

**Interfaces:**
- Consumes: Task 3 (`AvailabilitySearch`).
- Produces (todo público en `Modules.Scheduling.Application`; los instantes de salida en la zona del tenant, `TimeZoneInfo.ConvertTime`):
  - `SchedulingErrorCodes.ResourceNotChoosable = "scheduling.appointment.resource_not_choosable"`.
  - `AppointmentCancellationRules.ChangeByChatUntil(DateTimeOffset startsAt, int cutoffHours) → DateTimeOffset` (`startsAt - cutoffHours h`); `TooLateToMoveMessage(int cutoffHours) → string` («Ya no puedes cambiar la hora de esta cita por tu cuenta: se puede hasta {n} horas antes. Comunícate con nosotros.»); `NotMovableByChatMessage` = «Esta cita sólo la puede mover el negocio.»
  - `AppointmentStarts.EnsureClientChoice(LoadedAvailability, Service, IReadOnlyList<Session>, DateTimeOffset startsAt, ResourceId requested)` — opción de las reglas (`ignoreOccupancy: true`) de ese recurso en ese inicio sin `AllowClientChoice` → `SchedulingDomainException(ResourceNotChoosable)`.
  - `IAppointmentReadRepository.ListUpcomingByCustomerAsync(Guid tenantId, Guid customerId, DateTimeOffset now, int take, CancellationToken) → Task<IReadOnlyList<Appointment>>` — sin seguimiento, `Pending`/`Confirmed`, `starts_at >= now`, por inicio y luego id.
  - Registros:
    - `ChannelReadiness(bool ModuleEnabled, int BookableServices)`
    - `ChannelBookableService(Guid ServiceId, string Name, int DurationMinutes)`; `ChannelServiceList(string TimeZone, IReadOnlyList<ChannelBookableService> Services, bool Truncated)`
    - `ChannelSlotResource(Guid ResourceId, string Name)`; `ChannelSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt, int SeatsLeft, IReadOnlyList<ChannelSlotResource> Resources)`; `ChannelAvailability(string TimeZone, Guid ServiceId, IReadOnlyList<ChannelSlot> Slots, bool Truncated)`
    - `ChannelAppointment(Guid AppointmentId, string Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZone, Guid ServiceId, string ServiceName, string ResourceName, string LocationName, bool CanChangeByChat, bool CanRescheduleByChat, DateTimeOffset ChangeByChatUntil)`; `ChannelAppointmentList(string TimeZone, IReadOnlyList<ChannelAppointment> Appointments, bool Truncated)`
    - `ChannelStartCheck(string Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZone, string ServiceName, string ResourceName, string LocationName)`
  - `public interface IChannelScheduling` (lecturas de esta tarea):
    - `Task<ChannelReadiness> GetReadinessAsync(Guid tenantId, CancellationToken)` — no lanza con el módulo apagado: `ModuleEnabled: false, BookableServices: 0`.
    - `Task<ChannelServiceList> ListBookableServicesAsync(Guid tenantId, int take, CancellationToken)`
    - `Task<ChannelAvailability> SearchAvailabilityAsync(Guid tenantId, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, int take, CancellationToken)`
    - `Task<ChannelAppointmentList> ListCustomerAppointmentsAsync(Guid tenantId, Guid customerId, int take, CancellationToken)`
    - `Task<ChannelAppointment?> FindCustomerAppointmentAsync(Guid tenantId, Guid customerId, Guid appointmentId, CancellationToken)` — cualquier estado; `null` si no existe o es de otro cliente.
    - `Task<ChannelStartCheck> CheckStartAsync(Guid tenantId, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken)` — lanza lo mismo que `BookAsync` sin candado ni escritura.
  - Todo método: `TenantModuleGuard.EnsureEnabledAsync(…, TenantModuleKeys.Scheduling, …)` (salvo `GetReadinessAsync`) → `SchedulingFirstAccess.EnsureAsync` → `ResourceScope.All`. «Reservable» = activo, `BookableByChannel`, y alguna franja no archivada lo ofrece con algún recurso activo; una sola función privada `BookableServicesAsync` la comparten readiness y lista. Servicio archivado, de otro tenant o sin `BookableByChannel` en la búsqueda → `SchedulingFieldErrors.Throw("serviceId", …)`. `resourceId` archivado o inexistente → `SchedulingNotFound.Resource`; que ninguna opción del rango deja elegir → `ResourceNotChoosable`. En la búsqueda, `Resources` va vacío en un inicio cuya opción no deja elegir. Hora local inexistente en la zona (`TimeZoneInfo.IsInvalidTime`) → `OutsideRules`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
// AppointmentCancellationRulesTests
[Fact] ChangeByChatUntilIsTheStartMinusTheCutoff() => Assert.Equal(start.AddHours(-24), AppointmentCancellationRules.ChangeByChatUntil(start, 24));
// SchedulingErrorCodesTests: el código nuevo tiene el prefijo scheduling. y está en la lista de la prueba
// SchedulingChannelServiceTests
[Fact] ReadinessCountsOnlyChannelServicesOfferedByAnActiveBlock()   // 3 servicios: uno sin bookableByChannel, uno sin franja → BookableServices == 1
[Fact] ReadinessWithTheModuleOffIsNotEnabled()                      // ModuleEnabled == false, sin excepción
[Fact] ListsBookableServicesByNameWithTheCap()                      // take 2 de 3 → Truncated, orden por nombre
[Fact] SearchRejectsAServiceWithoutBookableByChannel()              // ValidationException con "serviceId"
[Fact] SearchHidesResourcesWhereTheBlockDoesNotAllowChoice()        // slot.Resources vacío; con resourceId de esa franja → ResourceNotChoosable
[Fact] SearchReturnsLocalTimesInTheTenantZone()                     // StartsAt.Offset == offset de la zona del tenant
[Fact] UpcomingByCustomerSkipsCancelledPastAndOtherCustomers()
[Fact] FindReturnsNullForAnotherCustomersAppointment()
[Fact] CanRescheduleByChatIsFalseForANonChannelService()
[Fact] CheckStartOfANonexistentLocalTimeIsOutsideRules()            // hora del salto de horario (zona de prueba con DST)
[Fact] EveryReadWithTheModuleOffIsForbidden()                       // RequestForbiddenException "tenancy.module_not_enabled"
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests`. Esperado: `CS0246 IChannelScheduling`.
- [ ] **Step 3: Implementa** las firmas.
- [ ] **Step 4: GREEN** — unitarias de las dos clases tocadas y `dotnet test tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests --filter "FullyQualifiedName~SchedulingChannelServiceTests" -- xUnit.MaxParallelThreads=4`. Esperado: PASS. Mira antes el plan de ejecución de `ListUpcomingByCustomerAsync` (`EXPLAIN`) en la base de prueba; sólo si hace seq scan sobre `appointments` se agrega el índice `(tenant_id, customer_id, starts_at)` en una migración aparte.
- [ ] **Step 5: Commit** — `feat(scheduling): sessionless reads of the agenda for channels`

### Task 5: `IChannelScheduling`, escrituras

**Files:**
- Modify: `ChannelScheduling.cs`, `AppointmentBooking.cs`, `AppointmentRescheduling.cs`
- Test: `SchedulingChannelServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 3 y 4.
- Produces:
  - `ChannelBooking(Guid TenantId, Guid AgentId, Guid CustomerId, Guid? ConversationId, Guid ServiceId, DateTime LocalStartsAt, Guid? ResourceId, string? Note, string ClientRequestId)`; `ChannelWriteResult(ChannelAppointment Appointment, bool AlreadyDone)`.
  - `IChannelScheduling` gana:
    - `Task<ChannelWriteResult> BookAsync(ChannelBooking booking, CancellationToken)` — `BookingInput(Source: Channel, RequireClientChoice: true)` con `SchedulingActor.Agent(AgentId)`; `AlreadyDone = outcome.Replayed`. Al `ResourceConflictException` con `SlotTaken`, busca la llave una vez más con `FindByClientRequestAsync`; si hay cita, `AlreadyDone: true` con esa cita; si no, relanza.
    - `Task<ChannelWriteResult> CancelAsync(Guid tenantId, Guid agentId, Guid customerId, Guid appointmentId, string? reason, CancellationToken)` — `IAppointmentRepository.FindAsync` sin `If-Match`; ajena o inexistente → `SchedulingNotFound.Appointment`; ya `Cancelled` → `AlreadyDone: true` sin escribir; si no, `AppointmentCancellation.CancelAsync(…, byClient: true, SchedulingActor.Agent(agentId))`.
    - `Task<ChannelWriteResult> RescheduleAsync(Guid tenantId, Guid agentId, Guid customerId, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken)` — misma carga; mismo inicio y (`resourceId` nulo o el mismo) → `AlreadyDone: true` sin escribir; si no, `AppointmentRescheduling.RescheduleAsync(…, byClient: true, requireClientChoice: true)`.
  - `AppointmentBooking`: con `RequireClientChoice` y `ResourceId`, `AppointmentStarts.EnsureClientChoice` después de `RulesCandidates`.
  - `AppointmentRescheduling` con `byClient`: cita abierta y servicio sin `BookableByChannel` → `SchedulingDomainException(InvalidTransition, NotMovableByChatMessage)`; fuera de `ClientMayCancel(appointment.StartsAt, now, cutoff)` → `InvalidTransition` con `TooLateToMoveMessage`; con `requireClientChoice` y recurso pedido, `EnsureClientChoice`. Escribe con `actor.HistoryMemberId` y `actor.ByChannel`.

- [ ] **Step 1: Escribe las pruebas que fallan**

```csharp
[Theory, InlineData("Automatic","Confirmed"), InlineData("Manual","Pending")]
BookFollowsConfirmationModeAsAChannelAppointment(mode, status)   // Source Channel, ConversationId, Status, history[0].ByChannel true, ActorMemberId null
[Fact] BookAuditsAsTheAgent()                                     // audit.entries: actor_type AiAgent, actor_id == agentId, action scheduling.appointment.created
[Fact] SameKeyInParallelBooksOnceAndBothDescribeIt()             // dos BookAsync con la misma llave y recursos distintos (Task.WhenAll): 1 fila; un AlreadyDone true; mismo AppointmentId
[Fact] RepeatingTheKeyReturnsTheStoredAppointmentEvenWithOtherArguments()
[Fact] BookOfANonChannelServiceIsOutsideRules()
[Fact] BookOnAResourceThatCannotBeChosenIsResourceNotChoosable()
[Fact] BookWithAnInactiveCustomerFailsOnCustomerId()              // ValidationException con "customerId"
[Fact] AnotherCustomersAppointmentIsNotFound()                    // Cancel y Reschedule → ResourceNotFoundException; la cita no cambia
[Fact] CancelInsideTheCutoffIsInvalidTransition()
[Fact] CancellingTwiceIsAlreadyDone()
[Fact] RescheduleInsideTheCutoffIsInvalidTransition()
[Fact] RescheduleOfANonChannelServiceIsInvalidTransition()
[Fact] RescheduleToTheSameTimeIsAlreadyDone()                     // Version sin cambio
[Fact] AgentRescheduleMarksTheHistoryByChannel()                  // último evento Rescheduled, ByChannel true, auditoría AiAgent
[Fact] BackOfficeWritesLeaveByChannelFalse()                      // en SchedulingAppointmentTransitionsApiTests: POST cancel por HTTP → history[^1].byChannel == false
[Fact] WritesWithTheModuleOffAreForbidden()
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests`. Esperado: `CS1061 'IChannelScheduling' does not contain 'BookAsync'`.
- [ ] **Step 3: Implementa** las firmas.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests --filter "FullyQualifiedName~SchedulingChannelServiceTests|FullyQualifiedName~BackOfficeWritesLeaveByChannelFalse" -- xUnit.MaxParallelThreads=4`. Esperado: PASS.
- [ ] **Step 5: Commit** — `feat(scheduling): sessionless booking, cancel and reschedule for channels`

### Cierre del bloque 1

- [ ] `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` (si hay uno de otra sesión, espera).
- [ ] Uno por vez: `dotnet test tests/Modules/Scheduling/Modules.Scheduling.UnitTests`; `dotnet test tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests -- xUnit.MaxParallelThreads=4`; `dotnet test tests/ArchitectureTests/ArchitectureTests`. Esperado: todo PASS, con la red de la extracción (`SchedulingBookingApiTests`, `SchedulingAvailabilityApiTests`, `SchedulingAppointmentTransitionsApiTests`, `SchedulingAuditApiTests`) sin cambios en las pruebas.
- [ ] Una revisión del bloque (diff del bloque contra el spec §6 y D-15 a D-18).

---

## Bloque 2 — Ai y Bootstrapper (Tasks 6 a 10)

Un implementador. Al terminar: «Cierre del bloque 2» y una revisión del bloque.

### Task 6: `config.tools.scheduling` y validador

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Domain/AgentConfig.cs:54,73`, `AgentConfigValidator.cs:32-38`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentConfigValidatorTests.cs`, `AgentExportTests.cs`

**Interfaces:**
- Produces:
  - `AgentTools(bool Catalog, bool OrderStatus, bool QuotationDraft, bool Scheduling = false)`; `AgentConfig.Default` pasa `Scheduling: false`.
  - `public sealed record AgentSchedulingReadiness(bool ModuleEnabled, int BookableServices) { public bool Ready => ModuleEnabled && BookableServices > 0; }` (Domain).
  - `AgentValidationContext(…, bool? QuotationBillingAccountEligible = null, AgentSchedulingReadiness? Scheduling = null)`.
  - Con `config.Tools.Scheduling` y `Scheduling` no nulo: `ModuleEnabled == false` → `Error("scheduling_module_disabled", "tools.scheduling", "Activa el módulo Agenda para que el agente pueda agendar citas.")`; si no, `BookableServices == 0` → `Error("scheduling_no_bookable_service", "tools.scheduling", "Ningún servicio de la Agenda se puede reservar por WhatsApp en una franja activa: marca uno como «Se puede reservar por WhatsApp» y ofrécelo en una franja.")`.

- [ ] **Step 1: Pruebas que fallan**

```csharp
[Fact] SchedulingWithTheModuleOffIsAnError()       // código scheduling_module_disabled, path "tools.scheduling", Error
[Fact] SchedulingWithoutBookableServicesIsAnError()
[Fact] SchedulingReadyHasNoIssue()
[Fact] SchedulingOffIgnoresTheReadiness()          // Tools.Scheduling false + readiness en cero → ningún código scheduling_*
// AgentExportTests
[Fact] AConfigWithoutTheSchedulingKeyReadsFalse()  // AgentConfig.FromJson de un documento guardado sin "scheduling" → Tools.Scheduling == false
[Fact] SchedulingTravelsInTheExport()              // export/import conserva true
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests`. Esperado: `CS1739 Scheduling`.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentConfigValidatorTests|FullyQualifiedName~AgentExportTests"`. PASS.
- [ ] **Step 5: Commit** — `feat(ai): scheduling tools switch and its publish checks`

### Task 7: contrato común, puerto `IAiScheduling` y códigos

**Files:**
- Create: `src/Modules/Ai/Modules.Ai.Application/AgentSchedulingTools.cs` (en esta tarea sólo el puerto y sus registros)
- Modify: `AgentTools.cs:8-79,445-466`, `AgentHandoff.cs:47-78`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentSchedulingContractTests.cs` (nuevo), `tests/Bootstrapper/Bootstrapper.UnitTests/HandoffCauseKindParityTests.cs`

**Interfaces:**
- Produces:
  - `AgentToolNames.ListBookableServices = "list_bookable_services"`, `CheckAvailability = "check_availability"`, `BookAppointment = "book_appointment"`, `ListMyAppointments = "list_my_appointments"`, `CancelAppointment = "cancel_appointment"`, `RescheduleAppointment = "reschedule_appointment"`; `Writes(name)` verdadero para `create_quotation_draft`, `book_appointment`, `cancel_appointment`, `reschedule_appointment`.
  - `AiServicePrincipal.SchedulingRead = "scheduling.read"`, `SchedulingReadOwn = "scheduling.read_own"`, `SchedulingBookOwn = "scheduling.book_own"`, `SchedulingChangeOwn = "scheduling.change_own"` (en `Capabilities`).
  - `AgentToolErrors`: `SchedulingUnavailable = "scheduling_unavailable"`, `SlotTaken = "slot_taken"`, `SessionFull = "session_full"`, `OutsideRules = "outside_rules"`, `AppointmentChangeBlocked = "appointment_change_blocked"`, `AppointmentNotFound = "appointment_not_found"`, `ServiceNotFound = "service_not_found"`, `ResourceNotChoosable = "resource_not_choosable"`, `AppointmentChanged = "appointment_changed"`.
  - `AgentHandoffCodes.AppointmentChangeBlocked = "appointment_change_blocked"`, `AppointmentCustomerUnavailable = "appointment_customer_unavailable"`, los dos en `All`.
  - `AgentToolInvocation(Guid TenantId, Guid JobId, string CallId, Guid? CustomerId, bool IsTest, AgentQuotationDraft? QuotationDraft = null, Guid AgentId = default, Guid? ConversationId = null, int BookingOrdinal = 1)`.
  - En `AgentSchedulingTools.cs`:
    - `public sealed record AiSchedulingCaller(Guid TenantId, Guid AgentId, Guid JobId, Guid? ConversationId, Guid? CustomerId, bool IsTest)` y `public static AiSchedulingCaller From(AgentToolInvocation invocation)`.
    - `public sealed record AiSchedulingResult<T>(T? Value, string? ErrorCode, string? HandoffCode) where T : class` con `Ok(T)`, `Error(string code)`, `HandedOff(string errorCode, string handoffCode)`.
    - `AiBookableService(Guid ServiceId, string Name, int DurationMinutes)`; `AiBookableServices(string TimeZone, IReadOnlyList<AiBookableService> Services, bool Truncated)`; `AiSlotResource(Guid ResourceId, string Name)`; `AiSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt, int SeatsLeft, IReadOnlyList<AiSlotResource> Resources)`; `AiAvailability(string TimeZone, Guid ServiceId, IReadOnlyList<AiSlot> Slots, bool Truncated)`; `AiAppointment(Guid AppointmentId, string Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZone, Guid ServiceId, string ServiceName, string ResourceName, string LocationName, bool CanChangeByChat, bool CanRescheduleByChat, DateTimeOffset ChangeByChatUntil)`; `AiAppointments(string TimeZone, IReadOnlyList<AiAppointment> Appointments, bool Truncated)`; `AiAppointmentWrite(AiAppointment Appointment, bool AlreadyDone)`; `AiStartPreview(string Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZone, string ServiceName, string ResourceName, string LocationName)`.
    - `public interface IAiScheduling`:
      - `Task<AgentSchedulingReadiness> GetReadinessAsync(Guid tenantId, CancellationToken)`
      - `Task<AiSchedulingResult<AiBookableServices>> ListBookableServicesAsync(AiSchedulingCaller, CancellationToken)`
      - `Task<AiSchedulingResult<AiAvailability>> SearchAvailabilityAsync(AiSchedulingCaller, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, CancellationToken)`
      - `Task<AiSchedulingResult<AiAppointmentWrite>> BookAsync(AiSchedulingCaller, Guid serviceId, DateTime localStartsAt, Guid? resourceId, string? note, string clientRequestId, CancellationToken)`
      - `Task<AiSchedulingResult<AiStartPreview>> PreviewBookingAsync(AiSchedulingCaller, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken)`
      - `Task<AiSchedulingResult<AiAppointments>> ListMyAppointmentsAsync(AiSchedulingCaller, CancellationToken)`
      - `Task<AiSchedulingResult<AiAppointment>> FindMyAppointmentAsync(AiSchedulingCaller, Guid appointmentId, CancellationToken)`
      - `Task<AiSchedulingResult<AiAppointmentWrite>> CancelAsync(AiSchedulingCaller, Guid appointmentId, string? reason, CancellationToken)`
      - `Task<AiSchedulingResult<AiAppointmentWrite>> RescheduleAsync(AiSchedulingCaller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken)`

- [ ] **Step 1: Pruebas que fallan**

```csharp
// AgentSchedulingContractTests
[Fact] WritesCoverTheThreeSchedulingWrites()   // Writes true para book/cancel/reschedule y create_quotation_draft; false para las tres lecturas
[Fact] ThePrincipalHasTheFourSchedulingCapabilities()
[Fact] TheCallerComesFromTheInvocation()       // AiSchedulingCaller.From copia TenantId, AgentId, JobId, ConversationId, CustomerId, IsTest
// HandoffCauseKindParityTests.EveryAgentHandoffCodeSurvivesTheThreadCleaning: agrega
Assert.Contains("appointment_change_blocked", AgentHandoffCodes.All);
Assert.Contains("appointment_customer_unavailable", AgentHandoffCodes.All);
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests`. Esperado: `CS0117 'AgentToolNames' does not contain 'BookAppointment'`.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentSchedulingContractTests"`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~HandoffCauseKindParityTests"`. PASS.
- [ ] **Step 5: Commit** — `feat(ai): scheduling port, tool names, capabilities and handoff codes`

### Task 8: ordinal de reserva en el perfil del agente

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentTurnRunner.cs:214-252`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentTurnRunnerTests.cs`

**Interfaces:**
- Consumes: Task 7 (`AgentToolInvocation.AgentId|ConversationId|BookingOrdinal`, `AgentToolNames.BookAppointment`).
- Produces: `AgentProfile` guarda `int _bookings`; arma la invocación con `_context.AgentId`, `_context.ConversationId` y `BookingOrdinal: _bookings + 1`; después de ejecutar, si `toolCall.Name == AgentToolNames.BookAppointment && !result.IsError`, `_bookings++`. Una llamada rechazada por `one_write_per_response` no llega a `ExecuteAsync` y no cuenta.

- [ ] **Step 1: Pruebas que fallan** (herramienta falsa `book_appointment` que anota la invocación y responde según un guion)

```csharp
[Fact] TwoBookingsInOneJobGetOrdinalsOneAndTwo()          // dos respuestas del modelo, cada una con un book → ordinales [1, 2]
[Fact] AFailedBookingDoesNotConsumeAnOrdinal()            // primera responde Error("slot_taken"), segunda ok → ordinales [1, 1]
[Fact] ResumedJobFindsTheBookingItAlreadyMade()           // dos RunAsync con el mismo JobId: la primera llamada de cada corrida lleva ordinal 1 → misma llave "ai:{jobId:N}:book:1"
[Fact] ASecondSchedulingWriteInOneResponseIsRejected()    // una respuesta con book y cancel: cancel vuelve one_write_per_response; la siguiente book lleva ordinal 2
[Fact] TheInvocationCarriesAgentAndConversation()
```

- [ ] **Step 2: RED** — las pruebas compilan desde la Task 7; corre `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentTurnRunnerTests"`. Esperado: FAIL (`Assert.Equal([1, 2], …)` recibe `[1, 1]`; `AgentId` vacío).
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: GREEN** — mismo comando. PASS.
- [ ] **Step 5: Commit** — `feat(ai): count bookings per job for the idempotency key`

### Task 9: las seis herramientas y sus simuladas

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentSchedulingTools.cs`
- Modify: `src/Modules/Ai/Modules.Ai.Infrastructure/AiInfrastructureExtensions.cs:57-61`
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentSchedulingToolsTests.cs` (nuevo, con un `FakeAiScheduling`)

**Interfaces:**
- Consumes: Tasks 6 y 7.
- Produces:
  - `public static class AgentSchedulingFormat { public const string LocalPattern = "yyyy-MM-dd'T'HH:mm"; public static string Local(DateTimeOffset value); public static bool TryParseLocal(string? text, out DateTime value); public static bool TryParseDay(string? text, out DateOnly value); }` — cultura invariante, exacto; `TryParseLocal` devuelve `DateTimeKind.Unspecified`.
  - Argumentos, cada uno con `public static readonly AiToolDefinition Definition` (JSON de spec §5 tal cual) y `TryRead`:
    - `CheckAvailabilityArguments.TryRead(JsonElement, out Guid serviceId, out DateOnly from, out DateOnly to, out Guid? resourceId)` — `to >= from`, `to - from <= 13` días (14 inclusive); `MaxDays = 14`.
    - `BookAppointmentArguments.TryRead(JsonElement, out Guid serviceId, out DateTime localStartsAt, out Guid? resourceId, out string? note)` — `NoteMaxLength = 500`; vacía → `null`.
    - `CancelAppointmentArguments.TryRead(JsonElement, out string appointmentId, out string? reason)` — el id viaja como texto para que la simulada reconozca `PRUEBA-…`; la real exige `Guid.TryParse` (si no, `invalid_arguments`) — `ReasonMaxLength = 500`. Definición: `appointmentId` string, `reason` `["string","null"]`, los dos en `required`.
    - `RescheduleAppointmentArguments.TryRead(JsonElement, out string appointmentId, out DateTime localStartsAt, out Guid? resourceId)` (mismo criterio del id). Definición: tres campos en `required`.
    - `ListBookableServicesTool` y `ListMyAppointmentsTool` usan el esquema vacío del spec.
  - Herramientas (`IAgentTool`), `IsOfferedTo`: lecturas `tools.Scheduling`; reales `!isTest && tools.Scheduling`; simuladas `isTest && tools.Scheduling`:
    - `ListBookableServicesTool(IAiScheduling)` (`SchedulingRead`), `CheckAvailabilityTool(IAiScheduling)` (`SchedulingRead`, tope 30 inicios), `ListMyAppointmentsTool(IAiScheduling)` (`SchedulingReadOwn`, tope 10).
    - `BookAppointmentTool(IAiScheduling)` y `SimulatedBookAppointmentTool(IAiScheduling)` (`SchedulingBookOwn`); `internal static string BookAppointmentTool.ClientRequestId(AgentToolInvocation invocation) => $"ai:{invocation.JobId:N}:book:{invocation.BookingOrdinal}"`.
    - `CancelAppointmentTool` / `SimulatedCancelAppointmentTool`, `RescheduleAppointmentTool` / `SimulatedRescheduleAppointmentTool` (`SchedulingChangeOwn`).
  - Mapeo común: `ErrorCode` → `AgentToolResult.Error(code)`; con `HandoffCode` además `with { Handoff = new AgentToolHandoff(handoffCode, null) }` (D-19). Resultados con los nombres de campo de spec §5 y las horas por `AgentSchedulingFormat.Local`; `book_appointment` con `alreadyBooked` = `AlreadyDone`; cancelar y mover con `alreadyDone`.
  - Simuladas (D-7): no llaman `BookAsync`, `CancelAsync` ni `RescheduleAsync`. Book: `PreviewBookingAsync`, devuelve `appointmentId = $"PRUEBA-{invocation.JobId.ToString("N")[..8].ToUpperInvariant()}"` (como el borrador de cotización) con `alreadyBooked: false`. Cancel/Reschedule: un id que empieza por `PRUEBA-` se da por encontrado y abierto: cancelar responde `{ appointmentId, status: "Cancelled", alreadyDone: false }`; mover responde `{ appointmentId, status: "Pending", startsAt, alreadyDone: false }` con el `startsAt` pedido, sin validar el inicio (la cita simulada no guarda su servicio; decisión de este plan, el spec §7.7 sólo dice que se da por encontrada); un id real → `FindMyAppointmentAsync`; `null` → `appointment_not_found`; cerrada o `!CanChangeByChat` (cancelar) / `!CanRescheduleByChat` (mover) → error y traspaso `appointment_change_blocked`; mover además llama `PreviewBookingAsync` con `ServiceId` de la cita.

- [ ] **Step 1: Pruebas que fallan**

```csharp
[Theory, MemberData(nameof(BadCheckAvailabilityArguments))] CheckAvailabilityRejectsBadArguments(string json)  // fecha mala, 15 días, al revés, serviceId no Guid → invalid_arguments, el puerto no se llama
[Fact] BookRejectsANoteOver500()
[Fact] BookUsesTheJobAndOrdinalKey()                     // ClientRequestId == "ai:{jobId:N}:book:2" con BookingOrdinal 2
[Fact] BookFormatsLocalTimesWithoutOffset()               // result.startsAt == "2026-11-24T09:00", timeZone presente
[Fact] AlreadyBookedDescribesTheStoredAppointment()
[Fact] ChangeBlockedHandsOffToTheConfiguredDestination()  // Handoff.Code appointment_change_blocked, MemberId null
[Fact] CustomerUnknownOnBookHandsOff()                    // HandedOff("customer_unknown","appointment_customer_unavailable") → Handoff
[Fact] RealWritesAreNotOfferedInTests() / SimulatedWritesAreOnlyOfferedInTests() / ReadsAreOfferedInBoth()
[Fact] NothingIsOfferedWithTheSwitchOff()
[Fact] SimulatedBookNeverWrites()                         // FakeAiScheduling.BookCalls == 0; appointmentId empieza por "PRUEBA-"
[Fact] SimulatedCancelOfAnotherCustomersAppointmentIsNotFound()
[Fact] SimulatedCancelPastTheCutoffHandsOff()
[Fact] TheAgentStaysWithinAnthropicStrictLimits()         // todas las definiciones del agente + 3 finales: <= 20 herramientas, uniones ["string","null"] == 8, 0 opcionales (cada propiedad en required)
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests`. Esperado: `CS0246 BookAppointmentTool`.
- [ ] **Step 3: Implementa** y registra las nueve `AddScoped<IAgentTool, …>()`.
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentSchedulingToolsTests"`. PASS.
- [ ] **Step 5: Commit** — `feat(ai): scheduling tools for the agent and their playground twins`

### Task 10: prompt, editor, actividad y adaptador de Bootstrapper

**Files:**
- Modify: `src/Modules/Ai/Modules.Ai.Application/AgentPromptBuilder.cs:121`, `AgentEditorBuilder.cs`, `AgentDtos.cs:67-82`, `AgentActivity.cs:24-26`
- Modify: `src/Modules/Ai/Modules.Ai.Infrastructure/Persistence/AgentActivityReader.cs:50-70`
- Create: `src/Bootstrapper/AiScheduling.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:2004` (`AddScoped<IAiScheduling, AiScheduling>()`)
- Test: `tests/Modules/Ai/Modules.Ai.UnitTests/AgentPromptBuilderTests.cs`, `AgentEditorBuilderTests.cs`; `tests/Modules/Ai/Modules.Ai.IntegrationTests/AgentActivityApiTests.cs`; `tests/Bootstrapper/Bootstrapper.UnitTests/AiSchedulingTests.cs` (nuevo)

**Interfaces:**
- Consumes: Task 5 (`IChannelScheduling`), Tasks 6 y 7.
- Produces:
  - `AgentPromptBuilder.SchedulingSection` (`internal const string`, el texto de spec §7.8 literal), agregado por `SystemPromptOf` sólo con `config.Tools.Scheduling`.
  - `AgentEditorBuilder` gana `IAiScheduling scheduling`; `ValidateAsync` pasa `Scheduling: await scheduling.GetReadinessAsync(…)` sólo con `Tools.Scheduling`; `BuildAsync` siempre la lee y llena `AgentEditorDto.Scheduling`.
  - `public sealed record AgentSchedulingReadinessDto(bool ModuleEnabled, int BookableServices, bool Ready)`; `AgentEditorDto(…, IReadOnlyList<AiBillingAccountOption> QuotationBillingAccounts, AgentSchedulingReadinessDto Scheduling)`.
  - `AgentTodayDto(int Handled, int HandedOff, int QuotationDrafts, int Appointments)`; `None = new(0, 0, 0, 0)`. El lector cuenta `book_appointment` con `Outcome == Ok` y `ResultJson.Contains("\"alreadyBooked\":false")`, sólo de sesiones (no pruebas), como los borradores.
  - `internal sealed class AiScheduling(IChannelScheduling channel, IConversationQueries conversations, ConversationCustomerResolver resolver) : IAiScheduling`:
    - Cliente: con `ConversationId`, `conversations.FindAsync(tenantId, conversationId)` en **cada** llamada (D-9); `BookAsync` usa `resolver.ResolveAsync(row)` (`Linked` → cliente; `NoUser`/`Unavailable` → `HandedOff("customer_unknown", "appointment_customer_unavailable")`); las demás sólo `row.CustomerId`. Sin `ConversationId` (playground): `caller.CustomerId`. Sin cliente: `ListMyAppointmentsAsync` devuelve `Ok(new AiAppointments(timeZone, [], false))`, con la zona que trae `channel.ListBookableServicesAsync(tenantId, 0, …)` (sigue pasando por la guarda del módulo); `FindMy`/`Cancel`/`Reschedule` → `Error("appointment_not_found")`.
    - Traducción (spec §8): `RequestForbiddenException` `tenancy.module_not_enabled` → `scheduling_unavailable`; `ResourceConflictException` `…slot_taken` / `…session_full` → `slot_taken` / `session_full`; `SchedulingDomainException` `…outside_rules` → `outside_rules`, `…invalid_transition` → `HandedOff("appointment_change_blocked", "appointment_change_blocked")`, `…resource_not_choosable` → `resource_not_choosable`, `…before_start` → `tool_failed`; `ResourceNotFoundException` con `…appointment.not_found` → `appointment_not_found`, `…service.not_found` → `service_not_found`, `…resource.not_found` → `resource_not_choosable`; `ValidationException` por campo `serviceId` → `service_not_found`, `resourceId` → `resource_not_choosable`, `customerId` → `HandedOff("customer_unknown", "appointment_customer_unavailable")`; `RequestConcurrencyException` → `appointment_changed`. Lo demás sube y lo convierte en `tool_failed` el bucle.

- [ ] **Step 1: Pruebas que fallan**

```csharp
// AgentPromptBuilderTests
[Fact] TheSchedulingSectionAppearsOnlyWithTheTool()
[Fact] TheSystemPromptIsStableAcrossCallsWithScheduling()   // dos builds con distinta hora: SystemPrompt idéntico
// AgentEditorBuilderTests
[Fact] TheEditorAlwaysCarriesTheSchedulingReadiness()       // Tools.Scheduling false → Scheduling != null, Ready según el fake
[Fact] ValidationReadsReadinessOnlyWithTheTool()            // fake cuenta llamadas: 0 con la herramienta apagada
// AgentActivityApiTests
[Fact] TodayCountsBookingsButNotRepeats()                   // dos tool_calls book ok: alreadyBooked false y true, + una de prueba → Appointments == 1
// AiSchedulingTests (FakeChannelScheduling, FakeConversationQueries, ConversationCustomerResolver con dobles de IConversationRepository, IMessagingCustomerDirectory, IMessagingNotifier)
[Theory, MemberData(nameof(Translations))] TranslatesEachSchedulingFailure(Exception thrown, string code, string? handoff)  // una fila por renglón de spec §8
[Fact] RereadsTheConversationCustomerOnEveryCall()          // la conversación cambia de cliente entre dos llamadas → la segunda usa el nuevo
[Fact] BookCreatesAndLinksTheCustomerWhenMissing()          // row.CustomerId null + UserId → EnsureAsync y LinkCustomerAsync llamados; ChannelBooking.CustomerId == el nuevo
[Fact] BookWithoutChannelIdentityHandsOff()                 // UserId null → HandoffCode appointment_customer_unavailable; channel.BookAsync no llamado
[Fact] ListWithoutCustomerIsEmpty()
[Fact] CancelWithoutCustomerIsNotFound()
[Fact] ThePlaygroundUsesTheTestCustomerAndNeverTheConversation()  // ConversationId null → caller.CustomerId; conversations.FindAsync no llamado
[Fact] BookPassesAgentConversationAndKey()                  // ChannelBooking.AgentId, ConversationId, ClientRequestId tal cual
```

- [ ] **Step 2: RED** — `dotnet build tests/Modules/Ai/Modules.Ai.UnitTests`; `dotnet build tests/Bootstrapper/Bootstrapper.UnitTests`. Esperado: `CS1729`/`CS0246 AiScheduling`.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: GREEN** — `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests --filter "FullyQualifiedName~AgentPromptBuilderTests|FullyQualifiedName~AgentEditorBuilderTests"`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~AiSchedulingTests"`; `dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests --filter "FullyQualifiedName~TodayCountsBookingsButNotRepeats" -- xUnit.MaxParallelThreads=4`. PASS.
- [ ] **Step 5: Commit** — `feat(ai): wire the scheduling port, prompt, editor readiness and activity`

### Cierre del bloque 2

- [ ] `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` (espera si hay uno ajeno).
- [ ] Uno por vez: `dotnet test tests/Modules/Ai/Modules.Ai.UnitTests`; `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests`; `dotnet test tests/Modules/Ai/Modules.Ai.IntegrationTests -- xUnit.MaxParallelThreads=4`; `dotnet test tests/ArchitectureTests/ArchitectureTests`. PASS (sin cambios en `AiLayerTests` ni `SchedulingLayerTests`).
- [ ] Una revisión del bloque (diff contra spec §5, §7, §8, §9, D-14 y D-19).

---

## Bloque 3 — Reglas, merge y estado (Task 11)

### Task 11: regla del módulo, suite antes del merge, `develop` y `docs/estado.md`

**Files:**
- Modify: `.claude/rules/ai-copiloto.md` (gotchas: llave `ai:{jobId:N}:book:{n}` y por qué el ordinal no cuenta fallas; `IChannelScheduling` es el único camino sin sesión; las simuladas no escriben; `by_channel` sólo del agente), `.claude/rules/build-y-despliegue.md` si hace falta una nota de la migración.
- Modify (en `develop`, al mergear): `docs/estado.md` (fila de la Agenda y del agente IA, y el Registro con el commit del merge; nota de despliegue: backend antes que frontend, riesgo de pestaña vieja en el editor de agentes y en las reglas de la Agenda, spec §12).

- [ ] **Step 1:** commit de las reglas en la rama — `docs(ai): scheduling agent gotchas`.
- [ ] **Step 2: Suite de los proyectos tocados**, uno por vez y mirando antes `Get-Process testhost, dotnet`: `Modules.Scheduling.UnitTests`, `Modules.Scheduling.IntegrationTests`, `Modules.Ai.UnitTests`, `Modules.Ai.IntegrationTests`, `Bootstrapper.UnitTests`, `Modules.Messaging.IntegrationTests`, `Modules.Customers.IntegrationTests`, `ArchitectureTests` (con `-- xUnit.MaxParallelThreads=4` en las de integración). Esperado: todo PASS. Sin eval.
- [ ] **Step 3: Revisión fuerte** (modelo más capaz, toda la rama contra el spec y el Review Focus). Correcciones en commits aparte.
- [ ] **Step 4: Merge a `develop`** (nunca a `main`): `git -C <checkout principal> branch --show-current` debe decir `develop`; merge `--no-ff` de `feature/agenda-agente-ia`.
- [ ] **Step 5:** en `develop`, actualiza `docs/estado.md` con evidencia (clases y commit) — `docs(estado): log the scheduling agent merge`.

---

## Cobertura del spec

| Spec | Tasks |
| --- | --- |
| §5 herramientas, §5.7 contrato común | 7, 8, 9 |
| §6.1–6.2 extracción, auditoría por agente | 3 |
| §6.3 servicio sin sesión, D-11/D-16, D-12, D-15 | 4, 5 |
| §6.4 / D-18 remitente y su nombre | 1, 2 |
| §7.1–7.2 puerto y adaptador, D-9, D-6 | 7, 10 |
| §7.3 / D-14 llave y carrera | 5, 8, 9 |
| §7.5 / D-3 / D-19 traspasos | 7, 9, 10 |
| §7.7 / D-7 playground | 9 |
| §7.8 prompt | 10 |
| §8 errores | 10 |
| §9.1–9.4 config, validador, editor, exportar | 6, 10 |
| §9.5 actividad | 10 |
| §10 / D-17 historial `byChannel` | 1, 3, 5 |
| §12 despliegue | 1 (migración), 11 |
| §14 contrato | 1, 2, 6, 9, 10 |
