# Mensajería: asistente en la conversación y horarios de atención — plan de implementación (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que la conversación sepa si la atiende un asistente (`None | Active | Paused`), que tomar, responder y
transferir lo pausen, que pausar y devolver sean explícitos con `If-Match`, y que Messaging tenga un catálogo de
horarios de atención con nombre, por conexión, con predeterminado, duplicar, festivos de Colombia y `openNow`.

**Architecture:** Cinco columnas y seis `CHECK` en `messaging.conversations`, dos eventos del historial y dos
endpoints, siguiendo `ConversationAssignmentSteps`. Un agregado nuevo `BusinessHoursSchedule` (semana y
excepciones en `jsonb`, festivos calculados por `ColombianHolidays`) con su tabla de asignación por conexión, seis
endpoints bajo `/messaging/business-hours`, un permiso nuevo y el puerto `IBusinessHours` para el motor futuro.
Nada pone `Active` en producción: lo hace el harness (D-A2).

**Tech Stack:** .NET 10, EF Core + Npgsql (PostgreSQL 18), FluentValidation, xUnit v3 + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-21-mensajeria-asistente-y-horarios-design.md` (léelo entero; los D-A#,
D-H#, I-# y § de este plan son de ahí). El frontend tiene su propio plan en
`qep-frontend/docs/superpowers/plans/2026-10-21-mensajeria-asistente-y-horarios-frontend.md`.

**Rama:** `feature/mensajeria-asistente-y-horarios` desde `develop`, en el worktree
`.claude/worktrees/mensajeria-asistente-y-horarios`. Antes de **cada** commit `git branch --show-current` tiene que
decir esa rama; si dice `main` o `develop`, para. Nunca `git stash`.

## Global Constraints

- TDD obligatorio: RED con la salida literal antes del GREEN, los dos en el reporte de cada tarea.
- Conventional commits en español, **sin atribución de IA** (sin `Co-Authored-By`; la regla del repo pisa cualquier
  recordatorio del harness).
- No corras la suite completa: `Modules.Messaging.UnitTests`, `Modules.Messaging.IntegrationTests` y
  `ArchitectureTests`, siempre con `--filter "FullyQualifiedName~<Clase>"`. Docker corriendo para las de integración.
- `Api.exe`/`dotnet run` corriendo bloquea `build`, `test` y `ef`: detenlo primero.
- **`dotnet format --verify-no-changes` ya falla en `develop`** por `ReplyMarksReadApiTests.cs` (`WHITESPACE`,
  l.139 y 208; `docs/estado.md` l.102). No lo arregles en esta rama: verifica el formato con
  `dotnet format --verify-no-changes --include <tus archivos>`.
- Aislamiento de tenant en doble capa: ruta `{tenantId:guid}` + política, **y** `MessagingAuthorization.EnsureAsync`
  en el handler antes del repositorio. Otro tenant → 403, nunca 404. Todo método de repositorio recibe `tenantId`.
- Permiso nuevo = **dos mitades** en `QepServiceCollectionExtensions`: `PermissionDefinition` y `AddPolicy`. Sin la
  política el síntoma es 500. Los handlers se registran a mano; sin registro, 500.
- Texto libre en un comando → validador de FluentValidation (422 `validation.failed` con `errors`) aunque el dominio
  valide. Ningún status a mano: `src/Api/ApiExceptionHandler.cs` (409 `ResourceConflictException`, 412
  `RequestConcurrencyException`, 428 `PreconditionRequiredException`, 422 `DomainException`).
- Errores de base se traducen en `MessagingUnitOfWork` **por nombre de índice** (constantes en `MessagingDbContext`).
- Auditoría y evento en la misma transacción que el cambio (`IMessagingAuditRecorder`, `IConversationEvents` →
  `SaveChangesAsync`), y `IMessagingNotifier.ConversationChangedAsync` después.
- Messaging.Application sólo referencia `Modules.Messaging*`, `Modules.Tenancy*` y `Modules.Audit*`
  (`MessagingLayerTests`); `ITenantClock` está permitido. Nunca Integrations: las conexiones entran por
  `IMessagingConnectionDirectory` con adaptador en Bootstrapper.
- Migraciones con el factory de diseño (`MessagingDbContextFactory` ya existe):
  `dotnet ef migrations add <Nombre> --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`.
- BFF: cada DTO nuevo dice en su `<summary>` por qué tiene esa forma; colecciones fijas completas (`weekly` siempre
  7, `counts.automated` siempre); enums por nombre.
- Textos para personas y comentarios en español colombiano tuteando; códigos, rutas, enums y permisos tal cual.
- `docs/estado.md` **no se toca** en esta rama: se actualiza en `develop` al mergear.
- `Directory.Packages.props`, `global.json`, `.mcp.json`: no se tocan. Sin paquetes nuevos.

## Review Focus

1. **Enviar un mensaje en una conversación `Active` sin asesor** la autoasigna **y** la pausa en la misma transacción;
   si se olvida la mitad de `TryAutoAssignAsync`, el síntoma es un 500 por `CK_conversations_automation_assignment`,
   no un 422. Prueba en Task 3 (`SendingInAnActiveConversationAutoAssignsAndPausesIt`).
2. **Reanudar una conversación resuelta** responde `messaging.conversation.not_open`, no `not_available`: `Open` se
   comprueba antes que el estado de automatización (§4.2). Prueba en Task 1 y Task 3.
3. **Un festivo con excepción propia abre en la franja de la excepción** (D-H9: la excepción gana al festivo), y el
   mismo día sin excepción cierra. Prueba en Task 4.
4. **Una conexión borrada en Integrations que seguía asignada** no bloquea el `DELETE` del horario (fila huérfana, D-H10)
   y se limpia. Prueba en Task 6 (`AnOrphanAssignmentDoesNotCountAsInUseAndIsRemoved`).
5. **Dos «predeterminado» a la vez** no dejan dos: el índice `IX_business_hours_tenant_default` se traduce a 409
   `messaging.business_hours.default_conflict`. Prueba en Task 5 (persistencia) y el handler relee antes (Task 6).

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
| --- | --- | --- |
| `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs` | Modificar | Estado de automatización, `StartAutomation`, `PauseAutomation`, `ResumeAutomation`, `Take`/`TransferTo` devuelven lista |
| `src/Modules/Messaging/Modules.Messaging.Domain/ConversationAutomation.cs` | Crear | Enums `ConversationAutomationStatus`, `AutomationPauseReason` |
| `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs` | Modificar | Dos tipos y `PauseReason` |
| `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` | Modificar | Códigos de automatización y de horarios |
| `src/Modules/Messaging/Modules.Messaging.Domain/BusinessHoursSchedule.cs` | Crear | Agregado: semana, excepciones, festivos, `IsOpenAt`, `NextChangeAt`, `Duplicate` |
| `src/Modules/Messaging/Modules.Messaging.Domain/BusinessHoursValues.cs` | Crear | `TimeRange`, `WeekdayHours`, `DateException`, `HolidayCountry` |
| `src/Modules/Messaging/Modules.Messaging.Domain/ColombianHolidays.cs` | Crear | Ley 51 de 1983, puro |
| `src/Modules/Messaging/Modules.Messaging.Domain/ConnectionBusinessHours.cs` | Crear | Fila de asignación (sin comportamiento) |
| `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs` | Modificar | `pauseReason` |
| `src/Modules/Messaging/Modules.Messaging.Application/ConversationAssignment.cs` | Modificar | `CommitAsync` con lista de eventos; handlers Take/Transfer/Release |
| `src/Modules/Messaging/Modules.Messaging.Application/ConversationAutomation.cs` | Crear | Comandos y handlers de pausar / reanudar |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs` | Modificar | Acciones nuevas, `ForEvent`, `RecordBusinessHours` |
| `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs` | Modificar | `ConversationRow` y `ConversationListFilter` con automatización |
| `src/Modules/Messaging/Modules.Messaging.Application/IConversationRepository.cs` | Modificar | `TryAutoAssignAsync` deja de ser el único que no conoce la automatización (sin cambio de firma) |
| `src/Modules/Messaging/Modules.Messaging.Application/ListConversations.cs` | Modificar | `Automation` en el query, validador y filtro |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs` | Modificar | `ConversationAutomationDto`, `Automation`, `Automated`, `PauseReason` |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs` | Modificar | `ToSummary` arma `Automation` |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs` | Modificar | `BusinessHoursManage` |
| `src/Modules/Messaging/Modules.Messaging.Application/BusinessHoursPorts.cs` | Crear | `IBusinessHoursRepository`, `IBusinessHours`, `BusinessHoursStatus` |
| `src/Modules/Messaging/Modules.Messaging.Application/BusinessHours.cs` | Crear | DTOs, request, validador, seis handlers |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessagingConnectionDirectory.cs` | Modificar | `ListAllAsync` |
| `src/Modules/Messaging/Modules.Messaging.Application/IMessageTemplates.cs` | Modificar | `ListMessagingConnectionsHandler` completa horario y `openNow` |
| `src/Modules/Messaging/Modules.Messaging.Application/MessageTemplates.cs` | Modificar | `MessagingConnectionDto` con tres campos |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` | Modificar | Columnas, `CHECK`, índice, entidades nuevas, constantes de índices |
| `…/Persistence/Migrations/*_AddConversationAutomation.cs` | Crear (ef) | Columnas, `CHECK`, índice parcial |
| `…/Persistence/Migrations/*_AddBusinessHours.cs` | Crear (ef) | Dos tablas, `CHECK`, índices únicos por SQL |
| `…/Persistence/ConversationRepository.cs` | Modificar | `TryAutoAssignAsync` pausa y escribe el evento |
| `…/Persistence/ConversationQueries.cs` | Modificar | Proyección, filtro, `Automated` |
| `…/Persistence/BusinessHoursRepository.cs` | Crear | Repositorio + `IBusinessHours` |
| `…/Persistence/MessagingUnitOfWork.cs` | Modificar | `name_taken`, `default_conflict` por nombre |
| `…/Persistence/MessagingAuditRecorder.cs` | Modificar | `RecordBusinessHours` |
| `…/MessagingInfrastructureExtensions.cs` | Modificar | Registro del repositorio y del puerto |
| `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` | Modificar | `automation/pause`, `automation/resume`, montar el grupo |
| `src/Modules/Messaging/Modules.Messaging.Api/BusinessHoursEndpoints.cs` | Crear | Seis rutas |
| `src/Bootstrapper/MessagingConnectionDirectory.cs` | Modificar | `ListAllAsync` |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Modificar | Handlers, permiso (dos mitades), rol `admin` |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs` | Modificar | Automatización |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationEventJsonTests.cs` | Modificar | `pauseReason` |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/BusinessHoursScheduleTests.cs` | Crear | Dominio de horarios |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/ColombianHolidaysTests.cs` | Crear | Festivos |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs`, `CampaignTestDoubles.cs`, `WebhookRoutingTests.cs` | Modificar | Dobles de `IMessagingConnectionDirectory`, `IMessagingAuditRecorder`, `ConversationRow` |
| `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingApiHarness.cs` | Modificar | `ActivateAutomationAsync`, `BusinessHoursUrl`, permisos |
| `…/IntegrationTests/MessagingPersistenceTests.cs` | Modificar | Los `CHECK` nuevos |
| `…/IntegrationTests/ConversationAutomationApiTests.cs` | Crear | Pausar, reanudar, autoasignación, filtro, contador |
| `…/IntegrationTests/BusinessHoursPersistenceTests.cs` | Crear | Índices, `RESTRICT`, `jsonb` ida y vuelta |
| `…/IntegrationTests/BusinessHoursApiTests.cs` | Crear | El catálogo entero |
| `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` | Modificar | El permiso en `admin` y no en `advisor` |
| `README.md` | Modificar | Tabla de endpoints de Messaging y § Horarios de atención |

**Comandos de prueba:**

```powershell
dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/Modules/Messaging/Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~<Clase>"
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~<Clase>"
```

---

### Task 1: Dominio de la automatización en `Conversation`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationAutomation.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/Conversation.cs` (propiedades l.49-111; `Resolve` l.210; `Take` l.235; `TransferTo` l.248; `Release` l.266)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/ConversationEvent.cs` (enum l.4; record l.25)
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` (al final)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationEventJson.cs` (`Serialize` l.11, `Parse` l.32)
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/ConversationTests.cs`, `ConversationEventJsonTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum ConversationAutomationStatus { None, Active, Paused }
  public enum AutomationPauseReason { AdvisorTook, AdvisorReplied, AdvisorTransferred, Manual, Handoff }
  // Conversation
  public ConversationAutomationStatus AutomationStatus { get; private set; }   // None al nacer
  public DateTimeOffset? AutomationStartedAt, AutomationPausedAt { get; private set; }
  public Guid? AutomationPausedByMemberId { get; private set; }
  public AutomationPauseReason? AutomationPauseReason { get; private set; }
  public void StartAutomation(DateTimeOffset now);                                  // reglas debajo del bloque
  public ConversationEvent PauseAutomation(Guid? actorMemberId, AutomationPauseReason reason, DateTimeOffset now);   // exige Active
  public IReadOnlyList<ConversationEvent> ResumeAutomation(Guid actorMemberId, DateTimeOffset now);                  // exige Open y Paused
  public IReadOnlyList<ConversationEvent> Take(Guid memberId, DateTimeOffset now);                                   // antes ConversationEvent?
  public IReadOnlyList<ConversationEvent> TransferTo(Guid actorMemberId, Guid targetMemberId, DateTimeOffset now);  // antes ConversationEvent?
  // Release sigue devolviendo ConversationEvent? (no toca la automatización, D-A4)
  // ConversationEvent gana AutomationPauseReason? PauseReason = null; ConversationEventType suma AutomationPaused, AutomationResumed
  ```
  Reglas de `StartAutomation`: `Status != Open` → `ConversationNotOpen`; `AssignedMemberId != null` → `AutomationNotAvailable`
  («una conversación con asesor no la atiende el asistente»); `Active` → `AutomationAlreadyActive`; `Paused` →
  `AutomationNotAvailable` (el motor no reactiva lo que un humano pausó: eso es `ResumeAutomation`).
  `PauseAutomation`: `Paused` → `AutomationAlreadyPaused`; `None` → `AutomationNotAvailable`. `ResumeAutomation`:
  `Status != Open` primero (`ConversationNotOpen`), `None` → `AutomationNotAvailable`, `Active` → `AutomationAlreadyActive`;
  con asesor devuelve `[Released(actor, Previous: asesor), AutomationResumed(actor)]`, sin asesor `[AutomationResumed(actor)]`.
  `Take` sobre `Active`: `[Taken(...), AutomationPaused(Actor: memberId, PauseReason: AdvisorTook)]`; `TransferTo` igual
  con `AdvisorTransferred` y actor el que transfiere; sobre `None`/`Paused` sólo el evento de asignación; sin cambio → lista vacía.
  `Resolve` llama a `ClearAutomation()` privado (las cinco propiedades a su valor vacío). `Reopen` no toca nada.
- Produces, en `MessagingErrorCodes`: `AutomationNotAvailable = "messaging.automation.not_available"`,
  `AutomationAlreadyPaused = "messaging.automation.already_paused"`, `AutomationAlreadyActive = "messaging.automation.already_active"`.
- Produces, en `ConversationEventJson`: `Serialize` escribe `"pauseReason":"AdvisorTook"` cuando no es `null`; `Parse` lo lee con
  `Enum.TryParse<AutomationPauseReason>` (desconocido → `null`, no falla).

- [ ] **Step 1: Escribe las pruebas que fallan** (en `ConversationTests.cs`, con `Open()`, `Now`, `Andres`, `Beatriz` del archivo; `Active()` = `Open()` + `StartAutomation(Now)`)

```csharp
[Fact] public void StartAutomationNeedsAnOpenUnassignedConversationInNone()
// Open() → StartAutomation OK: Status Active, StartedAt Now, Version 2
// Open() + Take(Andres) → StartAutomation lanza AutomationNotAvailable
// Open() + Resolve(Now) → StartAutomation lanza ConversationNotOpen
// Active() → StartAutomation lanza AutomationAlreadyActive
[Fact] public void TakingAnActiveConversationPausesItWithAdvisorTook()
// Active().Take(Andres, t) → [Taken(Actor: Andres), AutomationPaused(Actor: Andres, PauseReason: AdvisorTook)]; AutomationStatus Paused; PausedByMemberId Andres; PausedAt t
[Fact] public void TransferringAnActiveConversationPausesItWithAdvisorTransferred()
// Active().TransferTo(Andres, Beatriz, t) → [Transferred(Actor: Andres, Target: Beatriz), AutomationPaused(Actor: Andres, PauseReason: AdvisorTransferred)]
[Fact] public void TakingANoneConversationReturnsOnlyTheTakenEvent()      // Open().Take → lista de 1; Take otra vez por el mismo → lista vacía
[Fact] public void ReleasingLeavesTheAutomationPaused()                    // Active + Take + Release → Paused sigue
[Fact] public void ResolvingClearsTheAutomation()                          // Active + Resolve → None y las cuatro columnas null
[Fact] public void ResumingWithAnAssigneeReleasesFirst()                   // Active + Take(Andres) + ResumeAutomation(Beatriz) → [Released(Actor: Beatriz, Previous: Andres), AutomationResumed(Actor: Beatriz)]; AssignedMemberId null; Active
[Fact] public void ResumingChecksOpenBeforeTheAutomationState()            // Active + Resolve → ResumeAutomation lanza ConversationNotOpen (no AutomationNotAvailable)
[Theory] [InlineData(...)] public void PausingManuallyRejectsPausedAndNone()  // Paused → AutomationAlreadyPaused; None → AutomationNotAvailable
```
En `ConversationEventJsonTests.cs`: ida y vuelta de `new ConversationEvent(AutomationPaused, Actor: Andres, PauseReason: AdvisorReplied)`
(el JSON contiene `"pauseReason":"AdvisorReplied"`) y `Parse` de un `{"type":"AutomationPaused","pauseReason":"Whatever"}` → evento con `PauseReason null`.
Arregla los tests existentes de `Take`/`TransferTo` que esperan `ConversationEvent?`: ahora `Assert.Single(list)`.

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test tests/Modules/Messaging/Modules.Messaging.UnitTests --filter "FullyQualifiedName~ConversationTests|FullyQualifiedName~ConversationEventJsonTests"`
Expected: error de compilación (`StartAutomation` no existe).

- [ ] **Step 3: Implementa el dominio y el JSON**

`Take`/`TransferTo` devuelven `IReadOnlyList<ConversationEvent>` (`[]` cuando antes era `null`). Los llamadores en
Application (`ConversationAssignment.cs`) dejan de compilar: en esta tarea sólo adáptalos lo mínimo para compilar
(`CommitAsync` recibe la lista; la Task 3 la termina). El JSON escribe el enum con `ToString()`.

- [ ] **Step 4: Corre y verifica que pasan** (los dos filtros) — y `dotnet build` de la solución compila.

- [ ] **Step 5: Commit** — `feat(messaging): estado de automatización en la conversación`

---

### Task 2: Persistencia y lectura de la automatización

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs` (`ConfigureConversation` l.104-182; constantes de `CHECK` l.21-36)
- Create (ef): `…/Persistence/Migrations/*_AddConversationAutomation.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IConversationQueries.cs` (`ConversationRow` l.6; `ConversationListFilter` l.41)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingDtos.cs` (`ConversationSummary` l.29; `ConversationCountsDto` l.54; `MessageEventDto` l.85)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingSupport.cs` (`ToSummary` l.83)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ListConversations.cs` (query l.8; validador l.18; filtro l.81)
- Modify: `…/Persistence/ConversationQueries.cs` (proyección l.12; `ListAsync` l.18-83; `CountsAsync` l.85-102)
- Modify: dobles `tests/…/UnitTests/MessagingTestBed.cs` (`InMemoryConversationQueries` l.277) si construyen `ConversationRow`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/MessagingPersistenceTests.cs`, `ConversationListFiltersApiTests.cs`

**Interfaces:**
- Produces, en `MessagingDbContext`: columnas `automation_status varchar(16) NOT NULL DEFAULT 'None'` (`HasConversion<string>()`,
  `HasDefaultValue(ConversationAutomationStatus.None)`, `HasSentinel((ConversationAutomationStatus)(-1))` como `channel`),
  `automation_started_at`, `automation_paused_at`, `automation_paused_by_member_id`, `automation_pause_reason varchar(32)`
  (`HasConversion<string>()`). Seis `HasCheckConstraint` con los nombres y expresiones **exactos** de §4.1
  (`CK_conversations_automation_status`, `_pause`, `_started`, `_reason`, `_assignment`, `_resolved`) y el índice parcial
  `IX_conversations_tenant_automation_activity` (`tenant_id, automation_status, last_activity_at DESC` con filtro
  `automation_status <> 'None'`). Expresiones como `public const string` junto a `AssignmentCheck`.
- Produces, en Application:
  ```csharp
  // ConversationRow gana (al final, con default):
  ConversationAutomationStatus AutomationStatus = ConversationAutomationStatus.None, DateTimeOffset? AutomationStartedAt = null,
  DateTimeOffset? AutomationPausedAt = null, Guid? AutomationPausedByMemberId = null, AutomationPauseReason? AutomationPauseReason = null
  public enum AutomationFilter { Any, Active, Paused, None }
  // ConversationListFilter gana AutomationFilter? Automation = null (null = sin filtro; Any = <> 'None')
  /// <summary>null cuando la conversación no tiene asistente: la bandeja no dibuja nada (D-A10). PausedBy resuelve el nombre como AssignedTo.</summary>
  public sealed record ConversationAutomationDto(string Status, DateTimeOffset StartedAt, DateTimeOffset? PausedAt, MemberRefDto? PausedBy, string? PauseReason);
  // ConversationSummary gana ConversationAutomationDto? Automation = null (último parámetro)
  // ConversationCountsDto gana int Automated (Open y <> 'None'); MessageEventDto gana string? PauseReason = null
  // ListConversationsQuery gana string? Automation = null; el validador acepta active|paused|any|none
  ```
- `ToSummary` arma `Automation` con `memberNames[PausedByMemberId]` o «Miembro eliminado» (igual que `AssignedTo`).
  `MessageMapping.ToDto` (`MessagingSupport.cs` l.149) pasa `PauseReason?.ToString()`.

- [ ] **Step 1: Escribe las pruebas que fallan**

En `MessagingPersistenceTests.cs`, junto a `TheNewChecksHoldIdentityAssignmentAndEventShape` (l.138):
```csharp
[Fact] public async Task TheAutomationChecksHoldStatusPauseStartedAssignmentAndResolved()
// Siembra una conversación Open sin asesor. Cada UPDATE viola un solo CK y se asserta ConstraintName:
//  SET automation_status = 'Bot'                                                       → CK_conversations_automation_status
//  SET automation_status = 'Paused', automation_started_at = now()                     → CK_conversations_automation_pause   (sin paused_at ni reason)
//  SET automation_status = 'Active'                                                    → CK_conversations_automation_started (started_at null)
//  SET automation_status='Active', automation_started_at=now(), automation_pause_reason='Nope' → CK_conversations_automation_reason
//  SET automation_status='Active', automation_started_at=now(), assigned_member_id=gen_random_uuid(), assigned_at=now() → CK_conversations_automation_assignment
//  SET status='Resolved', automation_status='Active', automation_started_at=now()      → CK_conversations_automation_resolved
// Y el camino feliz: SET automation_status='Active', automation_started_at=now() afecta 1 fila.
```
En `ConversationListFiltersApiTests.cs`: `AutomationFilterAndCountNeedTheHarnessToActivate` — con dos conversaciones,
`UPDATE … SET automation_status='Active', automation_started_at=now()` en una (SQL directo acá; el helper del harness
llega en Task 3), `GET ?automation=active` devuelve 1, `?automation=none` devuelve 1, `?automation=any` devuelve 1,
`counts.automated == 1`; sin el parámetro devuelve 2 y el resumen de la activa trae `automation.status == "Active"` y la
otra `automation == null`; `?automation=bot` → 422 `validation.failed`.

- [ ] **Step 2: Genera la migración y corre las pruebas para verificar que fallan**

Run: `dotnet ef migrations add AddConversationAutomation --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`
(después de configurar el `DbContext`; revisa que el `Up` traiga las cinco columnas, los seis `CHECK` y el índice parcial —
si EF no genera el filtro del índice, escríbelo con `migrationBuilder.Sql` como `AddResolutionReasons` hace con el suyo).
Run: `dotnet test …IntegrationTests --filter "FullyQualifiedName~MessagingPersistenceTests|FullyQualifiedName~ConversationListFiltersApiTests"`
Expected: FAIL — la lista no filtra y `counts.automated` no existe.

- [ ] **Step 3: Implementa proyección, filtro y contador**

`ListAsync`: `Automation` → `Any` = `AutomationStatus != None`; `None` = `== None`; `Active`/`Paused` = igualdad.
`CountsAsync`: quinta consulta `count(Status == Open && AutomationStatus != None)`.

- [ ] **Step 4: Corre y verifica que pasan**

- [ ] **Step 5: Commit** (incluye la migración y el snapshot) — `feat(messaging): columnas, filtro y contador de automatización`

---

### Task 3: Pausar, reanudar, y la pausa al tomar, transferir y responder

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/ConversationAssignment.cs` (`CommitAsync` l.49; handlers l.65-110)
- Create: `src/Modules/Messaging/Modules.Messaging.Application/ConversationAutomation.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingAuditRecorder.cs` (`MessagingAuditActions` l.27)
- Modify: `…/Persistence/ConversationRepository.cs` (`TryAutoAssignAsync` l.106-150)
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (después de `release`, l.177)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers junto a l.701-705)
- Modify: `tests/…/IntegrationTests/MessagingApiHarness.cs`
- Test: `tests/…/IntegrationTests/ConversationAutomationApiTests.cs` (crear), `AssignmentApiTests.cs` (si algo cambia de forma)

**Interfaces:**
- Produces:
  ```csharp
  // MessagingAuditActions
  public const string AutomationPaused = "messaging.conversation.automation_paused";
  public const string AutomationResumed = "messaging.conversation.automation_resumed";
  public static string ForEvent(ConversationEventType type)   // Taken→Taken, Transferred→Transferred, Released→Released, AutoTaken→AutoTaken, AutomationPaused→AutomationPaused, AutomationResumed→AutomationResumed; otro → ArgumentOutOfRangeException
  // ConversationAssignmentSteps
  public Task<ConversationSummary> CommitAsync(Guid tenantId, Conversation conversation, IReadOnlyList<ConversationEvent> changes, DateTimeOffset now, CancellationToken ct)
  //   por cada evento: events.Record + audit.Record(tenantId, actorUserId, ForEvent(e.Type), ...); si hay alguno: SaveChanges + notifier; siempre relee y arma el resumen
  // ConversationAutomation.cs
  public sealed record PauseAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;
  public sealed record ResumeAutomationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;
  public sealed class PauseAutomationHandler(ConversationAssignmentSteps steps)   // LoadAsync → EnsureVersion → [PauseAutomation(member, Manual, now)] → CommitAsync
  public sealed class ResumeAutomationHandler(ConversationAssignmentSteps steps)  // LoadAsync → EnsureVersion → ResumeAutomation(member, now) → CommitAsync
  ```
  Los tres handlers de asignación pasan a `CommitAsync(..., conversation.Take(...), ...)` sin derivar la acción a mano
  (`TransferConversationHandler` deja de mirar `change.Type`: `ForEvent` lo hace).
- `TryAutoAssignAsync` (firma igual): el `UPDATE` suma
  `automation_status = CASE WHEN automation_status = 'Active' THEN 'Paused' ELSE automation_status END`,
  `automation_paused_at = CASE WHEN automation_status = 'Active' THEN {at} ELSE automation_paused_at END`,
  `automation_paused_by_member_id = CASE … THEN {memberId} …`, `automation_pause_reason = CASE … THEN 'AdvisorReplied' …`,
  y el `RETURNING` devuelve `connection_id AS "ConnectionId", (automation_pause_reason = 'AdvisorReplied' AND automation_paused_at = {at}) AS "PausedNow"`
  (record privado `AutoAssignRow(Guid ConnectionId, bool PausedNow)`). Con `PausedNow`, además del `AutoTaken`, inserta
  `ConversationEventRows.InsertAsync(… new ConversationEvent(AutomationPaused, Actor: memberId, PauseReason: AdvisorReplied) …)`
  y `audit.Record(tenantId, actorUserId, MessagingAuditActions.AutomationPaused, conversationId, at)`, en la misma transacción.
- Endpoints (`MessagingEndpoints.cs`, mismo bloque que `take`): `POST /conversations/{conversationId:guid}/automation/pause`
  y `…/automation/resume`, `RequireAuthorization(MessagingPermissions.ConversationManage)`, `Produces<ConversationSummary>()`,
  problemas 403/404/412/422/428; handlers privados `PauseAutomationAsync` / `ResumeAutomationAsync` con `RequireVersion(httpContext)`.
- Harness: `public static async Task ActivateAutomationAsync(string connectionString, Guid conversationId)` ejecuta
  `UPDATE messaging.conversations SET automation_status='Active', automation_started_at=now(), version=version+1, updated_at=now() WHERE id=@id AND status='Open' AND assigned_member_id IS NULL`
  y `Assert.Equal(1, affected)`. `ActionUrl(f, "automation/pause")` reutiliza el helper de `AssignmentApiTests`.

- [ ] **Step 1: Escribe las pruebas que fallan** (`ConversationAutomationApiTests.cs`, `ArrangeAsync` copiado de `AssignmentApiTests` + `ActivateAutomationAsync`)

```csharp
[Fact] WithoutIfMatchIs428AndAStaleVersionIs412()                 // pause sin If-Match → 428; resume con version-1 → 412
[Fact] PausingManuallyRecordsTheEventTheAuditAndTheActor()          // 200; summary.automation.status "Paused", pauseReason "Manual", pausedBy.displayName del caller; último mensaje de GET /messages es evento AutomationPaused con pauseReason; audit.entries con action automation_paused == 1
[Fact] PausingTwiceIsAlreadyPausedAndANoneConversationIsNotAvailable()   // 422 messaging.automation.already_paused / messaging.automation.not_available
[Fact] TakingPausesWithAdvisorTookAndReleasingKeepsItPaused()      // take → automation Paused/AdvisorTook, dos eventos nuevos (Taken, AutomationPaused); release → sigue Paused
[Fact] ResumingWithAnAssigneeReleasesAndReactivates()               // take, luego resume → 200, assignedTo null, automation.status Active, eventos Released + AutomationResumed, audit automation_resumed == 1
[Fact] ResumingAResolvedConversationIsNotOpen()                     // resolve → resume → 422 messaging.conversation.not_open (no not_available)
[Fact] ResolvingClearsTheAutomation()                               // resolve → summary.automation == null; reopen → sigue null
[Fact] SendingInAnActiveConversationAutoAssignsAndPausesIt()        // Active sin asesor; POST /messages texto (ventana abierta por el webhook) → 201/200; summary: assignedTo caller, automation Paused/AdvisorReplied; eventos AutoTaken + AutomationPaused; audit automation_paused == 1
[Fact] SendingAsTheAssigneeDoesNotWriteASecondPause()               // Active → take (pausa) → enviar → sólo un evento AutomationPaused en el hilo
[Fact] TheFilterAndTheCountSeeTheHarnessActivation()                // ?automation=active → 1 ítem; counts.automated 1 (mueve acá o deja en ConversationListFiltersApiTests la de Task 2; una sola copia)
```

- [ ] **Step 2: Corre y verifica que fallan**

Run: `dotnet test …IntegrationTests --filter "FullyQualifiedName~ConversationAutomationApiTests"`
Expected: 404 en las rutas nuevas / compilación por `ActivateAutomationAsync`.

- [ ] **Step 3: Implementa handlers, endpoints, `TryAutoAssignAsync`, registro y harness**

Registro: `AddScoped<ICommandHandler<PauseAutomationCommand, ConversationSummary>, PauseAutomationHandler>()` y el de
`Resume`, junto a los de asignación (l.701-705).

- [ ] **Step 4: Corre y verifica que pasan** — también `AssignmentApiTests`, `SendAssignmentApiTests` y `ConversationLifecycleApiTests` (tocan el mismo `CommitAsync`).

- [ ] **Step 5: Commit** — `feat(messaging): pausar y devolver al asistente; tomar, transferir y responder lo pausan`

---

### Task 4: Dominio de horarios: `BusinessHoursSchedule` y `ColombianHolidays`

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Domain/BusinessHoursValues.cs`, `BusinessHoursSchedule.cs`, `ColombianHolidays.cs`, `ConnectionBusinessHours.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.UnitTests/BusinessHoursScheduleTests.cs`, `ColombianHolidaysTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public readonly record struct TimeRange(int From, int To);                        // minutos locales, [From, To), 0 ≤ From < To ≤ 1440
  public sealed record WeekdayHours(int Day, IReadOnlyList<TimeRange> Ranges);      // Day 1 = lunes … 7 = domingo
  public sealed record DateException(DateOnly Date, IReadOnlyList<TimeRange> Ranges);   // Ranges vacío = no atiende
  public enum HolidayCountry { CO }
  public static class ColombianHolidays
  {
      public static IReadOnlySet<DateOnly> For(int year);                             // 18 fechas, Ley 51 de 1983 (regla completa en el spec §5.2)
      public static DateOnly EasterSunday(int year);                                  // Meeus/Jones/Butcher
      public static IReadOnlyList<DateOnly> Upcoming(DateOnly fromInclusive, int count);
  }
  public sealed class BusinessHoursSchedule
  {
      public const int NameMaxLength = 60, MaxRangesPerDay = 3, MaxExceptions = 100, MinutesPerDay = 1440;
      public static readonly TimeSpan NextChangeHorizon = TimeSpan.FromDays(14);
      public Guid Id, TenantId; public string Name; public bool IsDefault; public IReadOnlyList<WeekdayHours> Weekly; // siempre 7, ordenados 1..7
      public IReadOnlyList<DateException> Exceptions; public HolidayCountry? HolidayCountry; public DateTimeOffset CreatedAt, UpdatedAt; public long Version;
      public static BusinessHoursSchedule Create(Guid id, Guid tenantId, string? name, IReadOnlyList<WeekdayHours> weekly, IReadOnlyList<DateException> exceptions, HolidayCountry? holidayCountry, DateOnly today, DateTimeOffset now);
      public void Update(string? name, IReadOnlyList<WeekdayHours> weekly, IReadOnlyList<DateException> exceptions, HolidayCountry? holidayCountry, DateOnly today, DateTimeOffset now);  // Version++
      public void MakeDefault(DateTimeOffset now); public void ClearDefault(DateTimeOffset now);                 // Version++ las dos
      public BusinessHoursSchedule Duplicate(Guid newId, string name, DateTimeOffset now);                        // misma semana, excepciones y país; IsDefault false
      public bool IsOpenAt(DateTimeOffset instant, TimeZoneInfo timeZone);
      public DateTimeOffset? NextChangeAt(DateTimeOffset instant, TimeZoneInfo timeZone);                        // null si nada cambia en 14 días
      public IReadOnlyList<DateOnly> UpcomingHolidays(DateOnly fromLocal, int months = 12);                      // vacío sin HolidayCountry
      public static string DuplicateName(string name, int attempt);                                               // attempt 1 → "{name} (copia)", n → "{name} (copia n)", recortado a 60
  }
  public sealed class ConnectionBusinessHours { public Guid ConnectionId, TenantId, ScheduleId; public DateTimeOffset AssignedAt; public static ConnectionBusinessHours Create(Guid connectionId, Guid tenantId, Guid scheduleId, DateTimeOffset now); }
  ```
  Reglas (`MessagingDomainException`): nombre `Trim` 1..60 → `BusinessHoursNameInvalid`; `weekly` exactamente 7 con días
  1..7 sin repetir, cada día 0..3 franjas válidas, ordenadas por `From`, sin solaparse (`a.To <= b.From`) →
  `BusinessHoursRangesInvalid`; excepciones con fechas únicas, cada una con 0..3 franjas válidas → `BusinessHoursExceptionsInvalid`;
  **las excepciones con `Date < today` se descartan antes** de aplicar el tope de 100 (D-H15). `IsOpenAt`: fecha local
  con `TimeZoneInfo.ConvertTime`; excepción de esa fecha → sus franjas; si no, festivo (`HolidayCountry == CO &&
  ColombianHolidays.For(year).Contains(date)`) → cerrado; si no, franjas del día de la semana (`DayOfWeek.Monday → 1`,
  `Sunday → 7`). `NextChangeAt`: recorre minuto a minuto no; recorre los **bordes** de las franjas de cada día local desde
  `instant` hasta `instant + 14 días` y devuelve el primer instante cuyo estado difiere del actual (convierte el minuto
  local a UTC con `TimeZoneInfo`; en un hueco de horario de verano usa el primer instante válido posterior).
- Produces, en `MessagingErrorCodes`: `BusinessHoursNotFound = "messaging.business_hours.not_found"`,
  `BusinessHoursNameInvalid = "…name_invalid"`, `BusinessHoursRangesInvalid = "…ranges_invalid"`,
  `BusinessHoursExceptionsInvalid = "…exceptions_invalid"`, `BusinessHoursHolidayCountryInvalid = "…holiday_country_invalid"`,
  `BusinessHoursNameTaken = "…name_taken"`, `BusinessHoursDefaultConflict = "…default_conflict"`,
  `BusinessHoursInUse = "…in_use"`, `BusinessHoursIsDefault = "…is_default"`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`ColombianHolidaysTests.cs`:
```csharp
[Fact] public void TwentyTwentySixHasTheEighteenOfficialDates()   // tabla literal: 2026-01-01, 01-12, 03-23, 04-02, 04-03, 05-01, 05-18, 06-08, 06-15, 06-29, 07-20, 08-07, 08-17, 10-12, 11-02, 11-16, 12-08, 12-25
[Fact] public void TwentyTwentySevenHasTheEighteenOfficialDates()  // 2027-01-01, 01-11, 03-22, 03-25, 03-26, 05-01, 05-10, 05-31, 06-07, 07-05, 07-20, 08-07, 08-16, 10-18, 11-01, 11-15, 12-08, 12-25
[Fact] public void AFixedHolidayOnAWeekendIsNotMoved()              // 2027-12-25 es sábado y queda
[Fact] public void AnEmilianiHolidayAlreadyOnMondayStays()           // 2025-01-06 es lunes → 2025-01-06
[Fact] public void EasterOf2026IsApril5AndDrivesFiveHolidays()       // EasterSunday(2026) == 2026-04-05; jueves/viernes santo 04-02/04-03; Ascensión 05-18; Corpus 06-08; Sagrado Corazón 06-15
[Fact] public void UpcomingCrossesTheYearBoundary()                  // Upcoming(2026-11-20, 3) == [2026-12-08, 2026-12-25, 2027-01-01]
```
(Verifica las dos tablas contra el calendario oficial antes de dar la prueba por buena: la prueba es la autoridad del
cálculo, así que no la deduzcas del código.)

`BusinessHoursScheduleTests.cs` (`Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")`, `Today = new DateOnly(2026,10,21)`, semana `Office()` lun–vie 480–1080):
```csharp
[Fact] CreateTrimsTheNameAndKeepsTheSevenDaysInOrder()
[Theory] AnInvalidNameIsRejected(null/""/61 chars) → BusinessHoursNameInvalid
[Theory] InvalidRangesAreRejected()        // From<0, To>1440, From==To, From>To, solapadas (480–720 y 600–1080), cuatro franjas, seis días, día 8, día repetido → BusinessHoursRangesInvalid
[Fact] RepeatedExceptionDatesAreRejected()  → BusinessHoursExceptionsInvalid
[Fact] PastExceptionsArePrunedBeforeTheCap()  // 100 pasadas + 1 futura → Exceptions.Count == 1; 101 futuras → BusinessHoursExceptionsInvalid
[Fact] IsOpenInsideAndOutsideARange()      // miércoles 2026-10-21 10:00 Bogotá (15:00Z) → true; 19:00 Bogotá → false; sábado → false
[Fact] MidnightRangeIsOpenUntilTheEnd()    // franja 1320–1440: 23:59 abierto, 00:00 del siguiente cerrado
[Fact] AClosedExceptionClosesAWorkingDay() // excepción 2026-10-21 sin franjas → cerrado a las 10:00
[Fact] AnExceptionWithRangesOpensOutsideTheWeek()   // domingo con excepción 540–780 → abierto 11:00
[Fact] AHolidayClosesTheDayUnlessAnExceptionOpensIt()  // HolidayCountry CO: lunes 2026-11-02 10:00 → false; con excepción 2026-11-02 480–720 → true a las 10:00; HolidayCountry null → true (Review Focus 3)
[Fact] NextChangeFromOpenIsTodaysClose()   // 10:00 → 2026-10-21T23:00:00Z
[Fact] NextChangeFromClosedSkipsWeekendAndHoliday()  // viernes 2026-10-30 19:00 Bogotá; lunes 11-02 festivo → 2026-11-03T13:00:00Z
[Fact] NextChangeIsNullWhenNothingChangesInTwoWeeks()   // semana vacía → null; semana 0–1440 todos los días → null
[Fact] DaylightSavingIsHonoured()          // America/New_York, semana 540–1020, 2026-11-01 (cambio a las 2:00): abierto a las 09:00 local de ese domingo si el domingo atiende; NextChangeAt convierte con el offset correcto (-05:00 después del cambio)
[Fact] DuplicateCopiesEverythingButDefault()
[Theory] DuplicateNameNumbersAndTrims()    // ("Oficina",1) → "Oficina (copia)"; ("Oficina",2) → "Oficina (copia 2)"; nombre de 58 chars → recortado a 60
[Fact] UpcomingHolidaysIsEmptyWithoutACountry()
```

- [ ] **Step 2: Corre y verifica que fallan** — Run: `dotnet test …UnitTests --filter "FullyQualifiedName~BusinessHoursScheduleTests|FullyQualifiedName~ColombianHolidaysTests"`

- [ ] **Step 3: Implementa el dominio** (sin EF, sin JSON: el agregado guarda listas; la serialización es de Infrastructure)

- [ ] **Step 4: Corre y verifica que pasan**

- [ ] **Step 5: Commit** — `feat(messaging): agregado de horarios de atención y festivos de Colombia`

---

### Task 5: Persistencia de horarios, puerto `IBusinessHours` y conexiones del tenant

**Files:**
- Modify: `…/Persistence/MessagingDbContext.cs` (DbSets l.62-79; constantes de índices l.42-60; `ConfigureResolutionReason` l.360 como modelo)
- Create (ef): `…/Persistence/Migrations/*_AddBusinessHours.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/BusinessHoursPorts.cs`
- Create: `…/Persistence/BusinessHoursRepository.cs`
- Modify: `…/Persistence/MessagingUnitOfWork.cs` (dos `catch` nuevos junto a l.44-51)
- Modify: `…/MessagingInfrastructureExtensions.cs` (registro junto a l.92-93)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IMessagingConnectionDirectory.cs` (l.32), `src/Bootstrapper/MessagingConnectionDirectory.cs` (junto a `ListNamesAsync` l.109), dobles `MessagingTestBed.FakeConnectionDirectory` (l.315) y `WebhookRoutingTests.FakeDirectory` (l.43)
- Test: `tests/…/IntegrationTests/BusinessHoursPersistenceTests.cs` (crear)

**Interfaces:**
- Produces, en `MessagingDbContext`: tablas de §5.1 **con** `holiday_country varchar(2) NULL` y
  `CK_business_hours_holiday_country` (`holiday_country IS NULL OR holiday_country IN ('CO')`). `weekly` y `exceptions` como
  `jsonb` vía `HasConversion` a `string` con `System.Text.Json` (`JsonSerializerDefaults.Web`; `DateOnly` serializa como
  `"yyyy-MM-dd"` de fábrica) y `ValueComparer` por igualdad de secuencia; forma `[{"day":1,"ranges":[{"from":480,"to":1080}]}]`
  y `[{"date":"2026-11-02","ranges":[]}]`. `version` `IsConcurrencyToken()`. Índices únicos por SQL en la migración
  (como `AddResolutionReasons`): `IX_business_hours_tenant_name` sobre `(tenant_id, lower(name))` y
  `IX_business_hours_tenant_default` sobre `(tenant_id) WHERE is_default`; constantes `BusinessHoursNameIndex`,
  `BusinessHoursDefaultIndex`. `connection_business_hours`: PK `connection_id`, FK a `business_hours_schedules`
  `OnDelete(Restrict)` con nombre `FK_connection_business_hours_schedule`, índice `IX_connection_business_hours_schedule`.
- Produces, en `BusinessHoursPorts.cs`:
  ```csharp
  public interface IBusinessHoursRepository
  {
      Task<BusinessHoursSchedule?> FindAsync(Guid tenantId, Guid id, CancellationToken ct);                    // tracking
      Task<IReadOnlyList<BusinessHoursSchedule>> ListAsync(Guid tenantId, CancellationToken ct);              // por nombre, tracking
      Task<BusinessHoursSchedule?> FindDefaultAsync(Guid tenantId, CancellationToken ct);
      Task<bool> NameExistsAsync(Guid tenantId, string name, CancellationToken ct);                          // lower(name)
      void Add(BusinessHoursSchedule schedule); void Remove(BusinessHoursSchedule schedule);
      Task<IReadOnlyList<ConnectionBusinessHours>> ListAssignmentsAsync(Guid tenantId, CancellationToken ct);
      void AddAssignment(ConnectionBusinessHours row); void RemoveAssignment(ConnectionBusinessHours row);
  }
  public sealed record BusinessHoursStatus(bool IsOpen, DateTimeOffset? NextChangeAt, Guid? ScheduleId, string? ScheduleName);
  public interface IBusinessHours
  {
      /// <summary>Asignación de la conexión → predeterminado del tenant → siempre abierto (D-H4).</summary>
      Task<BusinessHoursStatus> GetStatusAsync(Guid tenantId, Guid connectionId, CancellationToken ct);
      Task<IReadOnlyDictionary<Guid, BusinessHoursStatus>> GetStatusesAsync(Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken ct);  // una consulta
  }
  ```
  `BusinessHoursRepository(MessagingDbContext dbContext, ITenantClock tenantClock) : IBusinessHoursRepository, IBusinessHours`.
  `GetStatusesAsync` carga asignaciones + horarios del tenant una vez y evalúa con `TenantCalendar.TimeZone` y `UtcNow`.
- Produces, en `IMessagingConnectionDirectory`:
  ```csharp
  /// <summary>Todas las conexiones del tenant, en cualquier estado (D-H10): la página de horarios lista y valida contra esto, no contra ListActiveAsync.</summary>
  Task<IReadOnlyList<MessagingConnectionRef>> ListAllAsync(Guid tenantId, CancellationToken ct);
  public sealed record MessagingConnectionRef(Guid Id, string Name, ConversationChannel Channel, string Status);   // Status = nombre del estado de Integrations (Active, Paused, …)
  ```
  El adaptador de Bootstrapper lo implementa junto a `ListNamesAsync`; los dos fakes devuelven listas configurables.
- `MessagingUnitOfWork`: `BusinessHoursNameIndex` → `ResourceConflictException(MessagingErrorCodes.BusinessHoursNameTaken, …)`;
  `BusinessHoursDefaultIndex` → `ResourceConflictException(MessagingErrorCodes.BusinessHoursDefaultConflict, …)`.
- Registro: `AddScoped<IBusinessHoursRepository, BusinessHoursRepository>()` y `AddScoped<IBusinessHours>(sp => (BusinessHoursRepository) sp.GetRequiredService<IBusinessHoursRepository>())`.

- [ ] **Step 1: Escribe las pruebas que fallan** (`BusinessHoursPersistenceTests.cs`, con `StartDatabaseAsync`, `RegisterTenantAsync`, y el repositorio resuelto del contenedor del `QepApiFactory`)

```csharp
[Fact] WeeklyAndExceptionsRoundTripThroughJsonb()        // Create con 3 franjas un día y una excepción → SaveChanges → nueva scope → FindAsync igual (Assert.Equal de las listas)
[Fact] TheNameIndexIgnoresCaseAndIsTranslatedByName()    // "Oficina" y "OFICINA" → SaveChangesAsync lanza ResourceConflictException con Code business_hours.name_taken
[Fact] TwoDefaultsAreADefaultConflict()                  // dos filas is_default por SQL → PostgresException ConstraintName IX_business_hours_tenant_default; por el UoW → ResourceConflictException default_conflict
[Fact] DeletingAnAssignedScheduleIsRestricted()          // fila en connection_business_hours → DELETE por SQL → PostgresException ConstraintName FK_connection_business_hours_schedule
[Fact] TheHolidayCountryCheckRejectsOtherCountries()     // UPDATE holiday_country='US' → CK_business_hours_holiday_country
[Fact] StatusesResolveAssignmentThenDefaultThenAlwaysOpen()   // tres conexiones: asignada (horario cerrado ahora con TestClock), sin asignar con predeterminado (abierto), tenant sin predeterminado → IsOpen true y ScheduleId null
```
Para fijar la hora usa `WithTestClock(new TestClock { UtcNow = … })` del harness (l.728): `TenantClock` lee `IClock`.

- [ ] **Step 2: Genera la migración y corre las pruebas para verificar que fallan**

Run: `dotnet ef migrations add AddBusinessHours --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations`
Run: `dotnet test …IntegrationTests --filter "FullyQualifiedName~BusinessHoursPersistenceTests"`

- [ ] **Step 3: Implementa DbContext, migración, repositorio, UoW, directorio y fakes**

- [ ] **Step 4: Corre y verifica que pasan** — y `dotnet test …UnitTests` entero (los fakes cambiaron) y `ArchitectureTests --filter MessagingLayerTests`.

- [ ] **Step 5: Commit** — `feat(messaging): persistencia de horarios de atención y puerto IBusinessHours`

---

### Task 6: Catálogo de horarios: DTOs, handlers, endpoints, permiso y auditoría

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/BusinessHours.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Api/BusinessHoursEndpoints.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (montar en l.206-210)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessagingPermissions.cs`, `IMessagingAuditRecorder.cs`, `…/Persistence/MessagingAuditRecorder.cs`, dobles `CampaignTestDoubles.RecordingAudit` (l.101)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/MessageTemplates.cs` (`MessagingConnectionDto` l.107) e `IMessageTemplates.cs` (`ListMessagingConnectionsHandler` l.21)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (handlers junto a l.680-683; rol `admin` l.1093-1104; definición junto a l.1597-1604; política junto a l.1935-1938)
- Modify: `tests/…/IntegrationTests/MessagingApiHarness.cs` (`BusinessHoursUrl`, `BusinessHoursPermissions`)
- Test: `tests/…/IntegrationTests/BusinessHoursApiTests.cs` (crear), `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs` (junto a l.220)

**Interfaces:**
- Produces, en `BusinessHours.cs`:
  ```csharp
  public sealed record TimeRangeDto(int From, int To);
  /// <summary>Siempre 7 (lunes a domingo): un día ausente obligaría a la pantalla a conocer el calendario del backend.</summary>
  public sealed record WeekdayHoursDto(int Day, IReadOnlyList<TimeRangeDto> Ranges);
  public sealed record ExceptionHoursDto(DateOnly Date, IReadOnlyList<TimeRangeDto> Ranges);
  public sealed record BusinessHoursConnectionDto(Guid ConnectionId, string Name, string Channel);
  /// <summary>UpcomingHolidays viaja calculada para que el frontend no tenga la Ley 51 (D-H9). OpenNow y NextChangeAt se evalúan con el reloj y el huso del tenant.</summary>
  public sealed record BusinessHoursDto(Guid Id, string Name, bool IsDefault, IReadOnlyList<WeekdayHoursDto> Weekly, IReadOnlyList<ExceptionHoursDto> Exceptions,
      string? HolidayCountry, IReadOnlyList<DateOnly> UpcomingHolidays, IReadOnlyList<BusinessHoursConnectionDto> Connections, bool OpenNow, DateTimeOffset? NextChangeAt, DateTimeOffset UpdatedAt, long Version);
  /// <summary>Lleva las conexiones del tenant en cualquier estado con su horario actual: GET /messaging/connections sólo trae las activas y el editor tiene que mostrar también una pausada para no desasignarla en silencio (D-H10).</summary>
  public sealed record BusinessHoursConnectionOptionDto(Guid ConnectionId, string Name, string Channel, string Status, Guid? ScheduleId, string? ScheduleName);
  public sealed record BusinessHoursListDto(IReadOnlyList<BusinessHoursDto> Items, string TimeZone, IReadOnlyList<BusinessHoursConnectionOptionDto> Connections);
  public sealed record SaveBusinessHoursRequest(string? Name, IReadOnlyList<WeekdayHoursDto>? Weekly, IReadOnlyList<ExceptionHoursDto>? Exceptions, string? HolidayCountry, IReadOnlyList<Guid>? ConnectionIds);
  public sealed record ListBusinessHoursQuery(Guid TenantId) : IQuery<BusinessHoursListDto>;
  public sealed record CreateBusinessHoursCommand(Guid TenantId, SaveBusinessHoursRequest Request) : ICommand<BusinessHoursDto>;
  public sealed record UpdateBusinessHoursCommand(Guid TenantId, Guid ScheduleId, long ExpectedVersion, SaveBusinessHoursRequest Request) : ICommand<BusinessHoursDto>;
  public sealed record DeleteBusinessHoursCommand(Guid TenantId, Guid ScheduleId, long ExpectedVersion) : ICommand<bool>;
  public sealed record DuplicateBusinessHoursCommand(Guid TenantId, Guid ScheduleId) : ICommand<BusinessHoursDto>;
  public sealed record SetDefaultBusinessHoursCommand(Guid TenantId, Guid ScheduleId, long ExpectedVersion) : ICommand<BusinessHoursDto>;
  public sealed class SaveBusinessHoursRequestValidator : AbstractValidator<SaveBusinessHoursRequest>
  // Name: NotEmpty, MaximumLength 60 («Escribe un nombre de hasta 60 caracteres.»); Weekly: NotNull, Count 7, días 1..7 únicos («La semana trae los siete días, de lunes a domingo.»);
  // Weekly[i].Ranges: Count ≤ 3; cada franja 0 ≤ From < To ≤ 1440 con el mensaje «Una franja no puede pasar de medianoche: divídela en dos días.» cuando From ≥ To, y sin solapes;
  // Exceptions: NotNull, ≤ 100, fechas únicas; HolidayCountry: null o "CO" («Sólo están disponibles los festivos de Colombia.»); ConnectionIds: NotNull, sin repetidos.
  // Nombres de propiedad tal cual (Name, Weekly, Exceptions, HolidayCountry, ConnectionIds) para que el mapa errors case con el formulario.
  ```
  Handlers (todos: `MessagingAuthorization.EnsureAsync(…, permiso)` primero; `ITenantClock.GetAsync` para `Today`, `TimeZone` y `UtcNow`):
  - `ListBusinessHoursHandler` (read): `ListAsync` + `ListAssignmentsAsync` + `connections.ListAllAsync`; arma cada DTO (`OpenNow`,
    `NextChangeAt`, `UpcomingHolidays(today)`), `Connections` del horario sólo con conexiones que existen en `ListAllAsync`
    (las huérfanas no se muestran), y `BusinessHoursListDto.Connections` con todas.
  - `CreateBusinessHoursHandler` (manage): validador → `Create(Guid.CreateVersion7(), …)` → `Add` → `ApplyConnectionsAsync` →
    `RecordBusinessHours(Created)` (+ `ConnectionsAssigned` si hubo) → Save → DTO.
  - `UpdateBusinessHoursHandler`: validador → `FindAsync` (404 `BusinessHoursNotFound`) → `EnsureVersion` (misma forma que
    `ConversationConcurrency.EnsureVersion`, para el agregado) → `Update` → `ApplyConnectionsAsync` → auditoría → Save.
  - `DeleteBusinessHoursHandler`: find → versión → `IsDefault` → 422 `BusinessHoursIsDefault`; asignaciones **vivas** (cuya
    conexión está en `ListAllAsync`) → 422 `BusinessHoursInUse`; las huérfanas se `RemoveAssignment` → `Remove` →
    `RecordBusinessHours(Deleted)` → Save → `true`.
  - `DuplicateBusinessHoursHandler`: find → `for attempt = 1..` hasta `!NameExistsAsync(DuplicateName(name, attempt))` →
    `Duplicate(Guid.CreateVersion7(), …)` → `Add` → `RecordBusinessHours(Duplicated)` → Save.
  - `SetDefaultBusinessHoursHandler`: find → versión → si ya es default, 200 sin cambios; `FindDefaultAsync` →
    `ClearDefault` en el anterior → `MakeDefault` → `RecordBusinessHours(DefaultSet)` → Save (el índice es la red).
  - `ApplyConnectionsAsync` privado compartido: `ListAllAsync` del tenant; un id que no esté → `ValidationException` con la
    propiedad `ConnectionIds` («Una de las conexiones no es de este negocio.»); por cada asignación del horario que no venga
    → `RemoveAssignment`; por cada id que venga: si tiene fila de otro horario, muévela (`ScheduleId`), si no, `AddAssignment`;
    las filas del horario cuya conexión ya no existe → `RemoveAssignment`.
- Produces: `MessagingPermissions.BusinessHoursManage = "messaging.business_hours.manage"`;
  `IMessagingAuditRecorder.RecordBusinessHours(Guid tenantId, Guid actorId, string action, Guid scheduleId, DateTimeOffset occurredAt)`;
  `MessagingAuditActions.BusinessHoursResourceType = "business_hours_schedule"` y `BusinessHoursCreated|Updated|Deleted|Duplicated|DefaultSet|ConnectionsAssigned = "messaging.business_hours.<x>"`.
- `MessagingConnectionDto` gana `Guid? BusinessHoursScheduleId = null, string? BusinessHoursName = null, bool OpenNow = true`;
  `ListMessagingConnectionsHandler` recibe `IBusinessHours` y completa los tres con `GetStatusesAsync` en una llamada.
- Endpoints (`BusinessHoursEndpoints.MapBusinessHoursEndpoints(this RouteGroupBuilder messaging)`, grupo `/business-hours`):
  `GET /` (read), `POST /` (manage, 201 con `Results.Created($"{path}/{dto.Id}", dto)`), `PUT /{scheduleId:guid}` (manage, `RequireVersion`),
  `DELETE /{scheduleId:guid}` (manage, `RequireVersion`, 204), `POST /{scheduleId:guid}/duplicate` (manage, 201),
  `POST /{scheduleId:guid}/default` (manage, `RequireVersion`, 200). `Produces`/`ProducesProblem` como `ResolutionReasonEndpoints`.
- Bootstrapper: seis handlers; `PermissionDefinition(MessagingPermissions.BusinessHoursManage, "Administrar horarios de atención",
  "Permite crear, editar, duplicar y asignar los horarios de atención de los números de la bandeja.", "Messaging", "low", RequiredModules: [])`;
  `AddPolicy`; en `admin` (l.1093-1104) **y no** en `advisor`.
- Harness: `BusinessHoursUrl(tenantId)`, `BusinessHoursPermissions = [ConversationRead, BusinessHoursManage]`.

- [ ] **Step 1: Escribe las pruebas que fallan**

`BusinessHoursApiTests.cs` (`ArrangeAsync`: tenant, módulo, `SeedWhatsAppConnectionAsync` ×2, cliente con `BusinessHoursPermissions`; `Office()` = request lun–vie 480–1080 con 7 días; `TestClock` fijado en `2026-10-21T15:00:00Z`):
```csharp
[Fact] CreateReturnsTheSevenDaysOpenNowAndNextChange()        // 201; weekly.Count 7; openNow true; nextChangeAt 2026-10-21T23:00:00Z; holidayCountry "CO"; upcomingHolidays contiene 2026-11-02; connections vacía; audit created == 1
[Fact] TheListBringsTheTimeZoneAndEveryConnectionWithItsSchedule()  // timeZone "America/Bogota"; connections.Count 2 con scheduleId null
[Fact] ANameInUseIs409AndAnInvalidBodyIs422WithTheField()     // mismo nombre otra vez → 409 name_taken; weekly de 6 días → 422 errors["Weekly"]; franja 1320→120 → errors["Weekly[0].Ranges[0]"] (o la clave que FluentValidation produzca: fíjala en la prueba y en el README); holidayCountry "US" → errors["HolidayCountry"]
[Fact] UpdateNeedsIfMatchAndReplacesTheConnections()          // 428 sin If-Match; 412 con vieja; PUT con connectionIds [c1] → connections [c1], GET /messaging/connections c1 trae businessHoursName y openNow; PUT con [c2] → c1 queda sin horario (businessHoursScheduleId null)
[Fact] AConnectionOfAnotherTenantIs422OnConnectionIds()
[Fact] APausedConnectionIsAccepted()                           // UPDATE integrations.connections SET status='Paused' (nombre del valor: cópialo del enum de Integrations) → PUT con ese id → 200 y la opción sigue en la lista con status "Paused"
[Fact] DuplicateNumbersTheCopies()                             // "Oficina (copia)", "Oficina (copia 2)"; sin If-Match; el origen conserva su version
[Fact] DefaultIsUniqueAndBumpsBothVersions()                   // default en A (v2); default en B → A.isDefault false con version 3, B version 2; sin predeterminado antes: GET /messaging/connections openNow true (D-H4); con A predeterminado y reloj a las 19:00 Bogotá → openNow false
[Fact] DeleteIsRefusedInUseOrDefaultAndWorksOtherwise()        // 422 in_use; 422 is_default; 204 y la lista ya no lo trae
[Fact] AnOrphanAssignmentDoesNotCountAsInUseAndIsRemoved()     // asignar c1 → DELETE FROM integrations.connections WHERE id=c1 (o el soft-delete que Integrations use: mira cómo lo hace su DELETE) → DELETE del horario 204 → connection_business_hours vacío
[Fact] PastExceptionsArePrunedOnSave()                        // exceptions 2026-01-01 y 2026-12-25 → GET trae sólo 2026-12-25
[Fact] AHolidayClosesTheConnection()                          // reloj 2026-11-02T15:00:00Z (festivo), horario lun–vie con CO asignado → openNow false; con holidayCountry null → true
[Fact] ManagingNeedsThePermissionTheTenantAndTheModule()      // sin BusinessHoursManage: GET 200, POST 403; otro tenant 403; módulo apagado → tenancy.module_not_enabled
```
`CompositionRootTests.cs`: `AdminCarriesBusinessHoursManageAndAdvisorDoesNot` junto a l.220.

- [ ] **Step 2: Corre y verifica que fallan** — Run: `dotnet test …IntegrationTests --filter "FullyQualifiedName~BusinessHoursApiTests"` y `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`

- [ ] **Step 3: Implementa DTOs, validador, handlers, endpoints, permiso, auditoría y el enriquecimiento de conexiones**

- [ ] **Step 4: Corre y verifica que pasan** — también `MessageTemplatesApiTests`/`CampaignApiTests` si leen `GET /messaging/connections` (el DTO ganó campos con default; no deberían cambiar).

- [ ] **Step 5: Commit** — `feat(messaging): catálogo de horarios de atención con permiso propio`

---

### Task 7: README y verificación final

**Files:**
- Modify: `README.md` (tabla de endpoints de Messaging; nueva subsección «Horarios de atención» junto a «Motivos de resolución»; nota del permiso `messaging.business_hours.manage` en la lista de permisos de fábrica)

- [ ] **Step 1:** Documenta en el README las ocho rutas nuevas con política, `If-Match` y códigos; la regla de resolución
  asignación → predeterminado → siempre abierto; que un festivo cierra salvo excepción con franjas; y que **nada pone
  `Active` en producción hasta el spec 4** (el harness lo hace en pruebas).
- [ ] **Step 2:** `dotnet build --no-restore` limpio; `dotnet format --verify-no-changes --include <archivos tocados>` limpio.
- [ ] **Step 3:** Las tres suites acotadas, una vez cada una: `Modules.Messaging.UnitTests` entero,
  `Modules.Messaging.IntegrationTests --filter "FullyQualifiedName~Conversation|FullyQualifiedName~BusinessHours|FullyQualifiedName~Assignment|FullyQualifiedName~MessagingPersistence"`,
  `ArchitectureTests` entero. Pega los conteos.
- [ ] **Step 4:** `git log --format=%B develop..HEAD | grep -i "co-authored"` vacío.
- [ ] **Step 5:** Commit `docs(messaging): endpoints de automatización y horarios de atención`. No merges: reporta rama y
  SHA. Al mergear a `develop` (lo decide el developer) se actualiza `docs/estado.md` con la fila
  `2026-10-21 mensajería: asistente y horarios`.
