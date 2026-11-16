# Prioridad de tickets de Soporte — plan backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prioridad (`Low`·`Medium`·`High`·`Urgent`) por ticket: elegida al crear, cambiable con `PUT /priority` salvo resuelto o cerrado, en la traza, y filtro + orden en `GET /tickets`.

**Architecture:** Enum de dominio en `SupportTicket`, guardado por nombre; la acción calca «cambiar tipo» (`ChangeTicketType.cs`). La lista suma `priority` y `sort` al `ListTicketsQuery` y ordena en la base.

**Tech Stack:** .NET 10, EF Core + Npgsql, FluentValidation, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-15-support-ticket-priority-design.md`

## Global Constraints

- Niveles fijos, por nombre: `Low`, `Medium`, `High`, `Urgent`. Default y valor de filas existentes: `Medium`.
- La prioridad **no** toca `due_on`, término, pausas ni reloj.
- `Resolved` y `Closed` rechazan el cambio con `SupportErrorCodes.NotEditable` (reusar `EnsureEditable()`, `SupportTicket.cs:519`, que ya rechaza los dos).
- Misma prioridad: 200 con el detalle, sin subir versión ni evento.
- `sort=priority`: `Urgent`, `High`, `Medium`, `Low`; luego `ReceivedAt` desc; luego `Id`. `sort` default `received`.
- RED antes que GREEN. Integración **filtrada por nombre** (`--filter "FullyQualifiedName~<Prueba>"`); la clase entera, una vez por bloque. **La suite completa de integración no se corre sin pedido del owner**: antes de mergear, avisarle que falta.
- Nunca dos corridas de integración a la vez en la máquina: mirar `Get-Process testhost, dotnet` antes.
- Otra sesión trabaja `feature/support-phase-4a` con migraciones de Soporte: si llega antes a `develop`, regenerar la migración de esta rama sobre su snapshot al mergear.

## Review Focus

- Ticket `WaitingOnCustomer` (reloj pausado): cambiar prioridad no debe tocar `due_on` ni pausas → prueba en Task 1.
- `PUT /priority` sin `If-Match` → el mismo error que `/type` sin `If-Match` → prueba en Task 3.
- `priority=High,Urgent` (varios) y `priority=Foo` → filtra / 422 → pruebas en Task 5.
- `sort=priority` con más filas que una página → la página 2 sigue el orden sin repetir → prueba en Task 5.
- Alta con `"priority": "urgent"` (minúsculas) → 422, no se acepta en silencio → prueba en Task 4.

---

## Bloque A — dominio y persistencia (un implementador)

### Task 1: Prioridad en el dominio

**Files:**
- Create: `src/Modules/Support/Modules.Support.Domain/SupportTicketPriority.cs`
- Modify: `src/Modules/Support/Modules.Support.Domain/SupportTicket.cs` (propiedad, fábricas `CreateManual`/`CreateFromConversation`/`CreateFromPublicForm`, `Start`, método nuevo)
- Modify: `src/Modules/Support/Modules.Support.Domain/SupportTicketEventKind.cs` (+ `PriorityChanged`, al final)
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportTicketTests.cs`

**Interfaces:**
- Produces: `enum SupportTicketPriority { Low, Medium, High, Urgent }`; `SupportTicket.Priority { get; private set; }`; `SupportTicket.ChangePriority(SupportTicketPriority priority, Guid actorMemberId, DateTimeOffset now)`; `CreateManual(..., SupportTicketPriority priority, ...)` y `CreateFromConversation(..., SupportTicketPriority priority, ...)` (parámetro nuevo después de `description`); `CreateFromPublicForm` sin parámetro, siempre `Medium`. Evento `PriorityChanged` con detalles `{"from":"Medium","to":"High"}`.

- [ ] **Step 1: Pruebas que fallan** en `SupportTicketTests`:
  - `AManualTicketKeepsThePriorityItWasCreatedWith` → `CreateManual(..., SupportTicketPriority.High, ...)`, `Priority == High`.
  - `APublicFormTicketIsAlwaysMedium`.
  - `ChangingThePriorityWritesAnEventWithFromAndTo` → kind `PriorityChanged`, details `from` = `"Medium"`, `to` = `"Urgent"`, versión +1.
  - `ChangingToTheSamePriorityDoesNothing` → sin evento nuevo, misma versión.
  - `ChangingThePriorityKeepsTheDueDateAndThePauses` → ticket `WaitingOnCustomer` (pausado): `DueOn` y pausas iguales antes y después.
  - `AResolvedTicketRejectsAPriorityChange` y `AClosedTicketRejectsAPriorityChange` → `SupportDomainException` con `SupportErrorCodes.NotEditable`.
- [ ] **Step 2:** `dotnet build` → falla de compilación (`SupportTicketPriority` no existe). La salida del build es la evidencia del RED; no levantar contenedores.
- [ ] **Step 3:** Implementar. `ChangePriority`: `EnsureEditable()`; si igual, `return`; si no, asignar y `AddEvent(SupportTicketEventKind.PriorityChanged, actorMemberId, now, new { from = <nombre>, to = <nombre> })`. Arreglar los llamadores de las fábricas (handlers de alta y seeds de prueba) pasando `SupportTicketPriority.Medium` por ahora; Task 4 los conecta al request.
- [ ] **Step 4:** `dotnet test tests/Modules/Support/Modules.Support.UnitTests` → PASS.
- [ ] **Step 5:** Commit `feat(support): add ticket priority to the domain`.

### Task 2: Columna y migración

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Infrastructure/Persistence/SupportDbContext.cs` (junto a `Intake`/`ManualMedium`, ~línea 405)
- Create: `src/Modules/Support/Modules.Support.Infrastructure/Persistence/Migrations/<timestamp>_AddSupportTicketPriority.cs` (+ Designer, snapshot)
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportPersistenceTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: columna `priority` text(24) NOT NULL default `'Medium'`; `CK_ticket_events_kind` con `'PriorityChanged'` agregado a la lista actual (`...,'ReplySent','ConversationLinked','PriorityChanged'`); check nuevo `CK_tickets_priority` = `priority IN ('Low','Medium','High','Urgent')`.

- [ ] **Step 1:** Prueba `ATicketPriorityAndItsChangeEventRoundTrip` → guarda un ticket `High`, cambia a `Urgent`, relee: `Priority == Urgent` y el evento `PriorityChanged` persiste (sin el check ampliado, falla al guardar).
- [ ] **Step 2:** Correr filtrada → FAIL.
- [ ] **Step 3:** Mapeo `ticket.Property(value => value.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(24)`; migración con `dotnet ef migrations add AddSupportTicketPriority` (proyecto Infrastructure de Support, contexto `SupportDbContext`) y ajustar a mano el `defaultValue: "Medium"` y los dos checks (drop + create de `CK_ticket_events_kind` con la lista nueva; `Down` restaura la anterior). `Api.exe` detenido antes (bloquea el build).
- [ ] **Step 4:** Filtrada → PASS. Luego la clase `SupportPersistenceTests` entera (fin del bloque).
- [ ] **Step 5:** Commit `feat(support): persist the ticket priority`.

## Bloque B — aplicación y HTTP (un implementador)

### Task 3: `PUT /tickets/{id}/priority` y la traza

**Files:**
- Create: `src/Modules/Support/Modules.Support.Application/Tickets/ChangeTicketPriority.cs`
- Modify: `src/Modules/Support/Modules.Support.Application/SupportAuditActions.cs` (+ `TicketPriorityChanged = "support.ticket.priority_changed"`)
- Modify: `src/Modules/Support/Modules.Support.Application/SupportDtos.cs` (`Priority` en `SupportTicketListItemDto` y `SupportTicketDetailDto`; `FromPriority`, `ToPriority` al final de `SupportTicketEventDto`)
- Modify: `src/Modules/Support/Modules.Support.Application/Tickets/SupportTicketDtoComposer.cs` (mapear `Priority`; `EventDetails` parsea `PriorityChanged` → `Text(root,"from")`, `Text(root,"to")`)
- Modify: `src/Modules/Support/Modules.Support.Api/SupportEndpoints.cs:315-318` (+ `MapTicketAction<ChangeTicketPriorityRequest>(group, "priority", ChangePriorityAsync)`) y registro del handler/validador donde se registran los de `ChangeTicketType`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTicketActionsApiTests.cs`

**Interfaces:**
- Produces: `record ChangeTicketPriorityRequest(string? Priority)`; `record ChangeTicketPriorityCommand(Guid TenantId, Guid TicketId, SupportTicketPriority Priority, long ExpectedVersion) : ICommand<SupportTicketDetailDto>` con `From(...)` que parsea el nombre (sensible a mayúsculas; inválido → falla de validación); JSON `priority`, `fromPriority`, `toPriority` por nombre.

- [ ] **Step 1:** Pruebas en `SupportTicketActionsApiTests`:
  - `ChangingThePriorityAnswersTheDetailAndAddsTheEvent` → 200, `priority` = `"High"`, último evento `kind` `"PriorityChanged"`, `fromPriority` `"Medium"`, `toPriority` `"High"`, `version` +1, auditoría `support.ticket.priority_changed` con campo `priority`, aviso de tiempo real.
  - `TheSamePriorityAnswers200WithoutANewVersion`.
  - `AStalePriorityChangeIs412`; `APriorityChangeWithoutIfMatchFailsLikeTheTypeChange` (mismo status que `/type` sin `If-Match`).
  - `AResolvedTicketRejectsThePriorityWith422NotEditable`.
  - `AnUnknownPriorityIs422` (`"Foo"` y `"urgent"`).
  - `ReadingWithoutManageCannotChangeThePriority` (mismo status que `/type`).
- [ ] **Step 2:** Build → falla de compilación (evidencia del RED).
- [ ] **Step 3:** Implementar calcando `ChangeTicketType.cs` (autorización `SupportPermissions.Manage`, `LoadAsync` con versión, `SaveIfChangedAsync` con `TicketPriorityChanged` y `["priority"]`, tiempo real sólo si subió la versión). No lee el reloj del tenant salvo lo que `ComposeDetailAsync` ya pida.
- [ ] **Step 4:** Filtradas por nombre → PASS.
- [ ] **Step 5:** Commit `feat(support): change a ticket's priority`.

### Task 4: Prioridad al crear

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Application/Tickets/CreateTicket.cs` (`CreateTicketRequest` + `string? Priority`; command, validador, handler)
- Modify: `src/Modules/Support/Modules.Support.Application/Conversations/CreateTicketFromConversation.cs` (igual)
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTicketsApiTests.cs`, `SupportConversationTicketsApiTests.cs`

**Interfaces:**
- Consumes: fábricas de Task 1.
- Produces: `priority` opcional en los dos `POST`; null/ausente → `Medium`; nombre inválido → 422 de validación con campo `priority`.

- [ ] **Step 1:** Pruebas: `CreatingWithAPriorityKeepsIt` (manual, `"Urgent"`), `CreatingWithoutAPriorityIsMedium`, `CreatingWithALowercasePriorityIs422`; en conversación `ATicketFromAConversationTakesItsPriority`.
- [ ] **Step 2:** Filtradas → FAIL.
- [ ] **Step 3:** Parseo con `Enum.TryParse(value, ignoreCase: false, out ...)` y `Enum.IsDefined`; quitar el `Medium` provisional de Task 1 en estos handlers.
- [ ] **Step 4:** Filtradas → PASS.
- [ ] **Step 5:** Commit `feat(support): pick the priority when creating a ticket`.

### Task 5: Filtro y orden de la bandeja

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Application/Tickets/ListTickets.cs` (`ListTicketsQuery` + `string? Priority`, `string? Sort`; constantes `SortReceived = "received"`, `SortPriority = "priority"`; validador; doc)
- Modify: `src/Modules/Support/Modules.Support.Api/SupportEndpoints.cs:472-484` (`string? priority`, `string? sort`)
- Modify: `src/Modules/Support/Modules.Support.Infrastructure/Persistence/SupportTicketRepository.cs:32` (orden y filtro)
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportInboxApiTests.cs`

**Interfaces:**
- Produces: `GET /tickets?priority=High,Urgent&sort=priority`. `priority`: nombres separados por coma, como `status` (`TryParsePriorities`); `sort` vacío = `received`.

- [ ] **Step 1:** Pruebas:
  - `FilteringByPriorityKeepsOnlyThoseLevels` (`High,Urgent`).
  - `SortingByPriorityPutsUrgentFirstThenNewest` → 4 tickets con niveles y fechas cruzadas, orden esperado exacto.
  - `SortingByPriorityPagesWithoutRepeats` → `pageSize=2`, páginas 1 y 2 sin ids repetidos y en orden.
  - `WithoutSortTheInboxStaysNewestFirst`.
  - `AnUnknownPriorityOrSortIs422` (`priority=Foo`, `sort=due`).
  - `TheSummaryIgnoresThePriorityFilter` (el `summary` no cambia con `priority`).
- [ ] **Step 2:** Filtradas → FAIL.
- [ ] **Step 3:** Orden traducible por EF: rango con expresión condicional (`Urgent`→0 … `Low`→3), luego `ReceivedAt` desc, luego `Id`. Índice sólo si el plan de la consulta lo pide (decisión del implementador, anotarla en el commit).
- [ ] **Step 4:** Filtradas → PASS; luego las clases tocadas del bloque B enteras, una por vez.
- [ ] **Step 5:** Commit `feat(support): filter and sort the inbox by priority`.

## Cierre

- [ ] `dotnet test tests/ArchitectureTests` → PASS.
- [ ] Revisión fuerte de la rama entera (una sola vez).
- [ ] Avisar al owner que falta la suite completa de integración de Support antes de mergear a `develop`, y esperar su decisión.
