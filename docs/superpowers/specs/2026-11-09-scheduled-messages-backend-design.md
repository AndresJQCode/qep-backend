# Mensajes programados dentro de la ventana de 24 h (backend)

Fecha: 2026-11-09. Repo: `qep-backend`. Contraparte: `qep-frontend`,
`docs/superpowers/specs/2026-11-09-scheduled-messages-frontend-design.md`. Este spec manda el contrato.

Marcas: **[owner]** decisión del owner; **[orq]** decisión del orquestador, revisable; **[spec]** decisión de
este spec, revisable. Todo nombre marcado **NUEVO** no existe hoy en el código.

## Problema

Un cliente escribe a las 5 p. m.; el asesor lo ve a las 9 p. m. y no quiere responder a esa hora. Hoy la
única opción es responder ya o esperar y acordarse. El asesor quiere dejar la respuesta programada para
una hora que elija, siempre dentro de la ventana de atención de 24 h, y que salga aunque cierre el
navegador.

## Metas

- Programar **un** mensaje por conversación [owner], de cualquier tipo que el chat manda hoy: texto,
  imagen, documento, video, audio adjunto y nota de voz [owner].
- La hora es un instante absoluto (UTC) que no puede pasar del cierre de la ventana [owner].
- Lo envía el **backend** a la hora, con un worker, nunca un temporizador del navegador [orq].
- Nada lo detiene: un entrante nuevo, una transferencia o resolver la conversación no lo cancelan [owner].
- Se puede ver, editar, cancelar, enviar ya, reintentar y descartar; todo queda auditado [owner], [orq].

## No metas

- Plantillas programadas (ventana cerrada). Eso es una campaña o una plantilla manual.
- Varios mensajes programados por conversación, o una cola de mensajes en serie.
- Programar con cita (`replyTo`) o con varios adjuntos en un solo mensaje programado [spec].
- Recurrencia («todos los lunes»).
- Un indicador en la lista de conversaciones (se puede sumar después a `ConversationSummary`).

## Anti-metas

- **No** caer a una plantilla cuando la ventana cerró a la hora del envío [orq].
- **No** reenviar solo un envío de resultado incierto: duplicar un mensaje al cliente es peor que no
  mandarlo.
- **No** depender del staging `messaging/staging/` para guardar el adjunto horas: lo purga la regla de
  lifecycle del bucket (README, «Regla de lifecycle para `messaging/staging/`», `--expire-days 1`).
- **No** tocar los no leídos ni mandar el acuse de lectura al enviar: el asesor leyó a las 9 p. m.; lo que
  el cliente escriba después no lo leyó nadie.

## Lo que existe hoy (verificado en el código)

- Envío de texto: `POST …/conversations/{id}/messages` (`SendMessageHandler`), idempotente por
  `(conversation_id, client_id)` (índice único `IX_messages_conversation_client`).
- Adjuntos: `POST …/messages/media/upload-session` (`CreateMediaUploadSessionHandler`) firma un `PUT` a
  `messaging/staging/{tenantId}/{conversationId}/{memberId}/{uploadId}{ext}` (`MessagingMediaKeys.Staging`);
  la URL vive `Storage:PresignedUrlMinutes` (5). `POST …/messages/media` (`SendMediaMessageHandler`)
  confirma: stat, firma binaria (`OutboundMediaSniffer`), compresión de imagen, subida a Meta, reclamo,
  copia a `messaging/{tenantId}/{messageId}` (`MessagingMediaKeys.For`) y envío.
- Nota de voz: **no** usa la sesión de subida. Es `multipart/form-data` a `POST …/messages/audio`
  (`SendAudioMessageHandler`), OGG/Opus validado por contenido, ≤ 16 MB; Instagram la transcodifica a WAV.
- Guardas comunes (`OutboundGuards`): permiso `messaging.conversation.manage` + módulo, membresía activa,
  contacto no bloqueado, conversación `Open`, ventana (`LastInboundAt + Conversation.WindowLength > now`,
  24 h, igual para WhatsApp e Instagram), cita válida, y **sólo el asignado responde** (sin asignar,
  responder es tomar: `TryAutoAssignAsync`).
- Reclamo y liquidación: `IOutboundMessages.ClaimAsync` + `OutboundSettlement`. El bot
  (`BotOutbound`) y la cotización por Meta (`QuotationTemplateOutbound`) ya envían sin
  `IExecutionContext`; la cotización no cambia estado ni asignación y pasa `seen: 0`.
- `{saludo}`: el SPA lo resuelve con la hora local del navegador; el backend ya tiene
  `TimeOfDayGreeting.For(TimeOnly)` y lo usa con la zona del tenant (`ITenantClock`) en campañas
  (`CampaignDispatch.GreetingAsync`) y en `SystemTemplateSend`.
- Worker con reclamo seguro entre pods: `CampaignDispatchWorker` + `CampaignDispatchStore`
  (`PeriodicTimer`, un scope por lote, `FOR UPDATE SKIP LOCKED`, `locked_until` como lease, `attempts`,
  recuperación de leases vencidos). Opciones en `MessagingWorkerOptions` (`Messaging:Workers`).
- Tiempo real: `IMessagingNotifier.ConversationChangedAsync` después del commit; el SPA invalida.
- Auditoría atómica: `IMessagingAuditRecorder` (incluye variantes `bySystem` para el worker).

## Decisiones

- **D1. Instagram entra** [spec]. El código aplica la misma ventana de 24 h a los dos canales
  (`OutboundGuards.AdmitAsync`) y los tres envíos ya bifurcan por `ConversationChannel.Instagram`. Rigen
  sus reglas: texto ≤ 1000, el pie se guarda pero no viaja, formatos de `OutboundMediaPolicy` por canal,
  la nota de voz se guarda ya transcodificada a WAV.
- **D2. Crear un programado exige poder responder ahora** [spec]. Corre la misma admisión que un envío
  (`LoadAsync` + `AdmitAsync`: bloqueo, `Open`, ventana, asignado o autoasignación). Programar es
  responder: si nadie la tenía, la toma quien programa. La cita no se admite (no metas).
- **D3. Gestionar el programado sólo exige el permiso** [orq]. Editar, cancelar, «Enviar ya»,
  reintentar y descartar piden `messaging.conversation.manage`, módulo y membresía, **no** ser el
  asignado: el programado sobrevive a una transferencia [owner] y quien lo programó tiene que poder
  cancelarlo. Ver discrepancia X3.
- **D4. La ejecución no mira estado ni asignación** [owner], calco de `QuotationTemplateOutbound`: no
  reabre una resuelta, no asigna, no autoasigna y liquida con `seen: 0`. Sí mira bloqueo, ventana y
  conexión. `sent_by_member_id` del mensaje es `author_member_id` (quien escribió el contenido).
  **Con el asistente activo a la hora del envío, el programado sale igual** (decisión del orquestador,
  revisable): es el mensaje explícito de un asesor, no una respuesta del bot, y la guarda
  `AutomationStatus` de `BotOutbound` no aplica.
- **D5. `{saludo}` lo resuelve el backend al enviar, con la hora del tenant** [orq]. El texto (o el pie)
  se guarda con el token literal `{saludo}` y el ejecutor lo reemplaza con
  `TimeOfDayGreeting.For(hora local del tenant al enviar)`, como las campañas. Es la única variable que
  se resuelve: las demás llegan ya escritas desde la caja. Justificación: no hay navegador a la hora del
  envío, y el saludo correcto es el del momento en que el cliente lo lee. Las franjas de
  `TimeOfDayGreeting` (< 12, < 18) coinciden con `GREETING_SLOTS` del SPA. El tope de largo se mide con
  el saludo más largo (`Buenas tardes`/`Buenas noches`, 13), igual que `sentTextLength` del SPA.
- **D6. El adjunto se fija al programar, no al enviar** [orq]. Al crear: stat del staging, tope,
  firma binaria, compresión de la imagen (JPEG, igual que hoy) y copia a la clave **NUEVA**
  `messaging/scheduled/{tenantId}/{scheduledMessageId}{ext}`; después se borra el staging. Así un
  archivo inválido falla al programar y no a las 8 a. m., y el objeto no depende del lifecycle del
  staging ni del `memberId` de la clave. La subida a Meta sí ocurre al enviar.
- **D7. Hora mínima y máxima** [spec]. `scheduledAt ≥ now + 1 min` (**NUEVO** `ScheduledMessage.MinimumLead`)
  y `scheduledAt ≤ LastInboundAt + 24 h − 2 min` (**NUEVO** `ScheduledMessage.WindowSafetyMargin`): el margen
  cubre el intervalo del worker y la llamada a Meta. Si el cliente vuelve a escribir, la ventana se
  extiende y una edición puede elegir más tarde.
- **D8. Falla y reintento** [orq]. A la hora, si la ventana cerró, el contacto está bloqueado, la
  conexión no sirve, falta el objeto o Meta rechaza, el programado queda `Failed` con `failure_code`,
  sigue fijado, y ofrece «Reintentar» (si `canRetry`) y «Descartar». Nunca cae a plantilla. El reintento
  reusa el mismo `client_id`, así que el reclamo reutiliza la fila `Failed` del mensaje y no duplica.
- **D8b. Descartar cierra también el mensaje `Failed`** (decisión del orquestador, revisable). Hoy no
  existe en el código una marca de «no reintentable» en `messaging.messages` (sólo las de campañas), así que
  se agrega **NUEVA** columna `messages.retry_disabled boolean NOT NULL DEFAULT false` y **NUEVO** campo
  `MessageDto.retryDisabled` (un backend viejo no lo manda; el SPA lo lee como `false`). `discard`, en la
  misma transacción que `Failed → Cancelled`, pone `retry_disabled = true` en la fila con ese
  `(conversation_id, client_id)` si existe y está `Failed`. Consecuencia del índice único
  `IX_messages_conversation_client`: ese `client_id` queda **quemado**. Los tres handlers de envío
  (`SendMessageHandler`, `SendMediaMessageHandler`, `SendAudioMessageHandler`) y el ejecutor tratan una
  fila `Failed` con `retry_disabled` como final y responden **NUEVO** `messaging.message.not_retriable`
  (409) en vez de reenviarla; un programado nuevo siempre usa un `clientId` nuevo (también único en
  `scheduled_messages`).
- **D9. Resultado incierto no se reenvía solo** [spec]. Un lease vencido en `Sending` se recupera mirando
  el mensaje con ese `client_id`: `Sent`/`Delivered`/`Read` → `Sent`; `Failed` → `Failed`
  (`messaging.message.rejected`); sin fila → `Failed` con **NUEVO** `messaging.scheduled.unknown_outcome`
  (el proceso pudo morir con Meta ya aceptado). Reintentarlo lo decide una persona.
- **D10. «Enviar ya» y «Reintentar» no envían en el request** [spec]: ponen `scheduled_at = now` y estado
  `Scheduled`, responden 202 y el worker lo toma en el siguiente tick. Un envío de un documento de 100 MB
  no cabe en los 100 s de Cloudflare, y así hay un solo camino de envío.
- **D11. Tiempo real sin evento nuevo** [orq]: cada cambio de estado llama
  `IMessagingNotifier.ConversationChangedAsync` después del commit. El SPA ya invalida la conversación
  con ese evento.

## Modelo de datos (NUEVO)

Tabla `messaging.scheduled_messages`, entidad `ScheduledMessage` (Domain), migración
`AddScheduledMessages` (`MessagingDbContext`, factory de diseño).

| Columna | Tipo | Nota |
| --- | --- | --- |
| `id` | uuid PK | `Guid.CreateVersion7()` |
| `tenant_id` | uuid | todo método de repositorio lo recibe |
| `conversation_id` | uuid FK `conversations` | |
| `client_id` | uuid | idempotencia del POST **y** del mensaje que saldrá |
| `kind` | varchar(16) | nombre de `MessageKind`: `Text`, `Image`, `Document`, `Video`, `Audio` |
| `is_voice_note` | bool | `Audio` + `true` sale con `voice: true` |
| `text` | varchar(4096) null | sólo `Text`, con `{saludo}` literal |
| `caption` | varchar(1024) null | `Image`, `Document`, `Video` |
| `media_storage_key` | varchar(512) null | `messaging/scheduled/…`; `null` tras purgar |
| `media_mime_type`, `media_file_name`, `media_size_bytes`, `media_sha256` | | como `message_media` |
| `scheduled_at` | timestamptz | UTC |
| `status` | varchar(16) | `Scheduled`, `Sending`, `Sent`, `Failed`, `Cancelled` |
| `author_member_id` | uuid | quien escribió el contenido (crear o editar contenido) |
| `created_by_member_id`, `created_at` | | |
| `updated_by_member_id`, `updated_at` | null | última edición |
| `cancelled_by_member_id`, `cancelled_at` | null | cancelar o descartar |
| `sent_message_id` | uuid null | `messages.id` al quedar `Sent` |
| `failure_code` | varchar(64) null | |
| `attempts` | int | sube en cada reclamo del worker |
| `locked_until` | timestamptz null | lease |
| `version` | bigint | concurrencia optimista |

Índices:

- `UX_scheduled_messages_active_conversation` único sobre `(conversation_id)` con filtro
  `status IN ('Scheduled','Sending','Failed')`: **uno activo por conversación en la base** [owner]. Un
  `Failed` cuenta como activo porque sigue fijado; hay que descartarlo para programar otro.
  `MessagingSaveChanges` lo traduce por nombre de índice a **NUEVO** `messaging.scheduled.already_exists`.
- `UX_scheduled_messages_conversation_client` único sobre `(conversation_id, client_id)`: repetir el POST
  con el mismo `clientId` devuelve el existente (como el envío).
- `IX_scheduled_messages_due` sobre `(scheduled_at)` con filtro `status = 'Scheduled'`.
- `CHECK`: `Text` ⇒ `text IS NOT NULL AND media_mime_type IS NULL`; resto ⇒ `media_mime_type IS NOT NULL`;
  `is_voice_note` ⇒ `kind = 'Audio'`.

Transiciones (dominio, cada una sube `version`):
`Scheduled → Sending` (worker) · `Sending → Sent | Failed` (worker) · `Scheduled → Cancelled` (cancelar) ·
`Failed → Scheduled` (reintentar) · `Failed → Cancelled` (descartar) · `Scheduled → Scheduled` (editar,
«Enviar ya»). Cualquier otra es **NUEVO** `messaging.scheduled.not_editable` (409).

## Contrato HTTP (NUEVO)

Bajo `/api/v1/tenants/{tenantId:guid}/messaging`. Todas con `RequireAuthorization(MessagingPermissions.ConversationManage)`
salvo el `GET`, que pide `ConversationRead`. Los handlers revalidan tenant, permiso y módulo (403, nunca 404
ante otro tenant). La versión viaja en el cuerpo (`expectedVersion`), como campañas; vieja → 412
`concurrency.conflict`.

### `ScheduledMessageDto`

```json
{
  "id": "0193…",
  "conversationId": "0192…",
  "clientId": "6f1c…",
  "status": "Scheduled",
  "scheduledAt": "2026-11-10T13:00:00Z",
  "kind": "Image",
  "isVoiceNote": false,
  "text": null,
  "caption": "{saludo}, te comparto el catálogo",
  "media": { "mimeType": "image/jpeg", "fileName": "catalogo.jpg", "sizeBytes": 183422 },
  "author": { "memberId": "…", "displayName": "Ana Gómez" },
  "createdBy": { "memberId": "…", "displayName": "Ana Gómez" },
  "updatedBy": null,
  "updatedAt": null,
  "failureCode": null,
  "canRetry": false,
  "sentMessageId": null,
  "version": 1
}
```

`canRetry` lo calcula el backend (BFF): `Failed` + ventana abierta con el margen de D7 + contacto no
bloqueado. `displayName` sale de `IMessagingMemberNames`, como `sentBy`.

### Rutas

| Método y ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `GET /conversations/{id}/scheduled-message` | — | 200 `{ "scheduledMessage": ScheduledMessageDto \| null }` (sólo `Scheduled`, `Sending`, `Failed`) |
| `POST /conversations/{id}/scheduled-messages` | JSON, ver abajo | 201 `ScheduledMessageDto` (200 si el `clientId` ya existía) |
| `POST /conversations/{id}/scheduled-messages/audio` | multipart `clientId`, `scheduledAt`, `file` | 201 / 200 |
| `PUT /conversations/{id}/scheduled-messages/{smId}` | `{ expectedVersion, scheduledAt, text?, caption? }` | 200 `ScheduledMessageDto` |
| `POST /conversations/{id}/scheduled-messages/{smId}/cancel` | `{ expectedVersion }` | 204 |
| `POST /conversations/{id}/scheduled-messages/{smId}/send-now` | `{ expectedVersion }` | 202 `ScheduledMessageDto` |
| `POST /conversations/{id}/scheduled-messages/{smId}/retry` | `{ expectedVersion }` | 202 `ScheduledMessageDto` |
| `POST /conversations/{id}/scheduled-messages/{smId}/discard` | `{ expectedVersion }` | 204 |
| `GET /conversations/{id}/scheduled-messages/{smId}/media` | — | el archivo (mismo manejo de rangos que `GET /media/{messageId}`) |

Cuerpo del `POST` JSON:

```json
{ "clientId": "6f1c…", "scheduledAt": "2026-11-10T13:00:00Z", "text": "{saludo}, ya te confirmo el envío." }
```

```json
{ "clientId": "6f1c…", "scheduledAt": "2026-11-10T13:00:00Z", "uploadId": "0193…", "fileName": "catalogo.jpg", "caption": "Te lo comparto" }
```

Exactamente uno de `text` o `uploadId`. Validadores FluentValidation (claves del JSON): las reglas de
`SendMessageValidator` (texto, midiendo `{saludo}` como 13 caracteres) o de `SendMediaMessageValidator`
(nombre, pie ≤ 1024, audio sin pie), más `scheduledAt` requerido. La nota de voz calca
`SendAudioMessageValidator` y su tope de cuerpo de 17 MB.

**Orden del `POST`** (decisión del orquestador, revisable): **primero** la búsqueda por
`(conversation_id, client_id)`. Si existe, responde 200 con esa fila en el estado que tenga (también
`Sent` o `Cancelled`), sin validar la hora, sin admisión y sin tocar el staging: un reintento tras perder
el 201 no puede fallar con `upload_not_found` porque el primero ya consumió el staging. Sólo si no existe
siguen validador de hora, `LoadAsync`/`AdmitAsync`, stat del staging y el resto.

`PUT`: sólo en `Scheduled`. `text` sólo si `kind = Text`; `caption` sólo en imagen, documento y video.
El adjunto y la nota de voz no se reemplazan: se cancela y se programa otro [spec]. Editar el contenido
cambia `author_member_id`. Semántica de los campos (decisión del orquestador, revisable):

- Campo **omitido** = se conserva. `null` se trata como omitido (el frontend nunca manda `null`).
- `caption: ""` (o sólo espacios) **borra** el pie: queda `null`.
- `text` de un mensaje de texto no se puede vaciar: `""` o sólo espacios es 422 `validation.failed` con
  `errors.text`, el mismo de `SendMessageValidator`.
- `text` o `caption` en un `kind` que no lo admite es 422 `validation.failed` en ese campo.
- Un `PUT` sin cambios reales responde 200 sin subir `version` ni auditar.
- `changedFields` de la auditoría lista sólo los campos cuyo valor cambió de verdad (`scheduledAt`,
  `text`, `caption`), comparando contra lo guardado después de normalizar (trim, `""` → `null`).

### Errores

| Código | Status | Cuándo |
| --- | --- | --- |
| **NUEVO** `messaging.scheduled.outside_window` | 422 | `scheduledAt` pasa de `LastInboundAt + 24 h − 2 min`, o la ventana ya cerró (crear, editar, reintentar) |
| **NUEVO** `messaging.scheduled.too_soon` | 422 | `scheduledAt < now + 1 min` (crear, editar) |
| **NUEVO** `messaging.scheduled.already_exists` | 409 | ya hay uno activo en la conversación (índice) |
| **NUEVO** `messaging.scheduled.not_found` | 404 | el id no es de esta conversación del tenant |
| **NUEVO** `messaging.scheduled.not_editable` | 409 | editar, cancelar o «Enviar ya» fuera de `Scheduled` |
| **NUEVO** `messaging.scheduled.not_retriable` | 409 | reintentar o descartar fuera de `Failed` |
| **NUEVO** `messaging.message.not_retriable` | 409 | reenviar por el hilo un `clientId` cuyo mensaje `Failed` se descartó (D8b) |
| `messaging.window_closed`, `messaging.conversation.not_open`, `messaging.conversation.assigned_to_other`, `messaging.contact.blocked` | 422 | crear (D2) |
| `messaging.media.upload_not_found`, `too_large`, `unsupported_format`, `invalid_image`, `upload_failed` | 422 | crear con adjunto |
| `validation.failed` | 422 | forma del cuerpo, con `errors` |

`failure_code` de un `Failed`: `messaging.window_closed`, `messaging.contact.blocked`,
`messaging.connection_unavailable`, `messaging.message.rejected` (existentes) y **NUEVOS**
`messaging.scheduled.unknown_outcome`, `messaging.scheduled.media_missing`.

## Worker y ejecución

- **NUEVO** `ScheduledMessageWorker` (Infrastructure, `BackgroundService`), calco de
  `CampaignDispatchWorker`: `PeriodicTimer` con **NUEVO** `Messaging:Workers:ScheduledPollSeconds`
  (default 5), un scope por fila, paralelismo 4. La lógica vive en **NUEVO** `ScheduledMessageDispatcher`
  (Application, probado con fakes); el SQL en **NUEVO** `ScheduledMessageStore` (Infrastructure).
- **Reclamo** (dos pods nunca envían dos veces):

  ```sql
  UPDATE messaging.scheduled_messages
     SET status = 'Sending', locked_until = {now} + {lease}, attempts = attempts + 1, version = version + 1
   WHERE id IN (SELECT id FROM messaging.scheduled_messages
                 WHERE status = 'Scheduled' AND scheduled_at <= {now}
                 ORDER BY scheduled_at LIMIT {take} FOR UPDATE SKIP LOCKED)
  RETURNING …
  ```

  Lease de 10 min (**NUEVO** `ScheduledMessageDispatcher.LeaseLength`): cubre la subida a Meta
  (`WhatsAppCloudClient.MediaUploadTimeout`, 5 min) más el envío. Una edición o cancelación concurrente
  toma la fila con `FOR UPDATE`; el worker la salta en ese tick (`SKIP LOCKED`) y, si ya la reclamó, la
  edición recibe `not_editable`.
- **Ejecución** (`ScheduledMessageExecutor`, **NUEVO**, Application, sin `IExecutionContext`, como
  `BotOutbound`): relee la conversación sin seguimiento → `FindByClientIdAsync`: si ya salió, `Sent` sin
  llamar a Meta → bloqueo → ventana (`LastInboundAt + 24 h > now`) → conexión (`ResolveSenderWithReasonAsync`
  / `ResolveInstagramSenderAsync`) → resolver `{saludo}` (D5) → **almacenamiento antes del reclamo**
  (decisión del orquestador, revisable): el `messageId` es el de la fila `Failed` existente con ese
  `client_id` (reintento) o el id nuevo del `OutboundDraft`; se copia `messaging/scheduled/…` a
  `MessagingMediaKeys.For(tenant, messageId)` y, en WhatsApp, se sube a Meta → `ClaimAsync` con el
  `client_id` del programado y `SentByMemberId = author_member_id` → enviar con el mismo cliente de
  Meta/Instagram que hoy → `OutboundSettlement.SettleAsync(…, seen: 0)`. El lease hace del ejecutor el
  único escritor; si aun así el reclamo devuelve otro `MessageId`, se borra la copia y se rehace con ese id.
  Lo que hoy es privado de `SendMediaMessageHandler` (`PrepareAsync`, `SendInstagramAttachmentAsync`) se
  extrae a un servicio interno compartido sin cambiar sus pruebas.
- **Fallas de almacenamiento** (decisión del orquestador, revisable):
  - **Antes del reclamo**, una falla al leer, copiar o subir el adjunto (R2 o la subida a Meta) deja el
    programado `Failed` con `messaging.scheduled.media_missing`, reintentable (`canRetry` según D8): no hay
    fila de mensaje nueva y nada salió. La copia parcial en la clave final se borra (best effort).
  - **Después de un reclamo exitoso**, cualquier falla que no sea una respuesta de Meta (excepción, R2,
    firma de la URL de Instagram, caída) deja el programado `Failed` con
    `messaging.scheduled.unknown_outcome`, nunca reenviado solo. La fila del mensaje sigue la regla del
    reclamo: se deshace con su transacción (`claim.RollbackAsync`), así que una fila insertada en este
    intento desaparece y una fila `Failed` reutilizada queda `Failed` sin cambios; el objeto de la clave
    final se borra sólo si la fila era nueva (como `SendMediaMessageHandler`). Una respuesta de Meta, en
    cambio, la liquida `OutboundSettlement` y el programado queda `Sent` o `Failed`
    (`messaging.message.rejected`).
- **Cierre**, compare-and-set por `(id, status = 'Sending', attempts)`: `Sent` con `sent_message_id`, o
  `Failed` con `failure_code`. Si el CAS no toca filas (el lease venció y otro lo recuperó), no se escribe.
  Después: borrar el objeto de `messaging/scheduled/` si quedó `Sent` (best effort), auditoría en la misma
  transacción, y `ConversationChangedAsync` tras el commit.
- **Recuperación** en cada tick: `Sending` con `locked_until < now` según D9, con `FOR UPDATE SKIP LOCKED`.
- **Purga**: `Cancelled` y `Sent` borran su objeto al transicionar; un barrido diario del mismo worker
  borra los objetos de filas terminales con `media_storage_key` no nulo (fallas de borrado) y los
  pone en `null`. El prefijo `messaging/scheduled/` **no** lleva regla de lifecycle (o, si se pone, más de
  2 días): el objeto vive hasta 24 h antes de enviarse.
- **No** consume `connection_send_budgets`: el envío de un asesor tampoco lo hace.

## Auditoría (NUEVO)

`IMessagingAuditRecorder.RecordScheduledMessage(tenantId, actorId, bySystem, action, scheduledMessageId,
occurredAt, changedFields?)`, recurso **NUEVO** `MessagingAuditActions.ScheduledMessageResourceType =
"scheduled_message"`. Acciones: `messaging.scheduled_message.created`, `.updated` (con
`["scheduledAt"]`, `["text"]`, `["caption"]`), `.cancelled`, `.send_now`, `.retried`, `.discarded` (por una
persona, en el handler) y `.sent`, `.failed` (por el worker, `bySystem`). Misma transacción que el cambio.

## Pruebas (TDD, RED antes que GREEN)

Unitarias (Domain/Application, con fakes):

1. `ScheduledMessageTests`: transiciones válidas e inválidas (`not_editable`, `not_retriable`), `version`
   sube, `author_member_id` cambia sólo con contenido.
2. `ScheduleWindowTests`: `too_soon` a 59 s; `outside_window` a `expira − 1 min`; justo en `expira − 2 min`
   pasa; sin `LastInboundAt` es `outside_window`.
3. `CreateScheduledMessageHandlerTests`: la búsqueda por `clientId` va primero: un `clientId` existente
   devuelve 200 con la fila en cualquier estado (`Scheduled`, `Failed`, `Sent`, `Cancelled`) sin llamar al
   stat del staging (el fake falla si se lo llama), sin admisión y aunque la hora ya no sea válida o la
   conversación esté resuelta; admisión como el envío (bloqueado, resuelta, de otro asignado, autoasigna sin
   asignado); adjunto: staging ausente → `upload_not_found`, tope, firma, imagen comprimida guardada en
   `messaging/scheduled/` y staging borrado; Instagram: texto > 1000, formato no admitido, nota
   transcodificada a WAV.
3b. **Reintento tras perder el 201**: el primer `POST` con adjunto confirma y consume el staging; el segundo,
   con el mismo `clientId` y el mismo `uploadId`, responde 200 con la misma fila y no 422 `upload_not_found`;
   igual con la nota de voz multipart.
4. `ScheduledMessageManagementTests`: editar/cancelar/«Enviar ya» de otro miembro con permiso pasa sin ser
   el asignado; `expectedVersion` vieja → 412. `PUT`: campo omitido o `null` conserva; `caption: ""` borra;
   `text: ""` en un texto → 422 `validation.failed` (`errors.text`); `caption` en una nota de voz → 422; un
   `PUT` idéntico no sube `version` ni audita; `changedFields` sólo con lo que cambió.
5. `ScheduledMessageExecutorTests`: ventana cerrada → `Failed window_closed` sin reclamo ni llamada;
   bloqueado; conexión pausada; Meta rechaza → `Failed message.rejected` y fila `Failed` del mensaje;
   `{saludo}` resuelto con la hora del tenant (06:00 → «Buenos días», 13:00 → «Buenas tardes»); resuelta,
   de otro asignado y con el asistente `Active` sí sale sin cambiar estado ni asignación; `seen: 0`;
   reintento con el mismo `clientId` reutiliza la fila `Failed` y su clave final; ya enviado → `Sent` sin
   llamar a Meta; copia a la clave final antes del reclamo; objeto ausente, copia fallida o subida a Meta
   fallida → `media_missing` sin fila de mensaje; excepción después del reclamo → `unknown_outcome`, fila
   nueva deshecha, fila `Failed` reutilizada intacta.
5b. `DiscardScheduledMessageTests`: descartar marca `retry_disabled` en la fila `Failed` del mensaje; un
   `POST …/messages` (texto, media y audio) con ese `clientId` responde 409 `messaging.message.not_retriable`
   sin llamar a Meta; descartar sin fila de mensaje (falla antes del reclamo) sólo cancela.
6. `ScheduledMessageRecoveryTests`: lease vencido con mensaje `Sent` → `Sent`; con `Failed` → `Failed`;
   sin fila → `unknown_outcome`, nunca vuelve a `Scheduled`.

Integración (Testcontainers, por proyecto, `MaxParallelThreads=4`):

7. `ScheduledMessagePersistenceTests`: el índice parcial rechaza un segundo activo →
   `already_exists`; un `Cancelled` no cuenta; `Failed` sí.
8. `ScheduledMessageApiTests`: contrato JSON de cada ruta, 403 con otro tenant, 404 `not_found`, 202 de
   «Enviar ya» y `GET` vacío tras `Sent`.
9. `ScheduledMessageWorkerTests`: dos workers sobre la misma fila → un solo envío (fake de Meta cuenta
   llamadas); edición concurrente con el reclamo.
10. `MessagingAuditTests`: cada acción deja su entrada con actor o `System`.
11. `MessagingLayerTests` sigue verde (Application sin EF/Npgsql).

## Discrepancias y decisiones pendientes

- **X1. La nota de voz no usa la sesión de subida.** El brief la agrupa con el flujo de dos pasos; el
  código la manda en `multipart` a `…/messages/audio`. Por eso hay una ruta `…/scheduled-messages/audio`.
- **X2. La sesión de subida no sirve para guardar horas.** La URL vive 5 min y el staging lo borra la regla
  de lifecycle (`--expire-days 1`, operación, no código), además de llevar el `memberId` en la clave. D6
  mueve el objeto al programar.
- **X3. «Cualquiera que pueda responder» vs. «sólo el asignado responde».** El permiso de enviar es
  `messaging.conversation.manage`, pero `OutboundGuards.AdmitAsync` exige además ser el asignado. D3
  separa crear (admisión completa) de gestionar (sólo permiso). **Decisión del owner, 2026-11-09:**
  cualquiera con permiso de responder puede «Enviar ya», «Editar» o «Cancelar envío» el programado,
  aunque la conversación no sea suya.
- **X4. Resolver y transferir.** El envío normal exige `Open` y asignado; la ejecución los ignora (D4).
  **Decisión del owner, 2026-11-09:** si la conversación está resuelta a la hora del envío, el mensaje
  sale sin reabrirla (como la cotización por Meta).
- **X5. `{saludo}`.** Hoy el chat usa la hora del navegador y el backend no resuelve variables en texto
  libre. D5 lo resuelve en el backend con la zona del tenant: un asesor en otra zona verá en la tarjeta el
  saludo de la hora del tenant.
- **X6. Falla de Meta deja dos superficies.** El mensaje `Failed` aparece en el hilo y el programado
  `Failed` en la tarjeta. El reintento oficial es el de la tarjeta (mismo `clientId`); el SPA oculta el
  reintento de ese mensaje en el hilo (spec frontend).
- **X7. Un programado y el bot.** Resuelto en D4 (decisión del orquestador, revisable): con el asistente
  `Active` el programado sale igual, porque es el mensaje explícito de un asesor.
- **X8. Un adjunto y sin cita.** **Decisión del owner, 2026-11-09:** un programado lleva a lo sumo un
  adjunto y no admite cita (`replyTo`).
- **X9. Marca de no reintentable.** No existía en `messaging.messages`; D8b agrega `retry_disabled`, que
  toca los tres handlers de envío existentes (migración y pruebas de esos handlers).

## Despliegue

1. Backend primero: migración `AddScheduledMessages`, worker y rutas. Sin cambios en rutas existentes.
2. No agregar regla de lifecycle a `messaging/scheduled/` (o > 2 días). Verificar que la de
   `messaging/staging/` sigue con su prefijo exacto.
3. Frontend después. Un SPA viejo no llama estas rutas; un SPA nuevo contra un backend viejo recibe 404
   sin código en el `GET` y esconde la función. Verificado: una conversación inexistente responde 404 con
   `code: "messaging.conversation.not_found"` (`MessagingNotFound.Conversation` → `ResourceNotFoundException`
   → `ApiExceptionHandler`), y `Program.cs` no registra `UseStatusCodePages` ni `MapFallback`, así que una
   ruta sin mapear es un 404 sin cuerpo ni `code`. El `GET` nuevo debe seguir cargando la conversación con
   `MessagingNotFound.Conversation` para que la señal no cambie.
4. `docs/estado.md` se actualiza en `develop` al mergear, no en esta rama. README: sección «Mensajes
   programados» en Mensajería.
