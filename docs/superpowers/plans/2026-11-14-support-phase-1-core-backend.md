# Soporte (PQRSF) — Fase 1 «Núcleo», backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un tenant con el módulo `support` prendido cree tickets a mano, los vea en una bandeja con plazos en días hábiles, les cambie estado, responsable, tipo y cliente con trazabilidad y auditoría, y configure sus tipos de ticket; y que un ticket bloquee el borrado de su cliente y viaje con la fusión.

**Architecture:** Módulo nuevo `Modules.Support.{Domain,Application,Infrastructure,Api}`, copia estructural de Loans: esquema `support`, auditoría por puerto propio (`ISupportAuditPublisher`), unidad de trabajo con `CustomerReferenceWrites`, clientes y miembros por puertos con adaptador en `src/Bootstrapper/`. El reloj de resolución es dominio puro sobre un `ISupportBusinessCalendar` que arma Bootstrapper: lunes a viernes, menos los festivos de Colombia (`ColombianHolidays`) si el país del tenant es `CO` [owner 2026-11-15, spec D2d; antes, el horario predeterminado de Mensajería]; Soporte nunca referencia Mensajería. Los estados, la pausa, la reapertura y el contador de ciclos de resolución quedan completos desde esta fase para que Mensajería, el formulario público y la encuesta (fases 2 a 4) sólo agreguen caminos de entrada y columnas.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql, `WebApplicationFactory<Program>`.

**Spec:** [`docs/superpowers/specs/2026-11-14-support-tickets-pqrs-design.md`](../specs/2026-11-14-support-tickets-pqrs-design.md) (commit `150f4dbe`), §12 fase 1. Las pantallas son de `qep-frontend` y quedan fuera; la sección «Contrato para las pantallas» dice qué DTO necesitan.

---

## Antes de empezar

- Worktree propio bajo tu home, nunca en `/tmp`: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-core`, rama `feat/support-core` desde `develop`. Ese worktree necesita su propio índice de CodeGraph (no copies el del checkout principal).
- Antes de **cada** commit: `git branch --show-current`. Si dice `main` o `develop`, frena.
- Commits: conventional commits en inglés, `git commit -m "…" -- <rutas>`, **sin `Co-Authored-By` ni ninguna atribución de IA**, aunque el harness la sugiera.
- Comandos en PowerShell, uno por línea, sin `&&` (`A; if ($?) { B }`). Si `Api.exe` (o `dotnet Api.dll`) está corriendo, el build falla por archivo bloqueado: avísale al developer, no lo detengas.
- Atajos: `$W` = la ruta del worktree; `$SU` = `$W\tests\Modules\Support\Modules.Support.UnitTests`; `$SI` = `$W\tests\Modules\Support\Modules.Support.IntegrationTests`; `$AT` = `$W\tests\ArchitectureTests\ArchitectureTests`; `$BU` = `$W\tests\Bootstrapper\Bootstrapper.UnitTests`; `$TU`/`$TI` = los de `Modules\Tenancy`; `$CU`/`$CI` = los de `Modules\Customers`; `$PI` = `$W\tests\Modules\Pipeline\Modules.Pipeline.IntegrationTests`.
- **Presupuesto de pruebas** (`CLAUDE.md` del workspace): el RED que falla por compilación no levanta contenedores, su evidencia es la salida de `dotnet build`. La integración corre en GREEN con `$env:Seed__Enabled = "false"` en la sesión, filtrada por nombre (`--filter "FullyQualifiedName~<Prueba>"`) y `-- xUnit.MaxParallelThreads=4`. La clase completa, una vez al final del bloque. Pruebas siempre en primer plano. Antes de una clase completa: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra corrida (de otra sesión), espera; nunca la detengas.
- Migraciones con el factory de diseño, desde `$W`:
  ```powershell
  dotnet ef migrations add AddSupportModuleKey --project src/Modules/Tenancy/Modules.Tenancy.Infrastructure --context TenancyDbContext -o Persistence/Migrations
  dotnet ef migrations add InitialSupport --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations
  ```
  La última de Tenancy hoy es `20261112055320_AddAiModuleKey`.
- `docs/estado.md` **no se toca** en esta rama: se actualiza en `develop`, al mergear.

## Global Constraints

- Clave de módulo `support` (`TenantModuleKeys.Support`), al final de `All` (después de `ai`), **sin dependencias** (C9) y **fuera** de `DefaultForNewTenants`. Migración `AddSupportModuleKey` que reescribe `CK_tenant_modules_module_key` con `…,'store','ai','support'`, sin backfill; su `Down` borra las filas `support` antes de volver al CHECK anterior (como `AddAiModuleKey`).
- Permisos (C1): `support.ticket.read`, `support.ticket.manage`, `support.settings.manage`, en `SupportPermissions` (`Read`, `Manage`, `SettingsManage`), los tres con `RequiredModules: [ModuleKeys.Support]`, categoría `"Soporte"`. Rol `admin`: los tres. Rol `advisor` (el «asesor» del spec): `Read` y `Manage`. `billing` y `cashier`: ninguno.
- Visibilidad (owner, §10): quien tenga `support.ticket.read` **o** `support.ticket.manage` ve **todos** los tickets del tenant. No hay «sólo los suyos».
- Seis estados, persistidos y enviados por nombre: `New` (`nuevo`), `Open` (`abierto`, «En gestión»), `WaitingOnCustomer` (`espera_cliente`), `WaitingInternal` (`espera_interna`), `Resolved` (`resuelto`), `Closed` (`cerrado`). Transiciones, exactas (§7.1):

  | Desde | A |
  | --- | --- |
  | `New` | `Open`, `WaitingOnCustomer`, `Resolved` |
  | `Open` | `WaitingOnCustomer`, `WaitingInternal`, `Resolved` |
  | `WaitingOnCustomer` | `Open`, `WaitingInternal`, `Resolved`, `Closed` |
  | `WaitingInternal` | `Open`, `WaitingOnCustomer`, `Resolved` |
  | `Resolved` | `Closed`, `Open` |
  | `Closed` | — |

  Fuera de la tabla: `422 support.ticket.transition_invalid`. Ningún estado vuelve a `New`.
- Reloj (§7.2). **Ya no aplica la parte del calendario [owner 2026-11-15, spec D2d]:** un día es hábil si es de lunes a viernes y, en un tenant cuyo país es `CO`, no es festivo (`ColombianHolidays.For(year)`); en otro país, sólo lunes a viernes. No se lee el horario de atención, ni sus excepciones, ni su país de festivos. Lo demás de este punto (inicio, fin, pausas, cambio de tipo) sigue vigente. Original [owner 2026-11-14]: Soporte usa **el mismo calendario que la bandeja**, el del horario **predeterminado** del tenant (`IBusinessHoursRepository.FindDefaultAsync`). Un día es hábil si su día de la semana tiene franjas, no es festivo y ninguna excepción de fecha lo cierra. Una excepción de fecha del horario manda sobre el día de la semana y sobre el festivo, como en `BusinessHoursSchedule.RangesFor`: sin franjas cierra el día; con franjas lo abre. País de los festivos: `schedule.HolidayCountry`, y si el horario no tiene o no hay horario, `Tenant.Country`; con `CO` se excluyen `ColombianHolidays.For(year)`, con otro país no hay festivos. Sin horario: lunes a viernes más los festivos de `Tenant.Country`. Cuenta desde el **día hábil siguiente** a la radicación y vence al **final** del día hábil, en `Tenant.TimeZone`. Se detiene en `WaitingOnCustomer`, `Resolved` y `Closed`; `WaitingInternal` **no** lo detiene. Al reanudar, el vencimiento se corre por los días hábiles detenidos. Cambiar el tipo recalcula desde la radicación con el término nuevo más las pausas; editar un tipo no mueve vencimientos ya calculados.
- Umbral de «por vencer» [owner 2026-11-14]: configurable por tenant, **3 días hábiles** por defecto; lo edita `support.settings.manage` y se lee con la política `support.settings.view` (`read`, `manage` o `settings.manage`). La bandeja (filtro `due=dueSoon` y `Summary.DueSoon`) y el `DueState` del ticket leen el valor del tenant, nunca una constante.
- Tipos sembrados (§7.5), en este orden (`position` 1 a 8), todos activos y publicados:

  | Nombre | Término | Legal | Respuesta formal |
  | --- | --- | --- | --- |
  | Petición general | 15 | sí | sí |
  | Petición de documentos o información | 10 | sí | sí |
  | Consulta | 30 | sí | sí |
  | Queja | 15 | sí | sí |
  | Reclamo | 15 | sí | sí |
  | Sugerencia | 15 | sí | no |
  | Felicitación | 15 | sí | no |
  | Soporte técnico | sin término | no | no |

  Un tipo usado no se borra (no hay `DELETE`): se desactiva. Un tipo sin término no tiene vencimiento y no puede ser «legal».
- Radicado `SOP-` + seis dígitos por tenant (`SOP-000127`), contador en la misma transacción que el alta, índice único `(tenant_id, number)`. El primer ticket de un tenant es `SOP-000001`.
- D4: **cualquier** ticket del cliente, en cualquier estado, bloquea su borrado (motivo `supportTickets`); la fusión mueve `support.tickets.customer_id` al cliente que queda, sin subir `version` (F15, como Loans).
- Aislamiento de tenant en dos capas: ruta `{tenantId:guid}` más política; el handler revalida con `SupportAuthorization` y responde **403 `authorization.denied`, nunca 404**. Con el módulo apagado, 403 (`ModuleEntitlementMask` quita el permiso).
- Concurrencia optimista: todo `PUT` de un ticket o de un tipo exige `If-Match` con la `version` cargada (sin él `428 precondition.if_match_required`; vieja, `412 concurrency.conflict`).
- `Modules.Support.Application` sólo referencia, entre los módulos de negocio, `Modules.Tenancy.*`; no referencia EF Core ni Npgsql. Lo traduce `SupportLayerTests`.
- Mensajes legibles, comentarios y textos que el sistema muestra: español, tuteando. Códigos de error, rutas, permisos y enums: contrato, en inglés.

## Decisiones del plan

Decisiones mías (planner) sobre lo que el spec deja al implementador (§11). El owner las puede revertir.

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | Siembra perezosa de tipos: `ISupportProvisioner.EnsureAsync` en el primer acceso, con `INSERT INTO support.tenant_settings … ON CONFLICT (tenant_id) DO NOTHING RETURNING tenant_id`; sólo quien inserta siembra los ocho tipos. La misma fila guarda el contador (`next_ticket_number`) y, en fases siguientes, la configuración (encuesta, cierre automático). | Tenancy no publica nada al activar un módulo (`CLAUDE.md`, «El embudo de un tenant nace en su primer acceso»); precedente `PipelineProvisioner`. |
| D2 | Contador: `ISupportTicketNumberGenerator.NextAsync` con `UPDATE support.tenant_settings SET next_ticket_number = next_ticket_number + 1 … RETURNING next_ticket_number - 1`, dentro de la transacción de `ISupportUnitOfWork.BeginTransactionAsync`. | Precedente `CucGenerator`; el lock de fila serializa dos altas. Un alta que falla deshace el número (va en la transacción). |
| D3 | Pausa a nivel de día: al reanudar se suman los días hábiles **estrictamente entre** la fecha local de la pausa y la de la reanudación. Pausar y reanudar el mismo día suma 0; pausar el martes y reanudar el lunes siguiente suma miércoles a viernes (3). | Un día en el que el reloj corrió algún momento cuenta como día de plazo: es la lectura más favorable al solicitante. Ver P2. |
| D4 | El vencimiento se guarda (`due_on date NULL`) y se recalcula sólo al crear, al reanudar y al cambiar el tipo. Mientras está detenido, `due_on` conserva el último valor y el DTO dice `Paused`/`Finished`. | La bandeja filtra «vencidos» en SQL. Cambiar el horario del tenant no mueve vencimientos ya calculados, igual que editar un tipo. |
| D5 | `support.ticket.read` y `support.ticket.manage` se leen con una política propia `SupportPolicies.TicketView = "support.ticket.view"`, que exige el claim `permission` con **cualquiera** de los dos (`RequireClaim(type, Read, Manage)`). Vive fuera de `SupportPermissions` para no contarse como permiso. | Es la visibilidad que fijó el owner; la política de un solo permiso no la puede expresar. Es un patrón nuevo en el repo: va en `CLAUDE.md`. |
| D6 | Alta manual: exige `customerId` **o** `requester` con nombre (`FirstNames`, o `BusinessName` con `NIT`). Un ticket anónimo sólo nace del formulario público (fase 3). | §9.3 define el anónimo sólo para el formulario. |
| D7 | `TicketRequester` de la fase 1: `FirstNames`, `LastNames`, `BusinessName`, `DocumentType`, `DocumentNumber`, `Email`, `MobilePhone` (E.164, `^\+[1-9][0-9]{7,14}$`), `LandlinePhone`, `CountryCode` (alfa-2; por defecto `Tenant.Country`), `Address`. La **ciudad** (Geography, C7) entra en la fase 3 con su puerto. El par documento va junto y el tipo se valida con `DocumentTypesByCountry.IsAllowed(type, country)`. | El celular en E.164 desde ya evita normalizar al mandar plantillas en la fase 2. |
| D8 | `Intake` = `Manual` \| `Conversation` \| `PublicForm` (sólo `Manual` se crea en esta fase). `ManualMedium` = `Phone` \| `InPerson` \| `Email` \| `Letter` \| `Other`. `ReplyChannel` = `Thread` \| `WhatsAppTemplate` \| `Email`. En el alta manual `Thread` es `422 support.ticket.reply_channel_unavailable` (no hay conversación), `Email` exige correo y `WhatsAppTemplate` exige celular (§8.3). | El CHECK de la base lista los tres valores de entrada desde ya: las fases 2 y 3 no migran el enum. |
| D9 | Sin notas internas en la fase 1: la traza son los eventos (`Created`, `StatusChanged`, `AssigneeChanged`, `TypeChanged`, `CustomerChanged`). Notas con adjuntos por Storage llegan con la fase 2. | §12 no las lista en la fase 1; con eventos el detalle ya es usable. |
| D10 | Orden de la traza: `ordinal` asignado por el agregado (`last_event_ordinal` en el ticket, único `(ticket_id, ordinal)`), no `occurred_at`. | Gotcha del repo: los GUID v7 no ordenan dentro del mismo milisegundo (precedente `DealStageHistoryEntry.Ordinal`). |
| D11 | Límites: asunto 1 a 200, descripción 1 a 5 000, nombre de tipo 1 a 80 y único por tenant sin distinguir mayúsculas (`IX_ticket_types_tenant_name` sobre `lower(name)` → `409 support.ticket_type.name_taken`, traducido **por nombre de índice**), término 1 a 365 o `null`, página 1 a 100 (25 por defecto). | Valores del implementador (§11). |
| D12 | **Ya no aplica [owner 2026-11-15, spec D2d]:** el calendario no lee el horario, así que no hay horario sin días que rellenar; el corte del dominio a 3 660 días se conserva como guarda. Original: Calendario de Bootstrapper: un horario predeterminado sin ningún día de la semana con franjas usa lunes a viernes como días de la semana, y conserva sus festivos y sus excepciones de fecha (como mucho `BusinessHoursSchedule.MaxExceptions` = 100, así que no pueden cerrar todo). El dominio, además, corta con `InvalidOperationException` si no encuentra un día hábil en 3 660 días. | Sin esto, `Add` gira para siempre (Review Focus 1). |
| D13 | **Ya no aplica [owner 2026-11-15, spec D2d]:** Soporte no lee el horario de Mensajería; sólo el país del tenant. Original: El horario predeterminado se lee aunque el módulo `messaging` esté apagado: es dato del tenant. Sin fila, lunes a viernes con los festivos de `Tenant.Country`. | C9: Soporte no depende de Mensajería para prenderse. |
| D14 | Vincular un cliente (alta o `PUT …/customer`) exige el módulo `customers` prendido (`TenantModuleGuard`, 403 `tenancy.module_not_enabled`) y un cliente activo del tenant (`422 support.ticket.customer_not_found` / `support.ticket.customer_inactive`; un fusionado está inactivo). Escribir `customer_id` toma `CustomerReferenceLock` compartido vía `CustomerReferenceWrites`. | §9.3 y §10. |
| D15 | Umbral de «por vencer» [owner 2026-11-14]: columna `due_soon_business_days int NOT NULL DEFAULT 3` en la fila por tenant `support.tenant_settings` (la que crea `ISupportProvisioner` en el primer acceso, D1), con `CK_tenant_settings_due_soon` (0 a 30) y `version bigint` como token. Rango **0 a 30 días hábiles; 0 apaga «por vencer»** (el ticket pasa de `OnTrack` a `Overdue`); el rango es decisión mía. `GET /settings` (política `SupportPolicies.SettingsView = "support.settings.view"`, que acepta `support.ticket.read`, `support.ticket.manage` o `support.settings.manage` [coordinador]: así un rol con sólo `settings.manage` lee la versión y guarda) y `PUT /settings` (`SettingsManage`, `If-Match`), con el patrón de `StoreEndpoints`: la respuesta lleva `ETag` y `version`; sin `If-Match`, 428; con versión vieja, 412. Validador de FluentValidation (`422 validation.failed` con `errors.DueSoonBusinessDays`) y auditoría `support.settings.updated` en la misma transacción. | Decisión del owner. La fila ya existe por la siembra: no hace falta otra tabla ni otro aprovisionamiento. |

## Review Focus

1. **Ya no aplica al adaptador [owner 2026-11-15]:** el calendario no lee el horario; queda la guarda del dominio (`AddStopsInsteadOfLoopingForeverWithoutBusinessDays`). Original: **Un horario predeterminado con todos los días de la semana cerrados** hace que sumar días hábiles no termine nunca: el alta se colgaría. Se espera lunes a viernes, con los festivos y las excepciones del horario. Tarea 6: `AScheduleWithEveryDayClosedFallsBackToMondayToFriday`; Tarea 3: `AddStopsInsteadOfLoopingForeverWithoutBusinessDays`.
2. **Una radicación de noche en Bogotá** cae en otro día UTC: contar desde la fecha UTC corre el vencimiento. Se espera la fecha local. Tarea 4: `ANightReceptionCountsFromTheLocalDate` (2026-11-13 03:30Z = jueves 12 local → vence 2026-12-04, no 2026-12-07).
3. **Pausas encadenadas** (`WaitingOnCustomer` → `Resolved` → reabrir) pueden contar la pausa dos veces o perder su inicio. Se espera una sola pausa desde la primera detención. Tarea 4: `AChainOfStopsCountsThePauseOnceFromItsStart`.
4. **Dos altas a la vez** en el mismo tenant pueden emitir el mismo radicado o devolver 500 por el índice. Se esperan números distintos y consecutivos. Tarea 8: `ConcurrentCreationsGetDistinctConsecutiveNumbers`; Tarea 5: `TwoFirstAccessesSeedTheTypesOnce`.
5. **Un cliente que se borra mientras se crea su ticket** dejaría un `customer_id` huérfano que nadie bloquea ni fusiona. Se espera que el alta espere al borrado y falle con 422. Tarea 8: `CreatingATicketWaitsForTheCustomerDeletionAndThenFails`.

---

## Contrato para las pantallas (BFF)

Lo que la bandeja, el detalle y los ajustes de `qep-frontend` necesitan dibujar sin conocer la máquina de estados ni el calendario. Rutas bajo `/api/v1/tenants/{tenantId:guid}/support`.

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /tickets?status=&typeId=&assignee=me\|unassigned\|<guid>&due=overdue\|dueSoon&search=&page=&pageSize=` | `TicketView` | `SupportTicketPageDto` |
| `POST /tickets` | `Manage` | `201 SupportTicketDetailDto` |
| `GET /tickets/{ticketId}` | `TicketView` | `SupportTicketDetailDto` |
| `PUT /tickets/{ticketId}/status` · `/assignee` · `/type` · `/customer` (`If-Match`) | `Manage` | `SupportTicketDetailDto` |
| `GET /assignees` | `TicketView` | `SupportAssigneesDto` |
| `GET /ticket-types` | `TicketView` | `IReadOnlyList<SupportTicketTypeDto>` |
| `GET /settings/ticket-types` | `SettingsManage` | `IReadOnlyList<SupportTicketTypeDto>` |
| `POST /settings/ticket-types` | `SettingsManage` | `201 SupportTicketTypeDto` |
| `PUT /settings/ticket-types/{typeId}` (`If-Match`) | `SettingsManage` | `SupportTicketTypeDto` |
| `PUT /settings/ticket-types/order` | `SettingsManage` | `IReadOnlyList<SupportTicketTypeDto>` |
| `GET /settings` | `SettingsView` (`Read`, `Manage` o `SettingsManage`) | `SupportSettingsDto` + `ETag` |
| `PUT /settings` (`If-Match`) | `SettingsManage` | `SupportSettingsDto` + `ETag` |

DTO (en `SupportDtos.cs`; cada uno lleva en su comentario el motivo de pantalla):

- `SupportTicketPageDto(IReadOnlyList<SupportTicketListItemDto> Items, int Total, int Page, int PageSize, SupportInboxSummaryDto Summary)`. El resumen es del tenant entero, no de la página ni del filtro.
- `SupportInboxSummaryDto(IReadOnlyList<SupportStatusCountDto> ByStatus, int Overdue, int DueSoon)`. `ByStatus` trae **los seis** estados, en el orden de `Global Constraints`, **incluso en cero** (las pestañas no conocen el enum).
- `SupportTicketListItemDto(Guid Id, string Number, string Subject, string Status, string TypeName, string RequesterDisplayName, SupportMemberDto? Assignee, DateTimeOffset ReceivedAt, DateOnly? DueOn, string DueState, int? BusinessDaysLeft)`. `RequesterDisplayName` lo resuelve el backend: nombre del cliente vinculado; si no, `FirstNames LastNames`; si no, `BusinessName`; si no, `NIT <número>`.
- `SupportTicketDetailDto(Guid Id, string Number, string Subject, string Description, string Status, IReadOnlyList<string> AllowedTransitions, SupportTicketTypeSnapshotDto Type, SupportClockDto Clock, string Intake, string? ManualMedium, string ReplyChannel, SupportCustomerRefDto? Customer, SupportRequesterDto? Requester, SupportMemberDto? Assignee, int ResolutionCount, DateTimeOffset ReceivedAt, IReadOnlyList<SupportTicketEventDto> Events, long Version)`. `AllowedTransitions` sale de la tabla del dominio: el selector de estado no la repite.
- `SupportClockDto(DateOnly? DueOn, string DueState, int? BusinessDaysLeft, int? TermBusinessDays, bool TermIsLegal, int PausedBusinessDays)`. `DueState` ∈ `NoTerm`, `OnTrack`, `DueSoon`, `Overdue`, `Paused` (`WaitingOnCustomer`), `Finished` (`Resolved`, `Closed`). `TermIsLegal` decide el texto «vence el plazo legal» o «vence el objetivo».
- `SupportTicketEventDto(int Ordinal, string Kind, DateTimeOffset OccurredAt, SupportMemberDto? Actor, string? FromStatus, string? ToStatus, SupportMemberDto? Assignee, string? FromTypeName, string? ToTypeName, SupportCustomerRefDto? Customer)`: campos planos y nulos según `Kind`, con los nombres ya resueltos.
- `SupportTicketTypeDto(Guid Id, string Name, int? TermBusinessDays, bool TermIsLegal, bool RequiresFormalResponse, bool IsPublished, bool IsActive, int Position, long Version)`; `SupportTicketTypeSnapshotDto(Guid Id, string Name, int? TermBusinessDays, bool TermIsLegal, bool RequiresFormalResponse)`.
- `SupportSettingsDto(int DueSoonBusinessDays, int MinDueSoonBusinessDays, int MaxDueSoonBusinessDays, long Version)` [owner 2026-11-14]: viaja con el rango (0 y 30) para que el formulario de ajustes no lo repita; `0` significa «apagado». Un tenant que nunca guardó ve 3.
- `SupportMemberDto(Guid Id, string DisplayName)`, `SupportCustomerRefDto(Guid Id, string Name, bool IsActive)`, `SupportRequesterDto` (los campos de D7), `SupportAssigneesDto(IReadOnlyList<SupportMemberDto> Items)`, `SupportStatusCountDto(string Status, int Count)`.

**Canal de respuesta en el alta manual** (revisión final, I1; owner, 2026-11-14). `Thread` nunca vale en un alta manual; `Email` exige un correo y `WhatsAppTemplate` un celular en la foto del solicitante; si falta, `422 support.ticket.reply_channel_unavailable`. Con un cliente vinculado, el alta copia en la foto el correo y el celular del cliente (`Customer.Email` y `Customer.PhoneE164`, vía `SupportCustomerRef`) sólo en los campos que `requester` dejó vacíos, y después valida el canal: lo que trae `requester` siempre gana, y sin `requester` la foto queda con sólo ese contacto y el país del tenant. La pantalla puede habilitar `Email` o `WhatsAppTemplate` si el solicitante **o** el cliente vinculado tienen ese dato, y el detalle devuelve en `requester` el contacto que quedó guardado. Sin contacto en ninguno de los dos, todo canal da ese 422. `PUT …/customer` no cambia la foto. Lo amarran `SupportTicketsApiTests.ALinkedCustomerEmailIsCopiedIntoAnEmptyRequesterForEmail`, `TheRequesterEmailWinsOverTheLinkedCustomerEmail`, `ALinkedCustomerMobileEnablesWhatsAppTemplate` y `ALinkedCustomerWithoutContactAndNoRequesterContactIs422ForEmail`, más `TicketRequesterCustomerContactTests`.

El frontend de clientes también debe mostrar el motivo de bloqueo nuevo `supportTickets` (Tarea 11).

## Estructura de archivos

```
src/Modules/Support/
  Modules.Support.Domain/       SupportTicketId, SupportTicketTypeId, SupportTicketStatus(+es), ISupportBusinessCalendar,
                                SupportBusinessDays, SupportClockContext, SupportDueState(+s), SupportTicket, SupportTicketEvent,
                                SupportTicketEventKind, TicketRequester, SupportTicketIntake, ManualIntakeMedium, SupportReplyChannel,
                                SupportTicketType, SupportTicketTypeDefaults, SupportTicketNumber, SupportErrorCodes, SupportDomainException
  Modules.Support.Application/  SupportPermissions, SupportPolicies, SupportAuthorization, SupportNotFound, SupportAuditActions,
                                ISupportAuditPublisher, ISupportUnitOfWork, ISupportTicketRepository, ISupportTicketTypeRepository,
                                ISupportProvisioner, ISupportTicketNumberGenerator, ISupportBusinessCalendarSource,
                                ISupportCustomerLookup, ISupportMemberDirectory, SupportDtos, SupportTicketDtoComposer,
                                TicketTypes/*.cs, Tickets/*.cs
  Modules.Support.Infrastructure/ SupportInfrastructureExtensions, SupportDatabaseInitializer, Persistence/*
  Modules.Support.Api/          SupportEndpoints
src/Bootstrapper/               SupportBusinessCalendarSource (+ SupportBusinessCalendar), SupportCustomerLookup,
                                SupportMemberDirectory, SupportUserReferenceProbe
tests/Modules/Support/          Modules.Support.UnitTests, Modules.Support.IntegrationTests
tests/ArchitectureTests/ArchitectureTests/SupportLayerTests.cs
```

---

## Bloque A — módulo, dominio y persistencia (Tareas 1–5, un implementador)

### Task 1: clave de módulo `support` y migración de Tenancy

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (después de `Ai`, línea 82; `All`, línea 89)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:151`
- Create: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/Migrations/<ts>_AddSupportModuleKey.cs` (+ Designer, snapshot)
- Test: `tests/Modules/Tenancy/Modules.Tenancy.UnitTests/TenantModuleKeysTests.cs`, `…/OperatorReadHandlerTests.cs:68`
- Test (literales a actualizar): `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/TenantModulesApiTests.cs:102,104,110`, `…/OperatorConsoleApiTests.cs:336`, `…/SeedStartupTests.cs:144,177,239,266,346`

**Interfaces:**
- Produces: `TenantModuleKeys.Support` (valor `"support"`).

- [ ] **Paso 1: prueba en rojo.** En `TenantModuleKeysTests`: renombra `AiIsLastDependsOnMessagingAndIsOffForNewTenants` a `AiDependsOnMessagingAndIsOffForNewTenants` y quítale la aserción de «último»; agrega:
  ```csharp
  [Fact]
  public void SupportIsLastHasNoDependenciesAndIsOffForNewTenants()
  {
      Assert.Equal("support", TenantModuleKeys.All[^1].Value);
      Assert.Empty(TenantModuleKeys.DependenciesOf(TenantModuleKeys.Support));
      Assert.DoesNotContain(TenantModuleKeys.Support, TenantModuleKeys.DefaultForNewTenants);
  }
  ```
  y suma `"support"` al final de la lista de `AllKeepsTheContractOrder` y de `OperatorReadHandlerTests`.
- [ ] **Paso 2:** `dotnet build $TU` → falla con `CS0117: 'TenantModuleKeys' does not contain a definition for 'Support'`. Guarda la salida.
- [ ] **Paso 3:** `public static readonly TenantModuleKey Support = TenantModuleKey.Define("support");` con su `<summary>` (spec 2026-11-14, sin dependencias, C9), al final de `All`; el CHECK de `TenancyDbContext` con `,'support'`; genera `AddSupportModuleKey` y escribe su `Down` como el de `AddAiModuleKey` (borra `support` y vuelve al CHECK sin ella). Actualiza los literales de las pruebas de integración de Tenancy: `"support"` al final de las listas en orden de `All`, en orden alfabético después de `"suppliers"` en `SeedStartupTests`, y en las exclusiones de «fuera de los defaults» de `TenantModulesApiTests` y `OperatorConsoleApiTests`.
- [ ] **Paso 4:** `dotnet test $TU` → PASS.
- [ ] **Paso 5:** commit `feat(tenancy): add support module key` con los archivos de este paso (la integración de Tenancy corre al final del bloque).

### Task 2: esqueleto del módulo, permisos y `SupportLayerTests`

**Files:**
- Create: `src/Modules/Support/Modules.Support.{Domain,Application,Infrastructure,Api}/*.csproj` (copias de los de Loans: mismas referencias y paquetes; `InternalsVisibleTo` a `Modules.Support.UnitTests` en Application e Infrastructure y a `Modules.Support.IntegrationTests` en Infrastructure)
- Create: `Modules.Support.Application/SupportPermissions.cs`, `SupportPolicies.cs`, `SupportAuthorization.cs`
- Create: `Modules.Support.Infrastructure/SupportInfrastructureExtensions.cs` (vacío por ahora: `AddSupportInfrastructure(this IServiceCollection, IConfiguration)`), `Modules.Support.Api/SupportEndpoints.cs` (`MapSupportEndpoints` con el grupo y sin rutas)
- Create: `tests/Modules/Support/Modules.Support.UnitTests/Modules.Support.UnitTests.csproj`, `tests/Modules/Support/Modules.Support.IntegrationTests/Modules.Support.IntegrationTests.csproj` (copias de los de Loans)
- Create: `tests/ArchitectureTests/ArchitectureTests/SupportLayerTests.cs`
- Modify: `Backend.slnx` (carpetas `/src/Modules/Support/` y `/tests/Modules/Support/`), `src/Api/Api.csproj`, `src/Bootstrapper/Bootstrapper.csproj`, `tests/ArchitectureTests/ArchitectureTests/ArchitectureTests.csproj`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (rol `admin` ~1698, rol `advisor` ~1834, `PermissionDefinition` después de Loans ~2559, políticas después de Loans ~2988, `AddSupportInfrastructure` junto a `AddLoansInfrastructure` 1145, `AddValidatorsFromAssemblyContaining` junto a 1127)
- Modify: `src/Api/Program.cs` (`MapSupportEndpoints()` después de `MapLoansEndpoints()`, línea 329)
- Modify: `tests/ArchitectureTests/ArchitectureTests/CompositionRootTests.cs:189-209`
- Modify: todos los `packages.lock.json` que cambien (`dotnet restore --force-evaluate` desde `$W`), en el mismo commit.

**Interfaces:**
- Produces: `SupportPermissions.Read = "support.ticket.read"`, `.Manage = "support.ticket.manage"`, `.SettingsManage = "support.settings.manage"`; `SupportPolicies.TicketView = "support.ticket.view"`; `SupportPolicies.SettingsView = "support.settings.view"` (la usa la Tarea 7).
- Produces: `internal static class SupportAuthorization { Task EnsureAsync(IExecutionContext, ITenantModules, Guid tenantId, string permission, CancellationToken); Task EnsureCanViewAsync(IExecutionContext, ITenantModules, Guid tenantId, CancellationToken); RequestForbiddenException Denied(); }` — copia de `LoansAuthorization` con `TenantModuleKeys.Support`; `EnsureCanViewAsync` acepta `Read` **o** `Manage`.

- [ ] **Paso 1: pruebas en rojo.**
  - `SupportLayerTests`: copia literal de `LoansLayerTests` con los tipos de Support (`DomainDoesNotReferenceOuterLayers`, `ApplicationDoesNotReferenceInfrastructureOrApi`, `InfrastructureDoesNotReferenceApi`, `ApplicationDoesNotReferencePersistenceLibraries`, `ApplicationOnlyReferencesTenancyAmongTheBusinessModules`).
  - `CompositionRootTests`:
    ```csharp
    [Fact]
    public void TheSupportPermissionsRequireTheSupportModuleAdminGetsAllThreeAndAdvisorReadAndManage()
    {
        // definitions[p].Category == "Soporte"; RequiredModules == [ModuleKeys.Support] para los tres
        // admin ⊇ {Read, Manage, SettingsManage}; advisor ⊇ {Read, Manage}; advisor ∌ SettingsManage
        // billing y cashier ∌ ninguno de los tres
    }

    [Fact]
    public async Task TheSupportPoliciesAreRegistered()
    {
        // GetPolicyAsync no es null para Read, Manage, SettingsManage, SupportPolicies.TicketView y SupportPolicies.SettingsView
    }
    ```
    y `PermissionDiscoveryFindsTheEightyTwoConstants` pasa a `PermissionDiscoveryFindsTheEightyFiveConstants` con `Assert.Equal(85, …)` y la línea de comentario «+ los tres de soporte (support.*, spec 2026-11-14)».
- [ ] **Paso 2:** `dotnet build $AT` → falla con `CS0246` (`SupportPermissions` no existe). Guarda la salida.
- [ ] **Paso 3:** crea los proyectos, los tipos de **Interfaces**, las tres `PermissionDefinition` («Ver tickets de soporte», `low`; «Gestionar tickets de soporte», `medium`; «Configurar soporte», `medium`; descripciones según §10), los permisos en `admin` y `advisor` con su comentario de spec, y en `AddAuthorization` las tres políticas con `AddPermissionRequirement` **más** `TicketView`:
  ```csharp
  .AddPolicy(SupportPolicies.TicketView, policy =>
  {
      policy.RequireAuthenticatedUser();
      policy.RequireClaim(QepClaimTypes.Permission, SupportPermissions.Read, SupportPermissions.Manage);
  })
  ```
  Registra proyectos en `Backend.slnx`, `Api.csproj`, `Bootstrapper.csproj` y `ArchitectureTests.csproj`; corre `dotnet restore --force-evaluate` y suma los lock files cambiados.
- [ ] **Paso 4:** `dotnet test $AT` → PASS (todas, también `EveryPermissionConstantDeclaresItsModules` y `NoSystemRolePermissionComesFromTheMetadataFallback`).
- [ ] **Paso 5:** commit `feat(support): scaffold support module with permissions and layer tests`.

### Task 3: días hábiles y estados (dominio puro)

**Files:**
- Create: `Modules.Support.Domain/ISupportBusinessCalendar.cs`, `SupportBusinessDays.cs`, `SupportClockContext.cs`, `SupportTicketStatus.cs`, `SupportDueState.cs`, `SupportErrorCodes.cs`, `SupportDomainException.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportBusinessDaysTests.cs`, `SupportTicketStatusesTests.cs`, `SupportDueStatesTests.cs`, `FakeCalendar.cs` (lunes a viernes más una lista de festivos)

**Interfaces:**
- Produces:
  ```csharp
  public interface ISupportBusinessCalendar { bool IsBusinessDay(DateOnly date); }
  public static class SupportBusinessDays
  {
      public const int MaxScanDays = 3660;
      public static DateOnly Add(ISupportBusinessCalendar calendar, DateOnly from, int businessDays);              // n-ésimo hábil estrictamente después de from; n = 0 → from
      public static int CountStrictlyBetween(ISupportBusinessCalendar calendar, DateOnly from, DateOnly to);       // hábiles d con from < d < to
  }
  public sealed record SupportClockContext(ISupportBusinessCalendar Calendar, TimeZoneInfo TimeZone)
  { public DateOnly LocalDate(DateTimeOffset instant); }
  public enum SupportTicketStatus { New, Open, WaitingOnCustomer, WaitingInternal, Resolved, Closed }
  public static class SupportTicketStatuses
  {
      public static IReadOnlyList<SupportTicketStatus> Ordered { get; }                    // los seis, en el orden del contrato
      public static IReadOnlyList<SupportTicketStatus> AllowedFrom(SupportTicketStatus from);
      public static bool StopsTheClock(SupportTicketStatus status);                       // WaitingOnCustomer, Resolved, Closed
  }
  public enum SupportDueState { NoTerm, OnTrack, DueSoon, Overdue, Paused, Finished }
  public static class SupportDueStates
  {
      public static (SupportDueState State, int? BusinessDaysLeft) Evaluate(SupportTicketStatus status, DateOnly? dueOn, DateOnly today,
          ISupportBusinessCalendar calendar, int dueSoonBusinessDays);                     // umbral del tenant [owner 2026-11-14]; 0 = nunca DueSoon
  }
  public static class SupportErrorCodes { /* ver lista abajo */ }
  public sealed class SupportDomainException(string code, string message) : DomainException(code, message);
  ```
  Códigos: `support.ticket.not_found`, `support.ticket.transition_invalid`, `support.ticket.requester_required`, `support.ticket.reply_channel_unavailable`, `support.ticket.customer_not_found`, `support.ticket.customer_inactive`, `support.ticket.assignee_invalid`, `support.ticket_type.not_found`, `support.ticket_type.unavailable`, `support.ticket_type.name_taken`, `support.ticket_type.order_incomplete`.

- [ ] **Paso 1: pruebas en rojo** (calendario: lunes a viernes con festivos `2026-11-16`, `2026-12-08`, `2026-12-25`; 2026-11-13 es viernes):
  ```csharp
  [Theory]
  [InlineData(15, "2026-12-07")]
  [InlineData(10, "2026-11-30")]
  [InlineData(30, "2026-12-30")]
  public void TheTermCountsFromTheNextBusinessDaySkippingHolidays(int term, string due) =>
      Assert.Equal(DateOnly.Parse(due), SupportBusinessDays.Add(Calendar, new DateOnly(2026, 11, 13), term));

  [Fact] public void ZeroBusinessDaysIsTheSameDate()                      // Add(…, 2026-11-13, 0) == 2026-11-13
  [Fact] public void ASaturdayReceptionStartsOnTheNextBusinessDay()       // Add(…, 2026-11-14, 15) == 2026-12-07
  [Fact] public void CountStrictlyBetweenSkipsBothEndsAndTheWeekend()     // (2026-11-17, 2026-11-23) == 3; (2026-11-17, 2026-11-17) == 0
  [Fact] public void AddStopsInsteadOfLoopingForeverWithoutBusinessDays() // calendario que nunca es hábil → InvalidOperationException
  ```
  `SupportTicketStatusesTests`: una `[Theory]` por fila de la tabla de `Global Constraints` (`AllowedFrom(from)` igual exacto, en ese orden); `NothingGoesBackToNew` (ningún `AllowedFrom` contiene `New`); `ClosedIsTerminal`; `OnlyWaitingOnCustomerResolvedAndClosedStopTheClock`.
  `SupportDueStatesTests` (vence 2026-12-07, umbral 3 salvo que se diga): `Open` hoy 2026-12-08 → `(Overdue, 0)`; hoy 2026-12-02 → `(DueSoon, 3)`; hoy 2026-12-01 → `(OnTrack, 4)`; hoy 2026-12-01 con umbral 5 → `(DueSoon, 4)`; hoy 2026-12-07 → `(DueSoon, 0)`; hoy 2026-12-07 con umbral 0 → `(OnTrack, 0)`; `WaitingOnCustomer` → `(Paused, null)`; `Resolved` y `Closed` → `(Finished, null)`; `dueOn == null` → `(NoTerm, null)`. `BusinessDaysLeft` = `CountStrictlyBetween(today, dueOn) + 1` cuando `today < dueOn`, 0 en el día del vencimiento.
- [ ] **Paso 2:** `dotnet build $SU` → falla con `CS0246`. Guarda la salida.
- [ ] **Paso 3:** implementa los tipos de **Interfaces**. `Add` y `CountStrictlyBetween` recorren día por día y lanzan `InvalidOperationException` al pasar `MaxScanDays` sin completar.
- [ ] **Paso 4:** `dotnet test $SU` → PASS.
- [ ] **Paso 5:** commit `feat(support): add business-day clock and ticket status machine`.

### Task 4: agregados `SupportTicketType` y `SupportTicket`

**Files:**
- Create: `Modules.Support.Domain/SupportTicketId.cs`, `SupportTicketTypeId.cs`, `SupportTicketType.cs`, `SupportTicketTypeDefaults.cs`, `SupportTicketNumber.cs`, `TicketRequester.cs`, `SupportTicketIntake.cs`, `ManualIntakeMedium.cs`, `SupportReplyChannel.cs`, `SupportTicketEventKind.cs`, `SupportTicketEvent.cs`, `SupportTicket.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportTicketTypeTests.cs`, `SupportTicketTests.cs`, `SupportTicketNumberTests.cs`

**Interfaces:**
- Consumes: Tarea 3 completa.
- Produces:
  ```csharp
  public readonly record struct SupportTicketId(Guid Value) { public static SupportTicketId New(); }        // GUID v7, como LoanId
  public readonly record struct SupportTicketTypeId(Guid Value) { public static SupportTicketTypeId New(); }
  public static class SupportTicketNumber { public static string Format(long number); public static bool TryParse(string? text, out long number); } // "SOP-000127"; acepta "SOP-000127", "sop-127" y "127"
  public sealed class SupportTicketType
  {
      public const int NameMaxLength = 80; public const int MaxTermBusinessDays = 365;
      public static SupportTicketType Create(SupportTicketTypeId id, Guid tenantId, string name, int? termBusinessDays, bool termIsLegal,
          bool requiresFormalResponse, bool isPublished, int position, DateTimeOffset now);   // IsActive = true, Version = 1
      public void Update(string name, int? termBusinessDays, bool termIsLegal, bool requiresFormalResponse, bool isPublished, bool isActive, DateTimeOffset now);
      public void MoveTo(int position, DateTimeOffset now);
      // Id, TenantId, Name, TermBusinessDays, TermIsLegal, RequiresFormalResponse, IsPublished, IsActive, Position, CreatedAt, UpdatedAt, Version
  }
  public static class SupportTicketTypeDefaults
  { public static IReadOnlyList<SupportTicketType> For(Guid tenantId, DateTimeOffset now); }                  // los ocho de Global Constraints
  public sealed record TicketRequester(string? FirstNames, string? LastNames, string? BusinessName, string? DocumentType,
      string? DocumentNumber, string? Email, string? MobilePhone, string? LandlinePhone, string CountryCode, string? Address)
  { public string DisplayName(); }                                                                            // regla de RequesterDisplayName
  public enum SupportTicketIntake { Manual, Conversation, PublicForm }
  public enum ManualIntakeMedium { Phone, InPerson, Email, Letter, Other }
  public enum SupportReplyChannel { Thread, WhatsAppTemplate, Email }
  public enum SupportTicketEventKind { Created, StatusChanged, AssigneeChanged, TypeChanged, CustomerChanged }
  public sealed class SupportTicketEvent { /* Id, TenantId, TicketId, Ordinal, Kind, ActorMemberId, OccurredAt, Details (string jsonb) */ }
  public sealed class SupportTicket
  {
      public static SupportTicket CreateManual(SupportTicketId id, Guid tenantId, long number, SupportTicketType type, string subject,
          string description, ManualIntakeMedium medium, SupportReplyChannel replyChannel, Guid? customerId, TicketRequester? requester,
          Guid? assigneeMemberId, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock);
      public void ChangeStatus(SupportTicketStatus target, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock);
      public void Assign(Guid? assigneeMemberId, Guid actorMemberId, DateTimeOffset now);       // igual al actual: no-op
      public void ChangeType(SupportTicketType type, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock); // mismo id: no-op
      public void LinkCustomer(Guid? customerId, Guid actorMemberId, DateTimeOffset now);        // igual al actual: no-op
      public string DisplayNumber { get; }
      // Id, TenantId, Number, TypeId, TypeName, TermBusinessDays, TermIsLegal, RequiresFormalResponse, Subject, Description, Intake,
      // ManualMedium, ReplyChannel, Status, AssigneeMemberId, CustomerId, Requester, ReceivedAt, ReceivedOn, DueOn, PausedSince,
      // PausedBusinessDays, ResolutionCount, ResolvedAt, ClosedAt, LastEventOrdinal, Events, CreatedAt, UpdatedAt, Version
  }
  ```
  Toda mutación real agrega un `SupportTicketEvent` con `Ordinal = ++LastEventOrdinal` y sube `Version`; un no-op no agrega ni sube.

- [ ] **Paso 1: pruebas en rojo** (`FakeCalendar` de la Tarea 3; zona `America/Bogota`; tipo de 15 días salvo que se diga):
  ```csharp
  [Fact] public void TheDefaultsAreTheEightOfTheSpecInOrder()
  // nombres, términos (15,10,30,15,15,15,15,null), legal (7 × true, false), formal (5 × true, 3 × false), IsPublished e IsActive true, Position 1..8
  [Fact] public void ATypeWithoutTermCannotBeLegal()                    // Create(…, term: null, termIsLegal: true) → ArgumentException
  [Fact] public void ANewTicketIsNewWithDueDateAndACreatedEvent()
  // recibido 2026-11-13T15:00Z: Status New, ReceivedOn 2026-11-13, DueOn 2026-12-07, Events = [Created ordinal 1], Version 1, Number 1, DisplayNumber "SOP-000001"
  [Fact] public void ANightReceptionCountsFromTheLocalDate()             // 2026-11-13T03:30Z → ReceivedOn 2026-11-12, DueOn 2026-12-04
  [Fact] public void ATypeWithoutTermHasNoDueDate()                      // "Soporte técnico" → DueOn null
  [Fact] public void ATicketNeedsACustomerOrARequesterName()             // sin customerId y requester null → SupportDomainException requester_required
  [Fact] public void PausingAndResumingMovesTheDueDateByTheStoppedBusinessDays()
  // Open 2026-11-16T15:00Z, WaitingOnCustomer 2026-11-17T15:00Z, Open 2026-11-23T14:00Z → PausedBusinessDays 3, DueOn 2026-12-11, PausedSince null
  [Fact] public void PausingAndResumingTheSameDayKeepsTheDueDate()        // DueOn 2026-12-07
  [Fact] public void WaitingInternalKeepsTheClockRunning()                // Open → WaitingInternal 11-17 → Open 11-23: PausedBusinessDays 0, DueOn 2026-12-07
  [Fact] public void AChainOfStopsCountsThePauseOnceFromItsStart()
  // WaitingOnCustomer 11-17 → Resolved 11-19 (PausedSince sigue en 11-17) → Open 11-23: PausedBusinessDays 3, DueOn 2026-12-11, ResolutionCount 1
  [Fact] public void ReopeningAndResolvingAgainCountsASecondCycle()      // Resolved → Open → Resolved: ResolutionCount 2
  [Fact] public void AnInvalidTransitionKeepsTheTicketAsItWas()          // New → WaitingInternal: transition_invalid; Status, Version y Events sin cambios
  [Fact] public void ChangingTheTypeRecalculatesFromTheReception()
  // recibido 11-13 con 15 días, ChangeType(10 días) el 11-18 → DueOn 2026-11-30, TypeName nuevo, evento TypeChanged; con PausedBusinessDays 3 → 2026-12-03
  [Fact] public void ChangingToATypeWithoutTermClearsTheDueDate()       // DueOn null
  [Fact] public void EditingTheTypeAfterwardsDoesNotMoveTheTicket()     // type.Update(term: 30) después del alta → ticket.DueOn sigue 2026-12-07
  [Fact] public void EventOrdinalsAreConsecutive()                       // Created, StatusChanged, AssigneeChanged → 1, 2, 3
  [Fact] public void NoOpsDoNotWriteEventsNorBumpTheVersion()            // Assign(mismo), LinkCustomer(mismo), ChangeType(mismo id)
  ```
  `SupportTicketNumberTests`: `Format(127) == "SOP-000127"`, `Format(1_234_567) == "SOP-1234567"`, `TryParse` acepta `"SOP-000127"`, `"sop-127"`, `"127"` y rechaza `"SOP-"`, `"abc"`, `"-1"`.
- [ ] **Paso 2:** `dotnet build $SU` → falla con `CS0246`. Guarda la salida.
- [ ] **Paso 3:** implementa. La pausa y la reanudación siguen D3 con `clock.LocalDate`; el vencimiento es `SupportBusinessDays.Add(calendar, ReceivedOn, term + PausedBusinessDays)` o `null`. Entrar a `Resolved` sube `ResolutionCount` y fija `ResolvedAt`; entrar a `Closed` fija `ClosedAt`. `Details` es JSON camelCase con `from`/`to` (estado, miembro, tipo o cliente) según el evento.
- [ ] **Paso 4:** `dotnet test $SU` → PASS.
- [ ] **Paso 5:** commit `feat(support): add ticket and ticket type aggregates`.

### Task 5: persistencia, siembra, contador y auditoría

**Files:**
- Create: `Modules.Support.Infrastructure/Persistence/SupportDbContext.cs`, `SupportDbContextFactory.cs`, `SupportUnitOfWork.cs`, `SupportTicketRepository.cs`, `SupportTicketTypeRepository.cs`, `SupportProvisioner.cs`, `SupportTicketNumberGenerator.cs`, `SupportAuditPublisher.cs`, `UniqueViolation.cs`, `Migrations/<ts>_InitialSupport.cs`
- Create: `Modules.Support.Infrastructure/SupportDatabaseInitializer.cs`
- Create: `Modules.Support.Application/ISupportUnitOfWork.cs`, `ISupportTicketRepository.cs`, `ISupportTicketTypeRepository.cs`, `ISupportProvisioner.cs`, `ISupportTicketNumberGenerator.cs`, `ISupportAuditPublisher.cs`, `SupportAuditActions.cs`, `SupportReadModels.cs`
- Modify: `SupportInfrastructureExtensions.cs`; `src/Api/Program.cs` (`InitializeSupportDatabaseAsync` después de `InitializeLoansDatabaseAsync`, línea 422, con su comentario)
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportApiHarness.cs`, `SharedSupportHost.cs`, `SupportPersistenceTests.cs`

**Interfaces:**
- Consumes: Tareas 3 y 4.
- Produces:
  ```csharp
  public interface ISupportUnitOfWork { Task SaveChangesAsync(CancellationToken ct); Task<ISupportTransaction> BeginTransactionAsync(CancellationToken ct); }
  public interface ISupportTransaction : IAsyncDisposable { Task CommitAsync(CancellationToken ct); }
  public interface ISupportProvisioner { Task EnsureAsync(Guid tenantId, CancellationToken ct); }
  public interface ISupportTicketNumberGenerator { Task<long> NextAsync(Guid tenantId, CancellationToken ct); }   // exige transacción abierta
  // Domain/SupportTenantSettings.cs [owner 2026-11-14]: no lo inserta EF (lo crea el provisionador) y no mapea el contador
  public sealed class SupportTenantSettings
  {
      public const int DefaultDueSoonBusinessDays = 3; public const int MinDueSoonBusinessDays = 0; public const int MaxDueSoonBusinessDays = 30;
      public Guid TenantId { get; } public int DueSoonBusinessDays { get; } public DateTimeOffset UpdatedAt { get; } public long Version { get; }
      public void ChangeDueSoon(int businessDays, DateTimeOffset now);   // fuera de 0..30 → ArgumentOutOfRangeException; igual al actual: no-op; si no, Version++
  }
  public interface ISupportSettingsRepository { Task<SupportTenantSettings> GetAsync(Guid tenantId, CancellationToken ct); }  // con tracking; después de EnsureAsync
  public interface ISupportTicketTypeRepository
  {
      Task<IReadOnlyList<SupportTicketType>> ListAsync(Guid tenantId, CancellationToken ct);                       // por Position, con tracking
      Task<SupportTicketType?> FindAsync(Guid tenantId, SupportTicketTypeId id, CancellationToken ct);
      void Add(SupportTicketType type);
  }
  public interface ISupportTicketRepository
  {
      Task<SupportTicket?> FindAsync(Guid tenantId, SupportTicketId id, CancellationToken ct);                      // con tracking, sin eventos
      Task<IReadOnlyList<SupportTicketEvent>> ListEventsAsync(Guid tenantId, SupportTicketId id, CancellationToken ct); // por Ordinal
      Task<SupportTicketPage> ListAsync(SupportTicketFilter filter, CancellationToken ct);
      Task<SupportInboxTotals> TotalsAsync(Guid tenantId, DateOnly today, DateOnly dueSoonLimit, CancellationToken ct);
      void Add(SupportTicket ticket);
  }
  public interface ISupportAuditPublisher { void Publish(Guid tenantId, Guid actorId, string action, string resourceType, string resourceId,
      string outcome, IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt); }          // mismo contrato que ILoansAuditPublisher
  public static class SupportAuditActions
  {
      TicketCreated = "support.ticket.created"; TicketStatusChanged = "support.ticket.status_changed"; TicketAssigned = "support.ticket.assigned";
      TicketTypeChanged = "support.ticket.type_changed"; TicketCustomerChanged = "support.ticket.customer_changed";
      TicketTypeCreated = "support.ticket_type.created"; TicketTypeUpdated = "support.ticket_type.updated"; TicketTypesReordered = "support.ticket_type.reordered";
      Resources.Ticket = "ticket"; Resources.TicketType = "ticket_type";
  }
  // SupportReadModels.cs
  public sealed record SupportTicketFilter(Guid TenantId, IReadOnlyCollection<SupportTicketStatus>? Statuses, Guid? TypeId,
      Guid? AssigneeMemberId, bool UnassignedOnly, DateOnly? DueBefore, (DateOnly From, DateOnly To)? DueBetween,
      long? Number, string? SearchTerm, IReadOnlyCollection<Guid>? SearchCustomerIds, int Page, int PageSize);
  public sealed record SupportTicketPage(IReadOnlyList<SupportTicket> Items, int Total);
  public sealed record SupportInboxTotals(IReadOnlyDictionary<SupportTicketStatus, int> ByStatus, int Overdue, int DueSoon);
  ```
- Esquema `support` (migración `InitialSupport`; enums como `varchar(24)` por nombre, `version bigint` como token):
  - `tenant_settings`: `tenant_id uuid PK`, `next_ticket_number bigint NOT NULL DEFAULT 1`, `due_soon_business_days int NOT NULL DEFAULT 3` con `CK_tenant_settings_due_soon` (`BETWEEN 0 AND 30`), `created_at`, `updated_at`, `version bigint NOT NULL DEFAULT 1` (D15). El `UPDATE` del contador no toca `version`: guardar el umbral nunca choca con un alta.
  - `ticket_types`: `id`, `tenant_id`, `name varchar(80)`, `term_business_days int NULL`, `term_is_legal`, `requires_formal_response`, `is_published`, `is_active`, `position int`, `created_at`, `updated_at`, `version`. `CK_ticket_types_term` (`NULL` o 1..365), `CK_ticket_types_legal_term` (`NOT term_is_legal OR term_business_days IS NOT NULL`), índice único `IX_ticket_types_tenant_name (tenant_id, lower(name))`.
  - `tickets`: `id`, `tenant_id`, `number bigint`, `ticket_type_id`, `type_name varchar(80)`, `term_business_days int NULL`, `term_is_legal`, `requires_formal_response`, `subject varchar(200)`, `description varchar(5000)`, `intake`, `manual_medium NULL`, `reply_channel`, `status`, `assignee_member_id uuid NULL`, `customer_id uuid NULL`, columnas `requester_*` de D7 (todas `NULL`; `requester_country_code varchar(2) NULL`), `received_at timestamptz`, `received_on date`, `due_on date NULL`, `paused_since timestamptz NULL`, `paused_business_days int NOT NULL DEFAULT 0`, `resolution_count int NOT NULL DEFAULT 0`, `resolved_at NULL`, `closed_at NULL`, `last_event_ordinal int`, `created_at`, `updated_at`, `version`. `CK_tickets_status` (los seis), `CK_tickets_intake` (`'Manual','Conversation','PublicForm'`), `CK_tickets_reply_channel`. Índices: único `IX_tickets_tenant_number (tenant_id, number)`, `IX_tickets_tenant_status_received (tenant_id, status, received_at DESC)`, `IX_tickets_tenant_due (tenant_id, due_on)`, `IX_tickets_tenant_customer (tenant_id, customer_id)`.
  - `ticket_events`: `id`, `tenant_id`, `ticket_id` FK → `tickets(id) ON DELETE CASCADE`, `ordinal int`, `kind varchar(24)`, `actor_member_id uuid NULL`, `occurred_at`, `details jsonb`. Único `IX_ticket_events_ticket_ordinal (ticket_id, ordinal)`.
  - Proyección de `audit.entries` con `AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false)`, como `LoansDbContext`.
- `SupportUnitOfWork.SaveChangesAsync` guarda con `CustomerReferenceWrites.SaveHoldingAsync` sobre `new CustomerReferenceSource(typeof(SupportTicket), nameof(SupportTicket.CustomerId), SupportErrorCodes.CustomerNotFound)` (lanza `SupportDomainException` con ese código); traduce `DbUpdateConcurrencyException` → `RequestConcurrencyException("concurrency.conflict", …)`, `IX_ticket_types_tenant_name` → `ResourceConflictException(SupportErrorCodes.TicketTypeNameTaken, …)` y `IX_ticket_events_ticket_ordinal` → `RequestConcurrencyException`, todos **por nombre de índice** con su `UniqueViolation` interno (copia del de Loans).

- [ ] **Paso 1: pruebas en rojo.** `SupportApiHarness` copia `LoansApiHarness` (tenant con fila, `SeedTenantAsync(factory, tenantId, modules)`, `SeedCustomerAsync`, `SeedMemberAsync`, `SeedCallerMembershipAsync`, `CountAuditEntriesAsync`, `CreateClient`, `MutableTenantClock`, `QepApiFactory` con `Seed:Enabled=false` y `configureServices`), slug `support-api-<sufijo>`, y suma `NewTenantId()` (un tenant por prueba, para el host compartido). `SharedSupportHost` copia `SharedCustomersHost`, con el comentario de qué pruebas pueden compartirlo (las que siembran su propio tenant y no cambian `ITenantClock` ni cuentan sobre toda la base). `SupportPersistenceTests` (host propio):
  ```csharp
  [Fact] public async Task TheFirstAccessSeedsTheEightDefaultTypesInOrder()   // EnsureAsync → ListAsync: 8 nombres de Global Constraints, Position 1..8
  [Fact] public async Task TwoFirstAccessesSeedTheTypesOnce()                 // Task.WhenAll de dos EnsureAsync en scopes distintos → 8 filas
  [Fact] public async Task NumbersAreConsecutivePerTenant()                   // tenant A: 1, 2; tenant B: 1
  [Fact] public async Task ARolledBackCreationDoesNotKeepItsNumber()          // NextAsync en una transacción sin Commit, luego otra → 1
  [Fact] public async Task ATicketRoundTripsWithItsRequesterAndEvents()       // Add + Save; FindAsync y ListEventsAsync devuelven lo mismo
  [Fact] public async Task ANewTenantHasTheDefaultDueSoonOf3()               // EnsureAsync → GetAsync: DueSoonBusinessDays 3, Version 1
  [Fact] public async Task NumberingDoesNotBumpTheSettingsVersion()          // NextAsync ×2 → Version sigue en 1
  [Fact] public async Task ATakenTypeNameIs409WhateverTheCase()               // "queja" con "Queja" sembrado → ResourceConflictException support.ticket_type.name_taken
  ```
- [ ] **Paso 2:** `dotnet build $SI` → falla con `CS0246` (`ISupportProvisioner`, `SupportDbContext`…). Guarda la salida.
- [ ] **Paso 3:** implementa contexto, repositorios, unidad de trabajo, `SupportProvisioner` (D1, SQL crudo en transacción propia como `PipelineProvisioner`, siembra con `SupportTicketTypeDefaults.For`), `SupportTicketNumberGenerator` (D2; lanza `InvalidOperationException` si no hay transacción o la fila no vuelve), `SupportAuditPublisher` (copia de `LoansAuditPublisher`, `Source = "support"`), registro en `AddSupportInfrastructure` (DbContext con historial `__ef_migrations_history` en `support`) e inicializador. Genera `InitialSupport` y revisa el SQL: los CHECK e índices de arriba con esos nombres.
- [ ] **Paso 4:** `$env:Seed__Enabled = "false"`; `dotnet test $SI --filter "FullyQualifiedName~SupportPersistenceTests" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Paso 5:** commit `feat(support): add persistence, type seeding and ticket numbering`.

- [ ] **Fin del Bloque A:** `dotnet test $SU`; `dotnet test $TU`; `dotnet test $AT`; `dotnet test $TI --filter "FullyQualifiedName~TenantModulesApiTests|FullyQualifiedName~OperatorConsoleApiTests|FullyQualifiedName~SeedStartupTests" -- xUnit.MaxParallelThreads=4`; `dotnet test $SI -- xUnit.MaxParallelThreads=4`. Revisión del bloque.

---

## Bloque B — adaptadores, API de tipos y de tickets (Tareas 6–10, un implementador)

### Task 6: adaptadores de Bootstrapper (calendario, clientes, miembros, sonda de usuarios)

**Files:**
- Create: `Modules.Support.Application/ISupportBusinessCalendarSource.cs`, `ISupportCustomerLookup.cs`, `ISupportMemberDirectory.cs`
- Create: `src/Bootstrapper/SupportBusinessCalendarSource.cs`, `SupportCustomerLookup.cs`, `SupportMemberDirectory.cs`, `SupportUserReferenceProbe.cs`
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs` (junto a los de Loans, ~1449–1454)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/SupportBusinessCalendarTests.cs`; `tests/Modules/Support/Modules.Support.IntegrationTests/SupportUserReferenceProbeTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface ISupportBusinessCalendarSource { Task<SupportClockContext> GetAsync(Guid tenantId, CancellationToken ct); }
  public sealed record SupportCustomerRef(Guid Id, string Name, bool IsActive);
  public interface ISupportCustomerLookup                                     // copia recortada de ILoansCustomerLookup
  {
      Task<SupportCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken ct);
      Task<IReadOnlyList<SupportCustomerRef>> FindManyAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken ct);
      Task<IReadOnlyCollection<Guid>> SearchIdsAsync(Guid tenantId, string term, CancellationToken ct);
  }
  public sealed record SupportMemberRef(Guid MemberId, string DisplayName, bool IsActive, bool CanManage);
  public interface ISupportMemberDirectory                                    // copia de ILoansMemberDirectory con support.ticket.manage
  {
      Task<IReadOnlyList<SupportMemberRef>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);
      Task<IReadOnlyList<SupportMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken ct);
  }
  // Bootstrapper
  internal sealed class SupportBusinessCalendar(
      IReadOnlySet<DayOfWeek> workingDays, bool colombianHolidays, IReadOnlyDictionary<DateOnly, bool> exceptionIsOpen) : ISupportBusinessCalendar
  {
      public static SupportBusinessCalendar From(BusinessHoursSchedule? schedule, string tenantCountry);
  }
  internal sealed class SupportBusinessCalendarSource(IBusinessHoursRepository schedules, ITenantDirectory tenants, ITenantClock clock)
      : ISupportBusinessCalendarSource;
  internal sealed class SupportUserReferenceProbe(SupportDbContext dbContext, IMembershipDirectory memberships) : IUserReferenceProbe; // Source = "support"
  ```
  > **Ya no aplica [owner 2026-11-15, spec D2d]:** el calendario es lunes a viernes con los festivos de Colombia si el tenant es `CO`, y no lee el horario. Hoy son `SupportBusinessCalendar(bool colombianHolidays)` con `From(string tenantCountry)` y `SupportBusinessCalendarSource(ITenantDirectory tenants)`; las pruebas de abajo se reemplazaron por las de `SupportBusinessCalendarTests` (rama `feature/support-owner-decisions`). Lo que sigue es el diseño original.

  `From` [owner 2026-11-14]: el mismo calendario que la bandeja. Días de la semana: los que tienen franjas en `schedule.Weekly` (1 = lunes … 7 = domingo); sin horario **o** sin ningún día con franjas, lunes a viernes (D12). País de los festivos: `schedule.HolidayCountry` si tiene valor; si no (o sin horario), `tenantCountry`; `colombianHolidays` = ese país es `CO`. Excepciones: cada `schedule.Exceptions[i]` entra en `exceptionIsOpen` con `Ranges.Count > 0`. `IsBusinessDay(date)`: si hay excepción para la fecha, decide la excepción; si no, no es festivo **y** su día de la semana es hábil. Es el mismo orden de `BusinessHoursSchedule.RangesFor` (excepción → festivo → día de la semana); el comentario de la clase lo dice y avisa que, si ese orden cambia en Mensajería, cambia acá. La zona sale de `ITenantClock.GetAsync(...).TimeZone` y el país de `ITenantDirectory.GetRegionalSettingsAsync(...).Country`.

- [ ] **Paso 1: pruebas en rojo.** `SupportBusinessCalendarTests` (con `BusinessHoursSchedule.Create`).
  > **Reemplazadas [owner 2026-11-15, spec D2d]:** las diez pruebas de esta lista se quitaron. Hoy la clase tiene
  > `MondayToFridayAreBusinessDaysAndTheWeekendIsNot` (`CO`, `ES`, `""`), `ColombiaClosesItsHolidaysAndOtherCountriesDoNot`,
  > `TheDeadlineSkipsTheWeekendAndColombianHolidays` y `TheCalendarIsMondayToFridayLessColombianHolidaysOnEveryDayOfAYear` (en lugar de
  > `TheCalendarMatchesTheInboxOnEveryDayOfAYear`). La lista que sigue es la original.
  ```csharp
  [Fact] public void WithoutAScheduleMondayToFridayAreBusinessDays()                 // 2026-11-13 (vie) sí, 2026-11-14 (sáb) no
  [Fact] public void ColombiaClosesItsHolidays()                                       // From(null, "CO"): 2026-11-16 no; From(null, "ES"): sí
  [Fact] public void TheScheduleHolidayCountryWins()                                  // horario L-V con HolidayCountry CO y tenantCountry "ES": 2026-11-16 no
  [Fact] public void WithoutScheduleHolidayCountryTheTenantCountryIsUsed()           // horario L-V con HolidayCountry null y tenantCountry "CO": 2026-11-16 no; con "ES": sí
  [Fact] public void AClosingDateExceptionClosesAWeekday()                            // horario L-V con excepción 2026-11-18 sin franjas: 2026-11-18 no; 2026-11-19 sí
  [Fact] public void AnOpeningDateExceptionOpensTheDay()                              // excepción 2026-11-14 (sáb) con franja 09:00-12:00: sí
  [Fact] public void AClosingExceptionMovesTheDeadlineOneBusinessDay()
  // horario L-V, HolidayCountry CO, excepción 2026-11-18 sin franjas: SupportBusinessDays.Add(calendar, 2026-11-13, 15) == 2026-12-09 (sin la excepción, 2026-12-07; el 12-08 es festivo)
  [Fact] public void TheCalendarMatchesTheInboxOnEveryDayOfAYear()
  // para el mismo horario (L-S, HolidayCountry CO, dos excepciones): IsBusinessDay(d) == schedule.RangesFor(d).Count > 0 en cada día de 2026
  [Fact] public void TheScheduleDecidesTheWeekdays()                                   // horario lunes a sábado: 2026-11-14 sí, 2026-11-15 no
  [Fact] public void AScheduleWithEveryDayClosedFallsBackToMondayToFriday()            // los siete sin franjas, HolidayCountry CO → vie 2026-11-13 sí, sáb no, 2026-11-16 (festivo) no
  ```
  `SupportUserReferenceProbeTests` (host propio): `AnAssigneeKeepsTheUser` (ticket con `assignee_member_id` de la membresía → `true`), `AnEventActorKeepsTheUser` (sólo `ticket_events.actor_member_id` → `true`), `WithoutReferencesTheUserIsFree` → `false`.
- [ ] **Paso 2:** `dotnet build $BU` → falla con `CS0246` (`SupportBusinessCalendar`). Guarda la salida.
- [ ] **Paso 3:** implementa los puertos y adaptadores (copias de `LoansCustomerLookup`, `LoansMemberDirectory` con `SupportPermissions.Manage`, `LoansUserReferenceProbe` sobre `tickets.assignee_member_id` y `ticket_events.actor_member_id`) y regístralos (`services.AddScoped<IUserReferenceProbe, SupportUserReferenceProbe>()`, …). El `<remarks>` de la sonda avisa que una columna nueva con id de membresía en `support` entra ahí.
- [ ] **Paso 4:** `dotnet test $BU --filter "FullyQualifiedName~SupportBusinessCalendarTests"`; `dotnet test $SI --filter "FullyQualifiedName~SupportUserReferenceProbeTests" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Paso 5:** commit `feat(support): add calendar, customer and member adapters`.

### Task 7: API de tipos de ticket y de ajustes de Soporte

**Files:**
- Create: `Modules.Support.Application/TicketTypes/ListTicketTypes.cs`, `CreateTicketType.cs`, `UpdateTicketType.cs`, `ReorderTicketTypes.cs`; `Settings/GetSupportSettings.cs`, `Settings/UpdateSupportSettings.cs`; `SupportDtos.cs` (tipos de tipo y `SupportSettingsDto`), `SupportNotFound.cs`
- Modify: `Modules.Support.Api/SupportEndpoints.cs`; `QepServiceCollectionExtensions.cs` (handlers a mano y con ruta completa, como Loans 420–463)
- Test: `tests/Modules/Support/Modules.Support.UnitTests/TicketTypeValidatorsTests.cs`, `SupportSettingsValidatorTests.cs`; `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTicketTypesApiTests.cs`, `SupportSettingsApiTests.cs`, `SupportAccessApiTests.cs`

**Interfaces:**
- Consumes: `ISupportProvisioner`, `ISupportTicketTypeRepository`, `ISupportSettingsRepository`, `ISupportUnitOfWork`, `ISupportAuditPublisher`, `SupportAuthorization`.
- Produces:
  ```csharp
  public sealed record ListTicketTypesQuery(Guid TenantId, bool ForSettings) : IQuery<IReadOnlyList<SupportTicketTypeDto>>;  // ForSettings exige SettingsManage; si no, Read o Manage
  public sealed record TicketTypeRequest(string? Name, int? TermBusinessDays, bool? TermIsLegal, bool? RequiresFormalResponse, bool? IsPublished, bool? IsActive);
  public sealed record CreateTicketTypeCommand(Guid TenantId, …) : ICommand<SupportTicketTypeDto> { public static CreateTicketTypeCommand From(Guid tenantId, TicketTypeRequest request); } // va al final (Position = max + 1), activo
  public sealed record UpdateTicketTypeCommand(Guid TenantId, Guid TypeId, …, long ExpectedVersion) : ICommand<SupportTicketTypeDto>;
  public sealed record ReorderTicketTypesRequest(IReadOnlyList<Guid>? TypeIds);
  public sealed record ReorderTicketTypesCommand(Guid TenantId, IReadOnlyList<Guid> TypeIds) : ICommand<IReadOnlyList<SupportTicketTypeDto>>;
  // [owner 2026-11-14] umbral de «por vencer»
  public sealed record GetSupportSettingsQuery(Guid TenantId) : IQuery<SupportSettingsDto>;                // SupportAuthorization.EnsureCanViewSettingsAsync: Read, Manage o SettingsManage
  public sealed record UpdateSupportSettingsRequest(int? DueSoonBusinessDays);
  public sealed record UpdateSupportSettingsCommand(Guid TenantId, int DueSoonBusinessDays, long ExpectedVersion) : ICommand<SupportSettingsDto>
  { public static UpdateSupportSettingsCommand From(Guid tenantId, UpdateSupportSettingsRequest request, long expectedVersion); } // null → -1, que el validador rechaza
  public sealed class UpdateSupportSettingsValidator : AbstractValidator<UpdateSupportSettingsCommand>; // DueSoonBusinessDays InclusiveBetween(0, 30); ExpectedVersion > 0
  // se suman a SupportAuditActions (Tarea 5): SettingsUpdated = "support.settings.updated"; Resources.Settings = "settings"
  ```
  Ajustes: `GET /settings` con la política `SupportPolicies.SettingsView` [coordinador]: `RequireAuthenticatedUser()` más `RequireClaim(QepClaimTypes.Permission, Read, Manage, SettingsManage)`, registrada en `AddAuthorization` junto a `TicketView`, y en el handler `EnsureCanViewSettingsAsync` con los mismos tres. `PUT /settings` con `SupportPermissions.SettingsManage`: autorización → validador → `EnsureAsync` → `GetAsync` → `Version != ExpectedVersion` → 412 `concurrency.conflict` → `ChangeDueSoon` → auditoría `SettingsUpdated` con `changedFields = ["dueSoonBusinessDays"]` → guardar. Las dos respuestas ponen `ETag: "<version>"`, como `StoreEndpoints.GetSettingsAsync`; el `RequireVersion` es el mismo de los tipos.
  Validador: `Name` 1..80 recortado; `TermBusinessDays` `null` o 1..365; `TermIsLegal` sólo con término (error en `TermIsLegal`). Todo handler llama `provisioner.EnsureAsync` antes de leer. Reordenar exige **todos** los ids del tenant, sin repetidos (si no, `422 support.ticket_type.order_incomplete`).

- [ ] **Paso 1: pruebas en rojo.** Unitarias: `ANameLongerThan80Fails`, `ATermOf0Or366Fails`, `LegalWithoutTermFailsOnTermIsLegal`. Integración (`SupportTicketTypesApiTests`, host compartido, tenant propio por prueba):
  ```csharp
  [Fact] public async Task TheFirstReadReturnsTheEightDefaultTypes()          // GET /ticket-types: 8 DTO con los valores exactos de Global Constraints
  [Fact] public async Task CreatingATypePutsItLast()                          // POST "Garantía" 15 legal → Position 9, IsActive true, 201
  [Fact] public async Task ATakenNameIs409()                                  // POST "queja" → 409 support.ticket_type.name_taken
  [Fact] public async Task LegalWithoutTermIs422OnTheField()                  // 422 validation.failed, errors contiene "TermIsLegal"
  [Fact] public async Task UpdatingWithoutIfMatchIs428AndWithAnOldVersionIs412()
  [Fact] public async Task DeactivatingKeepsTheTypeListedForSettings()        // IsActive false; sigue en /settings/ticket-types y en /ticket-types con IsActive false
  [Fact] public async Task ReorderingNeedsTheCompleteList()                   // 7 de 8 ids → 422 support.ticket_type.order_incomplete; los 8 invertidos → Position 1..8 invertidas
  ```
  `SupportSettingsApiTests` (host compartido, tenant propio por prueba) [owner 2026-11-14]:
  ```csharp
  [Fact] public async Task WithoutSettingsTheDueSoonIs3()                     // GET /settings: DueSoonBusinessDays 3, Min 0, Max 30, Version 1, ETag "\"1\""
  [Fact] public async Task TheAdminSetsItTo5()                                // PUT {5} con If-Match "1" → 200, DueSoonBusinessDays 5, Version 2; 1 fila de audit support.settings.updated
  [Theory, InlineData(-1), InlineData(31)]
  public async Task OutOfRangeIs422OnTheField(int value)                      // 422 validation.failed, errors contiene "DueSoonBusinessDays"; nada cambia
  [Fact] public async Task ZeroIsAccepted()                                   // PUT {0} → 200, DueSoonBusinessDays 0
  [Fact] public async Task WithoutIfMatchIs428AndWithAnOldVersionIs412()
  [Fact] public async Task AnAdvisorCannotChangeIt()                          // Read + Manage (los de advisor), sin SettingsManage → PUT 403; GET 200
  [Fact] public async Task ARoleWithOnlySettingsManageReadsAndSavesTheThreshold() // sólo SettingsManage: GET 200 con Version 1; PUT {7} con If-Match "1" → 200, DueSoonBusinessDays 7
  ```
  Unitarias: `SupportSettingsValidatorTests.AValueOutside0To30Fails` (−1 y 31, error en `DueSoonBusinessDays`) y `ZeroAnd30Pass`.
  `SupportAccessApiTests` (host compartido): `WithoutThePermissionTheTypesAre403` (sólo `tenancy.settings.read`), `AnotherTenantInTheRouteIs403Never404` (código `authorization.denied`), `WithTheModuleOffTheRouteIs403` (host propio, módulos sin `support`), `AMemberWithOnlyManageReadsTheTypes` (200), `SettingsNeedSettingsManage` (con `Read` y `Manage`, `GET /settings/ticket-types` → 403).
- [ ] **Paso 2:** `dotnet build $SU` → falla con `CS0246`. Guarda la salida.
- [ ] **Paso 3:** implementa handlers, validadores, rutas (`/ticket-types` con `SupportPolicies.TicketView`; `/settings/ticket-types*` con `SupportPermissions.SettingsManage`; `RequireVersion` copiado de `LoansEndpoints` con el mensaje «…the loaded ticket type version…»), auditoría `TicketTypeCreated`/`Updated`/`TypesReordered`.
- [ ] **Paso 4:** `dotnet test $SU`; `dotnet test $SI --filter "FullyQualifiedName~SupportTicketTypesApiTests|FullyQualifiedName~SupportSettingsApiTests|FullyQualifiedName~SupportAccessApiTests" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Paso 5:** commit `feat(support): add ticket type and support settings api`.

### Task 8: alta manual, detalle y responsables

**Files:**
- Create: `Modules.Support.Application/Tickets/CreateTicket.cs`, `GetTicket.cs`, `ListSupportAssignees.cs`, `SupportTicketDtoComposer.cs`, `SupportTicketRules.cs`; `SupportDtos.cs` (DTO de ticket de «Contrato para las pantallas»)
- Modify: `SupportEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/CreateTicketValidatorTests.cs`; `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTicketsApiTests.cs`, `SupportCustomerReferenceLockApiTests.cs`

**Interfaces:**
- Consumes: Tareas 4–7; `IMembershipDirectory.FindActiveMembershipIdAsync` (actor; sin membresía activa → `SupportAuthorization.Denied()`), `ITenantClock`, `ITenantDirectory`, `TenantModuleGuard`.
- Produces:
  ```csharp
  public sealed record TicketRequesterRequest(string? FirstNames, string? LastNames, string? BusinessName, string? DocumentType, string? DocumentNumber,
      string? Email, string? MobilePhone, string? LandlinePhone, string? CountryCode, string? Address);
  public sealed record CreateTicketRequest(Guid? TicketTypeId, string? Subject, string? Description, string? ManualMedium, string? ReplyChannel,
      Guid? CustomerId, TicketRequesterRequest? Requester, Guid? AssigneeMemberId);
  public sealed record CreateTicketCommand(…) : ICommand<SupportTicketDetailDto> { public static CreateTicketCommand From(Guid tenantId, CreateTicketRequest request); }
  public sealed record GetTicketQuery(Guid TenantId, Guid TicketId) : IQuery<SupportTicketDetailDto>;
  public sealed record ListSupportAssigneesQuery(Guid TenantId) : IQuery<SupportAssigneesDto>;
  public sealed class SupportTicketDtoComposer { Task<SupportTicketDetailDto> ComposeDetailAsync(SupportTicket ticket, CancellationToken ct);
      Task<IReadOnlyList<SupportTicketListItemDto>> ComposeListAsync(IReadOnlyList<SupportTicket> tickets, CancellationToken ct); }
  internal static class SupportTicketRules { Task EnsureCustomerAsync(…); Task EnsureAssigneeAsync(…); void EnsureReplyChannel(SupportReplyChannel, TicketRequester?, SupportCustomerRef?); }
  ```
  Orden del alta: autorización (`Manage`) → validador → provisión → tipo activo del tenant (`422 support.ticket_type.unavailable`) → cliente (D14) → responsable (activo y con `Manage`) → canal (D8; con cliente, el correo cuenta si lo trae el requester) → `BeginTransactionAsync` → `NextAsync` → `SupportTicket.CreateManual` → auditoría `TicketCreated` → `SaveChangesAsync` → `CommitAsync`. Validador: `Subject` 1..200, `Description` 1..5000, `ManualMedium` y `ReplyChannel` parseables, par de documento junto, `DocumentType` permitido para `CountryCode` (por defecto `Tenant.Country`) con error en `Requester.DocumentType`, `MobilePhone` E.164, `Email` con formato.

- [ ] **Paso 1: pruebas en rojo.** Unitarias del validador: `NieIsNotAllowedForColombia` (error en `Requester.DocumentType`), `ADocumentNumberWithoutTypeFails`, `AMobileWithoutPlusFails`, `AnEmptySubjectFails`. Integración, `SupportTicketsApiTests`:
  ```csharp
  // Host propio con MutableTenantClock(2026-11-13T15:00Z); tipo "Petición general"; caller con membresía admin
  [Fact] public async Task CreatingAManualTicketReturnsSOP000001()
  // 201; Number "SOP-000001", Status "New", AllowedTransitions ["Open","WaitingOnCustomer","Resolved"], Clock.DueOn 2026-12-07,
  // Clock.DueState "OnTrack", Clock.TermIsLegal true, Intake "Manual", Events = [Created, ordinal 1, actor = caller]; 1 fila de audit support.ticket.created
  [Fact] public async Task ConcurrentCreationsGetDistinctConsecutiveNumbers()     // 10 POST en paralelo → todos 201, números {SOP-000001..SOP-000010}
  [Fact] public async Task WithoutCustomerOrRequesterNameIs422()                   // support.ticket.requester_required
  [Fact] public async Task ACustomerOfAnotherTenantIs422NotFound()                 // support.ticket.customer_not_found
  [Fact] public async Task AnInactiveCustomerIs422()                               // DeactivateCustomerAsync → support.ticket.customer_inactive
  [Fact] public async Task ACustomerWithTheCustomersModuleOffIs403()               // módulos sin customers → 403 tenancy.module_not_enabled
  [Fact] public async Task TheThreadChannelIsUnavailableForAManualTicket()         // 422 support.ticket.reply_channel_unavailable
  [Fact] public async Task TheEmailChannelNeedsAnEmail()                           // 422 support.ticket.reply_channel_unavailable
  [Fact] public async Task AnInactiveTypeIs422()                                   // support.ticket_type.unavailable
  [Fact] public async Task AnAssigneeWhoCannotManageIs422()                        // membresía "billing" → support.ticket.assignee_invalid
  [Fact] public async Task TheDetailOfATicketOfAnotherTenantUnderMyRouteIs404()    // support.ticket.not_found
  [Fact] public async Task TheAssigneesAreTheActiveManagers()                      // admin y advisor activos sí; billing y removido no
  ```
  `SupportCustomerReferenceLockApiTests` (copia de `Loans.CustomerReferenceLockApiTests`): `CreatingATicketWaitsForTheCustomerDeletionAndThenFails` (candado exclusivo tomado a mano, `DELETE` del cliente y commit → el `POST` espera y responde `422 support.ticket.customer_not_found`; con el calentamiento que esa prueba explica) y `WithoutADeletionNothingChanges`.
- [ ] **Paso 2:** `dotnet build $SU` → falla con `CS0246`. Guarda la salida.
- [ ] **Paso 3:** implementa handlers, composer (nombres de clientes y miembros en lote, `SupportDueStates.Evaluate` con el calendario, el `Today` y el umbral `DueSoonBusinessDays` del tenant, leído una vez por petición de `ISupportSettingsRepository`; eventos con nombres resueltos) y rutas (`POST /tickets` → `Results.Created($"…/tickets/{id}", dto)`).
- [ ] **Paso 4:** `dotnet test $SU`; `dotnet test $SI --filter "FullyQualifiedName~SupportTicketsApiTests|FullyQualifiedName~SupportCustomerReferenceLockApiTests" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Paso 5:** commit `feat(support): create manual tickets and read their detail`.

### Task 9: bandeja con filtros y resumen

**Files:**
- Create: `Modules.Support.Application/Tickets/ListTickets.cs`
- Modify: `SupportTicketRepository.cs` (`ListAsync`, `TotalsAsync`), `SupportEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/ListTicketsValidatorTests.cs`; `tests/Modules/Support/Modules.Support.IntegrationTests/SupportInboxApiTests.cs`

**Interfaces:**
- Produces: `public sealed record ListTicketsQuery(Guid TenantId, string? Status, Guid? TypeId, string? Assignee, string? Due, string? Search, int Page, int PageSize) : IQuery<SupportTicketPageDto>` con `DefaultPageSize = 25`, `MaxPageSize = 100`. `Status` admite varios separados por coma; `Assignee` = `me` | `unassigned` | guid; `Due` = `overdue` | `dueSoon`.
- Reglas: `overdue` = `due_on < today` y estado que corre (`New`, `Open`, `WaitingInternal`); `dueSoon` = `due_on` entre `today` y `SupportBusinessDays.Add(calendar, today, settings.DueSoonBusinessDays)` con estado que corre, con el umbral **del tenant** [owner 2026-11-14]; con umbral 0, `dueSoon` no devuelve nada y `Summary.DueSoon` es 0. `Search`: si `SupportTicketNumber.TryParse` da número, filtra por número; si no, `ILIKE` escapado sobre asunto y nombres del requester, más los clientes de `ISupportCustomerLookup.SearchIdsAsync`. Orden: `received_at DESC, id DESC`. El resumen usa `TotalsAsync` con los mismos límites.

- [ ] **Paso 1: pruebas en rojo.** Unitarias: `APageSizeAbove100Fails`, `AnUnknownStatusFails`, `AnUnknownDueFilterFails`. Integración (host propio con `MutableTenantClock`, que se mueve entre altas):
  ```csharp
  [Fact] public async Task TheSummaryCountsTheSixStatusesEvenAtZero()     // 2 New, 1 Open → ByStatus = [New 2, Open 1, WaitingOnCustomer 0, WaitingInternal 0, Resolved 0, Closed 0]
  [Fact] public async Task OverdueShowsOnlyRunningPastDueTickets()
  // alta 2026-11-13 (vence 12-07); reloj a 2026-12-08: Open aparece; otro en WaitingOnCustomer y uno "Soporte técnico" no; Summary.Overdue == 1
  [Fact] public async Task DueSoonShowsTicketsWithinThreeBusinessDays()  // sin ajustes (3): reloj 2026-12-02: aparece; reloj 2026-12-01: no
  [Fact] public async Task TheTenantThresholdOf5MakesFourDaysLeftDueSoon()
  // PUT /settings {5}; reloj 2026-12-01, vence 2026-12-07 (4 días hábiles): aparece con due=dueSoon, Summary.DueSoon 1 y el detalle dice Clock.DueState "DueSoon", BusinessDaysLeft 4
  [Fact] public async Task AThresholdOf0TurnsDueSoonOff()                // PUT {0}; reloj 2026-12-07: due=dueSoon vacío, Summary.DueSoon 0, DueState "OnTrack"
  [Fact] public async Task SearchingByNumberFindsTheTicket()              // "SOP-000002" y "2" → sólo el segundo
  [Fact] public async Task SearchingByCustomerNameFindsTheTicket()
  [Fact] public async Task AssigneeMeAndUnassignedFilter()
  [Fact] public async Task TheRequesterDisplayNameFallsBackToNit()        // requester sólo con NIT 900123456 → "NIT 900123456"
  ```
- [ ] **Paso 2:** `dotnet build $SU` → falla con `CS0246` (`ListTicketsQuery`). Guarda la salida.
- [ ] **Paso 3:** implementa query, validador, repositorio y ruta `GET /tickets`.
- [ ] **Paso 4:** `dotnet test $SU`; `dotnet test $SI --filter "FullyQualifiedName~SupportInboxApiTests" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Paso 5:** commit `feat(support): list tickets with due filters and status summary`.

### Task 10: acciones del ticket (estado, responsable, tipo, cliente)

**Files:**
- Create: `Modules.Support.Application/Tickets/ChangeTicketStatus.cs`, `AssignTicket.cs`, `ChangeTicketType.cs`, `LinkTicketCustomer.cs`
- Modify: `SupportEndpoints.cs`, `QepServiceCollectionExtensions.cs`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportTicketActionsApiTests.cs`

**Interfaces:**
- Produces (todos `ICommand<SupportTicketDetailDto>`, con `ExpectedVersion` de `If-Match`):
  ```csharp
  public sealed record ChangeTicketStatusRequest(string? Status);       → ChangeTicketStatusCommand(Guid TenantId, Guid TicketId, string Status, long ExpectedVersion)
  public sealed record AssignTicketRequest(Guid? AssigneeMemberId);     → AssignTicketCommand(…, Guid? AssigneeMemberId, long ExpectedVersion)
  public sealed record ChangeTicketTypeRequest(Guid? TicketTypeId);     → ChangeTicketTypeCommand(…, Guid TicketTypeId, long ExpectedVersion)
  public sealed record LinkTicketCustomerRequest(Guid? CustomerId);     → LinkTicketCustomerCommand(…, Guid? CustomerId, long ExpectedVersion)
  ```
  Cada handler: autorización (`Manage`) → validador → ticket del tenant (404) → `Version != ExpectedVersion` → `412 concurrency.conflict` → reglas (D14 para cliente, responsable activo con `Manage`, tipo activo) → método del agregado con `SupportClockContext` → auditoría (`TicketStatusChanged`, `TicketAssigned`, `TicketTypeChanged`, `TicketCustomerChanged`) → guardar. Quitar el cliente (`null`) se permite y deja la foto del requester; si no hay requester con nombre, `422 support.ticket.requester_required`.

- [ ] **Paso 1: pruebas en rojo** (host propio con `MutableTenantClock`; alta el 2026-11-13T15:00Z con «Petición general»):
  ```csharp
  [Fact] public async Task AStatusOutsideTheTableIs422()                     // New → WaitingInternal: 422 support.ticket.transition_invalid; nada cambia
  [Fact] public async Task WithoutIfMatchIs428AndWithAnOldVersionIs412()
  [Fact] public async Task PausingAndResumingMovesTheDueDate()               // Open 11-16, WaitingOnCustomer 11-17, Open 11-23 → Clock.DueOn 2026-12-11, PausedBusinessDays 3
  [Fact] public async Task WhilePausedTheClockSaysPaused()                   // WaitingOnCustomer → Clock.DueState "Paused", AllowedTransitions ["Open","WaitingInternal","Resolved","Closed"]
  [Fact] public async Task ReopeningCountsASecondResolutionCycle()           // Resolved → Open → Resolved: ResolutionCount 2
  [Fact] public async Task ClosedIsTerminal()                                // Closed: AllowedTransitions []; cualquier PUT de estado → 422
  [Fact] public async Task ChangingTheTypeRecalculatesFromTheReception()     // → "Petición de documentos o información": DueOn 2026-11-30; → "Soporte técnico": DueOn null, DueState "NoTerm"
  [Fact] public async Task AssigningAndUnassigningWritesEvents()             // AssigneeChanged ×2, ordinales consecutivos, auditoría support.ticket.assigned ×2
  [Fact] public async Task LinkingAMergedCustomerIs422Inactive()             // cliente inactivo → support.ticket.customer_inactive
  [Fact] public async Task EveryActionWritesOneEventAndOneAuditEntry()
  ```
- [ ] **Paso 2:** `dotnet build $SI` → falla con `CS0246` (`ChangeTicketStatusRequest`). Guarda la salida.
- [ ] **Paso 3:** implementa handlers y rutas (`PUT /tickets/{ticketId:guid}/status|assignee|type|customer`, `RequireVersion` como Loans).
- [ ] **Paso 4:** `dotnet test $SI --filter "FullyQualifiedName~SupportTicketActionsApiTests" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Paso 5:** commit `feat(support): change ticket status, assignee, type and customer`.

- [ ] **Fin del Bloque B:** `dotnet test $SU`; `dotnet test $AT`; `dotnet test $BU --filter "FullyQualifiedName~SupportBusinessCalendarTests"`; `dotnet test $SI -- xUnit.MaxParallelThreads=4` (proyecto entero, una vez). Revisión del bloque.

---

## Bloque C — clientes, documentación y cierre (Tareas 11–12, un implementador)

### Task 11: un ticket bloquea el borrado del cliente y la fusión lo mueve

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/BulkDeleteCustomers.cs:31-41`
- Modify: `src/Bootstrapper/CustomerDeletionReferences.cs` (`FindBlockersCoreAsync`, línea 83; el `<summary>` de «Lo que lee por nombre»)
- Modify: `src/Bootstrapper/CustomerMergeReferences.cs` (`MoveCoreAsync`, después de `loans.loans` y **antes** de `shopify.orders`, que sigue siendo la última; el `<summary>`)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/BulkDeleteCustomersTests.cs:64-71`; `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerDeletionAcrossModulesTests.cs` (`Blockers`, `SeedBlockerAsync`); `tests/Bootstrapper/Bootstrapper.UnitTests/CustomerMergeReferencesTests.cs` (`ReferenceColumns`, réplicas); `tests/Modules/Pipeline/Modules.Pipeline.IntegrationTests/CustomerMergeAcrossModulesApiTests.cs` (`TheRealAdapterRunsAgainstTheMigratedSchema`)

**Interfaces:**
- Produces: `CustomerDeletionBlockReasons.SupportTickets = "supportTickets"`; `Ordered = [Quotations, Orders, Loans, ServiceContracts, SupportTickets, BlockedContact]`.

- [ ] **Paso 1: pruebas en rojo.**
  - `BulkDeleteCustomersTests.EachReason`: suma `CustomerDeletionBlockReasons.SupportTickets`.
  - `CustomerDeletionAcrossModulesTests.Blockers`: `{ "supportTicket", [CustomerDeletionBlockReasons.SupportTickets] }` y `{ "loan+supportTicket", [CustomerDeletionBlockReasons.Loans, CustomerDeletionBlockReasons.SupportTickets] }`; en `SeedBlockerAsync`, `case "supportTicket"`: `INSERT INTO support.tickets` con todas las columnas `NOT NULL` y `status = 'Closed'` (D4: cualquier estado bloquea).
  - `CustomerMergeReferencesTests`: `("support.tickets", "customer_id")` en `ReferenceColumns`; en las réplicas, `CREATE SCHEMA support;` y `CREATE TABLE support.tickets (id uuid PRIMARY KEY DEFAULT gen_random_uuid(), tenant_id uuid NOT NULL, customer_id uuid);`.
  - `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`: un ticket del fusionado, uno del espectador y uno de otro tenant con el id del fusionado (`InsertSupportTicketAsync`, SQL crudo con las columnas `NOT NULL`); después de `MoveAsync`, el del fusionado pertenece al que queda, el del espectador no cambia y el del otro tenant sigue con el fusionado.
- [ ] **Paso 2:** `dotnet build $CU` → falla con `CS0117` (`SupportTickets`). Guarda la salida.
- [ ] **Paso 3:** constante y `Ordered`; en `FindBlockersCoreAsync`:
  ```csharp
  // D4 (spec de soporte 2026-11-14): cualquier ticket, abierto o cerrado, bloquea, como cotizaciones y pedidos.
  [CustomerDeletionBlockReasons.SupportTickets] = [.. await database.SqlQuery<Guid>($"""
      SELECT DISTINCT customer_id AS "Value" FROM support.tickets WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})
      """).ToListAsync(cancellationToken)],
  ```
  y en `MoveCoreAsync`, antes de Shopify, `UPDATE support.tickets SET customer_id = {survivorId} WHERE tenant_id = {tenantId} AND customer_id = ANY({ids})` sin tocar `version` (F15). `CountAsync` **no** cambia (precedente de billing: sumar una columna cambiaría la vista previa del frontend). Actualiza los dos `<summary>`.
- [ ] **Paso 4:** `dotnet test $CU --filter "FullyQualifiedName~BulkDeleteCustomersTests"`; `dotnet test $BU --filter "FullyQualifiedName~CustomerMergeReferencesTests"`; `dotnet test $CI --filter "FullyQualifiedName~CustomerDeletionAcrossModulesTests.EachBlockerIsFound" -- xUnit.MaxParallelThreads=4`; `dotnet test $PI --filter "FullyQualifiedName~CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema" -- xUnit.MaxParallelThreads=4` → PASS.
- [ ] **Paso 5:** commit `feat(customers): block customer deletion by support tickets and move them on merge`.

### Task 12: documentación y verificación final

**Files:**
- Modify: `README.md` (sección `### Soporte` después de `### Préstamos`, línea 4627: módulo, esquema, clave `support` sin dependencias, permisos y roles, rutas, siembra perezosa, reloj, migraciones `AddSupportModuleKey` e `InitialSupport`)
- Modify: `CLAUDE.md` del repo: la lista de módulos construidos suma `Support` con `SupportLayerTests`; en «Borrar clientes escribe en muchos esquemas» y en «`CustomerMergeReferences` escribe en cinco esquemas», `support.tickets.customer_id` (bloquea y se mueve); gotcha nuevo: «`support.ticket.view` es una política, no un permiso: exige `read` o `manage` (D5); una ruta de lectura de Soporte con `RequireAuthorization(SupportPermissions.Read)` deja afuera a un rol con sólo `manage`»; gotcha nuevo: «una columna con id de membresía en `support` entra en `SupportUserReferenceProbe`».

- [ ] **Paso 1:** escribe las dos secciones (tuteando).
- [ ] **Paso 2:** suite antes de mergear. El cambio toca `Program.cs` y Bootstrapper, así que corre la solución entera **proyecto por proyecto, en serie**, cada uno con `-- xUnit.MaxParallelThreads=4`, después de confirmar con `Get-Process testhost, dotnet -ErrorAction SilentlyContinue` que no hay otra corrida. Anota fallas que ya fallaban en `develop` (compara con su baseline) aparte de las nuevas.
- [ ] **Paso 3:** commit `docs(support): document the support module core`.

- [ ] **Fin del Bloque C:** revisión del bloque y la revisión fuerte de toda la rama.

---

## Lo que esta fase deja preparado para las siguientes

- **Fase 2 (Mensajería):** suma `conversation_id`, `follow_thread`, `follow_off_at`, la tabla de vínculos y `SupportTicketIntake.Conversation`; reanudar por un entrante usa `ChangeStatus(Open)` tal cual; el canal `Thread` deja de ser `reply_channel_unavailable` cuando hay conversación.
- **Fase 3 (formulario):** usa `SupportTicketIntake.PublicForm`, `TicketRequester` (más la ciudad), el anónimo (requester y cliente nulos: el agregado ya los admite con otra fábrica) e `IsPublished` de los tipos.
- **Fase 4 (respuesta formal, encuesta, prórroga):** `RequiresFormalResponse` y `TermIsLegal` ya viajan en la foto; la encuesta es idempotente por `(ticket, ResolutionCount)`; la prórroga suma un campo de días al cálculo `term + paused + extension`; la configuración vive en `support.tenant_settings`.

## Decisiones pendientes

- ~~**P2 (owner con el abogado).** Regla del día parcial de una pausa (D3)~~. **Cerrada [owner 2026-11-15]:** no hay pausa parcial; se descuentan sólo los días hábiles completos detenidos, como ya hace D3 (`SupportTicket.ChangeStatus` con `SupportBusinessDays.CountStrictlyBetween`).
- §13.1 (inicio y fin del reloj, tope de la prórroga) y §13.4 (lunes a viernes, no el horario) los decidió el owner el 2026-11-15; ver el spec. Sigue abierto, del spec, el aplicar términos a tenants privados (§4).
- **R9 cerrada [owner 2026-11-15]:** un ticket `Resolved` o `Closed` no se reasigna, no cambia de tipo ni de cliente: 422 `support.ticket.not_editable`, guardado en el agregado (`SupportTicket.Assign`, `ChangeType`, `LinkCustomer`). Reabrir sigue la tabla de transiciones; la fusión de clientes sigue moviendo tickets en cualquier estado por SQL.

## Spec discrepancies (spec contra código)

1. **Dos fuentes de festivos (ya no aplica desde el 2026-11-15, spec D2d: sólo `ColombianHolidays` según el país del tenant).** El spec decidía festivos por `Tenant.Country`, pero `BusinessHoursSchedule` ya tiene su propio `HolidayCountry` y sus `DateException`s (`Modules.Messaging.Domain/BusinessHoursSchedule.cs:36-38`, `EffectiveRanges`). El owner decidió el 2026-11-14 usar los del horario, con `Tenant.Country` de respaldo, y el spec §7.2 se corrigió. Queda un borde: un horario con `HolidayCountry` vacío en un tenant `CO` no cierra festivos en la bandeja y sí en Soporte, por el respaldo que pidió el owner.
2. **Visibilidad «read o manage».** Ninguna política del repo acepta dos permisos: `AddPermissionRequirement` exige uno (`QepServiceCollectionExtensions.cs:2999`). Hace falta la política `support.ticket.view` (D5).
3. **«Asesor» es `advisor`.** Los roles de sistema se llaman `admin` y `advisor` (`QepServiceCollectionExtensions.cs:1699` y `1835`).
4. **«Siembra al prender el módulo».** Tenancy no publica nada al activar un módulo; el plan siembra en el primer acceso (D1), que el spec permite.
5. **`TenantModuleKeysTests.AiIsLastDependsOnMessagingAndIsOffForNewTenants`** fija que `ai` es la última clave; agregar `support` la rompe y la Tarea 1 la renombra.
6. **Referencias de línea.** Las `PermissionDefinition` de Loans van de la 2547 a la **2559** (el spec dice 2556). Lo demás que cita §3 coincide.

## Autorrevisión

- **Cobertura de §12 fase 1:** módulo (T2), dominio y estados (T3, T4), reloj (T3, T4, T6), permisos (T2), entitlement y migración de Tenancy (T1), bandeja y detalle (T8, T9), alta manual (T8), auditoría (T5, T7, T8, T10), bloqueo del borrado y fusión (T11), `SupportLayerTests` (T2), tipos sembrados (T5, T7). Pantallas: fuera, con su contrato arriba.
- **Tipos:** `SupportClockContext`, `SupportTicketStatuses`, `SupportDueStates.Evaluate` (con el umbral del tenant), `SupportTenantSettings`, `ISupportSettingsRepository.GetAsync`, `ISupportProvisioner.EnsureAsync`, `ISupportTicketNumberGenerator.NextAsync` y `SupportPolicies.TicketView` se usan con la misma firma en las tareas que los consumen.
- **Review Focus:** cada punto tiene su prueba en la tarea dueña (T3, T4, T5, T6, T8).
- **Valores del spec en pruebas:** 15/10/30 días, la tabla de transiciones, los ocho tipos, los tres permisos con admin y asesor, «cualquier ticket bloquea» (fila `Closed`) y «la fusión mueve».
