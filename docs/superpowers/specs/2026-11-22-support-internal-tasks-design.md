# Soporte: tareas internas entre miembros — diseño (backend)

Fecha: 2026-11-22. Estado: **por aprobar**. Spec hermano en `qep-frontend`
(`docs/superpowers/specs/2026-11-22-support-internal-tasks-design.md`). El backend va primero.

## 1. Intención

Un miembro del tenant le crea a otro miembro un ticket que es una **tarea**: «Manuel, envía un correo
a tal contacto el viernes». Hoy eso no cabe en Soporte porque el ticket nació para atender a un
externo (PQRSF y soporte técnico).

Se reutiliza el ticket y no se crea un módulo de tareas: así se heredan responsable, notas,
adjuntos, historial, tiempo real, permisos y auditoría. Es el patrón de los «back-office tickets» de
Intercom. Decisión del owner, 2026-11-22: opción A (ticket interno), sobre un módulo aparte.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**
- **Que cualquier miembro del tenant use tareas** (decisión del owner, 2026-11-22: se hace bien desde
  el principio, antes de que nadie lo use), sin darle acceso a los tickets de clientes.
- Crear un ticket interno con asunto, descripción, **responsable obligatorio**, prioridad y **fecha
  límite opcional**, sin cliente, solicitante ni canal de respuesta.
- Que el responsable lo vea en la bandeja y que el creador lo siga («creados por mí»).
- Que los internos no ensucien lo externo: bandeja, resumen, reportes, formulario público y portal.

**No objetivos (v1)**
- Avisos por WhatsApp o correo al asignar: **sí van, pero en el spec hermano** de avisos (§6, D-3).
  Aquí sólo la bandeja, el tiempo real y el evento que los dispara.
- Subtareas, checklists, recurrencia, tableros kanban.
- Ver tareas ajenas sin ser parte (ver §5.6: sólo quien lee o gestiona Soporte las ve todas).

**Anti-objetivos**
- Que usar tareas abra, aunque sea por una rendija, los casos de clientes (PQRSF, datos del solicitante).
- Que un ticket interno pueda terminar visible o respondible por un externo.
- Que cambiar el tipo convierta un ticket externo en interno, o al revés.
- Que las métricas de PQRSF cambien por cuenta de las tareas.

## 3. Restricciones verificadas en el código

- `SupportTicket.Start` rechaza un manual sin cliente ni solicitante con nombre
  (`requester_required`, plan D6). El anónimo es la única salida y fuerza canal `Portal`.
- `CreateTicketValidator` exige `ManualMedium` y `ReplyChannel`, y `Portal` sólo con anónimo.
- El vencimiento (`DueOn`) sale del término del **tipo**, en días hábiles
  (`DueOnFor`, `TermBusinessDays`); no hay fecha libre. `DueOn` ya es un `DateOnly?` persistido.
- Responsables candidatos = membresías activas con `support.ticket.manage`
  (`ListSupportAssigneesHandler`); `Assign` se rechaza en `Resolved` y `Closed`.
- Todo camino a `Resolved` encola una encuesta de satisfacción (H15).
- Hoy el acceso es **todo o nada**: `SupportPolicies.TicketView` (leer o gestionar) ve todos los
  tickets del tenant, y spec 2026-11-14 §10 (owner) dice que no hay «sólo los suyos». Un permiso por
  participante es una excepción nueva y acotada a los internos: **quien lee o gestiona sigue
  viendo todo**, sin cambio.
- Escribir está en `support.ticket.manage` en los endpoints (`RequireAuthorization(Manage)`) **y**
  otra vez en cada handler (`SupportAuthorization.EnsureAsync`); la validez del responsable sale de
  `ISupportMemberDirectory.ListManagersAsync`; el tiempo real lo decide `SupportRealtimeAuthorizer`
  contra la base, y el tópico `Inbox` es único para todos los suscriptores.
- Los permisos son `PermissionDefinition` y los roles de fábrica son `admin`, `advisor`, `billing` y
  `cashier` (`QepServiceCollectionExtensions.cs`); con `RequiredModules: [Support]` el permiso
  desaparece si el tenant no tiene el módulo. **Un tenant sin el módulo Soporte no tiene tareas.**
- `ChangeStatus` ya impide volver a `New`; `Closed` es terminal.
- Los tipos se siembran por tenant (`SupportTicketTypeDefaults.Seed`) y un tipo usado no se borra.
- `SupportTicketIntake` y `SupportReplyChannel` se persisten por nombre con CHECK en la base.

## 4. Enfoques considerados

| | Enfoque | Costo | Veredicto |
|---|---|---|---|
| A | Ticket interno: tipo marcado `IsInternal` y ticket con la misma marca | medio | **elegido** |
| B | Módulo de tareas aparte | alto: duplica responsable, notas, historial, avisos | descartado: el owner pidió usar Soporte |
| C | Híbrido: tareas aparte ligadas a un ticket | el más alto | descartado: sobra para el pedido |

## 5. Diseño

### 5.1 Modelo

- `SupportTicketType.IsInternal` (bool). Hay **un** tipo interno por tenant, «Tarea interna»,
  sembrado con los demás y con *backfill* para los tenants existentes.
  Sin término, no legal, sin respuesta formal, **no publicable**, no desactivable, no borrable y no
  renombrable. `Update` con esos cambios responde 422.
- `SupportTicket.IsInternal` (bool), copiado del tipo al crear, igual que el término. Es inmutable.
- `SupportTicket.CreatedByMemberId` (`Guid?`): hoy el creador sólo está en el evento `Created`, y
  el filtro «creados por mí» necesita columna. Null en los del formulario público. *Backfill* desde
  el evento `Created` para lo ya existente.
- Un interno **no** lleva cliente, solicitante ni medio manual. El canal de respuesta no aplica:
  valor `SupportReplyChannel.None`, agregado **al final** del enum (se persiste por nombre) y al CHECK.
  *Decisión del implementador:* si prefiere una columna anulable en vez de un valor, vale, mientras
  ningún camino de respuesta pueda ejecutarse sobre un interno.
- `DueOn` de un interno es la fecha que eligió el creador, sin término ni días hábiles. Es la única
  entrada que escribe `DueOn` directo; el resto sigue derivándolo del término.

### 5.2 Reglas

1. **Crear** con un endpoint propio, **`POST /tasks`** (permiso `support.task.use` o
   `support.ticket.manage`, §5.6). El cuerpo es sólo `subject`, `description`, `assigneeMemberId`,
   `priority?` y `dueOn?`: el servidor resuelve el tipo interno del tenant, así el cliente nunca
   necesita conocer su id ni tener permiso de leer tipos. Por eso no hay nada que «ocultar» que
   pueda mandarse: cliente, solicitante, canal y medio no existen en el cuerpo.
   - `assigneeMemberId` obligatorio: miembro activo del tenant que **use tareas** (§5.6;
     `assignee_invalid` si no). Puede ser el propio creador.
   - `dueOn` opcional; no puede ser anterior a hoy en la zona del tenant
     (`support.ticket.internal_due_in_past`).
   - `POST /tickets` con el tipo interno responde 422 `support.ticket.internal_type_locked`, y con
     `dueOn` en un ticket que no es interno, 422. Sale por un solo camino: `POST /tasks`.
2. **Estados:** se reutiliza la máquina. En un interno, `WaitingOnCustomer` no existe: la transición
   se rechaza con `transition_invalid` y no se ofrece. Quedan `New`, `Open`, `WaitingInternal`
   (se muestra como «En espera»), `Resolved`, `Closed`.
3. **Cambiar tipo:** un interno no cambia de tipo y a un externo no se le puede dar el tipo interno
   (`support.ticket.internal_type_locked`, 422). El selector de tipo de las demás pantallas
   no lista el interno.
4. **Responder al solicitante** (replies, plantillas, formal, PIN, portal): sobre un interno todo
   responde `support.ticket.internal_no_reply` (422). Las **notas internas y sus adjuntos** sí
   funcionan: son la conversación entre creador y responsable.
5. **Vínculos:** no se vincula cliente, conversación ni hilo a un interno. El alta desde una
   conversación (`CreateFromConversation`) rechaza el tipo interno.
6. **Prórroga** (`Extend`): no aplica (sin término). Cambiar `DueOn` de un interno se hace con un
   endpoint propio, `PUT /tickets/{id}/due-date`, con `If-Match`, sólo creador o responsable o
   quien tenga `manage`, y no en `Resolved` ni `Closed`. Evento nuevo `DueDateChanged`.
7. **Encuesta:** no se encola al resolver un interno.
8. **Autocierre:** se mantiene igual que en un externo (reabrir dentro de la ventana). *Mi llamada:*
   evita una rama más en el worker.
9. **Responsable fijo en `Resolved` y `Closed`**, como hoy.

### 5.3 Lectura

- `GET /tickets` recibe `kind=external|internal|all`. **Por defecto `external`**, para no cambiar
  la bandeja actual. `assignee=me` ya existe; se agrega `createdBy=me`. Quien **sólo** tiene
  `support.task.use` y pide `external` o `all` recibe 403 `authorization.denied`; con `internal`
  (o sin `kind`, que para él equivale a `internal`) recibe sólo sus tareas (§5.6).
- `GET /tasks/assignees`: los candidatos a responsable de una tarea (§5.6). `GET /assignees` no
  cambia.
- El resumen de la bandeja (tarjetas) se calcula por `kind`, igual que la lista.
- Los DTO de ticket traen `isInternal`, `createdBy` (`{ memberId, displayName }` o `null`) y
  `dueOn`. En un interno, `customer`, `requester` y `replyChannel` llegan `null`.
- Los tipos que lista la API de tipos traen `isInternal`.
- **Reportes de soporte, formulario público, consulta por radicado y portal excluyen internos.** El
  tipo interno nunca sale en las opciones públicas.
- El número (`SOP-000123`) sale de la misma secuencia. *Mi llamada:* una sola numeración evita
  colisiones y es lo que ya hay.

### 5.4 Tiempo real y auditoría

- El tópico `Inbox` avisa a todos sus suscriptores de cualquier cambio, así que **un miembro que
  sólo usa tareas no puede suscribirse a él**: sabría cuándo hay actividad en tickets de clientes.
  Se agrega el tópico **`support:tasks:<memberId>`**: sólo el propio miembro se suscribe, y recibe
  los eventos de las tareas donde es creador o responsable (avisa por id, como el resto: el SPA
  invalida y relee). El tópico de un ticket concreto sigue la regla de §5.6. Quien lee o gestiona
  Soporte sigue oyendo `Inbox`, que ahora también lleva los eventos de los internos.
- Auditoría: `TicketCreated`, `TicketAssigned` y los demás existentes; se agrega la acción de
  cambio de fecha límite.

### 5.6 Permisos y visibilidad (decisión del owner, 2026-11-22)

Nuevo permiso **`support.task.use`** («Usar tareas internas»: crear tareas y trabajar las propias),
grupo «Soporte», riesgo `low`, `RequiredModules: [Support]`, forma `support.task` / «Tareas
internas» / `use`. Se asigna de fábrica a **los cuatro roles** (`admin`, `advisor`, `billing`,
`cashier`) porque la meta es que cualquier miembro lo use; un rol personalizado lo recibe desde el
editor de roles. Un *backfill* o la propia definición de los roles de fábrica lo cubre: no hay
datos que migrar, pero el implementador verifica cómo se materializan los roles ya creados.

Quién puede qué sobre un ticket **T**, para un miembro **M**:

| Ticket | M tiene `read` | M tiene `manage` | M tiene sólo `task.use` |
|---|---|---|---|
| externo | ve todo | ve y actúa | **no existe**: 404 `support.ticket.not_found`, nunca 403 |
| interno | ve todo | ve y actúa | ve y actúa **si es creador o responsable**; si no, 404 |

- «Actúa» sobre un interno: notas y adjuntos, cambiar estado, prioridad, fecha límite y
  responsable. Es una tarea entre dos personas: los dos pueden hacerlo. Si el responsable la
  pasa a otro y no es el creador, pierde el acceso al instante; es traspaso, no un error.
- `read` sin `manage` ve todas las tareas pero **no actúa** (consistente con hoy: leer no escribe).
- 404 y no 403 para lo que no es suyo: un 403 confirmaría que el ticket existe.
- **Una sola puerta.** Hoy cada handler repite `EnsureAsync(…, Manage)`. Se agrega un único
  servicio de acceso por ticket (por ejemplo `SupportTicketScope`: `CanViewAsync` /
  `EnsureCanActAsync(ticket)`) que aplica la tabla, y **todos** los caminos que tocan un ticket
  pasan por él: detalle, historial, notas y sus adjuntos, adjuntos del ticket, estado, prioridad,
  responsable, fecha límite, lista, resumen, tiempo real. Los endpoints cambian de
  `RequireAuthorization(Manage)` a una política nueva `SupportPolicies.TicketAct` (manage **o**
  `task.use`), y la decisión fina la toma el servicio. Los endpoints que sólo tienen sentido para
  externos (responder, PIN, formal, vincular cliente, prórroga, hilo, no clasificados, ajustes)
  **siguen exigiendo `manage`**.
- **Candidatos a responsable de una tarea** (`GET /tasks/assignees`): membresías activas que tengan
  `support.task.use` **o** `support.ticket.manage`. Requiere un método nuevo en
  `ISupportMemberDirectory`; el actual (`ListManagersAsync`) queda para los externos.
- **Lista y resumen** de quien sólo tiene `task.use`: el filtro por participante se aplica **en la
  consulta**, nunca después de traer filas, y el resumen se calcula sobre lo mismo (si no, los
  conteos revelarían tareas ajenas).
- **Tiempo real:** `SupportRealtimeAuthorizer` hoy decide sólo por permiso; para un ticket
  concreto ahora también aplica la tabla (carga el ticket, que ya hace para el tenant) y
  `support:tasks:<memberId>` lo valida contra el propio miembro.
- La presencia («está escribiendo») sigue exigiendo `manage`, salvo en un interno donde M es
  participante.
- **Sin el módulo Soporte** el permiso se quita solo (`ModuleEntitlementMask`) y todo responde
  como hoy.

### 5.7 Migración

Una migración: columnas `is_internal` (tipo y ticket), `created_by_member_id`, valor `None` del
canal en el CHECK, *backfill* de `created_by_member_id` y siembra del tipo interno en los tenants
existentes. Reversible. Sin datos a convertir: ningún ticket actual es interno.

## 6. Decisiones pendientes del owner

**Resueltas (owner, 2026-11-22)**
- **D-1. Quién puede crear y recibir:** cualquier miembro, con permiso propio
  `support.task.use` y visibilidad por participante (§5.6). Se hace bien desde el principio porque
  nadie lo usa todavía.

- **D-2. Privacidad entre quienes leen Soporte:** quien tiene `support.ticket.read` ve **todas** las
  tareas, aunque no sea parte. Es lo más barato y es la regla de hoy. Si algún día deben existir
  tareas privadas (por ejemplo de RR. HH.), se agrega una marca «privada» por tarea en otro spec.
- **D-3. Avisos:** sí. Al asignar una tarea a otro miembro se le avisa **por WhatsApp si se puede y
  si no, por correo**. Va en un **spec hermano**
  (`2026-11-22-support-task-notices-design.md`), porque exige datos que no existen (el celular del
  miembro) y la campana de otra sesión. La escalera que pidió el owner es: campana y hub, y si no se
  lee en 30 minutos, WhatsApp, y si no se puede, correo. **Este spec sólo deja el enganche:**
  asignar una tarea a otro miembro (al crearla o al reasignarla; no a uno mismo) registra el evento
  `support.ticket.task-assigned.v1` por el outbox transaccional, con `ticketId`, `tenantId`,
  `ticketNumber`, `assigneeMemberId` y `assignedByMemberId`. **Soporte aún no tiene outbox:** lo
  mapea el spec de la campana (§5.6); si este trabajo llega antes, el implementador mapea
  `platform.outbox_messages` en `SupportDbContext` igual que ese spec, sin duplicarlo. Sin avisos,
  la tarea igual aparece en la lista y en tiempo real del responsable.
- **D-4. Roles de fábrica:** `support.task.use` va a `admin`, `advisor`, `billing` y `cashier`.

## 7. Pruebas (TDD, RED antes que GREEN)

**Acceso (lo más importante, va primero):** un miembro con sólo `task.use` no ve un ticket externo
por ningún camino (detalle, historial, notas, adjuntos, lista, resumen, tiempo real) y recibe 404;
ve una tarea ajena como 404; ve y trabaja las suyas; `read` ve todas sin actuar; `manage` ve y
actúa en todo; el traspaso quita el acceso al responsable anterior; el tópico `Inbox` le es
negado y `support:tasks:<otro miembro>` también; el resumen no cuenta tareas ajenas. Prueba de
arquitectura o de cobertura: ningún handler de ticket llama `EnsureAsync(…, Manage)` sin pasar por
el servicio de acceso, salvo los exclusivos de externos.

Dominio: crear interno sin solicitante; rechazo con cliente o solicitante; `IsInternal` inmutable;
transición a `WaitingOnCustomer` rechazada; sin encuesta al resolver; `DueOn` libre y fecha pasada.
Aplicación: alta con y sin responsable, responsable inválido, `kind` por defecto `external`,
`createdBy=me`, `internal_no_reply`, `internal_type_locked`, `PUT due-date`. Arquitectura:
`ArchitectureTests` intactas. Integración (Testcontainers): migración con *backfill*, y que
reportes y formulario público no vean internos. La integración corre filtrada por nombre mientras se
itera, y la clase completa una vez por bloque (reglas del workspace).

## 8. Lo que queda al implementador

Nombres finales de columnas y DTO, si el canal `None` es valor o columna anulable, la forma
exacta del validador de segunda etapa, y el orden de los bloques (propuesta: 1 dominio y
migración, 2 permiso, servicio de acceso por ticket y su prueba de cobertura, 3 alta (`POST
/tasks`) y reglas, 4 lectura, filtros y tiempo real, 5 reportes y exclusiones). El bloque 2 va
antes de cualquier endpoint nuevo: sin la puerta única no se abre nada.
