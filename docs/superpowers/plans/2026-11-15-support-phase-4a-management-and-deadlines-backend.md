# Soporte (PQRSF) — Fase 4a «Gestión y plazos», backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el asesor radique un ticket manual anónimo, borre un tipo de ticket que nadie usa, se asigne un ticket a sí mismo con un botón («Asignármelo») y vea en cada miembro si es él (`IsMe`), y que pueda prorrogar una vez el plazo de un ticket antes de que venza, con motivo y tope del doble del término, todo en la traza.

**Architecture:** Todo dentro del módulo Support, sobre el estado que deja `feature/support-realtime-notes` (notas internas y avisos por el hub global). Dos migraciones aditivas de `SupportDbContext`: la FK `tickets.ticket_type_id → ticket_types.id` (`RESTRICT`), que vuelve a prueba de carreras el «un tipo usado no se borra», y las columnas de la prórroga. El «quién soy» entra por un puerto nuevo, `ISupportViewer`, con adaptador en Bootstrapper, y llega a todos los `SupportMemberDto` por `SupportReadContext`. Cada escritura de ticket nueva llama `ISupportRealtime.TicketChangedAsync` después del commit, como manda la regla del hub.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql.

**Spec:** [`docs/superpowers/specs/2026-11-14-support-tickets-pqrs-design.md`](../specs/2026-11-14-support-tickets-pqrs-design.md): §7.2 (prórroga, «por vencer»), §7.4 (traza), §7.5 (un tipo usado no se borra), §9.4 (PIN, anónimo), §10 (permisos: `manage` incluye prorrogar), §12 fase 4 (los cuatro ajustes de gestión) y §13.1. Planes anteriores: [fase 1](2026-11-14-support-phase-1-core-backend.md), [fase 2](2026-11-15-support-phase-2-messaging-backend.md), [fase 3](2026-11-15-support-phase-3-public-form-backend.md) y, en su rama, `2026-11-15-support-realtime-and-notes-backend.md`.

---

## Antes de empezar

- **Prerrequisito duro:** `feature/support-realtime-notes` mergeada a `develop`, y esta rama rebasada (o recreada) sobre ese `develop`. Este plan cita archivos y nombres de esa rama (`ISupportRealtime`, `SupportRealtimeNoticesApiTests`, la migración `20261115233631_AddSupportTicketNotes`). Si al empezar `git log develop --oneline -S "ISupportRealtime"` no devuelve nada, **no empieces**: avisa.
- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-phase-4a`, rama `feature/support-phase-4a`. Rutas absolutas siempre: un script con `cwd` relativo escribe en el checkout de `develop`.
- Antes de **cada** commit: `git branch --show-current`. Si dice `main` o `develop`, frena.
- Conventional commits en inglés, `git commit -m "…" -- <rutas>`, **sin `Co-Authored-By` ni atribución de IA**, aunque el harness la sugiera.
- PowerShell, sin `&&`. Si `Api.exe` o `dotnet Api.dll` corre, el build falla por archivo bloqueado: avisa, no lo detengas.
- **Pruebas (owner, 2026-11-15):** el implementador **no corre pruebas**, ni unitarias ni de integración. TDD en el orden: la prueba primero; RED = `dotnet build` del proyecto de pruebas fallando por compilación (`CS0246`, `CS1061`, `CS7036`…); GREEN = `dotnet build` de la solución sin errores ni avisos. Una prueba que compila desde el principio se marca «RED sin evidencia: no se corre». Las dos salidas, literales, van al reporte del bloque. Al cerrar cada bloque el implementador entrega al owner la lista exacta de `--filter` del bloque (abajo, en cada tarea); el owner las corre, una a la vez, nunca dos corridas de integración juntas.
- Atajos: `$W` = worktree; `$SU`/`$SI` = `$W\tests\Modules\Support\Modules.Support.{UnitTests,IntegrationTests}`; `$BU` = `$W\tests\Bootstrapper\Bootstrapper.UnitTests`.
- Migraciones con el factory de diseño, desde `$W`:
  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations
  ```
- `docs/estado.md` **no se toca** en esta rama.
- Comentarios y documentación en español colombiano tuteando; identificadores, códigos y rutas en inglés.

## Global Constraints

- Support no referencia `Modules.Messaging.*`, `Customers.*`, `Geography.*`, `Store.*` ni `Notifications.*`; `SupportLayerTests` no se afloja. Nada nuevo en Application referencia EF Core ni Npgsql.
- Aislamiento de tenant en dos capas: ruta `{tenantId:guid}` + política, y el handler revalida (`SupportAuthorization.EnsureAsync`). Otro tenant = 403 por la política; un id ajeno bajo mi ruta = 404 como hoy.
- Toda escritura nueva de ticket: auditoría (`ISupportAuditPublisher`) en la misma transacción y `ISupportRealtime.TicketChangedAsync(…, CancellationToken.None)` después del commit, sólo si cambió la versión.
- Errores: ningún status a mano en un handler; `SupportDomainException` → 422, `RequestConcurrencyException` → 412, faltante de `If-Match` → 428 (`RequireVersion`). Texto libre (motivo de prórroga) con validador de FluentValidation además de la guarda del dominio.
- Traducción de errores de base en `SupportUnitOfWork`, **por nombre de restricción** (`SupportDbContext.TicketTypeForeignKey`) y SQLSTATE, nunca sólo por código.
- Migraciones aditivas (expand). Los valores nuevos de enum van **al final** (`SupportTicketEventKind.Extended`); se persisten por nombre.
- Sin hosted services ni singletons con estado nuevos: `MultiReplicaInventory` no cambia (`ISupportViewer` es scoped y sin estado).
- Ningún log, auditoría ni evento lleva un PIN (gotcha del repo). La auditoría lleva nombres de campo, nunca el motivo de la prórroga.

## Decisiones del plan

Mías (planner) sobre lo que el spec deja al implementador. Las marcadas **(owner)** están también en «Decisiones pendientes del owner» y el plan las asume como aquí dicen hasta que responda.

| # | Decisión | Por qué |
| --- | --- | --- |
| G1 | Alta anónima: `CreateTicketRequest` suma `bool? Anonymous` (null = `false`). Con `true`: `CustomerId` y `Requester` deben venir `null` y `ReplyChannel` debe ser `"Portal"` (422 `validation.failed` en el campo); `ManualMedium` sigue obligatorio. Sin anónimo, `Portal` sigue rechazado (`support.ticket.reply_channel_unavailable`, como hoy). | Spec §12: «sin anónimo, el canal de respuesta sigue obligatorio» y «reusa `IsAnonymous` y `Portal`». Exigir el canal explícito evita un segundo contrato para el mismo campo. |
| G2 | Dominio: `SupportTicket.CreateManual` gana `bool anonymous`; con `true` exige `customerId == null` y `requester == null`, fija `IsAnonymous = true` y `ReplyChannel = Portal`, y no exige nombre. `Start` deja de exigir solicitante cuando el ticket es anónimo. | Una sola fábrica para el manual; la regla D6 sigue para el manual con nombre. |
| G3 | El ticket manual anónimo nace con PIN igual que todos (`SupportTicketAccess.Issue`). La respuesta del `POST /tickets` lo trae **una vez** en `SupportTicketDetailDto.IssuedPin` (último campo, `string?`), sólo en ese alta; en toda otra lectura es `null`. **(owner, P-1)** | Sin el PIN, quien llamó o vino en persona no puede consultar su radicado y el anónimo no se puede responder (su único canal es la página de consulta, §9.4). Es el equivalente de «se muestra una vez en pantalla» del formulario. |
| G4 | Borrar tipo: `DELETE /settings/ticket-types/{typeId}` con `If-Match`, permiso `support.settings.manage`, 204. Con algún ticket (cualquier estado) → `422 support.ticket_type.in_use`. Versión vieja 412, sin `If-Match` 428, de otro tenant 404 `support.ticket_type.not_found`. Audita `support.ticket_type.deleted`. | Spec §12. |
| G5 | La regla «tiene tickets» se cierra en la base: FK `FK_tickets_ticket_types_ticket_type_id` con `ON DELETE RESTRICT`. El handler pregunta antes (respuesta amable) y `SupportUnitOfWork` traduce la carrera: `23001` (restrict) con esa FK → `support.ticket_type.in_use`; `23503` con esa FK (un alta o cambio de tipo contra un tipo recién borrado) → `support.ticket_type.unavailable`. | La pregunta previa sola deja un ticket apuntando a un tipo que ya no existe. Precedente `CatalogUnitOfWork.cs:231` (23503 vs 23001 según el lado). |
| G6 | `SupportTicketTypeDto` suma `bool InUse` al final: la pantalla de ajustes ofrece «Eliminar» o sólo «Desactivar» sin probar. | Regla BFF del repo; el motivo va en el DTO. |
| G7 | «Asignármelo»: `POST /tickets/{ticketId}/take` con `If-Match`, permiso `support.ticket.manage`, sin cuerpo; resuelve la membresía por la sesión, la valida con la misma regla del responsable (`EnsureAssigneeAsync`) y llama `SupportTicket.Assign(actor, actor, now)`. Ya asignado a mí: 200 sin cambio de versión. Resuelto o cerrado: `422 support.ticket.not_editable` (R9, ya en el dominio). Audita `support.ticket.assigned`. | Spec §12; el nombre `take` copia `POST /conversations/{id}/take` de Mensajería. |
| G8 | `SupportMemberDto(Guid Id, string DisplayName, bool IsMe)` en **todos** sus usos (responsable, actor de la traza, autor de respuesta y nota, `GET /assignees`, autor de textos legales). El «yo» sale de un puerto nuevo `ISupportViewer.MemberIdAsync(tenantId)` (adaptador en Bootstrapper: `null` sin usuario autenticado en el `HttpContext`), leído una vez en `ReadContextAsync` y guardado en `SupportReadContext.ViewerMemberId`. `TryReadContextAsync` (consulta pública) no lo lee. | Spec §12: «el SPA no conoce su `memberId`». Precedente `AssignedToDto.IsMe` de Mensajería. `HttpExecutionContext.SubjectId` lanza sin `HttpContext`; el puerto no. |
| G9 | Prórroga: `POST /tickets/{ticketId}/extension` con `If-Match`, cuerpo `{ businessDays, reason }`, permiso `support.ticket.manage`. Se guarda como **días hábiles** (`ExtensionBusinessDays`), no como fecha: `DueOn = Add(ReceivedOn, término + prórroga + pausas)`, así una pausa posterior o un cambio de tipo la siguen corriendo bien. | Spec §7.2 y §7.3 («instantes de pausa y de prórroga»). |
| G10 | Reglas de la prórroga, en el dominio y en este orden: resuelto o cerrado → `support.ticket.not_editable`; ya prorrogado → `support.ticket.extension_already_used`; sin término → `support.ticket.extension_no_term`; reloj detenido (`WaitingOnCustomer`) → `support.ticket.extension_clock_stopped` **(owner, P-4)**; hoy local > `DueOn` → `support.ticket.extension_overdue` (el mismo día del vencimiento todavía se puede: vence al final del día hábil); `businessDays > TermBusinessDays` → `support.ticket.extension_too_long` **(owner, P-3)**. | Spec §7.2: única, antes de que venza, el nuevo plazo no excede el doble del término inicial. |
| G11 | Motivo: 1 a 1000 caracteres recortado (`SupportTicket.ExtensionReasonMaxLength = 1000`); `businessDays` 1 a `SupportTicketType.MaxTermBusinessDays` (365) en el validador, el tope real lo juzga el dominio. Evento `Extended` con `{ businessDays, reason, fromDueOn, toDueOn }`; auditoría `support.ticket.extended` con campos `["dueOn", "extension"]` (sin el motivo). | §7.4: la prórroga queda en la traza. El motivo es texto del asesor, no de la auditoría. |
| G12 | Cambiar el tipo de un ticket prorrogado **conserva** los días de prórroga y recalcula con el término nuevo. **(owner, P-5)** | Es la regla de §7.2 («desde la radicación, más las pausas») con la prórroga como un sumando más; no inventa un error nuevo. |
| G13 | El aviso al solicitante de la prórroga **no** lo manda el backend en la prórroga: el SPA, con la prórroga hecha, abre el compositor con el canal del ticket y el asesor envía el aviso como respuesta (`POST …/replies`, que ya cubre hilo, plantilla, correo y `Portal`). **(owner, P-2)** | El spec no dice por qué canal ni con qué texto, y la respuesta ya resuelve canal, ventana, plantilla y reintento. El detalle trae `Clock.Extension` para prellenar. |
| G14 | «Avisos de vencimiento» internos (a quién, por dónde, cuándo): **no se construyen**. El spec sólo define los estados `DueSoon`/`Overdue` de la bandeja y del detalle, que ya existen (`SupportDueStates.Evaluate`, umbral 0–30 de `SupportTenantSettings`). **(owner, P-6)** | «No inventar». |

## Ya cubierto, sin tarea

- **Responsable fijo en `Resolved` y `Closed`** (spec §12, cuarto ajuste): ya está, y más amplio. `SupportTicket.EnsureEditable()` (`SupportTicket.cs:517-526`) rechaza `Assign`, `ChangeType` y `LinkCustomer` con `422 support.ticket.not_editable` en resuelto o cerrado, también con el mismo valor (R9, owner 2026-11-15, commit `203ccb3a` en `develop`). Lo amarran `SupportTicketTests.AFinishedTicketCannotBeReassignedRetypedOrRelinked` y `SupportTicketActionsApiTests.AFinishedTicketCannotBeAssignedRetypedOrLinked`. La Tarea 3 sólo corrige el `<summary>` desactualizado de `AssignTicketHandler` («en cualquier estado (pre-flight R9…)») y la Tarea 5 la frase del spec.

## Review Focus

1. **Carrera borrar tipo contra alta:** un `DELETE` del tipo y un `POST /tickets` con ese tipo a la vez dejan, o el tipo borrado y el alta en `422 support.ticket_type.unavailable`, o el ticket creado y el borrado en `422 support.ticket_type.in_use`; nunca un ticket con `ticket_type_id` huérfano. Tarea 2: `TheForeignKeyTurnsADeleteOfAUsedTypeInto422InUse` y `TheForeignKeyTurnsATicketOnADeletedTypeInto422Unavailable` (por SQL directo contra la base migrada, sin depender del orden).
2. **El PIN del anónimo manual sale una sola vez:** viene en el `201` del alta y es `null` en el `GET` del detalle, en la respuesta de cualquier acción y en la auditoría. Tarea 1: `AnAnonymousManualTicketReturnsItsPinOnlyOnCreation`.
3. **Prórroga el último día y el día siguiente:** con el reloj en el día del vencimiento se acepta; el primer día hábil después, `extension_overdue`. Tarea 4: `AnExtensionOnTheDueDayIsAccepted` y `AnExtensionTheDayAfterTheDueDateIsOverdue`.
4. **Pausa después de la prórroga:** prorrogar, pasar a `WaitingOnCustomer` dos días hábiles completos y volver corre el vencimiento por término + prórroga + pausa, no se pierde la prórroga. Tarea 4: `APauseAfterAnExtensionKeepsTheExtension`.
5. **`IsMe` fuera de una sesión:** la consulta pública y el worker del hilo componen sin usuario y no lanzan (`ViewerMemberId` null → todo `IsMe = false`). Tarea 3: `SupportViewerTests.ItIsNullWithoutAnAuthenticatedUser` y que `SupportPublicLookupApiTests` siga compilando sin cambios de contrato.

---

## Contrato para las pantallas (BFF)

Bajo `/api/v1/tenants/{tenantId:guid}/support`:

| Método y ruta | Permiso | Cuerpo | Respuesta |
| --- | --- | --- | --- |
| `POST /tickets` (cambia) | `Manage` | `CreateTicketRequest` + `bool? Anonymous` | `201 SupportTicketDetailDto` (`IssuedPin` sólo si es anónimo) |
| `DELETE /settings/ticket-types/{typeId:guid}` (`If-Match`) | `SettingsManage` | — | `204`; 404, 412, 422 `in_use`, 428 |
| `POST /tickets/{ticketId:guid}/take` (`If-Match`) | `Manage` | — | `SupportTicketDetailDto`; 404, 412, 422, 428 |
| `POST /tickets/{ticketId:guid}/extension` (`If-Match`) | `Manage` | `ExtendTicketRequest(int? BusinessDays, string? Reason)` | `SupportTicketDetailDto`; 404, 412, 422, 428 |

- `SupportMemberDto(Guid Id, string DisplayName, bool IsMe)`.
- `SupportTicketTypeDto(…, long Version, bool InUse)`.
- `SupportClockDto(…, int PausedBusinessDays, SupportExtensionDto? Extension, int? MaxExtensionBusinessDays)`; `SupportExtensionDto(int BusinessDays, string Reason, DateTimeOffset ExtendedAt)` (sin autor: está en la traza). `MaxExtensionBusinessDays` = `TermBusinessDays` si hoy se puede prorrogar (G10 entero pasa), `null` si no: la pantalla muestra u oculta «Prorrogar» y su tope sin repetir las reglas.
- `SupportTicketEventDto` suma al final `int? ExtensionBusinessDays, DateOnly? FromDueOn, DateOnly? ToDueOn, string? Reason`, llenos sólo en `Kind = Extended`.
- `SupportTicketDetailDto` suma al final `string? IssuedPin`.

## Estructura de archivos

```
src/Modules/Support/
  Domain/         SupportTicket.cs (CreateManual anónimo, Extend, DueOnFor), SupportTicketEventKind.cs (+Extended),
                  SupportErrorCodes.cs (+in_use, +5 extension_*)
  Application/    Tickets/CreateTicket.cs, Tickets/TakeTicket.cs (nuevo), Tickets/ExtendTicket.cs (nuevo),
                  Tickets/AssignTicket.cs (sólo summary), Tickets/SupportTicketDtoComposer.cs, Tickets/ListSupportAssignees.cs,
                  TicketTypes/DeleteTicketType.cs (nuevo), TicketTypes/ListTicketTypes.cs, ISupportViewer.cs (nuevo),
                  ISupportTicketRepository.cs, ISupportTicketTypeRepository.cs, SupportDtos.cs, SupportAuditActions.cs,
                  Notes/AddTicketNote.cs, Notes/ListTicketNotes.cs, Settings/GetLegalTexts.cs
  Infrastructure/ Persistence/SupportDbContext.cs (FK, columnas), SupportUnitOfWork.cs (traducción FK), repositorios,
                  migraciones AddTicketTypeForeignKey y AddTicketExtension
  Api/            SupportEndpoints.cs
src/Bootstrapper/ SupportViewer.cs (nuevo), QepServiceCollectionExtensions.cs (registro)
README.md § Soporte, CLAUDE.md (gotchas), spec §12 (corrección)
```

---

## Bloque A — Gestión (Tareas 1–3, un implementador)

### Task 1: Alta manual anónima

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Domain/SupportTicket.cs` (`CreateManual` :123, `Start` :213, guarda :222), `src/Modules/Support/Modules.Support.Application/Tickets/CreateTicket.cs` (request, command, validador, handler), `SupportDtos.cs` (`IssuedPin`), `Tickets/SupportTicketDtoComposer.cs` (pasa `IssuedPin = null`), `Conversations/CreateTicketFromConversation.cs` y `Public/FilePublicTicket.cs` sólo si llaman `CreateManual`/el constructor del DTO
- Test: `$SU\SupportTicketTests.cs`, `$SU\CreateTicketValidatorTests.cs`, `$SI\SupportTicketsApiTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static SupportTicket CreateManual(SupportTicketId id, Guid tenantId, long number, SupportTicketType type, string subject,
      string description, ManualIntakeMedium medium, SupportReplyChannel replyChannel, Guid? customerId, TicketRequester? requester,
      Guid? assigneeMemberId, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock, bool anonymous = false);
  public sealed record CreateTicketRequest(..., Guid? AssigneeMemberId, bool? Anonymous = null);   // al final
  public sealed record CreateTicketCommand(..., Guid? AssigneeMemberId, bool Anonymous = false);
  // SupportTicketDetailDto: último campo string? IssuedPin
  ```
  `CreateTicketHandler` devuelve `composer.ComposeDetailAsync(...) with { IssuedPin = pin }` sólo si `ticket.IsAnonymous`.

- [ ] **Paso 1: pruebas**
  - `SupportTicketTests.AManualAnonymousTicketHasNoCustomerNoRequesterAndRepliesByPortal` — `CreateManual(..., Portal, customerId: null, requester: null, ..., anonymous: true)` ⇒ `IsAnonymous`, `ReplyChannel == Portal`, `Intake == Manual`, `ManualMedium` el dado, evento `Created` con el actor.
  - `SupportTicketTests.AManualAnonymousTicketRejectsACustomerOrARequester` — con `customerId` o con `requester` ⇒ `ArgumentException` (el validador lo ataja antes; el dominio no confía).
  - `SupportTicketTests.ANamedManualTicketStillNeedsARequesterName` — `anonymous: false` sin cliente ni nombre ⇒ `support.ticket.requester_required` (regresión D6).
  - `CreateTicketValidatorTests.AnonymousNeedsPortalAndNoCustomerNorRequester` — tres casos, cada uno con su clave en `errors` (`ReplyChannel`, `CustomerId`, `Requester`).
  - `CreateTicketValidatorTests.PortalWithoutAnonymousIsRejected` — `ReplyChannel = "Portal"`, `Anonymous = null` ⇒ error en `ReplyChannel`.
  - `SupportTicketsApiTests.AnAnonymousManualTicketReturnsItsPinOnlyOnCreation` — `201`, `isAnonymous: true`, `replyChannel: "Portal"`, `issuedPin` de 6 dígitos (`^\d{6}$`); `GET` del detalle ⇒ `issuedPin` null; `consultAccess.channel == "None"`; la auditoría `support.ticket.created` no contiene el PIN (`CountAuditEntriesAsync` + búsqueda del texto en `changed_fields`).
  - `SupportTicketsApiTests.AnAnonymousManualTicketCanBeConsultedWithItsPin` — `POST /api/v1/public/support/{slug}/tickets/lookup` con número y `issuedPin` ⇒ 200 (usa el harness público que ya publica el formulario).
  - `SupportTicketsApiTests.ANamedManualTicketDoesNotReturnAPin` — alta normal ⇒ `issuedPin` null.
  - `SupportRealtimeNoticesApiTests.CreatingAnAnonymousManualTicketAnnouncesIt`.
- [ ] **Paso 2: RED** — `dotnet build $SU` y `dotnet build $SI` fallan (`CS1739`/`CS7036` por `anonymous`, `CS1061` por `IssuedPin`).
- [ ] **Paso 3: implementar** — validador: `When(Anonymous)` ⇒ `CustomerId` `Null()`, `Requester` `Null()`, `ReplyChannel` `Equal("Portal")`; `Unless(Anonymous)` ⇒ `ReplyChannel` `NotEqual("Portal")`; actualiza el mensaje de `ReplyChannel` a «Thread, WhatsAppTemplate, Email or Portal». Handler: con anónimo salta `EnsureRequester`, el cliente y `EnsureReplyChannel`; `CreatedFields` suma `"isAnonymous"`. El envío de apertura no cambia (`SupportOpeningSender.ChannelOf` ya da `None` a un anónimo).
- [ ] **Paso 4: GREEN** — build de la solución limpio.
- [ ] **Paso 5: commit** — `feat(support): file anonymous manual tickets`.
- Filtros para el owner: `dotnet test $SU --filter "FullyQualifiedName~SupportTicketTests|FullyQualifiedName~CreateTicketValidatorTests"`; `dotnet test $SI --filter "FullyQualifiedName~AnAnonymousManualTicket|FullyQualifiedName~ANamedManualTicketDoesNotReturnAPin|FullyQualifiedName~CreatingAnAnonymousManualTicketAnnouncesIt"`.

### Task 2: Borrar un tipo de ticket sin tickets

**Files:**
- Create: `src/Modules/Support/Modules.Support.Application/TicketTypes/DeleteTicketType.cs`; migración `AddTicketTypeForeignKey`
- Modify: `SupportErrorCodes.cs`, `SupportAuditActions.cs`, `ISupportTicketRepository.cs` + `SupportTicketRepository`, `ISupportTicketTypeRepository.cs` + `SupportTicketTypeRepository`, `SupportDbContext.cs` (FK; borra el comentario «un tipo nunca se borra» de :395), `SupportUnitOfWork.cs`, `SupportDtos.cs` (`InUse`, `SupportDtoMapping.ToDto(type, inUse)`), `TicketTypes/ListTicketTypes.cs`, `CreateTicketType.cs`, `UpdateTicketType.cs`, `Modules.Support.Api/SupportEndpoints.cs` (`MapTicketTypes`)
- Test: `$SI\SupportTicketTypesApiTests.cs`, `$SI\SupportPersistenceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public const string TicketTypeInUse = "support.ticket_type.in_use";          // SupportErrorCodes
  public const string TicketTypeDeleted = "support.ticket_type.deleted";       // SupportAuditActions
  public const string TicketTypeForeignKey = "FK_tickets_ticket_types_ticket_type_id";   // SupportDbContext
  Task<bool> AnyOfTypeAsync(Guid tenantId, SupportTicketTypeId typeId, CancellationToken ct);              // ISupportTicketRepository
  Task<IReadOnlySet<SupportTicketTypeId>> UsedTypeIdsAsync(Guid tenantId, CancellationToken ct);          // ISupportTicketRepository
  void Remove(SupportTicketType type);                                                                    // ISupportTicketTypeRepository
  public sealed record DeleteTicketTypeResult(Guid Id);      // el repo no tiene Unit: precedente CancelUploadResult (Storage)
  public sealed record DeleteTicketTypeCommand(Guid TenantId, Guid TypeId, long ExpectedVersion) : ICommand<DeleteTicketTypeResult>;
  ```

- [ ] **Paso 1: pruebas**
  - `SupportTicketTypesApiTests.DeletingAnUnusedTypeRemovesItAndAudits` — crear tipo, `DELETE` con su versión ⇒ 204; `GET /settings/ticket-types` ya no lo trae; una entrada `support.ticket_type.deleted`.
  - `SupportTicketTypesApiTests.DeletingATypeWithAClosedTicketIs422InUse` — ticket en el tipo, llevado a `Closed` ⇒ 422 `support.ticket_type.in_use`; el tipo sigue.
  - `SupportTicketTypesApiTests.DeletingWithoutIfMatchIs428AndWithAnOldVersionIs412`.
  - `SupportTicketTypesApiTests.DeletingATypeOfAnotherTenantIs404`.
  - `SupportTicketTypesApiTests.AReaderCannotDelete` — sólo `support.ticket.read` ⇒ 403.
  - `SupportTicketTypesApiTests.TheSettingsListSaysWhichTypesAreInUse` — `inUse` verdadero en el usado, falso en los demás.
  - `SupportTicketTypesApiTests.ADeletedSeedTypeIsNotSeededAgain` — borrar «Felicitación» (sin tickets) y volver a listar ⇒ siete.
  - `SupportPersistenceTests.TheForeignKeyTurnsADeleteOfAUsedTypeInto422InUse` — host propio; ticket sembrado; borrar el tipo con el repositorio y `SaveChangesAsync` del unit of work, saltándose la pregunta previa ⇒ `SupportDomainException` con `in_use`.
  - `SupportPersistenceTests.TheForeignKeyTurnsATicketOnADeletedTypeInto422Unavailable` — `DELETE` SQL del tipo, luego guardar un ticket que lo usa ⇒ `support.ticket_type.unavailable`.
- [ ] **Paso 2: RED** — build de `$SI` falla por `TicketTypeInUse`, `InUse`, `Remove`.
- [ ] **Paso 3: implementar**
  - Modelo: `ticket.HasOne<SupportTicketType>().WithMany().HasForeignKey(t => t.TypeId).HasConstraintName(TicketTypeForeignKey).OnDelete(DeleteBehavior.Restrict)`. Migración `AddTicketTypeForeignKey`: aditiva (los pods viejos nunca borran tipos). Antes de desplegar, en el README: `SELECT count(*) FROM support.tickets t WHERE NOT EXISTS (SELECT 1 FROM support.ticket_types y WHERE y.id = t.ticket_type_id);` debe dar 0.
  - `SupportUnitOfWork`: dos `catch` nuevos, antes de los de unicidad, que miran `PostgresException.ConstraintName == TicketTypeForeignKey` y `SqlState` `23001` (`RestrictViolation`) ⇒ `in_use`, `23503` (`ForeignKeyViolation`) ⇒ `unavailable`; las dos `SupportDomainException`.
  - Handler (orden): `EnsureAsync(SettingsManage)` → provisión → `FindAsync` (404) → versión (412) → `AnyOfTypeAsync` (422 `in_use`) → `Remove` + auditoría → `SaveChangesAsync`. Sin aviso por el hub: no cambia ningún ticket.
  - `ListTicketTypes` (los dos) usa `UsedTypeIdsAsync` una vez; `CreateTicketType` responde `InUse = false`; `UpdateTicketType` pregunta `AnyOfTypeAsync`.
  - Endpoint: `group.MapDelete("/settings/ticket-types/{typeId:guid}", …).RequireAuthorization(SupportPermissions.SettingsManage)` con `Produces(204)` y los problems 403/404/412/422/428; versión por `RequireVersion(httpContext, "ticket type")`.
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): delete ticket types that no ticket uses`.
- Filtros: `dotnet test $SI --filter "FullyQualifiedName~SupportTicketTypesApiTests|FullyQualifiedName~TheForeignKeyTurns"`.

### Task 3: «Asignármelo» e `IsMe`

**Files:**
- Create: `src/Modules/Support/Modules.Support.Application/Tickets/TakeTicket.cs`, `ISupportViewer.cs`; `src/Bootstrapper/SupportViewer.cs`
- Modify: `SupportDtos.cs` (`SupportMemberDto`), `Tickets/SupportTicketDtoComposer.cs` (`SupportReadContext.ViewerMemberId`, `ToMemberDto`), `Tickets/ListSupportAssignees.cs`, `Notes/AddTicketNote.cs`, `Notes/ListTicketNotes.cs`, `Settings/GetLegalTexts.cs`, `Tickets/AssignTicket.cs` (sólo el `<summary>`), `SupportEndpoints.cs` (`MapTicketPost(group, "take", TakeAsync)`), `src/Bootstrapper/QepServiceCollectionExtensions.cs` (registro scoped junto a `SupportMemberDirectory`, :1601)
- Test: `$SI\SupportTicketActionsApiTests.cs`, `$SI\SupportTicketsApiTests.cs`, `$SI\SupportRealtimeNoticesApiTests.cs`, `$BU\SupportViewerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface ISupportViewer { Task<Guid?> MemberIdAsync(Guid tenantId, CancellationToken cancellationToken); }
  public sealed record SupportReadContext(SupportClockContext Clock, DateTimeOffset Now, DateOnly Today, int DueSoonBusinessDays,
      Guid? ViewerMemberId = null);
  public sealed record SupportMemberDto(Guid Id, string DisplayName, bool IsMe);
  public sealed record TakeTicketCommand(Guid TenantId, Guid TicketId, long ExpectedVersion) : ICommand<SupportTicketDetailDto>;
  ```
  `SupportViewer`: `IHttpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated != true` ⇒ `null`; si no, `IMembershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId)`. Los handlers que no pasan por el composer (`ListSupportAssignees`, notas, textos legales) inyectan `ISupportViewer` directo.

- [ ] **Paso 1: pruebas**
  - `SupportTicketActionsApiTests.TakeAssignsTheTicketToTheCaller` — `POST …/take` con la versión ⇒ 200, `assignee.id == CallerMemberId`, `assignee.isMe == true`, evento `AssigneeChanged`, auditoría `support.ticket.assigned`.
  - `SupportTicketActionsApiTests.TakingATicketAlreadyMineKeepsTheVersion` — dos veces ⇒ la segunda devuelve la misma `version`.
  - `SupportTicketActionsApiTests.TakeWithoutIfMatchIs428AndWithAnOldVersionIs412`.
  - `SupportTicketActionsApiTests.TakingAResolvedTicketIs422NotEditable` (una sola `InlineData` con `Resolved` alcanza: `Closed` ya lo cubre la prueba R9).
  - `SupportTicketActionsApiTests.AReaderCannotTake` — sólo `read` ⇒ 403.
  - `SupportTicketsApiTests.IsMeIsTrueOnlyForTheCaller` — ticket asignado a otro gestor: en el detalle `assignee.isMe == false` y el actor del evento `Created` `isMe == true`; en `GET /assignees` exactamente un `isMe` verdadero.
  - `SupportRealtimeNoticesApiTests.TakingAnnouncesTheTicket` — y no anuncia si ya era mío.
  - `SupportViewerTests.ItIsNullWithoutAnAuthenticatedUser` y `ItIsNullWithoutAnHttpContext` (dobles de `IHttpContextAccessor` e `IMembershipDirectory`).
- [ ] **Paso 2: RED** — build de `$SI` y `$BU` falla (`IsMe`, `TakeTicketCommand`, `SupportViewer`).
- [ ] **Paso 3: implementar** — `TakeTicketHandler` copia el orden de `AssignTicketHandler` con `EnsureAssigneeAsync(members, tenantId, actor)` y `ticket.Assign(actor, actor, now)`; mismo `ChangedFields`. El `<summary>` de `AssignTicketHandler` pasa a decir que resuelto o cerrado se rechaza (R9). Con `IsMe` en el record, cada `new SupportMemberDto(` del módulo compara contra el viewer; un miembro desconocido («Sin nombre») también.
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): let an agent take a ticket and mark the caller in member refs`.
- Filtros: `dotnet test $SI --filter "FullyQualifiedName~Take|FullyQualifiedName~IsMeIsTrueOnlyForTheCaller|FullyQualifiedName~TakingAnnouncesTheTicket"`; `dotnet test $BU --filter "FullyQualifiedName~SupportViewerTests"`.

**Cierre del Bloque A** (implementador): una corrida de clase completa por clase tocada, para el owner, una a la vez: `SupportTicketsApiTests`, `SupportTicketTypesApiTests`, `SupportTicketActionsApiTests`, `SupportRealtimeNoticesApiTests`, `SupportPersistenceTests` (`--filter "FullyQualifiedName~<Clase>"`, `-- xUnit.MaxParallelThreads=4`).

**Revisión del Bloque A** (un revisor): G1–G8, la traducción de la FK por nombre y SQLSTATE, que el PIN no aparezca fuera del `201`, y que ningún `SupportMemberDto` quede sin `IsMe` real (buscar `new SupportMemberDto(` y `ToMemberDto`).

## Bloque B — Prórroga (Tareas 4–5, un implementador)

### Task 4: Dominio y persistencia de la prórroga

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Domain/SupportTicket.cs` (propiedades, `Extend`, `DueOnFor` :500), `SupportTicketEventKind.cs` (+`Extended` al final), `SupportErrorCodes.cs`, `SupportDbContext.cs` (columnas, CHECK); migración `AddTicketExtension`
- Test: `$SU\SupportTicketExtensionTests.cs` (nuevo), `$SI\SupportPersistenceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public const int ExtensionReasonMaxLength = 1000;                       // SupportTicket
  public int ExtensionBusinessDays { get; private set; }                  // 0 sin prórroga
  public string? ExtensionReason { get; private set; }
  public DateTimeOffset? ExtendedAt { get; private set; }
  public void Extend(int businessDays, string reason, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock);
  public int? MaxExtensionBusinessDays(DateOnly today);                   // null si hoy G10 rechaza
  // SupportErrorCodes:
  ExtensionAlreadyUsed = "support.ticket.extension_already_used"; ExtensionNoTerm = "support.ticket.extension_no_term";
  ExtensionClockStopped = "support.ticket.extension_clock_stopped"; ExtensionOverdue = "support.ticket.extension_overdue";
  ExtensionTooLong = "support.ticket.extension_too_long";
  ```
  `DueOnFor(term, clock)` = `Add(calendar, ReceivedOn, term + ExtensionBusinessDays + PausedBusinessDays)`.

- [ ] **Paso 1: pruebas** (`SupportTicketExtensionTests`, con `FakeCalendar` y un tipo de 15 días)
  - `AnExtensionMovesTheDueDateByBusinessDaysAndWritesTheTrace` — `Extend(5, "Falta respuesta del proveedor", …)` ⇒ `DueOn` = el de 20 días hábiles; `Version` +1; evento `Extended` con `businessDays: 5`, `fromDueOn`, `toDueOn` y el motivo recortado.
  - `ASecondExtensionIsRejected` ⇒ `extension_already_used`, nada cambia.
  - `ATicketWithoutTermCannotBeExtended` ⇒ `extension_no_term`.
  - `AFinishedTicketCannotBeExtended` (`Resolved`, `Closed`) ⇒ `not_editable`.
  - `AnExtensionWhileWaitingOnTheCustomerIsRejected` ⇒ `extension_clock_stopped`.
  - `AnExtensionOnTheDueDayIsAccepted` y `AnExtensionTheDayAfterTheDueDateIsOverdue`.
  - `AnExtensionLongerThanTheTermIsRejected` — 16 en un tipo de 15 ⇒ `extension_too_long`; 15 se acepta (doble exacto).
  - `APauseAfterAnExtensionKeepsTheExtension` — prorrogar 5, pausar y reanudar con dos días hábiles completos en medio ⇒ `DueOn` = 15 + 5 + 2.
  - `ChangingTheTypeKeepsTheExtension` — prorrogar 5, cambiar a un tipo de 10 ⇒ `DueOn` = 10 + 5 (G12).
  - `MaxExtensionIsTheTermWhileExtendableAndNullOtherwise`.
  - `AnEmptyOrTooLongReasonThrows` — `ArgumentException` (el validador de la Tarea 5 lo ataja antes).
  - `SupportPersistenceTests.AnExtendedTicketRoundTrips` — guardar y releer las tres columnas y el evento `Extended` (el CHECK de `kind` lo acepta).
- [ ] **Paso 2: RED** — `dotnet build $SU` falla por `Extend`.
- [ ] **Paso 3: implementar** — columnas en `support.tickets`: `extension_business_days int not null default 0`, `extension_reason varchar(1000) null`, `extended_at timestamptz null`; CHECK `CK_tickets_extension`: `(extended_at IS NULL) = (extension_reason IS NULL) AND extension_business_days >= 0 AND (extended_at IS NOT NULL OR extension_business_days = 0)`. `CK_ticket_events_kind` se rehace sola con `Enum.GetNames`. «Hoy» es `clock.LocalDate(now)`. Migración aditiva; nota de despliegue: un pod viejo que lea la traza de un ticket prorrogado durante el rollout no conoce `Extended` (mismo caso que los eventos nuevos de la fase 2) — el handoff lo dice.
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): model a single deadline extension per ticket`.
- Filtros: `dotnet test $SU --filter "FullyQualifiedName~SupportTicketExtensionTests"`; `dotnet test $SI --filter "FullyQualifiedName~AnExtendedTicketRoundTrips"`.

### Task 5: Endpoint de la prórroga, contrato y documentación

**Files:**
- Create: `src/Modules/Support/Modules.Support.Application/Tickets/ExtendTicket.cs`
- Modify: `SupportEndpoints.cs` (`MapTicketPost(group, "extension", ExtendAsync)` con `.Accepts<ExtendTicketRequest>`), `SupportDtos.cs` (`SupportClockDto`, `SupportExtensionDto`, `SupportTicketEventDto`), `Tickets/SupportTicketDtoComposer.cs` (clock y evento `Extended`), `SupportAuditActions.cs`, `README.md` § Soporte, `CLAUDE.md` (gotchas de Soporte), `docs/superpowers/specs/2026-11-14-support-tickets-pqrs-design.md` (§12)
- Test: `$SU\ExtendTicketValidatorTests.cs` (nuevo), `$SI\SupportTicketActionsApiTests.cs` (usa `ClockedSupportHost`), `$SI\SupportRealtimeNoticesApiTests.cs`

**Interfaces:**
- Consumes: Task 4 (`Extend`, `MaxExtensionBusinessDays`, códigos).
- Produces:
  ```csharp
  public sealed record ExtendTicketRequest(int? BusinessDays, string? Reason);
  public sealed record ExtendTicketCommand(Guid TenantId, Guid TicketId, int BusinessDays, string Reason, long ExpectedVersion)
      : ICommand<SupportTicketDetailDto>;
  public const string TicketExtended = "support.ticket.extended";      // SupportAuditActions
  public sealed record SupportExtensionDto(int BusinessDays, string Reason, DateTimeOffset ExtendedAt);
  ```

- [ ] **Paso 1: pruebas**
  - `ExtendTicketValidatorTests.BusinessDaysAreOneTo365` y `TheReasonIsOneTo1000Trimmed` (claves `BusinessDays`, `Reason` en `errors`).
  - `SupportTicketActionsApiTests.ExtendingMovesTheDueDateAndShowsItInTheClockAndTrace` — reloj en `ClockedSupportHost.Reception`, tipo de 15 días: `POST …/extension` `{ businessDays: 5, reason: "…" }` ⇒ 200; `clock.dueOn` corrido, `clock.extension.businessDays == 5`, `clock.maxExtensionBusinessDays` null; último evento `kind: "Extended"` con `fromDueOn`/`toDueOn`/`reason`; auditoría `support.ticket.extended` sin el motivo.
  - `SupportTicketActionsApiTests.AnExtendableTicketAdvertisesItsMaximum` — antes de prorrogar, `clock.maxExtensionBusinessDays == 15`.
  - `SupportTicketActionsApiTests.ExtendingAnOverdueTicketIs422` — `host.Clock.UtcNow` al día hábil siguiente del vencimiento ⇒ `extension_overdue`.
  - `SupportTicketActionsApiTests.ExtendingTwiceIs422AlreadyUsed`.
  - `SupportTicketActionsApiTests.ExtendWithoutIfMatchIs428AndWithAnOldVersionIs412`.
  - `SupportTicketActionsApiTests.AReaderCannotExtend` — 403.
  - `SupportTicketActionsApiTests.TheInboxCountsAnExtendedTicketByItsNewDueDate` — un ticket «por vencer» deja de contarse en `summary.dueSoon` tras prorrogar.
  - `SupportRealtimeNoticesApiTests.ExtendingAnnouncesTheTicket` — y un 422 no anuncia.
- [ ] **Paso 2: RED** — build de `$SU`/`$SI` falla (`ExtendTicketRequest`, `Extension`).
- [ ] **Paso 3: implementar** — handler con el orden de `AssignTicketHandler`: `EnsureAsync(Manage)` → validador → `SupportTicketActions.LoadAsync` → `composer.ReadContextAsync` → `ticket.Extend(...)` → `SaveIfChangedAsync(..., TicketExtended, ["dueOn", "extension"])` → `realtime.TicketChangedAsync` → `ComposeDetailAsync`. `<summary>` del DTO: «`MaxExtensionBusinessDays` decide si la pantalla ofrece Prorrogar y su tope; el aviso al solicitante lo manda el asesor como respuesta (G13)».
  - Docs, en el mismo commit: README § Soporte (las tres rutas nuevas, `Anonymous` del alta, `IssuedPin`, `InUse`, la consulta previa de la FK y los códigos nuevos); `CLAUDE.md`, gotchas de Soporte: (1) un tipo de ticket sí se borra, pero la regla la cierra la FK `RESTRICT` traducida por nombre — no la cambies por la consulta previa; (2) la prórroga se guarda en días hábiles y entra en `DueOnFor`: un camino nuevo que recalcule el vencimiento pasa por ahí o pierde la prórroga; (3) `IssuedPin` sólo en el `201` del alta anónima. Spec §12: la línea «Responsable fijo… hoy se permite. Tipo y cliente no cambian de regla» se corrige a lo que dice el código (R9: los tres bloqueados desde `203ccb3a`).
- [ ] **Paso 4: GREEN.**
- [ ] **Paso 5: commit** — `feat(support): extend a ticket deadline once before it expires`.
- Filtros: `dotnet test $SU --filter "FullyQualifiedName~ExtendTicketValidatorTests"`; `dotnet test $SI --filter "FullyQualifiedName~Extend|FullyQualifiedName~TheInboxCountsAnExtendedTicket"`.

**Cierre del Bloque B:** clase completa, una a la vez: `SupportTicketActionsApiTests`, `SupportRealtimeNoticesApiTests`, `SupportPersistenceTests`; y `dotnet test $SU` entero (unitarias, rápido).

**Revisión del Bloque B** (un revisor): G9–G13, el orden de las guardas de `Extend`, el último día hábil, que `DueOnFor` sea el único cálculo del vencimiento, y que el motivo no llegue a la auditoría.

**Antes de mergear a `develop`** (owner): proyectos tocados enteros, uno a la vez: `Modules.Support.UnitTests`, `Modules.Support.IntegrationTests` (`-- xUnit.MaxParallelThreads=4`), `Bootstrapper.UnitTests` y `ArchitectureTests` (`SupportLayerTests`, `MultiReplicaInventoryTests`). **Revisión final** (modelo más capaz, toda la rama contra `develop`).

---

## Decisiones pendientes del owner

- **P-1 PIN del anónimo manual** (G3): ¿se le muestra una vez al asesor en el alta para que se lo dicte al solicitante? Sin eso el anónimo manual no se puede consultar ni responder. Alternativa: no ofrecer anónimo en el alta manual. **Bloquea el Paso 1 de la Tarea 1** si responde que no.
- **P-2 Aviso al solicitante de la prórroga** (G13): el plan lo deja al asesor como respuesta normal. Alternativa: que el backend lo mande solo con un texto fijo (exige copy legal por canal, plantilla de Meta para WhatsApp fuera de ventana y plantilla de correo nueva).
- **P-3 «No excede el doble del término inicial»** (G10): el plan lo lee como prórroga ≤ término (total ≤ 2×). La otra lectura, prórroga ≤ 2× (total 3×), cambia una constante. Confirmar con el abogado.
- **P-4 Prórroga con el reloj detenido** (G10): el plan la rechaza en `WaitingOnCustomer`. ¿Debe permitirse?
- **P-5 Cambio de tipo después de la prórroga** (G12): el plan conserva los días de prórroga aunque superen el término del tipo nuevo.
- **P-6 Avisos internos de vencimiento** (G14): el spec no dice a quién, por qué canal ni cuándo. Sin definir, no se construyen; los estados `DueSoon`/`Overdue` ya existen.
- **P-7 Nombres** que fija el plan sin precedente en el spec: los cinco `support.ticket.extension_*`, `support.ticket_type.in_use` (sí sugerido en §12), las auditorías `support.ticket.extended` y `support.ticket_type.deleted`, la ruta `take` y el tope de 1000 del motivo.

## Spec discrepancies (spec contra código)

1. §12, cuarto ajuste: dice que reasignar un ticket resuelto o cerrado «hoy se permite» y que tipo y cliente «no cambian de regla». El código ya bloquea los tres (R9, `SupportTicket.EnsureEditable`, `203ccb3a`). Gana el código; la Tarea 5 corrige la frase.
2. `AssignTicketHandler` dice en su `<summary>` que asigna «en cualquier estado (pre-flight R9: también resuelto o cerrado)»: contradice al dominio. La Tarea 3 lo corrige.
3. `SupportDbContext` explica la falta de FK con «un tipo nunca se borra»; §12 introduce el borrado. La Tarea 2 pone la FK y cambia el comentario.
4. El plan de la fase 3 sugería que la prórroga reusara `SupportOpeningSender`; ese reparto sólo cubre correo e hilo dentro de ventana y lleva el PIN. Este plan no lo reusa (G13).
5. Petición incompleta (Ley 1755 art. 17), reclamación directa (Ley 1480) y conservación de 10 años: §4 y §13 los asignan a «la fase del flujo de requerimiento» o los dejan sin código; §12 no los pone en la fase 4. Fuera de 4a y de 4b.
