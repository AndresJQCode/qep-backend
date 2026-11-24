# Soporte: tareas internas entre miembros (backend). Plan de implementación

> **Para quien ejecute:** SUB-SKILL REQUERIDO: `superpowers:subagent-driven-development` (recomendado) o `superpowers:executing-plans`,
> **con las reglas del workspace por encima del skill** (owner, 2026-11-12): 5 bloques, **un implementador por bloque**, un commit por
> tarea, una revisión por bloque y una revisión fuerte de toda la rama al final. Los pasos usan casillas (`- [ ]`).

**Meta:** que cualquier miembro con `support.task.use` cree y trabaje tareas (tickets internos) con otro miembro, sin ver nunca un
ticket de cliente, y que las tareas no ensucien la bandeja, el resumen, los reportes ni el portal.

**Arquitectura:** el ticket gana `IsInternal`, `CreatedByMemberId` y un tipo interno único por tenant. Toda lectura o escritura de un
ticket por un miembro pasa por **una sola puerta**, `SupportTicketScope`, que aplica la tabla de §5.6 con una función pura
(`SupportTicketAccessPolicy`) que también usa el autorizador del hub. Las altas entran por `POST /tasks`; asignar una tarea a otro
deja `support.ticket.task-assigned.v1` en el outbox de plataforma, en la misma transacción.

**Tech stack:** .NET 10, EF Core + Npgsql (esquema `support`), FluentValidation, xUnit v3, Testcontainers (PostgreSQL), SignalR (app hub).

**Spec:** [`../specs/2026-11-22-support-internal-tasks-design.md`](../specs/2026-11-22-support-internal-tasks-design.md). El spec hermano
`2026-11-22-support-task-notices-design.md` **no** se implementa acá: sólo se deja el evento (spec §6 D-3).

## Global Constraints

- Rama `feature/support-internal-tasks`, worktree `qep-backend-worktrees/support-internal-tasks`. Antes de tocar un archivo y antes de
  cada commit: `git branch --show-current` (nunca `main`). Merge sólo a `develop`; `docs/estado.md` se toca en `develop`, al mergear.
- Conventional commits, **sin atribución de IA ni `Co-Authored-By`**.
- TDD con evidencia literal de RED y GREEN. RED por compilación = salida de `dotnet build`, sin contenedores. La integración corre en
  GREEN, **filtrada por nombre de prueba**; la clase completa una vez por bloque. Antes de cada corrida de integración:
  `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay una, se espera (no se detiene). Nunca dos a la vez. Las pruebas se
  corren en primer plano. Con `Api.exe` vivo, `build`/`test`/`ef` fallan: se avisa, no se mata.
- Suite completa sólo antes de mergear y limitada a: `Modules.Support.UnitTests`, `Modules.Support.IntegrationTests`, `ArchitectureTests`.
- Una sola migración, **expand-only** (columnas nuevas con default o anulables, CHECK nuevos, índice relajado), reversible.
- `ArchitectureTests` intactas: Application no referencia EF/Npgsql; un handler nuevo se registra a mano en
  `QepServiceCollectionExtensions.cs` (lo detecta `CompositionRootTests`). Este trabajo no agrega hosted services ni singletons con
  estado (`MultiReplicaInventory` no cambia).
- Valores fijados por el spec, literales: permiso `support.task.use` («Usar tareas internas», grupo «Soporte», riesgo `low`,
  `RequiredModules: [Support]`, forma `support.task` / «Tareas internas» / `use`), en los roles `admin`, `advisor`, `billing`, `cashier`;
  tipo «Tarea interna»; códigos `support.ticket.internal_type_locked`, `support.ticket.internal_no_reply`,
  `support.ticket.internal_due_in_past`, `support.ticket.transition_invalid`, `support.ticket.assignee_invalid`,
  `support.ticket.not_found`, `authorization.denied`; `kind=external|internal|all` con `external` por defecto (para un task-only, sin
  `kind` = `internal`); `createdBy=me`; evento `support.ticket.task-assigned.v1` con `ticketId`, `tenantId`, `ticketNumber`,
  `assigneeMemberId`, `assignedByMemberId`.
- 404 y nunca 403 para un ticket que el task-only no puede ver; el filtro por participante va **en la consulta**.
- Comandos para el developer en PowerShell. Plan, mensajes de prueba y comentarios de por qué en el estilo del repo; identificadores en
  inglés; textos visibles en español colombiano tuteando (`qep-backend/CLAUDE.md`).
- Gana el código: lo que el spec no alcanza está en «Discrepancias» con archivo:línea; lo pendiente del owner va marcado **[owner]**.

## Review Focus

1. **Reabrir una tarea no borra su fecha.** `ChangeStatus` recalcula `DueOn` con el término al salir de un estado que detiene el reloj
   (`SupportTicket.cs:360-366`); con término null, una tarea `Resolved → Open` quedaría sin fecha. Prueba:
   `ReopeningATaskKeepsItsDueDate` (Tarea 2).
2. **El 404 no tiene oráculo.** Un task-only recibe el mismo 404 `support.ticket.not_found` para un id inexistente, un externo y una
   tarea ajena, y una versión vieja o un cuerpo de acción sobre algo ajeno da 404, nunca 412. Pruebas:
   `TheSame404ForUnknownExternalAndForeignTask`, `AStaleVersionOnSomeoneElsesTaskIs404Not412` (Tarea 6).
3. **Permisos combinados.** `read` + `task.use` ve todo, actúa sólo en sus tareas y sobre un externo recibe 403 (lo ve, no hay nada que
   ocultar), no 404. Prueba: la teoría `TheAccessTable` (Tarea 5).
4. **Quien pierde `task.use` pierde la tarea**, aunque siga siendo creador o responsable en los datos: por HTTP (claims) y por el hub
   (permisos leídos de la base). Pruebas: `ParticipationWithoutTaskUseGivesNoAccess` (Tarea 5) y
   `AParticipantWhoLostTaskUseCannotSubscribe` (Tarea 7).
5. **«Hoy» es la fecha del tenant, no la UTC.** Una tarea creada a las 22:00 de Bogotá (03:00 UTC del día siguiente) acepta `dueOn` = ese
   mismo día local. Prueba: `TodayIsJudgedInTheTenantZone` (Tarea 2).

Fuera de las pruebas, a propósito: un doble clic en `POST /tasks` crea dos tareas. El spec no pide idempotencia y el alta manual de
hoy tampoco la tiene; se anota para el owner.

## Discrepancias con el spec y decisiones pendientes

| # | Spec | Código | Qué hace el plan |
|---|---|---|---|
| D1 | Tópico `support.tasks.<memberId>` (§5.4) | `RealtimeTopics.PrefixOf` corta en `:` (`src/Bootstrapper/Realtime/RealtimeTopics.cs:20-24`) y el hub elige el autorizador por ese prefijo (`AppHub.cs:240-241`); con puntos, el prefijo sería el tópico entero y nadie lo autoriza | `support:tasks:{memberId:D}` (forma exacta, como `support:ticket:{id}`). Avisar al frontend |
| D2 | El resumen se calcula por `kind` (§5.3) | `TotalsAsync(Guid tenantId, …)` es del tenant entero (`ISupportTicketRepository.cs:28`, `SupportTicketRepository.cs:107`) | `TotalsAsync` recibe `kind` y participante |
| D3 | `SupportReplyChannel.None` al final del enum (§5.1) | El enum alimenta también `CK_ticket_replies_channel` (`SupportDbContext.cs:743`) y los validadores de alta y respuesta parsean por nombre (`CreateTicket.cs:88-90`, `SendTicketReply.cs:55-57`): `None` pasaría como canal válido | Los dos validadores rechazan `None` explícito; el CHECK de replies acepta `None` sin que ningún camino lo escriba |
| D4 | No dice qué `Intake` lleva un interno | `CreateManual` exige `ManualIntakeMedium` no nulo (`SupportTicket.cs:167`); CHECK por nombre (`SupportDbContext.cs:520`) | `SupportTicketIntake.Internal` al final (decisión del implementador, §8) |
| D5 | `POST /tickets` con `dueOn` → 422 (§5.2.1) | `CreateTicketRequest` no tiene `dueOn` (`CreateTicket.cs:28-38`): el binder lo ignora | Se agrega `string? DueOn` al request y el validador exige null (`validation.failed` en `dueOn`) |
| D6 | `DueOn` libre en un interno (§5.1) | Reanudar recalcula con el término (`SupportTicket.cs:360-366`) | Un interno nunca recalcula `DueOn` en `ChangeStatus` (Review Focus 1) |
| D7 | — | El alta externa emite PIN y manda el correo de apertura (`CreateTicket.cs:390-409`) | Una tarea no crea `SupportTicketAccess` ni llama `SupportOpeningDelivery`; la consulta pública ya responde «no encontrado» sin clave (`ISupportTicketAccessRepository.LockByNumberAsync`) |
| D8 | Sembrar «Tarea interna» en todos los tenants (§5.7) | Único `IX_ticket_types_tenant_name` por `(tenant_id, lower(name))` (`InitialSupport.cs:174`): un tipo propio llamado así rompe la siembra | **[owner]** Propuesta: el índice pasa a parcial `WHERE NOT is_internal` (relaja; un pod viejo no se entera) |
| D9 | «Los tipos que lista la API traen `isInternal`»; «el selector no lista el interno» | `ISupportTicketTypeRepository.ListAsync` alimenta ajustes, reordenar (`order_incomplete`), reporte y formulario público | `ListAsync` excluye el interno; `GET /ticket-types` lo agrega al final con `isInternal: true`; `/settings/ticket-types` y el reordenar no lo ven. Avisar al frontend |
| D10 | Sólo nombra `SupportPolicies.TicketAct` (manage o task.use) | Las lecturas de detalle y notas hoy son `TicketView` (read o manage) | Política nueva `SupportPolicies.TicketScopedView` = read, manage o task.use (**[owner]** nombre) |
| D11 | Códigos no fijados | — | **[owner]** Update/Delete del tipo interno → `support.ticket.internal_type_locked`; vincular cliente, seguir hilo, fijar/soltar mensaje, PIN y `reply-options` sobre un interno → `support.ticket.internal_no_reply`; `PUT due-date` sobre un externo → `support.ticket.not_internal` (nuevo) |
| D12 | 404 nunca 403 para un task-only | Las rutas que siguen en `manage` responden 403 pelado de la política antes del handler | Se deja así: el 403 no depende del id, no revela existencia. El 404 aplica a las rutas abiertas al task-only |
| D13 | Prórroga no aplica (§5.2.6) | `Extend` ya responde `support.ticket.extension_no_term` sin término (`SupportTicket.cs:650-653`) | Sin código nuevo |
| D14 | Evento por el outbox (§6 D-3) | Soporte no mapea `platform.outbox_messages`; el `OutboxProcessor` de Tenancy marca procesado un evento sin consumidor (`IntegrationEventDispatcher.cs:24-50`) | Se mapea con los nombres del plan de la campana (`SupportOutboxMessage`, `ISupportIntegrationEvents`, `SupportIntegrationEvents`; `in-app-notifications/docs/superpowers/plans/2026-11-22-in-app-notifications-backend.md`, Tarea 10): el segundo merge sólo suma métodos. Hasta que exista el consumidor, el evento queda escrito y marcado sin efecto |
| D15 | Método nuevo en `ISupportMemberDirectory` | `SupportMemberRef` sólo trae `CanManage` (`ISupportMemberDirectory.cs:4`); dos dobles implementan la interfaz (`SupportOpeningSenderTests`, `SupportSurveySenderTests`) | `CanUseTasks` con default `false`; los dobles suman el método |
| D16 | `requester` null en un interno | `SupportTicketListItemDto.RequesterDisplayName` es `string` no nulo (`SupportDtos.cs:185`) | **[owner]** Se deja «Sin nombre» (lo de hoy sin solicitante) y la fila trae `createdBy` |
| D17 | `replyChannel` null en un interno | `SupportTicketDetailDto.ReplyChannel` es `string` (`SupportDtos.cs:211`) | Pasa a `string?`: cambio de contrato, avisar al frontend |
| D18 | «Historial» pasa por la puerta (§5.6) | La traza viaja en el detalle (`Events`); `GET …/messages` es el hilo de Mensajería | `…/messages` se trata como exclusivo de externos (sigue `TicketView`) |
| D19 | «Asignármelo» (`take`) no está en la tabla | Es asignar al propio miembro (`TakeTicket.cs`) | `TicketAct`; en un interno valida al responsable con la regla de tareas |
| D20 | No dice si `PUT due-date` puede quitar la fecha | — | `dueOn: null` la quita, por simetría con el alta sin fecha |
| D21 | Rollout | Un pod viejo no lee valores de enum nuevos (`None`, `Internal`, `DueDateChanged`), igual que `Extended` (`.claude/rules/soporte-tickets.md`) | Nadie crea tareas hasta que no quede un pod viejo (handoff al owner). `created_by_member_id` de lo que radique un pod viejo durante el rollout queda null |

## Mapa de archivos

Raíz de código: `src/Modules/Support/` (abreviado `Support/`). Pruebas: `tests/Modules/Support/`.

| Archivo | Responsabilidad | Tareas |
|---|---|---|
| `Support/Modules.Support.Domain/SupportTicketType.cs`, `SupportTicketTypeDefaults.cs` | tipo interno | 1 |
| `Support/Modules.Support.Domain/SupportTicket.cs`, `SupportTicketStatus.cs`, `SupportTicketIntake.cs`, `SupportReplyChannel.cs`, `SupportTicketEventKind.cs`, `SupportErrorCodes.cs` | ticket interno y sus guardas | 2 |
| `Support/Modules.Support.Infrastructure/Persistence/SupportDbContext.cs`, `SupportProvisioner.cs`, `SupportTicketTypeRepository.cs`, `SupportOutboxMessage.cs` (nuevo), `Migrations/<ts>_AddSupportInternalTasks.cs` | columnas, CHECK, índices, siembra, outbox | 3 |
| `src/Bootstrapper/SupportUserReferenceProbe.cs` | `created_by_member_id` retiene al usuario | 3 |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | permiso, roles, políticas, handlers | 4, 6, 8, 9, 11 |
| `Support/Modules.Support.Application/SupportPermissions.cs`, `SupportPolicies.cs` | permiso y políticas | 4 |
| `Support/Modules.Support.Application/Access/SupportTicketAccessPolicy.cs`, `Access/SupportTicketScope.cs` (nuevos) | la tabla de §5.6 y la puerta única | 5 |
| Los 25 handlers de la Tarea 6, `Tickets/SupportTicketActions.cs`, `Notes/OpenNoteAttachment.cs` | pasan por la puerta | 6 |
| `Support/Modules.Support.Api/SupportEndpoints.cs` | políticas por ruta, `/tasks`, `due-date` | 6, 9, 11 |
| `Support/Modules.Support.Api/SupportRealtimeAuthorizer.cs`, `Application/SupportRealtimeTopics.cs` | tópicos y presencia | 7 |
| `Application/SupportDtos.cs`, `Tickets/SupportTicketDtoComposer.cs` | `isInternal`, `createdBy`, `replyChannel` null | 8 |
| `Application/ISupportMemberDirectory.cs`, `src/Bootstrapper/SupportMemberDirectory.cs`, `Tasks/ListTaskAssignees.cs` (nuevo) | candidatos de tareas | 9 |
| `Application/Tasks/CreateTask.cs`, `Application/ISupportIntegrationEvents.cs`, `Infrastructure/Persistence/SupportIntegrationEvents.cs` (nuevos) | alta y evento | 9 |
| `Tickets/CreateTicket.cs`, `Conversations/CreateTicketFromConversation.cs`, `Tickets/SupportTicketRules.cs`, `TicketTypes/*.cs`, `Replies/*.cs`, `Tickets/AssignTicket.cs`, `Tickets/TakeTicket.cs` | reglas sobre lo existente | 10 |
| `Application/Tickets/ChangeTicketDueDate.cs` (nuevo) | `PUT due-date` | 11 |
| `Tickets/ListTickets.cs`, `SupportReadModels.cs`, `ISupportTicketRepository.cs`, `Persistence/SupportTicketRepository.cs` | `kind`, `createdBy`, participante, resumen | 12 |
| `ISupportRealtime.cs`, `Infrastructure/SupportRealtimeNotifier.cs`, `Application/SupportTicketNotices.cs` (nuevo) | avisos a `support:tasks:{id}` | 13 |
| `Persistence/SupportReportRepository.cs`, `Public/*.cs`, `Persistence/SupportPublicFormRepositories.cs` | exclusiones | 14 |

## Comandos (PowerShell)

```powershell
git branch --show-current
Get-Process testhost, dotnet -ErrorAction SilentlyContinue
dotnet build tests/Modules/Support/Modules.Support.UnitTests
dotnet test tests/Modules/Support/Modules.Support.UnitTests --filter "FullyQualifiedName~<Clase>"
dotnet build tests/Modules/Support/Modules.Support.IntegrationTests
dotnet test tests/Modules/Support/Modules.Support.IntegrationTests --filter "FullyQualifiedName~<Clase>.<Prueba>" -- xUnit.MaxParallelThreads=4
dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~<Clase>"
dotnet ef migrations add AddSupportInternalTasks --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations
```

En cada paso, `<Clase>` y `<Prueba>` son los que nombra la tarea. «Esperado: no compila» se acredita con la línea `error CS…` literal.

---

## Bloque 1 — Dominio y migración

### Task 1: el tipo interno

**Files:**
- Modify: `Support/Modules.Support.Domain/SupportTicketType.cs`, `Support/Modules.Support.Domain/SupportTicketTypeDefaults.cs`,
  `Support/Modules.Support.Domain/SupportErrorCodes.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportTicketTypeTests.cs`

**Interfaces:**
- Produces: `SupportTicketType.IsInternal { get; private set; }`;
  `static SupportTicketType CreateInternal(SupportTicketTypeId id, Guid tenantId, int position, DateTimeOffset now)`;
  `SupportTicketTypeDefaults.InternalTaskName = "Tarea interna"`;
  `static SupportTicketType SupportTicketTypeDefaults.Internal(Guid tenantId, int position, DateTimeOffset now)`;
  `SupportErrorCodes.InternalTypeLocked = "support.ticket.internal_type_locked"`.
  `SupportTicketTypeDefaults.For` **sigue** devolviendo los ocho externos (los índices `[0]…[7]` que usan las pruebas no se mueven).

- [ ] **Paso 1: pruebas que fallan** en `SupportTicketTypeTests`:
  - `TheInternalTypeIsATaskWithoutTermNorPublication`: `Internal(TenantId, 9, Now)` → `Name == "Tarea interna"`,
    `TermBusinessDays == null`, `!TermIsLegal`, `!RequiresFormalResponse`, `!IsPublished`, `IsActive`, `IsInternal`, `Position == 9`,
    `Version == 1`.
  - `TheDefaultsStayTheEightExternalOnes`: `Assert.All(For(TenantId, Now), type => Assert.False(type.IsInternal))`.
  - `UpdatingTheInternalTypeIsRejected` (teoría: renombrar, publicar, desactivar, término 5, formal `true`, siguiente paso `"x"`) →
    `SupportDomainException` con `Code == SupportErrorCodes.InternalTypeLocked`.
  - `UpdatingTheInternalTypeWithItsOwnValuesDoesNothing`: misma llamada con sus valores → `Version == 1`.
  - `TheInternalTypeCanMove`: `MoveTo(3, Now)` → `Position == 3`.
- [ ] **Paso 2:** `dotnet build tests/Modules/Support/Modules.Support.UnitTests`. Esperado: no compila (`IsInternal`, `Internal`).
- [ ] **Paso 3:** implementa. `Update` sobre un interno compara antes de normalizar: igual → no-op; cualquier diferencia → lanza.
- [ ] **Paso 4:** `dotnet test tests/Modules/Support/Modules.Support.UnitTests --filter "FullyQualifiedName~SupportTicketTypeTests"`. Esperado: PASS.
- [ ] **Paso 5:** commit `feat(support): add the locked internal ticket type`.

### Task 2: el ticket interno

**Files:**
- Modify: `Support/Modules.Support.Domain/SupportTicket.cs`, `SupportTicketStatus.cs`, `SupportTicketIntake.cs`, `SupportReplyChannel.cs`,
  `SupportTicketEventKind.cs`, `SupportErrorCodes.cs`
- Create: `tests/Modules/Support/Modules.Support.UnitTests/SupportTicketInternalTests.cs`
- Modify: `tests/Modules/Support/Modules.Support.UnitTests/SupportTicketStatusesTests.cs`

**Interfaces:**
- Consumes: `SupportTicketType.IsInternal`, `SupportTicketTypeDefaults.Internal` (Tarea 1).
- Produces:
  - `SupportTicket.IsInternal { get; private set; }`, `SupportTicket.CreatedByMemberId { get; private set; } : Guid?` (actor del alta;
    null en el formulario público).
  - `static SupportTicket CreateInternal(SupportTicketId id, Guid tenantId, long number, SupportTicketType type, string subject,
    string description, SupportTicketPriority priority, Guid assigneeMemberId, DateOnly? dueOn, Guid actorMemberId, DateTimeOffset now,
    SupportClockContext clock)`: `Intake = Internal`, `ManualMedium = null`, `ReplyChannel = None`, sin cliente ni solicitante,
    `DueOn = dueOn` (nunca del término).
  - `void ChangeDueOn(DateOnly? dueOn, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock)`: evento `DueDateChanged`
    con `{ fromDueOn, toDueOn }` (los nombres de `Extended`).
  - `static IReadOnlyList<SupportTicketStatus> SupportTicketStatuses.AllowedFor(SupportTicketStatus from, bool isInternal)`: la tabla
    sin `WaitingOnCustomer` cuando `isInternal`.
  - Enums, al final: `SupportTicketIntake.Internal`, `SupportReplyChannel.None`, `SupportTicketEventKind.DueDateChanged`.
  - `SupportErrorCodes.InternalNoReply = "support.ticket.internal_no_reply"`,
    `InternalDueInPast = "support.ticket.internal_due_in_past"`, `NotInternal = "support.ticket.not_internal"` (**[owner]** D11).

- [ ] **Paso 1: pruebas que fallan** en `SupportTicketInternalTests` (mismo `FakeCalendar`, `Clock` y `Received` = 2026-11-13 15:00 UTC
  que `SupportTicketTests`; `Task` = `SupportTicketTypeDefaults.Internal(TenantId, 9, Received)`):
  - `ATaskHasNoRequesterCustomerNorReplyChannel`: `IsInternal`, `Intake == Internal`, `ManualMedium == null`, `ReplyChannel == None`,
    `CustomerId == null`, `Requester == null`, `CreatedByMemberId == Actor`, `AssigneeMemberId == OtherMember`, un solo evento
    `Created` con actor `Actor`, `TermBusinessDays == null`.
  - `ATaskKeepsTheChosenDueDate`: `dueOn` 2026-11-20 → `DueOn == 2026-11-20`; `ATaskWithoutDueDateHasNone` → `DueOn == null`.
  - `ADueDateBeforeTodayIsRejected`: 2026-11-12 → `InternalDueInPast`; 2026-11-13 se acepta.
  - `TodayIsJudgedInTheTenantZone`: `now` = 2026-11-14 03:00 UTC (22:00 del 13 en Bogotá) y `dueOn` 2026-11-13 → se acepta.
  - `ATaskNeedsTheInternalType` (`CreateInternal` con «Petición general» → `ArgumentException`) y
    `NoOtherEntryAcceptsTheInternalType` (`CreateManual`, `CreateFromConversation` y `CreateFromPublicForm` con el interno →
    `ArgumentException`).
  - `ATaskCannotWaitOnTheCustomer`: desde `New` y desde `Open` → `TransitionInvalid`.
  - `ResolvingATaskRequestsNoSurvey`: `Open → Resolved` → `Surveys` vacío, `ResolutionCount == 1`.
  - `ReopeningATaskKeepsItsDueDate`: `dueOn` 2026-11-20, `Open → Resolved → Open` → `DueOn == 2026-11-20` (Review Focus 1).
  - `ATaskCannotChangeType` y `AnExternalTicketCannotTakeTheInternalType` → `InternalTypeLocked`.
  - `ATaskTakesNoCustomer`: `LinkCustomer(Customer, …)` → `InternalNoReply`.
  - `ATaskHasNoExtension`: `MaxExtensionBusinessDays(today) == null` y `Extend` → `ExtensionNoTerm`.
  - `ChangingTheDueDateOfATaskRecordsIt`: `ChangeDueOn(2026-11-25)` → `DueOn`, `Version == 2`, último evento `DueDateChanged` con
    `{"fromDueOn":"2026-11-20","toDueOn":"2026-11-25"}`; con el mismo valor no sube la versión; `null` la quita; 2026-11-12 → `InternalDueInPast`;
    `Resolved` → `NotEditable`; sobre un externo → `NotInternal`.
  - `CreatedByIsTheActorOrNobody`: manual y conversación → `Actor`; formulario público → `null`.
  - En `SupportTicketStatusesTests`: `AllowedForATaskNeverOffersWaitingOnCustomer` (todos los estados) y
    `AllowedForAnExternalTicketIsAllowedFrom`.
- [ ] **Paso 2:** `dotnet build tests/Modules/Support/Modules.Support.UnitTests`. Esperado: no compila.
- [ ] **Paso 3:** implementa. `Start` rechaza `type.IsInternal`; `CreateInternal` exige `type.IsInternal` y arma el ticket sin pasar por la
  regla del solicitante; `ChangeStatus` usa `AllowedFor(Status, IsInternal)`, no pide encuesta en un interno y no recalcula `DueOn` en un
  interno; `ChangeType` rechaza un interno o un tipo interno; `LinkCustomer` y `LinkConversation` rechazan un interno;
  `ChangeDueOn` usa `EnsureEditable()`.
- [ ] **Paso 4:** `dotnet test tests/Modules/Support/Modules.Support.UnitTests --filter "FullyQualifiedName~SupportTicketInternalTests|FullyQualifiedName~SupportTicketStatusesTests|FullyQualifiedName~SupportTicketTests"`. Esperado: PASS (las de `SupportTicketTests` siguen verdes).
- [ ] **Paso 5:** commit `feat(support): model internal tickets with a free due date`.

### Task 3: persistencia, siembra y migración

**Files:**
- Modify: `Support/Modules.Support.Infrastructure/Persistence/SupportDbContext.cs`, `SupportProvisioner.cs`,
  `SupportTicketTypeRepository.cs`; `Support/Modules.Support.Application/ISupportProvisioner.cs`, `ISupportTicketTypeRepository.cs`;
  `src/Bootstrapper/SupportUserReferenceProbe.cs`
- Create: `Support/Modules.Support.Infrastructure/Persistence/SupportOutboxMessage.cs`, la migración `AddSupportInternalTasks`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportPersistenceTests.cs`,
  `SupportInternalTasksBackfillTests.cs` (nuevo), `SupportUserReferenceProbeTests.cs`, `SupportTicketSeed.cs`

**Interfaces:**
- Consumes: Tareas 1 y 2.
- Produces:
  - Columnas: `ticket_types.is_internal bool NOT NULL DEFAULT false`; `tickets.is_internal bool NOT NULL DEFAULT false`;
    `tickets.created_by_member_id uuid NULL` con índice `IX_tickets_created_by` (`tenant_id, created_by_member_id`).
  - `CK_tickets_internal`: `NOT is_internal OR (customer_id IS NULL AND conversation_id IS NULL AND manual_medium IS NULL AND NOT is_anonymous AND <columnas del solicitante IS NULL>)`
    y `(reply_channel = 'None') = is_internal` (reusa `RequesterColumns`). `CK_ticket_types_internal`:
    `NOT is_internal OR (term_business_days IS NULL AND NOT term_is_legal AND NOT requires_formal_response AND NOT is_published AND is_active)`.
    Los CHECK por enum (`intake`, `reply_channel` de tickets y de replies, `kind` de eventos) cambian solos con el enum.
  - `UX_ticket_types_tenant_internal`: único por `tenant_id` `WHERE is_internal` (SQL a mano, como el de nombre).
  - **[owner] D8:** `IX_ticket_types_tenant_name` se recrea `WHERE NOT is_internal`.
  - *Backfill* en `Up`: `created_by_member_id` = `actor_member_id` del evento `Created` de cada ticket; un tipo interno por fila de
    `tenant_settings` que no lo tenga, en `max(position) + 1`, con `gen_random_uuid()`. `Down` borra los tipos internos sin tickets,
    restaura el índice completo y quita columnas y CHECK.
  - `SupportOutboxMessage` y su proyección `platform.outbox_messages` con `ExcludeFromMigrations` (copia de
    `GeographyDbContext.ConfigureOutboxProjection`, `GeographyDbContext.cs:279-292`), `internal DbSet<SupportOutboxMessage> OutboxMessages`.
    Va en este bloque para que la única migración lleve el snapshot completo.
  - `Task<SupportTicketType?> ISupportTicketTypeRepository.FindInternalAsync(Guid tenantId, CancellationToken)`; `ListAsync` **excluye**
    el interno (D9); `FindAsync` lo encuentra.
  - `Task ISupportProvisioner.EnsureInternalTypeAsync(Guid tenantId, CancellationToken)`: `INSERT … ON CONFLICT DO NOTHING`, idempotente,
    con su propia transacción como `EnsureAsync`. `EnsureAsync` siembra los ocho **y** el interno en la posición 9.
  - Prueba: `SupportTicketSeed.SaveInternalTicketAsync(QepApiFactory factory, Guid tenantId, Guid creatorMemberId, Guid assigneeMemberId, DateOnly? dueOn = null) : Task<SupportTicket>` (aprovisiona y guarda por repositorio).

- [ ] **Paso 1: pruebas que fallan.**
  - `SupportPersistenceTests`: `ATaskRoundTrips` (guardar y releer: `IsInternal`, `CreatedByMemberId`, `ReplyChannel == None`,
    `Intake == Internal`, `DueOn`); `TheInternalCheckRejectsATaskWithACustomer` (`UPDATE … SET customer_id` → SQLSTATE `23514`,
    `CK_tickets_internal`); `ATenantHasOneInternalType` (segundo `INSERT` interno → `23505`, `UX_ticket_types_tenant_internal`);
    `ProvisioningSeedsTheInternalTypeLast` (nueve filas, la última interna en la posición 9; `ListAsync` devuelve ocho);
    `EnsuringTheInternalTypeTwiceKeepsOne`.
  - `SupportInternalTasksBackfillTests` (base propia, copia de `SupportLegalDocumentsBackfillTests`: migra Soporte hasta
    `AddSupportAutoCloseAndSurveyIndexes`, siembra por SQL y corre la nueva): `TheCreatorComesFromTheCreatedEvent` (manual → actor,
    formulario público → null); `EveryProvisionedTenantGetsOneInternalTypeAfterItsLast`;
    `ATenantWithItsOwnTareaInternaTypeStillGetsTheInternalOne` (D8).
  - `SupportUserReferenceProbeTests`: `TheCreatorOfATicketIsRetained`.
- [ ] **Paso 2:** `dotnet build tests/Modules/Support/Modules.Support.IntegrationTests`. Esperado: no compila.
- [ ] **Paso 3:** implementa el mapeo, el aprovisionador, el repositorio y la sonda; genera la migración con el comando de «Comandos» y
  agrega a mano el SQL del índice parcial, el *backfill* y su `Down`. Revisa que el `.cs` generado sólo agregue (D21).
- [ ] **Paso 4:** GREEN, de a una: `--filter "FullyQualifiedName~SupportPersistenceTests.ATaskRoundTrips"`, luego cada prueba nueva por
  nombre, luego `SupportInternalTasksBackfillTests` y `SupportUserReferenceProbeTests.TheCreatorOfATicketIsRetained`.
- [ ] **Paso 5:** las aserciones que cuentan tipos no cambian en esta tarea: `SupportPersistenceTests.cs:74` y `:426` leen
  `ListAsync` (`TypesAsync`, `:907-911`), que excluye el interno; `/settings/ticket-types` sigue en 8
  (`SupportTicketTypesApiTests.cs:369`, `:392`); `/ticket-types` pasa a 9 en la Tarea 8. Corre `SupportPersistenceTests` y
  `SupportTicketTypesApiTests` completas para confirmarlo.
- [ ] **Paso 6:** cierre del bloque: `SupportTicketTypeTests`, `SupportTicketInternalTests` y `SupportPersistenceTests` completas.
- [ ] **Paso 7:** commit `feat(support): persist internal tickets and seed the internal type`.

---

## Bloque 2 — Permiso y puerta única (antes de cualquier endpoint nuevo)

### Task 4: permiso `support.task.use` y políticas

**Files:**
- Modify: `Support/Modules.Support.Application/SupportPermissions.cs`, `SupportPolicies.cs`;
  `src/Bootstrapper/QepServiceCollectionExtensions.cs` (los cuatro `RoleDefinition` en ~2573-2860, el `PermissionDefinition` junto a los
  de Soporte en ~3919-3950 y las políticas en ~4462-4481)
- Test: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs`

**Interfaces:**
- Produces: `SupportPermissions.TaskUse = "support.task.use"`; `SupportPolicies.TicketAct = "support.ticket.act"` (manage o task.use);
  `SupportPolicies.TicketScopedView = "support.ticket.scoped_view"` (read, manage o task.use; D10), registradas con
  `AddAnyPermissionRequirement`.

- [ ] **Paso 1: pruebas que fallan** en `CompositionRootTests`: `TheTaskUsePermissionIsLowNeedsSupportAndEveryFactoryRoleHasIt`
  (definición con los textos del spec, `Shape == ("support.task", "Tareas internas", "use")`, en `admin`, `advisor`, `billing`, `cashier`);
  y en `TheSupportPoliciesAreRegistered`: `AcceptedPermissions(TicketAct) == [Manage, TaskUse]`,
  `AcceptedPermissions(TicketScopedView) == [Read, Manage, TaskUse]`.
- [ ] **Paso 2:** `dotnet build tests/ArchitectureTests/ArchitectureTests`. Esperado: no compila.
- [ ] **Paso 3:** implementa. Los roles de sistema son código (`TenantRoleCatalog` los arma de `RoleDefinition` en cada request): no hay
  datos que migrar; un rol personalizado lo recibe del editor de roles.
- [ ] **Paso 4:** `dotnet test tests/ArchitectureTests/ArchitectureTests --filter "FullyQualifiedName~CompositionRootTests"`. Esperado: PASS.
- [ ] **Paso 5:** commit `feat(support): add the support.task.use permission to every factory role`.

### Task 5: la tabla de acceso y `SupportTicketScope`

**Files:**
- Create: `Support/Modules.Support.Application/Access/SupportTicketAccessPolicy.cs`, `Access/SupportTicketScope.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro scoped, junto a `SupportTicketDtoComposer`, ~947)
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportTicketAccessPolicyTests.cs` (nuevo)

**Interfaces:**
- Produces:
  - `public sealed record SupportGrants(bool Read, bool Manage, bool TaskUse)`;
    `public enum SupportTicketAccessLevel { None, View, Act }`.
  - `public static SupportTicketAccessLevel SupportTicketAccessPolicy.For(SupportGrants grants, SupportTicket ticket, Guid? memberId)`:
    `Manage` → `Act`; si no, `Read` → `View`; si no, `TaskUse` y `ticket.IsInternal` y `memberId` es `CreatedByMemberId` o
    `AssigneeMemberId` → `Act`; si no, `None`. Es la única tabla: la usan la puerta y el hub.
  - `public enum SupportTicketKind { External, Internal, All }`; `public sealed record SupportAccess(SupportGrants Grants, Guid? MemberId)`;
    `public sealed record SupportListScope(SupportTicketKind Kind, Guid? ParticipantMemberId)`.
  - `public sealed class SupportTicketScope(IExecutionContext, ITenantModules, IMembershipDirectory, ISupportTicketRepository)`, una
    instancia por request (memoiza el `SupportAccess` por tenant):
    - `Task<SupportAccess> EnsureAccessAsync(Guid tenantId, CancellationToken)`: tenant de la ruta = activo y al menos uno de los tres
      permisos (claims), si no 403 `authorization.denied`; después el módulo (`TenantModuleGuard`). `MemberId` = membresía activa o null.
    - `Task<SupportTicket> ViewAsync(Guid tenantId, Guid ticketId, CancellationToken)`: nivel `None` o inexistente → 404
      `support.ticket.not_found`, el mismo cuerpo.
    - `Task<(SupportTicket Ticket, Guid Actor)> ActAsync(Guid tenantId, Guid ticketId, long? expectedVersion, CancellationToken)`:
      sin membresía activa → 403; `None` → 404; `View` → 403 `authorization.denied`; `Act` → recién ahí compara la versión (412).
    - `Task<(SupportTicket Ticket, Guid Actor)> ManageAsync(Guid tenantId, Guid ticketId, CancellationToken)`: exige `Manage` (403) y
      membresía activa (403), 404 si no existe; la versión la mira quien llama.
    - `Task<SupportTicket?> FindManagedAsync(Guid tenantId, Guid ticketId, CancellationToken)`: exige `Manage`; null si no existe o es
      interno (para quien hoy traduce «no existe» a su propio 422).
    - `static void EnsureExternal(SupportTicket ticket)` → 422 `InternalNoReply` si es interno;
      `static void EnsureVersion(SupportTicket ticket, long expectedVersion)` → 412 `concurrency.conflict`.
    - `Task<SupportListScope> ListScopeAsync(Guid tenantId, SupportTicketKind? requested, CancellationToken)`: con `Read` o `Manage`,
      `requested ?? External` sin participante; sólo `TaskUse`: `External`/`All` → 403 `authorization.denied`, `null`/`Internal` →
      `Internal` con `ParticipantMemberId` = su membresía (403 sin membresía).

- [ ] **Paso 1: pruebas que fallan** en `SupportTicketAccessPolicyTests`: `TheAccessTable` (teoría sobre los ocho `SupportGrants` ×
  {externo, interno propio como creador, interno propio como responsable, interno ajeno} con los resultados de la tabla de §5.6 y Review
  Focus 3: `read+task.use` sobre externo → `View`, sobre interno propio → `Act`); `ParticipationWithoutTaskUseGivesNoAccess` (creador
  sin ningún permiso → `None`); `ANullMemberIsNeverAParticipant`.
- [ ] **Paso 2:** `dotnet build tests/Modules/Support/Modules.Support.UnitTests`. Esperado: no compila.
- [ ] **Paso 3:** implementa la política y el servicio; registra `SupportTicketScope` scoped.
- [ ] **Paso 4:** `dotnet test tests/Modules/Support/Modules.Support.UnitTests --filter "FullyQualifiedName~SupportTicketAccessPolicyTests"`. Esperado: PASS.
- [ ] **Paso 5:** commit `feat(support): decide ticket access in one table and one scoped service`.

### Task 6: todos los caminos por la puerta, y su prueba de cobertura

**Files:**
- Modify (handlers, uno por uno; la columna «Puerta» es lo que llaman en vez de `SupportAuthorization.Ensure*` + `tickets.FindAsync`):

| # | Handler (archivo) | Ruta | Política | Puerta |
|---|---|---|---|---|
| 1 | `GetTicketHandler` (`Tickets/GetTicket.cs`) | `GET /tickets/{id}` | `TicketScopedView` | `ViewAsync` |
| 2 | `ListTicketNotesHandler` (`Notes/ListTicketNotes.cs`) | `GET …/notes` | `TicketScopedView` | `ViewAsync` |
| 3 | `GetNoteFileDownloadHandler` (`Notes/GetNoteFileDownload.cs`) | `GET …/notes/{n}/files/{f}/download` | `TicketScopedView` | `ViewAsync` |
| 4 | `AddTicketNoteHandler` (`Notes/AddTicketNote.cs`) | `POST …/notes` | `TicketAct` | `ActAsync(version: null)` |
| 5 | `OpenNoteAttachmentHandler` (`Notes/OpenNoteAttachment.cs`, vía `SupportNoteAttachmentRules.EnsureWritableAsync`) | `POST …/notes/attachments` | `TicketAct` | `ActAsync(null)` |
| 6 | `CompleteNoteAttachmentHandler` (`Notes/CompleteNoteAttachment.cs`, ídem) | `POST …/notes/attachments/{f}/complete` | `TicketAct` | `ActAsync(null)` |
| 7 | `ChangeTicketStatusHandler` (`Tickets/ChangeTicketStatus.cs`) | `PUT …/status` | `TicketAct` | `ActAsync(version)` |
| 8 | `ChangeTicketPriorityHandler` (`Tickets/ChangeTicketPriority.cs`) | `PUT …/priority` | `TicketAct` | `ActAsync(version)` |
| 9 | `AssignTicketHandler` (`Tickets/AssignTicket.cs`) | `PUT …/assignee` | `TicketAct` | `ActAsync(version)` |
| 10 | `TakeTicketHandler` (`Tickets/TakeTicket.cs`) | `POST …/take` | `TicketAct` | `ActAsync(version)` (D19) |
| 11 | `ListTicketMessagesHandler` (`Thread/ListTicketMessages.cs`) | `GET …/messages` | `TicketView` | `ViewAsync` (D18) |
| 12 | `GetReplyOptionsHandler` (`Replies/GetReplyOptions.cs`) | `GET …/reply-options` | `TicketView` | `ViewAsync` + `EnsureExternal` |
| 13 | `GetReplyDocumentDownloadHandler` (`Replies/GetReplyDocumentDownload.cs`) | `GET …/replies/{r}/document` | `TicketView` | `ViewAsync` |
| 14 | `GetTicketAttachmentDownloadHandler` (`Tickets/GetTicketAttachmentDownload.cs`) | `GET …/attachments/{a}/download` | `TicketView` | `ViewAsync` |
| 15 | `GetLegalAcceptanceSnapshotHandler` (`Tickets/GetLegalAcceptanceSnapshot.cs`) | `GET …/legal-acceptances/{kind}` | `TicketView` | `ViewAsync` |
| 16 | `SendTicketReplyHandler` (`Replies/SendTicketReply.cs`) | `POST …/replies` | `Manage` | `ManageAsync` + `EnsureExternal` (antes del `clientId`) |
| 17 | `ReissueTicketPinHandler` (`Tickets/ReissueTicketPin.cs`) | `POST …/pin` | `Manage` | `ManageAsync` + `EnsureExternal` + `EnsureVersion` |
| 18 | `LinkTicketCustomerHandler` (`Tickets/LinkTicketCustomer.cs`) | `PUT …/customer` | `Manage` | `ManageAsync` + `EnsureVersion` (el dominio rechaza el interno) |
| 19 | `ExtendTicketHandler` (`Tickets/ExtendTicket.cs`) | `POST …/extension` | `Manage` | `ManageAsync` + `EnsureVersion` (D13) |
| 20 | `ChangeTicketTypeHandler` (`Tickets/ChangeTicketType.cs`) | `PUT …/type` | `Manage` | `ManageAsync` + `EnsureVersion` |
| 21 | `PinTicketMessageHandler` (`Thread/PinTicketMessage.cs`) | `POST …/messages/{m}/pin` | `Manage` | `ManageAsync` + `EnsureExternal` + `EnsureVersion` |
| 22 | `ReleaseTicketMessageHandler` (`Thread/ReleaseTicketMessage.cs`) | `POST …/messages/{m}/release` | `Manage` | ídem |
| 23 | `SetTicketFollowThreadHandler` (`Thread/SetTicketFollowThread.cs`) | `PUT …/follow-thread` | `Manage` | ídem |
| 24 | `AssignUnclassifiedHandler` (`Thread/AssignUnclassified.cs`, `TicketId` en el cuerpo) | `POST /conversations/{c}/unclassified/{m}/assign` | `Manage` | `FindManagedAsync`: null o interno siguen siendo 422 `support.unclassified.ticket_not_candidate`, como hoy (`AssignUnclassified.cs:63-68`), no 404 |
| 25 | `ListTicketsHandler` (`Tickets/ListTickets.cs`) | `GET /tickets` | `TicketView` **hasta la Tarea 12** | `EnsureAccessAsync` (el `ListScopeAsync` entra en la 12) |

  Quedan fuera, con su motivo en la prueba de cobertura: los caminos del sistema y del portal sin miembro
  (`SupportAutoCloser`, `SupportThreadFollower`, `SupportSurveySender`, `SupportSurveyAnswers`, `PublicSurveys`, `FilePublicTicket`,
  `SupportPublicAccess`/`LookupPublicTicket`, `ResendPublicPin`, `SupportOpeningDelivery`, `SupportSystemAuthor`), las relecturas de
  `SendTicketReplyHandler` después de la puerta (`SendTicketReply.cs:248`, `:551`), y los listados por conversación
  (`ListConversationTicketsHandler`, `ListUnclassifiedHandler`, `DismissUnclassifiedHandler`: los internos no tienen conversación ni
  cliente; la exclusión explícita va en la Tarea 14). `SupportTicketActions.LoadAsync` se borra: lo reemplaza `ActAsync`.
- Modify: `Support/Modules.Support.Api/SupportEndpoints.cs` (`MapTicketAction` y `MapTicketPost` reciben la política; cada ruta con la de
  la tabla).
- Test: `tests/ArchitectureTests/ArchitectureTests/SupportLayerTests.cs`,
  `tests/Modules/Support/Modules.Support.IntegrationTests/SupportEndpointPoliciesTests.cs` (nuevo),
  `SupportTaskAccessApiTests.cs` (nuevo, `SharedSupportHost`).

**Interfaces:**
- Consumes: `SupportTicketScope` y `SupportPolicies` (Tareas 4 y 5), `SupportTicketSeed.SaveInternalTicketAsync` (Tarea 3).
- Produces: ningún handler de ticket llama ya `SupportAuthorization.EnsureAsync`/`EnsureCanViewAsync` ni
  `ISupportTicketRepository.FindAsync`.

- [ ] **Paso 1: pruebas que fallan.**
  - `SupportLayerTests` (con `IlScanner`; `SupportAuthorization` y `SupportTicketScope` por `assembly.GetType`):
    - `EveryTicketHandlerGoesThroughTheScope`: todo tipo de `Modules.Support.Application` que implemente `ICommandHandler<,>` o
      `IQueryHandler<,>` cuyo request tenga una propiedad `TicketId` llama (en `IlScanner.MethodsOf([handler])`, que incluye la máquina de
      estados) algún método de `SupportTicketScope`. Un handler nuevo con `TicketId` que no pase por la puerta la pone en rojo.
    - `NoTicketHandlerAuthorizesOnItsOwn`: ninguno de esos handlers llama `SupportAuthorization.EnsureAsync` ni `EnsureCanViewAsync`.
    - `OnlyTheScopeAndTheListedSystemPathsLoadATicket`: `IlScanner.CallersOf(application, m => m.DeclaringType == typeof(ISupportTicketRepository) && m.Name == "FindAsync")`
      está dentro de `SupportTicketScope` o de la lista cerrada de excepciones de arriba (cada una con su comentario).
  - `SupportEndpointPoliciesTests.EveryTicketRouteHasItsPolicy`: lee `EndpointDataSource` del host y compara, para toda ruta bajo
    `/api/v1/tenants/{tenantId:guid}/support/tickets` y `/tasks`, `(método, patrón) → política` contra la tabla de arriba (más las de
    las Tareas 9 y 11, que se suman al crearlas). Una ruta sin fila en la tabla falla.
  - `SupportTaskAccessApiTests` (externo creado por API con un gestor; tarea sembrada con `SaveInternalTicketAsync`; task-only = membresía
    con rol `cashier` + cliente con `X-Permissions: support.task.use`):
    - `ATaskOnlyMemberNeverSeesAnExternalTicket`: detalle, notas, descarga de archivo de nota, `POST` de nota, estado y prioridad → 404
      `support.ticket.not_found`.
    - `TheSame404ForUnknownExternalAndForeignTask`: los tres cuerpos son iguales salvo el id del mensaje.
    - `AStaleVersionOnSomeoneElsesTaskIs404Not412`.
    - `ATaskOnlyMemberWorksHerTasks`: como creadora y como responsable: `GET` 200, `PUT priority` 200, `POST notes` 201.
    - `AReaderSeesEveryTaskButCannotAct`: `GET` 200; `PUT priority` 403 sin código (política).
    - `AReaderWithTaskUseGets403OnAnExternalAction` (Review Focus 3): `PUT priority` sobre externo → 403 `authorization.denied`.
    - `AManagerSeesAndActsOnEveryTask`.
    - `HandingTheTaskOverRemovesTheFormerAssigneesAccess`: la responsable (no creadora) la asigna a un asesor; su `GET` siguiente → 404.
    - `ExternalOnlyRoutesStay403ForATaskOnlyMember`: `POST …/replies`, `PUT …/type`, `GET …/messages`, `GET /reports/summary` → 403.
    - `WithoutTheModuleATaskOnlyMemberGets403`.
- [ ] **Paso 2:** `dotnet build tests/ArchitectureTests/ArchitectureTests` y `dotnet build tests/Modules/Support/Modules.Support.IntegrationTests`. Esperado: compilan; `EveryTicketHandlerGoesThroughTheScope` en rojo con la lista de handlers (pégala como evidencia), y `SupportTaskAccessApiTests.ATaskOnlyMemberWorksHerTasks` en rojo con 403 (`--filter` por nombre).
- [ ] **Paso 3:** migra los 25 handlers según la tabla, sin cambiar el orden observable de hoy salvo lo que fija la puerta: permiso (403)
  → validador (422) → ticket (404) → versión (412). Cambia las políticas de las rutas.
- [ ] **Paso 4:** GREEN: `SupportLayerTests` y `CompositionRootTests` completas; `SupportEndpointPoliciesTests`; cada prueba de
  `SupportTaskAccessApiTests` por nombre y la clase una vez; regresión filtrada de `SupportAccessApiTests`, `SupportTicketActionsApiTests`,
  `SupportTicketNotesApiTests`, `SupportTicketNoteFilesApiTests`, `SupportTicketRepliesApiTests` (una clase por corrida).
- [ ] **Paso 5:** commit `refactor(support): route every ticket path through the ticket scope`.

### Task 7: el hub aplica la misma tabla

**Files:**
- Modify: `Support/Modules.Support.Application/SupportRealtimeTopics.cs`, `Support/Modules.Support.Api/SupportRealtimeAuthorizer.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportRealtimeTopicsTests.cs`,
  `tests/Modules/Support/Modules.Support.IntegrationTests/AppHubTaskTopicsTests.cs` (nuevo, `SupportHostWithoutWorkers`)

**Interfaces:**
- Consumes: `SupportTicketAccessPolicy.For` (Tarea 5).
- Produces: `static string SupportRealtimeTopics.Tasks(Guid memberId)` = `support:tasks:{memberId:D}` (D1);
  `static bool SupportRealtimeTopics.TryParseTasks(string topic, out Guid memberId)` (forma exacta, como `TryParseTicket`).
  El autorizador arma `SupportGrants` con tres `ISupportMemberPermissions.HasAnyAsync` (read, manage, task.use) y decide:
  `Inbox` → `Read` o `Manage`; `support:tasks:{id}` → `id == memberId` y `TaskUse` o `Manage` o `Read`; ticket → nivel ≥ `View`;
  presencia → nivel `Act`.

- [ ] **Paso 1: pruebas que fallan.** `SupportRealtimeTopicsTests`: `TheTasksTopicRoundTrips`,
  `TryParseTasksRefusesUppercaseInboxAndTicketTopics`. `AppHubTaskTopicsTests` (miembros con rol `cashier`, que ya concede `task.use` en
  la base): `ATaskOnlyMemberCannotSubscribeToTheInbox`, `AMemberSubscribesToHerOwnTasksTopic`,
  `NobodySubscribesToAnotherMembersTasksTopic` (ni un asesor), `ATaskOnlyMemberSubscribesToHerTaskButNotToAnExternalTicket`,
  `PresenceOnATaskIsForParticipantsNotForReaders`, `AParticipantWhoLostTaskUseCannotSubscribe` (membresía quitada después de sembrar la
  tarea), `AMalformedTasksTopicIsForbidden`.
- [ ] **Paso 2:** `dotnet build tests/Modules/Support/Modules.Support.UnitTests`. Esperado: no compila (`Tasks`, `TryParseTasks`).
- [ ] **Paso 3:** implementa.
- [ ] **Paso 4:** unitarias por clase; `AppHubTaskTopicsTests` por nombre y una vez completa; regresión `AppHubSupportTests` y
  `AppHubPresenceTests` (de a una).
- [ ] **Paso 5:** cierre del bloque 2: `Modules.Support.UnitTests` completa y `ArchitectureTests` completa. Revisión del bloque.
- [ ] **Paso 6:** commit `feat(support): authorize task topics and task presence with the access table`.

---

## Bloque 3 — Alta y reglas

### Task 8: el contrato de lectura

**Files:**
- Modify: `Support/Modules.Support.Application/SupportDtos.cs`, `Tickets/SupportTicketDtoComposer.cs`, `TicketTypes/ListTicketTypes.cs`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTasksReadApiTests.cs` (nuevo, `ClockedSupportHost`),
  `SupportTicketTypesApiTests.cs`, `SupportAccessApiTests.cs`

**Interfaces:**
- Produces: `SupportTicketDetailDto.ReplyChannel : string?` (D17) y, al final, `bool IsInternal = false, SupportMemberDto? CreatedBy = null`;
  `SupportTicketListItemDto` gana al final `bool IsInternal = false, SupportMemberDto? CreatedBy = null` (D16);
  `SupportTicketTypeDto` gana al final `bool IsInternal = false`. `AllowedTransitions` sale de `SupportTicketStatuses.AllowedFor`.
  `GET /ticket-types` agrega el interno al final (D9). `createdBy` con `IsMe` como el responsable.

- [ ] **Paso 1: pruebas que fallan** (tarea sembrada): `TheDetailOfATaskHasNoCustomerRequesterNorReplyChannel` (`customer`, `requester`,
  `replyChannel` null, `isInternal` true, `intake` `"Internal"`); `TheDetailNamesItsCreator` (`createdBy.displayName`, `isMe`);
  `ATaskNeverOffersWaitingOnCustomer` (`allowedTransitions`); `AManualTicketNamesItsCreatorAndAPublicOneHasNone`;
  `TheTypeSelectorListsTheInternalTypeLastAndMarked`. Ajusta `SupportAccessApiTests.AMemberWithOnlyManageReadsTheTypes` y
  `SupportTicketTypesApiTests.cs:233` a 9.
- [ ] **Paso 2:** las aserciones son sobre el JSON (como el resto de la clase): compila. RED por nombre, `--filter
  "FullyQualifiedName~SupportTasksReadApiTests.TheDetailOfATaskHasNoCustomerRequesterNorReplyChannel"`. Esperado: FAIL con
  `KeyNotFoundException` en `isInternal`.
- [ ] **Paso 3:** implementa.
- [ ] **Paso 4:** GREEN por nombre; `SupportTicketTypesApiTests` y `SupportTicketsApiTests` completas.
- [ ] **Paso 5:** commit `feat(support): expose isInternal and the creator on tickets and types`.

### Task 9: candidatos, `POST /tasks` y el evento de asignación

**Files:**
- Modify: `Support/Modules.Support.Application/ISupportMemberDirectory.cs`, `src/Bootstrapper/SupportMemberDirectory.cs`,
  `Tickets/SupportTicketRules.cs`, `SupportAuditActions.cs` (sin cambios si `TicketCreated` alcanza), `SupportEndpoints.cs`,
  `QepServiceCollectionExtensions.cs`, los dobles de `SupportOpeningSenderTests.cs` y `SupportSurveySenderTests.cs`
- Create: `Support/Modules.Support.Application/Tasks/ListTaskAssignees.cs`, `Tasks/CreateTask.cs`, `ISupportIntegrationEvents.cs`,
  `Support/Modules.Support.Infrastructure/Persistence/SupportIntegrationEvents.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/CreateTaskValidatorTests.cs`,
  `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTasksApiTests.cs` (nuevo, `ClockedSupportHost`),
  `SupportBootstrapperAdaptersTests.cs`

**Interfaces:**
- Consumes: `SupportTicket.CreateInternal` (2), `ISupportProvisioner.EnsureInternalTypeAsync`, `FindInternalAsync`,
  `SupportOutboxMessage` (3), `SupportTicketScope.EnsureAccessAsync` (5).
- Produces:
  - `SupportMemberRef(Guid MemberId, string DisplayName, bool IsActive, bool CanManage, bool CanUseTasks = false)`;
    `Task<IReadOnlyList<SupportMemberRef>> ISupportMemberDirectory.ListTaskMembersAsync(Guid tenantId, CancellationToken)`: activas con
    `support.task.use` o `support.ticket.manage`; `FindAsync` llena `CanUseTasks`.
  - `static Task SupportTicketRules.EnsureTaskAssigneeAsync(ISupportMemberDirectory members, Guid tenantId, Guid assigneeMemberId, CancellationToken)`
    → 422 `assignee_invalid` si no es activo con `CanUseTasks || CanManage`.
  - `ListTaskAssigneesQuery(Guid TenantId) : IQuery<SupportAssigneesDto>` y su handler (orden de `ListSupportAssigneesHandler`), en
    `GET /tasks/assignees` con `TicketAct`.
  - `CreateTaskRequest(string? Subject, string? Description, Guid? AssigneeMemberId, string? Priority, string? DueOn)`;
    `CreateTaskCommand(Guid TenantId, string Subject, string Description, Guid AssigneeMemberId, string? Priority, string? DueOn)` con
    `From(Guid tenantId, CreateTaskRequest request)`; `CreateTaskValidator` (`MustFit` de asunto y descripción, `assigneeMemberId`
    distinto de vacío, `MustBeAPriority`, `dueOn` null o `yyyy-MM-dd` con `GetSupportReportValidator.TryParse`);
    `CreateTaskHandler : ICommandHandler<CreateTaskCommand, SupportTicketDetailDto>` en `POST /tasks` con `TicketAct`, `201` y `Location`
    `…/tickets/{id}`.
  - `ISupportIntegrationEvents { void PublishTaskAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt); }`;
    `SupportIntegrationEventNames.TaskAssigned = "support.ticket.task-assigned.v1"`; payload camelCase exacto
    `{ ticketId, tenantId, ticketNumber, assigneeMemberId, assignedByMemberId }` (`ticketNumber` = `SupportTicketNumber.Format`),
    `correlation_id` = id del ticket. Acumula en `SupportDbContext`: commitea con el `SaveChanges` del handler. Registro scoped en
    `SupportInfrastructureExtensions`.
  - Orden del alta: `EnsureAccessAsync` (403 si no tiene `manage` ni `task.use`; un `read` sólo no crea) → membresía activa (403) →
    validador → `EnsureTaskAssigneeAsync` → `EnsureAsync` + `EnsureInternalTypeAsync` → transacción: número de la misma secuencia,
    `CreateInternal`, auditoría `TicketCreated` (campos `subject`, `description`, `assigneeMemberId`, `priority`, `dueOn`), evento si el
    responsable no es quien crea → commit → aviso en tiempo real (el de hoy; la Tarea 13 lo cambia) → detalle. Sin PIN ni correo (D7).

- [ ] **Paso 1: pruebas que fallan.** `CreateTaskValidatorTests`: `TheBodyNeedsSubjectDescriptionAndAssignee`,
  `ADueDateMustBeAnIsoDate`, `PriorityIsOptionalButMustBeKnown`. `SupportBootstrapperAdaptersTests`:
  `TaskMembersAreActiveMembersWithTaskUseOrManage` (cashier sí, cashier quitado no). `SupportTasksApiTests`:
  - `ATaskOnlyMemberCreatesATaskForAnotherMember` → 201, `isInternal`, tipo «Tarea interna», responsable, `createdBy` con `isMe`.
  - `ATaskCanBeAssignedToItsCreator`; `TheAssigneeIsRequired` (422 `validation.failed` en `assigneeMemberId`);
    `ARemovedOrForeignAssigneeIsInvalid` (422 `assignee_invalid`).
  - `ADueDateBeforeTodayIsRejected` (2026-11-12 → 422 `internal_due_in_past`; 2026-11-13 → 201, reloj del host);
    `AMalformedDueDateIs422OnItsField`.
  - `ATaskTakesTheNextNumberOfTheTenant` (después de un externo `SOP-000001`, la tarea es `SOP-000002`).
  - `ATaskHasNoConsultAccess` (`consultAccess` null, sin fila en `ticket_access`) y `CreatingATaskIsAudited`.
  - `AssigningATaskToSomeoneElseWritesTheEvent` (una fila en `platform.outbox_messages` con `event_name` y los cinco campos) y
    `CreatingATaskForYourselfWritesNoEvent`.
  - `AReaderCannotCreateATask` (403 pelado); `TheTaskAssigneesListTaskUsersAndManagers` (`GET /tasks/assignees`, cajera y asesor,
    `isMe`); `AReaderCannotListTaskAssignees` (403).
- [ ] **Paso 2:** `dotnet build tests/Modules/Support/Modules.Support.UnitTests` y el de integración. Esperado: no compila.
- [ ] **Paso 3:** implementa (dobles incluidos). Agrega las dos rutas a la tabla de `SupportEndpointPoliciesTests`.
- [ ] **Paso 4:** GREEN: validador; adaptador por nombre; `SupportTasksApiTests` por nombre y una vez completa;
  `SupportEndpointPoliciesTests`; `CompositionRootTests`.
- [ ] **Paso 5:** commit `feat(support): create internal tasks through POST /tasks`.

### Task 10: reglas de los internos sobre lo que ya existe

**Files:**
- Modify: `Tickets/CreateTicket.cs`, `Conversations/CreateTicketFromConversation.cs`, `Tickets/SupportTicketRules.cs`,
  `Replies/SendTicketReply.cs`, `Tickets/AssignTicket.cs`, `Tickets/TakeTicket.cs`, `TicketTypes/UpdateTicketType.cs`,
  `TicketTypes/DeleteTicketType.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/CreateTicketValidatorTests.cs`, `SendTicketReplyValidatorTests.cs`;
  `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTaskRulesApiTests.cs` (nuevo, `ClockedSupportHost`)

**Interfaces:**
- Consumes: Tareas 2, 6 y 9.
- Produces: `CreateTicketRequest`/`CreateTicketCommand` ganan `string? DueOn = null` (D5); `SupportTicketRules.EnsureTypeAsync` responde
  422 `internal_type_locked` con el tipo interno; los validadores de alta y respuesta rechazan `None` (D3); `AssignTicketHandler` valida
  con `EnsureTaskAssigneeAsync` en un interno y con `EnsureAssigneeAsync` en un externo, y publica `TaskAssigned` si el nuevo responsable
  no es null ni quien actúa y cambió; `TakeTicketHandler` valida igual y nunca publica. `DeleteTicketTypeHandler` rechaza el interno con
  `internal_type_locked`.

- [ ] **Paso 1: pruebas que fallan.** Validadores: `ADueDateIsNotPartOfAnExternalTicket`, `NoneIsNotAReplyChannel` (alta y respuesta).
  `SupportTaskRulesApiTests`:
  - `TheInternalTypeIsLockedOutOfEveryOtherEntry`: `POST /tickets`, `POST /conversations/{c}/tickets` y `PUT …/type` (de externo a
    interno y de tarea a externo) → 422 `internal_type_locked`.
  - `ExternalOnlyActionsRefuseATask`: `POST …/replies`, `GET …/reply-options`, `POST …/pin`, `PUT …/customer`, `PUT …/follow-thread` →
    422 `internal_no_reply` (D11); `POST …/extension` → 422 `extension_no_term`.
  - `ATaskCannotWaitOnTheCustomer` (`PUT …/status` → 422 `transition_invalid`); `ResolvingATaskRequestsNoSurvey`.
  - `ReassigningATaskToSomeoneElseWritesTheEvent`; `TakingATaskWritesNoEvent`; `ATaskCanGoToATaskOnlyMember` (cajera → 200) y
    `AnExternalTicketStillNeedsAManager` (cajera → 422 `assignee_invalid`).
  - `TheInternalTypeCannotBeUpdatedNorDeleted` (`PUT` y `DELETE` en ajustes → 422 `internal_type_locked`);
    `ReorderingIgnoresTheInternalType` (el orden de los ocho sigue funcionando).
- [ ] **Paso 2:** `dotnet build` de los dos proyectos. Esperado: no compila (`DueOn`).
- [ ] **Paso 3:** implementa.
- [ ] **Paso 4:** GREEN: validadores por clase; `SupportTaskRulesApiTests` por nombre y una vez completa; regresión filtrada de
  `SupportTicketsApiTests`, `SupportConversationTicketsApiTests`, `SupportTicketTypesApiTests` (de a una).
- [ ] **Paso 5:** commit `feat(support): keep internal tickets out of every external-only path`.

### Task 11: `PUT /tickets/{id}/due-date`

**Files:**
- Create: `Support/Modules.Support.Application/Tickets/ChangeTicketDueDate.cs`
- Modify: `SupportEndpoints.cs`, `SupportAuditActions.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTaskDueDateApiTests.cs` (nuevo, `ClockedSupportHost`),
  `SupportEndpointPoliciesTests.cs`

**Interfaces:**
- Produces: `ChangeTicketDueDateRequest(string? DueOn)`; `ChangeTicketDueDateCommand(Guid TenantId, Guid TicketId, string? DueOn, long ExpectedVersion)`
  con `From(…)`; validador (`yyyy-MM-dd` o null, versión > 0); `ChangeTicketDueDateHandler : ICommandHandler<ChangeTicketDueDateCommand, SupportTicketDetailDto>`
  con `ActAsync(version)` → `ChangeDueOn` → `SaveIfChangedAsync` con `SupportAuditActions.TicketDueDateChanged = "support.ticket.due_date_changed"`
  y campos `["dueOn"]`. Ruta con `If-Match` (`RequireVersion`), política `TicketAct`.

- [ ] **Paso 1: pruebas que fallan:** `TheCreatorChangesTheDueDate` (200, evento `DueDateChanged` en `events`, auditoría);
  `TheAssigneeChangesItToo`; `ClearingTheDueDateLeavesNone` (D20); `ADueDateInThePastIsRejected`; `AResolvedTaskKeepsItsDueDate`
  (`not_editable`); `AnExternalTicketHasNoEditableDueDate` (422 `not_internal`); `WithoutIfMatchIs428AndStaleIs412`;
  `SomeoneElsesTaskIs404`. Fila nueva en `SupportEndpointPoliciesTests`.
- [ ] **Paso 2:** build. Esperado: no compila.
- [ ] **Paso 3:** implementa.
- [ ] **Paso 4:** GREEN por nombre y la clase una vez. Cierre del bloque 3: `SupportTasksApiTests`, `SupportTaskRulesApiTests`,
  `SupportTaskDueDateApiTests` completas (de a una) y `Modules.Support.UnitTests`. Revisión del bloque.
- [ ] **Paso 5:** commit `feat(support): change the due date of an internal task`.

---

## Bloque 4 — Lectura, filtros y tiempo real

### Task 12: `kind`, `createdBy=me`, participante y resumen

**Files:**
- Modify: `Tickets/ListTickets.cs`, `SupportReadModels.cs`, `ISupportTicketRepository.cs`, `Persistence/SupportTicketRepository.cs`,
  `SupportEndpoints.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/ListTicketsValidatorTests.cs`,
  `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTaskInboxApiTests.cs` (nuevo, `ClockedSupportHost`)

**Interfaces:**
- Consumes: `SupportTicketScope.ListScopeAsync`, `SupportTicketKind`, `SupportListScope` (Tarea 5).
- Produces: `ListTicketsQuery` gana al final `string? Kind = null, string? CreatedBy = null` (`KindExternal = "external"`,
  `KindInternal = "internal"`, `KindAll = "all"`, `CreatedByMe = "me"`); `SupportTicketFilter` gana al final
  `SupportTicketKind Kind = SupportTicketKind.External, Guid? ParticipantMemberId = null, Guid? CreatedByMemberId = null`;
  `TotalsAsync(Guid tenantId, SupportTicketKind kind, Guid? participantMemberId, DateOnly today, DateOnly? dueSoonLimit, CancellationToken)` (D2).
  El participante es `created_by_member_id = m OR assignee_member_id = m`, **en la consulta**, en la lista y en el resumen.
  `GET /tickets` pasa a `TicketScopedView` (fila de `SupportEndpointPoliciesTests`).

- [ ] **Paso 1: pruebas que fallan.** Validador: `KindMustBeExternalInternalOrAll`, `CreatedByMustBeMe`. `SupportTaskInboxApiTests`
  (un externo, una tarea de la cajera y una tarea entre otros dos, todas `New`):
  - `TheInboxDefaultsToExternalTickets` (gestor sin `kind`: sólo el externo; `summary` cuenta 1 en `New`).
  - `KindInternalListsTasksAndAllListsBoth` (gestor: 2 y 3; el resumen sigue al `kind`).
  - `ATaskOnlyMemberListsOnlyHerTasksAndTheSummaryCountsOnlyThose` (sin `kind`: 1 fila, `New` = 1, vencidas y por vencer sólo de ella).
  - `ATaskOnlyMemberAskingForExternalOrAllGets403` (`authorization.denied`).
  - `CreatedByMeListsTheTasksICreated` y `AssigneeMeStillWorks`.
- [ ] **Paso 2:** build. Esperado: no compila.
- [ ] **Paso 3:** implementa.
- [ ] **Paso 4:** GREEN por nombre; regresión completa de `SupportInboxApiTests`.
- [ ] **Paso 5:** commit `feat(support): filter the inbox by kind, creator and participant`.

### Task 13: los avisos llegan a los participantes

**Files:**
- Modify: `Support/Modules.Support.Application/ISupportRealtime.cs`, `Support/Modules.Support.Infrastructure/SupportRealtimeNotifier.cs`,
  los handlers que pueden tocar un interno (`CreateTask`, `ChangeTicketStatus`, `ChangeTicketPriority`, `AssignTicket`, `TakeTicket`,
  `ChangeTicketDueDate`, `AddTicketNote`, `AutoClose/SupportAutoCloser.cs`), los dobles de `SupportAutoCloseApiTests.cs:436` y
  `SupportSurveyApiTests.cs:550`
- Create: `Support/Modules.Support.Application/SupportTicketNotices.cs`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTaskRealtimeNoticesApiTests.cs` (nuevo, con `RecordingRealtimePublisher`
  como `SupportRealtimeNoticesApiTests`)

**Interfaces:**
- Consumes: `SupportRealtimeTopics.Tasks` (Tarea 7).
- Produces: `Task ISupportRealtime.TaskChangedAsync(Guid tenantId, Guid ticketId, IReadOnlyCollection<Guid> memberIds, CancellationToken)`:
  publica `supportTicketChanged { ticketId }` en el tópico del ticket, en `Inbox` y en `support:tasks:{m}` de cada miembro distinto; nunca
  lanza. `static Task SupportTicketNotices.ChangedAsync(ISupportRealtime realtime, SupportTicket ticket, Guid? formerAssigneeMemberId, CancellationToken)`:
  externo → `TicketChangedAsync`; interno → `TaskChangedAsync` con creador, responsable y responsable anterior.

- [ ] **Paso 1: pruebas que fallan:** `CreatingATaskAnnouncesItToBothParticipants`; `ChangingATaskAnnouncesItOnTheTasksTopics` (estado,
  prioridad, fecha, nota); `ReassigningATaskAlsoTellsTheFormerAssignee`; `ChangingAnExternalTicketNeverTouchesATasksTopic`;
  `ThePayloadIsTheIdOnly`.
- [ ] **Paso 2:** build. Esperado: no compila (`TaskChangedAsync`).
- [ ] **Paso 3:** implementa; el responsable anterior se lee antes de `Assign`.
- [ ] **Paso 4:** GREEN por nombre; regresión de `SupportRealtimeNoticesApiTests` y `SupportAutoCloseApiTests` (de a una). Cierre del bloque 4:
  `SupportTaskInboxApiTests`, `SupportTaskRealtimeNoticesApiTests`, `AppHubTaskTopicsTests` completas. Revisión del bloque.
- [ ] **Paso 5:** commit `feat(support): tell task participants about changes on their topic`.

---

## Bloque 5 — Reportes y exclusiones

### Task 14: reportes, formulario público, consulta y listados por conversación

**Files:**
- Modify: `Persistence/SupportReportRepository.cs` (`ListTicketsAsync` y `ListSurveysAsync` con `!ticket.IsInternal`),
  `Persistence/SupportPublicFormRepositories.cs` (`FindByNumberAsync` y `LockByNumberAsync` con `!item.IsInternal`),
  `Public/GetPublicForm.cs` y `Public/FilePublicTicket.cs` (`!type.IsInternal` explícito, además de `IsPublished`),
  `Persistence/SupportTicketRepository.cs` (`ListByConversationOrCustomerAsync` y `ListOpenByConversationAsync` con `!IsInternal`)
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportReportsApiTests.cs`, `SupportPublicFormApiTests.cs`,
  `SupportPublicFilingApiTests.cs`, `SupportPublicLookupApiTests.cs`

**Interfaces:**
- Consumes: `SupportTicketSeed.SaveInternalTicketAsync` (3).

- [ ] **Paso 1: pruebas que fallan:** `TheReportIgnoresTasks` (una tarea resuelta en el rango no mueve ningún total y no aparece «Tarea
  interna» en el desglose por tipo); `ThePublicFormNeverOffersTheInternalType`; `FilingWithTheInternalTypeIdIsTheUnpublishedError`
  (el mismo 422 que `AnUnpublishedOrInactiveTypeIs422`); `TheConsultOfATaskNumberAnswersLikeAnUnknownNumber` (mismo status y cuerpo).
- [ ] **Paso 2:** con `SaveInternalTicketAsync` ya existente compilan: RED por nombre (el reporte cuenta la tarea). Pega la salida.
- [ ] **Paso 3:** implementa.
- [ ] **Paso 4:** GREEN por nombre; `SupportReportsApiTests`, `SupportPublicFormApiTests`, `SupportPublicFilingApiTests`,
  `SupportPublicLookupApiTests` completas (de a una). Revisión del bloque.
- [ ] **Paso 5:** commit `feat(support): keep internal tasks out of reports and the public portal`.

---

## Cierre

- [ ] `git branch --show-current` = `feature/support-internal-tasks`; árbol limpio.
- [ ] Sin otra corrida viva (`Get-Process testhost, dotnet -ErrorAction SilentlyContinue`): `Modules.Support.UnitTests`,
  `ArchitectureTests` y `Modules.Support.IntegrationTests` completas, una tras otra, con `-- xUnit.MaxParallelThreads=4`.
- [ ] Revisión fuerte de toda la rama (modelo más capaz) contra el spec, la tabla de §5.6 y este plan.
- [ ] Merge a `develop`; en `develop`, `docs/estado.md` (fila y registro). Handoff al owner: D1 (tópico con `:`), D8, D9, D10, D11, D16,
  D17 y D21 (sin tareas hasta que no quede un pod viejo). Push sólo si el owner lo pide.
