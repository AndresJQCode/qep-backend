# Soporte — hub global en tiempo real, presencia por ticket y notas internas · diseño

> Estado: borrador para revisión del owner (2026-11-15). Complementa la fase 2 «Mensajería» del módulo
> (`2026-11-14-support-tickets-pqrs-design.md` §12 y plan `2026-11-15-support-phase-2-messaging-backend.md`), ya en
> `develop`. Lo pide la fase 2 de pantallas (`qep-frontend`, `2026-11-15-soporte-fase-2-pantallas-design.md`).

## 1. Intención

Que dos asesores que trabajan el mismo ticket se enteren al instante de que el otro está respondiendo, que el detalle de
un ticket se refresque solo cuando algo lo cambia (otro asesor, el worker que vincula un mensaje del cliente), y que el
asesor deje notas internas en el expediente.

Por qué ahora: la fase 1 de pantallas sólo tiene concurrencia optimista (`If-Match`, 412). Basta para acciones de un
campo, pero con «Responder al solicitante» un choque pierde texto escrito. El owner pidió presencia en la fase 2 (spec de
pantallas fase 1, §10) y notas internas (2026-11-15), que el spec del módulo sólo nombraba para sus adjuntos (§8.3).

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Un **hub global** de SignalR (`/hubs/app`), dueño de nadie: Bootstrapper lo aloja y cada módulo se suscribe por
  tópicos con su propia autorización. Soporte es su primer consumidor.
- Aviso `supportTicketChanged` por id cuando un ticket cambia (cualquier escritura de Soporte y el worker de seguimiento).
- **Presencia «está respondiendo»** por ticket, compartida entre réplicas.
- **Notas internas**: crear (texto y hasta 5 adjuntos por Storage) y verlas en el detalle.

**No objetivos**

- Migrar Mensajería al hub global. Sigue en `MessagingHub` y migra en un trabajo aparte [owner, 2026-11-15].
- Editar o borrar notas [owner, 2026-11-15]: el expediente es prueba; una corrección es otra nota.
- Presencia de «está mirando» (sólo «está escribiendo»): mirar no pisa nada.
- Bloqueo de la respuesta. El owner eligió aviso, no bloqueo.

**Anti-objetivos**

- Que el hub transporte datos: avisa por ids y la pantalla relee por HTTP con su autorización (decisión 2 del spec de
  Mensajería en tiempo real, `IMessagingNotifier.cs:4`).
- Que un miembro sin `support.ticket.read` o con el módulo `support` apagado reciba avisos o presencia de Soporte.
- Que una pestaña cerrada deje un «está respondiendo» fantasma más de ~45 s.
- Que una nota llegue al solicitante, a la consulta pública por radicado o a un evento de integración.
- Que Support referencie `Modules.Messaging.*` para esto.

## 3. Restricciones verificadas en el código

- `MessagingHub` exige `messaging.conversation.read` en `MapHub` (`MessagingRealtimeExtensions.cs:28`): un rol sólo de
  Soporte no conecta. Por eso no se reutiliza.
- Mensajería reparte eventos entre pods con `NOTIFY/LISTEN` de Postgres (`IMessagingNotifier.cs:8`), los manda
  **después del commit** y un fallo no deshace nada.
- La SPA conecta sin `negotiate` (`skipNegotiation`, varias réplicas sin afinidad), el tenant viaja en la query
  `tenantId` y la cookie la pone el navegador (CLAUDE.md del frontend, gotcha del hub).
- Presencia de tenant en Valkey: `ValkeyMessagingPresence`, `PresenceHeartbeatWorker` (área `Valkey:Areas:Presence`),
  sorted set por tenant con latido de 30 s.
- Storage ya tiene sesión de subida, `complete` y `download-url` por archivo (`/api/v1/tenants/{t}/files/…`), usados por
  imágenes de producto y comprobantes de pedidos.
- La traza del ticket (`SupportTicketEventDto`) y su versión optimista: el worker sólo sube la versión cuando cambia el
  estado o escribe un evento (plan fase 2, Global Constraints).

## 4. Enfoques considerados

| Enfoque | Trade-off | Decisión |
| --- | --- | --- |
| Latido HTTP de presencia cada 15 s | No depende de hub, anda en previews; pero el aviso llega hasta 15 s tarde, justo la ventana del choque | Descartado [owner] |
| Reusar `MessagingHub` | Rol sólo de Soporte no conecta; acopla módulos | Descartado [owner] |
| Hub propio de Soporte | Aislado; un socket más por cada módulo con tiempo real | Descartado [owner] |
| **Hub global con tópicos por módulo, Soporte primero** | Un socket por pestaña a futuro; contrato común en BuildingBlocks; mientras Mensajería no migre conviven dos sockets | **Elegido** [owner, 2026-11-15] |

## 5. Diseño

### 5.1 Hub global

- `AppHub` en `src/Bootstrapper/Realtime/` (Bootstrapper suma la referencia al framework de ASP.NET Core si le falta),
  mapeado en `/hubs/app` con `RequireAuthorization()` (sesión, sin permiso de módulo). Al conectar resuelve tenant y
  miembro **exactamente como `MessagingHub.cs:32-46`**: tenant del claim `tenant_id`, miembro por
  `FindActiveMembershipIdAsync(subject, tenant)`; sin membresía activa aborta. El tenant de un tópico es siempre el de la
  conexión, nunca uno que mande el cliente.
- Contrato en BuildingBlocks (`BuildingBlocks.Realtime`):
  - `IRealtimeTopicAuthorizer { string Prefix; Task<bool> CanSubscribeAsync(Guid tenantId, Guid memberId, string topic, CancellationToken) }`
    — cada módulo registra el suyo. El hub rechaza un tópico sin autorizador.
  - `IRealtimePublisher.PublishAsync(Guid tenantId, string topic, string kind, object payload, CancellationToken)` —
    se llama después del commit y reparte entre pods por `NOTIFY` (canal propio `qep_realtime`), igual que Mensajería.
    Un fallo se registra y no se propaga.
- Métodos del hub: `Subscribe(topic)`, `Unsubscribe(topic)`, `SetPresence(topic, state)` con `state ∈ {composing, none}`.
  El grupo de SignalR es `{tenantId}:{topic}`: un tópico nunca cruza tenants.
- Tópicos de Soporte: `support` (el tenant: bandeja) y `support:ticket:{ticketId}` (un detalle). Autoriza
  `SupportRealtimeAuthorizer` (en `Modules.Support.Api`): módulo `support` prendido y `support.ticket.read` (o
  `support.ticket.manage`, la regla de lectura de la fase 1); para un ticket, además, que sea del tenant. Para
  `SetPresence`, `support.ticket.manage`.

### 5.2 Avisos de Soporte

- `ISupportRealtime` en `Modules.Support.Application` con adaptador sobre `IRealtimePublisher`:
  `TicketChangedAsync(tenantId, ticketId)` publica `supportTicketChanged { ticketId }` en `support:ticket:{id}` y en
  `support`.
- Se llama después del commit en todo handler que escriba un ticket (estado, responsable, tipo, cliente, crear, fijar,
  soltar, seguir el hilo, responder, nota, clasificar, «De ninguno») y en el worker de seguimiento cuando vincula un
  mensaje (aunque un `Linked` no suba la versión), lo pasa a sin clasificar o reabre por un entrante.

### 5.3 Presencia

- Valkey: sorted set `qep:rt:presence:{tenantId}:{topic}` (como `ValkeyMessagingPresence`), miembro
  `{memberId}:{connectionId}`, score = epoch de expiración. `SetPresence(composing)` hace `ZADD` con ahora + 45 s y
  renueva el `EXPIRE` de la clave a 60 s; `none` hace `ZREM` de esa conexión. Al leer se recorta lo vencido
  (`ZREMRANGEBYSCORE`) y se agrupa por miembro. Al desconectar, el hub quita las entradas **de esa conexión** en los
  tópicos donde estaba (el hub guarda en memoria qué tópicos tiene cada conexión): dos pestañas del mismo miembro no se
  pisan.
- El cliente renueva `composing` cada 20 s mientras siga con texto. Una entrada vencida no se muestra aunque siga en el
  hash (se filtra al leer).
- Tras cada cambio, el hub publica `presenceChanged { topic, members: [{ memberId, name, state }] }` en el tópico con
  todos los miembros presentes. La SPA no conoce su `memberId` (D-F1 de Mensajería), así que el hub manda a cada
  conexión la lista **sin su propio miembro**. Al suscribirse, el hub responde con la foto actual.
- Sin Valkey (área `Presence` apagada o caída): presencia en memoria del pod. Con varias réplicas el aviso puede no
  cruzar pods; queda anotado como degradación aceptada (el `If-Match` sigue siendo la red final).

### 5.4 Notas internas

- Tabla `support.ticket_notes (id, tenant_id, ticket_id, author_member_id, text, created_at)` y
  `support.ticket_note_files (note_id, file_id, position)`. Insert-only.
- `POST /support/tickets/{ticketId}/notes` con `{ clientId, text, fileIds }`, permiso `support.ticket.manage`, **sin
  `If-Match`** y **sin subir la versión** del ticket [owner, 2026-11-15]. Texto 1 a 4 096 recortado, sin caracteres de
  control salvo `\n`, `\r` y `\t`. Hasta 5 archivos, cada uno de Storage, del tenant, `Available` y subido por el mismo
  miembro. Idempotente por `clientId`. Responde `201 SupportTicketNoteDto`.
- Ticket `Closed`: 422 `support.note.ticket_closed` (como fijar, soltar y seguir el hilo, `4d57426b`).
- Lectura: `GET /support/tickets/{ticketId}/notes?before=&take=` (más nuevo primero, 50 por defecto, 100 máximo), con
  `support.ticket.read`. `SupportTicketNoteDto(Guid Id, string Text, SupportMemberDto Author, DateTimeOffset CreatedAt,
  IReadOnlyList<SupportNoteFileDto> Files)`; `SupportNoteFileDto(Guid FileId, string Name, string MimeType, long SizeBytes)`.
- **Subida y descarga por Soporte, no por Storage directo.** `IssueDownloadUrl.cs:33` exige `storage.file.read` y
  `SystemOwnedFileGuard` deja los archivos `SupportTicket` sólo a Soporte, así que un asesor con sólo permisos de
  Soporte no podría usar las rutas de Storage. Se reusa el camino de la fase 3 (F30–F37): `IStorageSystemUploads` por el
  puerto `ISupportAttachmentStorage`, dueño `FileOwnerType.SupportTicket`, `ownerId` = id del ticket.
  - `POST /support/tickets/{ticketId}/notes/attachments` (`{ name, mimeType, sizeBytes }` → `{ fileId, uploadUrl, headers }`)
    y `POST …/notes/attachments/{fileId}/complete` (→ `{ status }`), con `support.ticket.manage`. Límites de
    `SupportPublicAttachmentRules` (10 MB, tipos de `FileUploadPolicy`).
  - Crear la nota exige cada `fileId` `Available`, de dueño `SupportTicket` con ese ticket, y no reclamado (índice único
    sobre `file_id`, traducido por nombre).
  - Descarga: `GET /support/tickets/{ticketId}/notes/{noteId}/files/{fileId}/download` → `{ url }` firmada de 5 min,
    con `support.ticket.read`, como `GetTicketAttachmentDownload`.
  - `IFileReferenceProbe` de Soporte suma `ticket_note_files.file_id` para que la limpieza de staging no borre un
    archivo de nota.
- Nada de la nota sale en `public/support` ni en eventos de integración. **No escribe evento en la traza**: la nota misma
  (autor y hora) es el registro, y un evento subiría la versión.

### 5.5 Códigos

`support.note.text_invalid`, `support.note.too_many_files`, `support.note.file_invalid`, `support.note.ticket_closed`,
`support.realtime.topic_forbidden` (error del hub, no HTTP).

## 6. Pruebas

- Arquitectura: `SupportLayerTests` no se afloja; BuildingBlocks no referencia módulos.
- Hub: un miembro sin `support.ticket.read` no se suscribe a `support:*`; un ticket de otro tenant tampoco; `SetPresence`
  sin `manage` falla; dos tenants con el mismo tópico no se ven.
- Presencia: composing → `presenceChanged` con el miembro; vence a los 45 s sin renovar; desconectar la limpia.
- Avisos: cada handler de escritura publica después del commit; el worker publica al vincular; un rollback no publica.
- Notas: 201 y lectura; idempotencia por `clientId`; 6 archivos → 422; archivo de otro miembro o no `Available` → 422;
  ticket `Closed` → 422; la consulta pública no expone notas; la versión del ticket no cambia.

## 7. Despliegue

Backend antes que el frontend. Sin flags: el hub nuevo no cambia nada mientras nadie se conecte. La migración de notas es
aditiva.

## 8. Lo que queda al implementador

Nombres exactos de clases; si `qep_realtime` comparte la conexión dedicada del listener de Mensajería
(`ListenerConnectionString.cs`) o abre otra; y cómo se excluye al propio miembro por conexión al entregar
`presenceChanged`.

## 9. Decisiones del diseñador tras la revisión

- Permisos reales: `support.ticket.read`, `support.ticket.manage` (`SupportPermissions.cs:7-11`).
- Tenant del hub por claim, como `MessagingHub`; el `?tenantId=` del cliente no decide nada.
- Presencia por conexión, no por miembro (dos pestañas no se pisan).
- Archivos de nota por rutas de Soporte (Storage exige `storage.file.read`).
- Sin evento `NoteAdded` en la traza: subiría la versión.
- El evento del hilo ya trae `TicketId` y `TicketNumber` (`MessagingDtos.cs:157`): no hay tarea de backend para eso.
