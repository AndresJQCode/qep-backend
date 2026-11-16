# Prioridad de tickets de Soporte — backend

Fecha: 2026-11-15. Repo: `qep-backend`. Rama: `feature/support-priority`.
Spec hermano del frontend: `qep-frontend/docs/superpowers/specs/2026-11-15-support-ticket-priority-design.md`.

## 1. Intención

Hoy un ticket no dice qué tan urgente es: la bandeja sólo ordena por fecha de recibido y el asesor no tiene cómo marcar que un
caso tiene que atenderse antes que otro. El owner quiere una **prioridad** por ticket, elegida al crear y cambiable después, que
sirva para **filtrar y ordenar** la bandeja y que quede en la traza.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Campo `priority` en el ticket, con cuatro niveles fijos: `Low`, `Medium`, `High`, `Urgent` (en pantalla Baja, Media, Alta,
  Urgente).
- Elegirla al crear a mano y desde la conversación; Media si no viene.
- Cambiarla con `PUT /tickets/{id}/priority`, salvo con el ticket cerrado.
- Evento `PriorityChanged` en la traza.
- Filtro `priority` y orden `sort=received|priority` en `GET /tickets`.
- `priority` en la fila de la lista y en el detalle.

**No objetivos**

- La prioridad **no mueve el plazo**: no cambia `due_on`, el término ni el reloj de días hábiles (owner, 2026-11-15).
- Sin catálogo por tenant: los niveles son fijos (owner).
- Sin conteo por prioridad en el `summary` de la bandeja.
- El formulario público no la ofrece: el cliente no elige la urgencia.

**Anti-objetivos**

- Que el orden por prioridad se arme en el navegador: el backend ordena y pagina.
- Que cambiar la prioridad pise el cambio de otro asesor sin avisar (`If-Match` siempre).
- Que un ticket cerrado acepte un cambio de prioridad.

## 3. Restricciones verificadas en el código

- El molde es «cambiar tipo»: `Tickets/ChangeTicketType.cs` (validador, `SupportAuthorization.EnsureAsync` con
  `SupportPermissions.Manage`, `SupportTicketActions.LoadAsync` con `ExpectedVersion` por `If-Match`, `SaveIfChangedAsync` con
  acción de auditoría y campos cambiados, `realtime.TicketChangedAsync` sólo si subió la versión, respuesta `SupportTicketDetailDto`).
- Rutas de acción: `MapTicketAction<TRequest>(group, "<accion>", handler)` en `Modules.Support.Api/SupportEndpoints.cs:315-318`.
- Los enums del ticket se guardan **por nombre** (`HasConversion<string>().HasMaxLength(24)`, `SupportDbContext.cs:405-407`).
- Eventos: `SupportTicketEventKind` se persiste por nombre; un valor nuevo no rompe filas viejas.
- Ticket cerrado: el dominio ya tiene `EnsureNotClosed()` (`SupportTicket.cs:529`) que lanza `SupportErrorCodes.NotEditable`.
- La lista ordena hoy por `ReceivedAt` descendente (`SupportTicketRepository.cs:32`; doc de `ListTicketsQuery`).
- Altas: `CreateManual`, `CreateFromConversation` y `CreateFromPublicForm` en `SupportTicket.cs:123-173`; requests
  `CreateTicketRequest` (`Tickets/CreateTicket.cs:25`) y `CreateTicketFromConversationRequest`
  (`Conversations/CreateTicketFromConversation.cs:12`).
- **Otra sesión** trabaja Soporte en `qep-backend-worktrees/support-phase-4a` y también toca el ticket y las migraciones. El que
  mergee segundo regenera su migración sobre el snapshot del otro.

## 4. Enfoques considerados

| Enfoque | A favor | En contra |
| --- | --- | --- |
| **1. Enum en el ticket + `PUT /priority` (elegido)** | Calca «cambiar tipo»; auditoría, tiempo real y traza ya resueltos | Una migración |
| 2. Catálogo de prioridades por tenant, como los tipos | Configurable | El owner fijó cuatro niveles; más tablas y pantallas |
| 3. Endpoint genérico de edición del ticket | Un solo endpoint | Rompe «un `PUT` por acción» del módulo y mezcla permisos |

El owner eligió el selector «Ordenar por» en la bandeja (orden por recibido por defecto, por prioridad a pedido) frente a ordenar
siempre por prioridad.

## 5. Diseño

### 5.1 Dominio

- `SupportTicketPriority { Low, Medium, High, Urgent }` en `Modules.Support.Domain`.
- `SupportTicket.Priority`, `Medium` por defecto.
- Las fábricas `CreateManual` y `CreateFromConversation` reciben la prioridad; `CreateFromPublicForm` siempre `Medium`.
- `ChangePriority(priority, actorMemberId, now)`: con el ticket `Closed` lanza `SupportErrorCodes.NotEditable` (vía
  `EnsureNotClosed` u otra guarda con el mismo código); con la misma prioridad no hace nada (no sube versión, no escribe evento);
  si cambia, escribe `PriorityChanged` con prioridad anterior y nueva y sube versión. No toca el reloj.
- Se puede cambiar en cualquier estado salvo `Closed` (owner, 2026-11-15). `Resolved` sí.

### 5.2 Traza

- `SupportTicketEventKind.PriorityChanged`.
- El evento lleva la prioridad anterior y la nueva por nombre. El DTO del evento suma `fromPriority` y `toPriority` (null en los
  demás eventos), igual que `fromTypeName`/`toTypeName`.

### 5.3 HTTP

| Ruta | Cambio |
| --- | --- |
| `POST /tickets` | `priority` opcional (`Low`·`Medium`·`High`·`Urgent`); ausente o null = `Medium`; otro valor = 422 de validación |
| `POST /conversations/{id}/tickets` | Igual |
| `PUT /tickets/{id}/priority` | Cuerpo `{ "priority": "High" }`, `If-Match` obligatorio, `support.ticket.manage`; responde el detalle. 412 si la versión no coincide; 422 `NotEditable` si está cerrado; 422 de validación si falta o es inválida |
| `GET /tickets` | `priority` opcional, uno o varios por nombre separados por coma (como `status`); `sort` opcional `received` (default) o `priority` |
| Lista y detalle | `priority` por nombre en `SupportTicketListItemDto` y `SupportTicketDetailDto` |

Orden con `sort=priority`: `Urgent`, `High`, `Medium`, `Low`; dentro de cada nivel, `ReceivedAt` descendente; desempate por id para
que la paginación sea estable. `sort` inválido = 422 de validación.

### 5.4 Persistencia

- Columna `priority` en la tabla de tickets, texto por nombre (`HasMaxLength(24)` como las demás), `NOT NULL` con default
  `'Medium'` para las filas existentes.
- Índice para el orden y el filtro: queda a criterio del builder (ver §7).

### 5.5 Auditoría y tiempo real

- Acción de auditoría nueva (p. ej. `SupportAuditActions.TicketPriorityChanged`) con campo cambiado `priority`.
- `realtime.TicketChangedAsync` cuando sube la versión, como el tipo.

## 6. Pruebas

TDD, RED antes que GREEN. Unitarias de dominio (cambio, mismo valor sin evento, cerrado rechaza, públicas en Media) y de
validación; integración **filtrada por nombre** de las rutas nuevas (`PUT /priority` feliz, 412, cerrado 422; `GET /tickets` con
`priority` y `sort=priority`; altas con y sin prioridad). **La suite completa de integración no se corre sin pedido del owner**
(decisión 2026-11-15); antes de mergear se le avisa que falta.

## 7. Decisiones tomadas al revisar el spec

- **Mismo valor:** `PUT /priority` con la prioridad que ya tiene responde 200 con el detalle sin cambios, sin subir versión ni
  escribir evento.
- **Estados terminales:** sólo `Closed` (los estados son `New`, `Open`, `WaitingOnCustomer`, `WaitingInternal`, `Resolved`,
  `Closed`).
- **Compatibilidad:** un frontend viejo no manda `priority` al crear (queda `Medium`); un backend viejo ignora `sort` y `priority`
  en la query (`ListTicketsAsync` enlaza parámetros por nombre) y el campo `priority` del cuerpo (no hay
  `UnmappedMemberHandling`).
- **Migración:** se escribe en esta rama ya. Si `feature/support-phase-4a` llega antes a `develop`, la migración de prioridad se
  regenera sobre su snapshot al mergear.

## 8. Lo que queda al builder

- Cómo ordenar por un enum guardado como texto: `CASE` traducible por EF, o un rango numérico derivado. Que pagine en la base.
- Si hace falta índice (`tenant_id, priority, received_at`) según el plan de la consulta.
- Nombre exacto de la migración y de la acción de auditoría.
- Si la guarda de cerrado reusa `EnsureNotClosed` (cambiando su mensaje) o una propia con el mismo código.
