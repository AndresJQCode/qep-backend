# Mensajería: notas de voz del asesor

**Fecha:** 2026-10-11
**Módulos:** Messaging (endpoint, handler, cliente de Graph, persistencia del saliente con medio).
Bootstrapper sin cambios: el adaptador `MessagingMediaStore` ya existe. Authorization **sin** permisos
nuevos. **Frontend:** `qep-frontend`, `features/messaging/`.
**Estado:** opción A, UX (sección 1), backend (sección 2) y frontend (sección 3) aprobados por el owner
en conversación el 2026-10-11.
**Es un delta sobre** `2026-10-09-mensajeria-whatsapp-design.md` («el spec base») y
`2026-10-10-mensajeria-asignacion-bsuid-design.md` («el spec de asignación»). Lo que este documento no
toca sigue como está ahí.
**Ramas:** `feature/notas-de-voz` desde `develop` `468997f` en `qep-backend`; la homónima desde
`develop` en `qep-frontend`.

## 1. Contexto y alcance

Hoy el asesor sólo responde texto (`SendMessageRequest(ClientId, Text, ReplyTo)`,
`SendMessageHandler`, `WhatsAppCloudClient.SendTextAsync`). Los audios entrantes ya se ven y se
escuchan (base §8.6). Falta que el asesor grabe y mande una **nota de voz** desde la bandeja.

### Dentro de alcance

- Grabar en el navegador, escuchar antes de enviar y mandar una nota de voz a la conversación.
- Que en WhatsApp llegue como **nota de voz** (ícono de micrófono, reproducción en línea), no como
  archivo de audio.
- Mismas reglas que el texto: ventana de 24 h, sólo el asignado, autoasignación, cita opcional,
  idempotencia por `clientId`, cola en orden con reintento.

### Fuera de alcance

- Adjuntar archivos (imágenes, documentos, audios existentes).
- Transcripción.
- Leyenda (`caption`) en la nota de voz: Meta no la admite para audio.

## 2. Decisiones del owner (2026-10-11)

1. **Opción A: codificar OGG/Opus en el navegador** con WASM (`opus-recorder`, MIT), cargado al tocar
   el micrófono. Descartada la opción B (convertir con ffmpeg en el backend): suma ~80 MB a la imagen,
   consume CPU y agrega superficie de ataque.
2. **UX:** el micrófono reemplaza a «Enviar» cuando la caja está vacía; un toque graba (no hay que
   mantenerlo); contador con Descartar y Detener; **escucha previa obligatoria** antes de Enviar;
   máximo 5 minutos, y la grabación se detiene sola; aviso si se niega el micrófono; misma cola en
   orden con `clientId`, burbuja optimista y Reintentar; la nota pendiente va a IndexedDB.
3. **Backend:** multipart directo a la API, síncrono como el texto. Descartada la subida por URL
   prefirmada a R2 seguida de un envío con `fileId`: son dos pasos, deja huérfanos y obligaría a
   Messaging a depender de Storage por un archivo de ~1 MB.

## 3. Lo verificado sobre Meta y los navegadores

- Meta trata un audio como **nota de voz** sólo si es `audio/ogg` con códec **Opus** y el mensaje lleva
  `audio.voice = true`. Cualquier otro formato aceptado llega como audio común.
- Tope de Meta para audio: **16 MB**.
- Subida de medios: `POST /{phone_number_id}/media`, multipart con `messaging_product=whatsapp`,
  `type=<mime>` y `file`. Responde `{ "id": "<media-id>" }`.
- Lo que graba `MediaRecorder` de forma nativa: Chrome y Edge, WebM/Opus (Meta no lo acepta); Safari,
  MP4/AAC; Firefox, OGG/Opus. Por eso se codifica con `opus-recorder`, que entrega OGG/Opus en todos.

## 4. Contrato HTTP

### 4.1 `POST /api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}/messages/audio`

- **Permiso:** `messaging.conversation.manage` (el mismo del texto).
- **Cuerpo:** `multipart/form-data`.

| Campo      | Tipo   | Regla                                                           |
| ---------- | ------ | --------------------------------------------------------------- |
| `clientId` | GUID   | obligatorio, no vacío. Lo genera la pantalla por intento.       |
| `file`     | binario| obligatorio, 1 B–16 MB, **contenido** OGG con stream Opus (§6.2)|
| `replyTo`  | GUID   | opcional; si viene, no vacío. Mismas reglas de cita que el texto|

- **Respuesta:** `201 Created` con el `MessageDto` de siempre: `kind = "Audio"`, `text = null`,
  `media = { url, mimeType: "audio/ogg", fileName: null, caption: null }`. Sin `Location`, igual que el
  texto.
- **Idempotencia:** repetir un `clientId` cuyo mensaje no está `Failed` devuelve el mensaje guardado
  (con su media), aunque el archivo del request sea otro.
- **Errores:** los del texto (spec de asignación §10 y base §10), más:

| Status | `code`                           | Cuándo                                                          |
| ------ | -------------------------------- | --------------------------------------------------------------- |
| 422    | `validation.failed`              | `errors.file` o `errors.clientId` o `errors.replyTo`            |
| 422    | `messaging.media.upload_failed`  | R2 no aceptó el audio. No queda fila; reintentar es seguro.     |
| 413    | —                                | El cuerpo supera el límite de Kestrel del endpoint (§6.1)       |

Los mensajes de `errors.file` tutean: «Graba una nota de voz de hasta 16 MB.» y «La nota de voz no
está en el formato que admite WhatsApp. Graba de nuevo.».

## 5. Trazabilidad

| Necesidad                              | Componente                                                      |
| -------------------------------------- | --------------------------------------------------------------- |
| Llega como nota de voz                 | OGG/Opus del navegador + `audio.voice = true` (§6.4)            |
| Mismas reglas que el texto             | `SendAudioMessageHandler` con las guardas de `SendMessageHandler`|
| Se escucha en el hilo                  | `message_media` con `stored_at` + GET de media existente        |
| No se duplica al reintentar            | `clientId` + reclamo de `IOutboundMessages`                      |
| Sobrevive a recargar la pantalla       | Blob en IndexedDB, metadatos en la cola de `localStorage`       |

## 6. Backend

### 6.1 Endpoint (`Modules.Messaging.Api`)

- Ruta propia (no la del texto): así el `Accepts` del texto queda en JSON y no se toca.
- `.Accepts<IFormFile>("multipart/form-data")`, `.DisableAntiforgery()` con el mismo criterio de
  `CustomerEndpoints.cs:122`. El CSRF por `X-Qep-Client` sigue aplicando
  (`RequireCsrfHeaderMiddleware`).
- Límite de cuerpo del endpoint: **17 MB** (16 MB de audio más el sobrante del multipart), puesto por
  `IHttpMaxRequestBodySizeFeature` como en `WhatsAppWebhookEndpoints.cs:61`. Arriba de eso, Kestrel
  responde 413.
- El endpoint copia `file` a un `MemoryStream` (≤ 16 MB) y arma `SendAudioMessageCommand(TenantId,
  ConversationId, ClientId, byte[]? Audio, ReplyTo)`. Con bytes en memoria el handler puede calcular
  el hash, revisar la cabecera y subir dos veces (R2 y Meta) sin volver a leer el request.

### 6.2 Validador `SendAudioMessageValidator`

- `clientId` no nulo ni vacío; `replyTo` no vacío si viene (como `SendMessageValidator`).
- `file` presente, largo entre 1 B y `MaxAudioBytes = 16 * 1024 * 1024`.
- **Formato por contenido**, nunca por el `Content-Type` del navegador: los primeros 4 bytes son
  `OggS` y en la primera página hay un paquete que empieza con `OpusHead`. Un WebM (Chrome sin
  codificador) o un MP4 se rechazan aquí y nunca llegan a Meta.
- La duración no se valida en el backend: el tope de 5 minutos es de la UX y el tamaño ya acota.
- La lógica de cabecera vive en una clase pura `OggOpusSniffer.IsOggOpus(ReadOnlySpan<byte>)`, con
  pruebas propias.

### 6.3 Handler `SendAudioMessageHandler`

Mismo orden que `SendMessageHandler` (spec de asignación §8.5): validador → tenant, permiso y módulo →
conversación del tenant → membresía → idempotencia por `clientId` sin candado → `Open` → ventana 24 h
→ cita → dueño o autoasignación → reclamo → conexión `Active`. Las guardas que comparten los dos
handlers se extraen a un colaborador interno (`OutboundGuards`) en lugar de copiarse, y
`SendMessageHandler` pasa a usarlo **sin cambiar su comportamiento**: sus pruebas existentes deben
seguir verdes sin tocarlas.

Dentro del reclamo, en este orden:

1. **R2:** `IMessagingMediaStore.UploadAsync(MediaTransfer.KeyFor(tenantId, messageId), audio,
   length, "audio/ogg", ct)`. Si lanza: rollback del reclamo y `MessagingDomainException(
   messaging.media.upload_failed)` → 422. No queda fila.
2. **Meta, subida:** `IWhatsAppCloudClient.UploadMediaAsync(sender, bytes, "audio/ogg", ct)` →
   `media_id`. Si falla, se trata con la tabla de resultados del envío (paso 4): 190/133010 →
   `NeedsAttention`; otro 4xx → `Failed`; 5xx, timeout o sin `id` → `Failed` con
   `MessageFailureReasons.Unconfirmed`. No envió nada todavía, así que reintentar es seguro.
3. **Meta, envío:** `IWhatsAppCloudClient.SendAudioAsync(sender, target, mediaId, callbackData,
   contextWamid, ct)` con `type: "audio"`, `audio: { id, voice: true }`, `context` si cita y el mismo
   `biz_opaque_callback_data`. El armado de `recipient`/`to` se comparte con `SendTextAsync` (un
   método privado), sin cambiar el cuerpo del texto.
4. **Resultado:** la misma tabla que el texto (`SendMessageHandler.cs:138-166`). En `Sent`,
   `CommitSentAsync` escribe en la misma transacción el mensaje y la fila de `message_media` (§7). En
   `Failed`, `CommitFailedAsync` también escribe la fila de `message_media` (con `storage_key` y
   `stored_at`), para que la burbuja fallida se pueda escuchar desde otro dispositivo.

Un reintento con el mismo `clientId` sobre una fila `Failed` reutiliza el `messageId`
(`claim.MessageId`): R2 sobrescribe la misma clave y `message_media` se actualiza con `ON CONFLICT
(message_id) DO UPDATE`. No quedan huérfanos.

El candado de la fila dura R2 + subida a Meta + envío: tres llamadas con tope de 10 s cada una
(`messaging.meta-graph`), y R2 con su propio tope. Sigue bloqueando **una** fila de mensaje, nunca la
conversación (base §8.3).

### 6.4 Cliente de Graph (`IWhatsAppCloudClient`)

```csharp
Task<MessagingGraphResult<string>> UploadMediaAsync(
    MessagingSender sender, ReadOnlyMemory<byte> content, string mimeType, CancellationToken cancellationToken);

Task<SendResult> SendAudioAsync(
    MessagingSender sender, SendTarget target, string mediaId, string callbackData, string? contextWamid,
    CancellationToken cancellationToken);
```

- `UploadMediaAsync`: `POST {version}/{phone_number_id}/media`, `MultipartFormDataContent` con
  `messaging_product=whatsapp`, `type=audio/ogg` y `file` (nombre `voice.ogg`, `Content-Type:
  audio/ogg`). Devuelve el `id` o un `MessagingGraphFailure` con el status y `error.code`.
- `SendAudioAsync` devuelve el mismo `SendResult` que el texto (antes `SendTextResult`; renombrado al
  dejar de ser sólo del texto, D-V4).
- Registro: igual que hoy, status, `error.code`, `error_subcode` y `fbtrace_id`. Nunca el token, los
  bytes ni el `media_id` junto a datos de la persona.

### 6.5 Persistencia (`IOutboundMessages`)

- `OutboundDraft` gana `MessageKind Kind` y `OutboundMedia? Media` (`StorageKey`, `MimeType`,
  `SizeBytes`, `Sha256Hex`). El texto sigue con `Kind = Text`, `Media = null`. `Text` pasa a nullable.
- `ClaimAsync` inserta `kind` desde el draft (hoy está fijo en `1`).
- `CommitSentAsync(wamid, occurredAt, metaMediaId?, ct)` y `CommitFailedAsync(..., metaMediaId?, ct)`:
  si el draft trae media, hacen el upsert en `message_media` con `meta_media_id` (o `''` si Meta nunca
  lo dio, ver §7), `storage_key`, `sha256`, `size_bytes`, `stored_at = occurredAt`,
  `next_attempt_at = occurredAt`, `attempts = 0`.
- La foto de la conversación toma `last_message_kind` del draft y `last_message_preview = null` cuando
  no hay texto, igual que un audio entrante (`InboundIngestion.Preview`).
- `ExistingOutbound` gana `Kind` y `MediaMimeType?`, para que la idempotencia devuelva la burbuja de
  audio con su `media.url`.

### 6.6 Errores nuevos

`MessagingErrorCodes.MediaUploadFailed = "messaging.media.upload_failed"` → 422 por
`MessagingDomainException`, que ya mapea `ApiExceptionHandler`.

## 7. Base de datos

**Sin migración.** `message_media` ya tiene todo lo necesario (`InitialMessaging`): `meta_media_id`
es `NOT NULL` y para el saliente siempre hay uno salvo cuando la subida a Meta falló; en ese caso se
guarda `''`, y el `MediaCopyWorker` no la toma porque `stored_at` no es nulo (filtro de
`IX_message_media_pending`). `MessageColumnCodes` ya traduce `Audio = 4`.

## 8. Frontend (`qep-frontend`, `features/messaging/`)

### 8.1 Grabación

- `utils/voice-recorder.ts` envuelve `opus-recorder` (dependencia nueva, MIT). Se importa con
  `import()` sólo al tocar el micrófono; el worker y el WASM salen como assets aparte de Vite.
- Configuración: mono, 48 kHz, `encoderApplication` VOIP, salida `audio/ogg; codecs=opus`.
- Corte automático a los **5 minutos**.
- `NotAllowedError` o `NotFoundError` → aviso «Activa el permiso del micrófono en tu navegador para
  grabar notas de voz.».

### 8.2 Hook y componentes

- `hooks/use-voice-recorder.ts`: máquina de estados `idle → recording → preview → idle` (Descartar o
  Enviar). Expone tiempo transcurrido, blob y duración.
- `components/voice-recorder-bar.tsx`: en `recording`, contador + Descartar + Detener; en `preview`,
  `<audio controls>` del blob + Descartar + Enviar. Enviar sólo existe en `preview`: la escucha previa
  es obligatoria.
- `message-composer.tsx`: con la caja vacía (tras `trim`), el botón de enviar es un micrófono. Mismos
  bloqueos que el texto (ventana, asignado, conexión, `takeOver`). La cita elegida viaja con la nota.

### 8.3 Cola

- `OutboxEntry` gana `kind: 'text' | 'audio'` y, en audio, `durationMs`. `text` queda `''` en audio.
  En `localStorage` sólo viajan metadatos: el candado entre pestañas, `mergeOutbox` y
  `restoreAfterReload` no cambian. Una entrada vieja sin `kind` se lee como `'text'`.
- `utils/voice-blob-store.ts`: IndexedDB nativa (`qep-messaging-voice`, store `blobs`, clave
  `clientId`): `put`, `get`, `delete`, `clear`. Cada operación tolera que IndexedDB no exista o falle.
- El blob se borra al confirmar el envío, al descartar la entrada y en `clearMessagingOutbox`.
- Si al enviar el blob no está → la entrada queda fallida con «La nota de voz ya no está disponible.
  Grábala de nuevo.» y sólo se puede descartar.

### 8.4 Envío

- `services/messaging.api.ts`: `sendAudioMessage(tenantId, conversationId, { clientId, file,
  replyTo? })` con `FormData` y la ruta de §4.1. No fija `Content-Type`: el navegador pone el
  `boundary`.
- `outbox-drainer.ts` elige `sendMessage` o `sendAudioMessage` según `kind` y comparte
  `describeSendFailure`, reintento e invalidaciones. `describeSendFailure` agrega
  `messaging.media.upload_failed` → «No pudimos guardar la nota de voz. Reintenta.» y el 413.

### 8.5 Burbuja

- `message-bubble.tsx`: `kind === 'Audio'` con `media` se dibuja con `<audio controls preload="none">`
  sobre `media.url`, entrante o saliente.
- La burbuja optimista usa `URL.createObjectURL(blob)` y lo revoca al desmontar o al confirmarse.

## 9. Seguridad

- El formato se valida por contenido; el `Content-Type` del cliente no se usa para decidir nada.
- Límite de cuerpo por endpoint, no global: el resto de la API conserva el suyo.
- El audio va al bucket **privado** de R2 y se sirve sólo por el GET de media autenticado, con la
  doble capa de tenant y permiso de siempre (403, nunca 404).
- Ningún log lleva el token, los bytes ni la URL firmada.

## 10. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

### Backend

- `OggOpusSnifferTests`: OGG/Opus válido, OGG con Vorbis, WebM, MP4, vacío, menos de una página.
- `SendAudioMessageValidatorTests`: `clientId` faltante, archivo vacío, > 16 MB, formato inválido,
  `replyTo` vacío. Las claves de `errors` son `clientId`, `file` y `replyTo`.
- `SendAudioMessageHandlerTests` (dobles de `MessagingTestBed`): orden de guardas igual al texto;
  falla de R2 → 422 sin fila ni llamada a Meta; falla de subida a Meta → `Failed`; 190 en la subida →
  `NeedsAttention`; éxito → fila `Audio` con media; idempotencia devuelve la burbuja de audio;
  reintento de `Failed` reutiliza `messageId` y la clave de R2.
- `SendMessageHandlerTests` existentes: verdes sin modificar, tras extraer `OutboundGuards`.
- `WhatsAppCloudClientTests`: cuerpo multipart de `/media`; cuerpo de `audio` con `voice: true`, con y
  sin `context`, con `recipient` y con `to`.
- Integración (`Modules.Messaging.IntegrationTests`): multipart real con un OGG/Opus de prueba que
  decodifica; verifica `messages.kind = 4`, la fila de `message_media` con `stored_at`, que el GET de
  media devuelve los mismos bytes, 413 arriba del límite y CSRF exigido con auth real.

### Frontend (Vitest)

- `use-voice-recorder`: transiciones, corte a 5 min, permiso negado (recorder simulado).
- `voice-blob-store`: con `fake-indexeddb` (devDependency nueva); IndexedDB ausente no rompe.
- `message-outbox`: entrada vieja sin `kind` se lee como texto; entrada de audio sobrevive a recargar
  como fallida.
- `outbox-drainer`: audio exitoso borra el blob; audio fallido lo conserva; blob faltante → fallida.
- `message-composer`: micrófono con caja vacía; Enviar sólo tras detener (escucha previa).
- `messaging.api`: `sendAudioMessage` arma el `FormData` correcto.

## 11. Despliegue

Backend primero, frontend después: el front nuevo llama una ruta que el backend viejo no tiene. Sin
claves ni configuración nuevas. Sin migración.

## 12. Decisiones menores tomadas por el spec (a ratificar)

- **D-V1 (ratificada 2026-10-11):** la fila `Failed` también guarda `message_media`, para que la nota se escuche desde otro
  dispositivo y el reintento sobrescriba en lugar de duplicar.
- **D-V2:** `meta_media_id = ''` cuando la subida a Meta falló, para no migrar la columna a nullable.
- **D-V3:** se extraen las guardas comunes a `OutboundGuards` en vez de duplicar ~50 líneas de
  `SendMessageHandler`.
- **D-V4 (revisada 2026-10-11):** el owner pidió renombrar `SendTextResult` a `SendResult`: Messaging no está desplegado.
