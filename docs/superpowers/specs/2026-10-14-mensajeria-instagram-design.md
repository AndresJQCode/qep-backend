# Instagram como segundo canal de la bandeja de Messaging

**Fecha:** 2026-10-14
**Módulos:** Integrations (proveedor `instagram`, onboarding `InstagramLogin`, canje y refresco del
token, probador, ruta por cuenta), Messaging (canal en la conversación, webhook de Instagram,
parser e ingesta propios, cliente de Graph de Instagram, reglas de envío por canal), Customers
(`instagram_user_id`, `instagram_username`), Api/Bootstrapper (webhook, adaptadores, políticas),
Authorization (sin permisos nuevos).
**Estado:** **análisis y plan**, sin código. Las decisiones de §2 son propuestas; las marcadas
*a ratificar* las decide el owner antes de empezar. Lo que no se pudo verificar en la documentación
de Meta desde este entorno queda en §3 como **a verificar**, con el paso del plan que lo cierra.
**Depende de:** `2026-10-09-mensajeria-whatsapp-design.md` (todo el módulo Messaging),
`2026-10-10-mensajeria-asignacion-bsuid-design.md` (identidad por `user_id`, clientes incompletos,
asignación), `2026-10-08-integraciones-design.md` (conexiones, secretos, rutas),
`2026-10-11-contactos-plantillas-campanas-design.md` (plantillas, `POST /conversations`, campañas).
**Corrige:** el análisis del 2026-10-14 que se escribió desde `main` creyendo que no existía
bandeja de conversaciones; existía en `develop` desde `de85c57` (precedente del CLAUDE.md sobre
leer el otro checkout sin mirar la rama).

## 1. Contexto y alcance

QEP ya recibe y responde WhatsApp en `/messaging/conversations`: lo que una persona escribe al número
del tenant entra por el webhook de Meta, queda atado a un cliente, se asigna a una asesora y se
responde dentro de la ventana de 24 horas. El owner quiere lo mismo con Instagram: que los mensajes
directos (DM) a la cuenta de Instagram del tenant lleguen a **la misma bandeja** y se respondan desde
ahí. El spec de WhatsApp dejó Instagram explícitamente fuera de alcance («Fuera de alcance:
Messenger e Instagram»); este spec lo mete.

### Dentro de alcance

1. Conectar la cuenta profesional de Instagram del tenant desde **Ajustes → Integraciones**, con el
   flujo oficial de Meta (Business Login for Instagram), y guardar el token cifrado como el de
   WhatsApp Cloud.
2. Recibir los DM por webhook, firmados y deduplicados, y crearlos como conversaciones y mensajes
   de la misma bandeja, con cliente (incompleto si no existe), asignación, eventos del sistema,
   leído/no leído, resolver/reabrir, búsqueda y respuestas rápidas **sin cambios** para la asesora.
3. Responder con texto, imagen y PDF dentro de la ventana de 24 horas que abre el último mensaje de
   la persona. Marcar visto. Ver «visto» de lo que enviamos.
4. Mostrar lo que el negocio responde desde la **app de Instagram** (los *echo*), que Meta sí entrega
   en el mismo webhook: la bandeja deja de ser ciega a lo que se contesta desde el celular.
5. Mantener el token vivo: el de Instagram vence a los 60 días y se refresca; si no se puede, la
   conexión pasa a `NeedsAttention` como con WhatsApp.

### Fuera de alcance (v1)

- **Iniciar una conversación** desde QEP, plantillas y campañas por Instagram: Instagram **no tiene**
  mensajes iniciados por el negocio ni plantillas; sólo se responde (§3). `POST /conversations`,
  `/messages/template` y las campañas siguen siendo sólo de WhatsApp.
- La etiqueta `HUMAN_AGENT` (responder hasta 7 días después): exige un permiso aparte en App Review y
  reglas de uso que Meta audita. Queda como slice posterior (§13, D-I9).
- Notas de voz: Instagram acepta `aac`, `m4a`, `wav` y `mp4`, y la nota de QEP es OGG/Opus
  (spec 2026-10-11). Sin transcodificar no hay forma de mandarla. Slice posterior.
- Comentarios en publicaciones, respuestas privadas a comentarios, menciones en historias como
  disparador, reacciones salientes, «escribiendo…», *ice breakers* y menú persistente.
- Messenger (Facebook Page): misma plataforma, pero otro producto y otro login. No entra.

## 2. Decisiones propuestas (a ratificar por el owner)

| # | Decisión | Elegido | Por qué |
| --- | --- | --- | --- |
| 1 | **Qué API de Instagram** | **Instagram API with Instagram Login** (Business Login for Instagram): host `graph.instagram.com`, token de *Instagram User*, **sin Página de Facebook** | La otra variante (Facebook Login) exige que la cuenta esté vinculada a una Página y que quien conecta sea admin de esa Página: dos requisitos que muchos tenants no cumplen. Con Instagram Login el modelo es el de WhatsApp Cloud: una credencial por cuenta, cifrada en Integrations, y un webhook por app. Costo: el token vence a los 60 días y hay que refrescarlo (decisión 5); el login es por **redirect**, no por el popup del SDK (decisión 2) |
| 2 | Onboarding | Nuevo `ProviderOnboarding.InstagramLogin`: el frontend abre `https://www.instagram.com/oauth/authorize` en una ventana, Meta vuelve a una ruta del SPA con `code`, y el SPA manda `POST /integrations/instagram/connect` con el `code`. **El canje lo hace el backend**, nunca el navegador: lleva el app secret | Mismo reparto que Embedded Signup: el frontend sólo transporta el `code`; el `code` vale 1 hora y una sola vez |
| 3 | Frontera entre módulos | **Integrations** es dueño del proveedor `instagram`, del canje, del refresco, del probador y de la ruta `(instagram, igUserId)`. **Messaging** es dueño del webhook, la ingesta, el envío y las reglas por canal | Decisión 1 del spec de WhatsApp, sin cambios |
| 4 | Canal como dato de la conversación | Columna `channel` en `messaging.conversations` (`WhatsApp` \| `Instagram`, texto), que la conexión determina y la ingesta escribe. `user_id` guarda el IGSID en Instagram y el BSUID en WhatsApp; `wa_id` queda `NULL`; `username` y `profile_name` se llenan del perfil | La bandeja es una sola y la lista mezcla canales; la fila, el hilo y el compositor necesitan saber el canal **sin** mirar la conexión. Un módulo `InstagramMessaging` aparte duplicaría asignación, clientes, búsqueda y respuestas rápidas |
| 5 | Token | Token de larga duración (60 días) cifrado en `connection_secrets` como `accessToken`, con `tokenExpiresAt` en `fields`. Un worker diario lo refresca cuando le quedan menos de 15 días; si Meta lo rechaza, la conexión pasa a `NeedsAttention` con `token_expired` y la persona vuelve a conectar | Meta sólo refresca tokens con más de 24 h y no vencidos; dejarlo vencer obliga a reconectar. Con refresco diario, un pod caído una semana no lo pierde |
| 6 | Webhook | **Ruta propia** `GET/POST /api/webhooks/instagram`, misma política de limitador `Webhook`, misma exención de CSRF, misma tabla `webhook_deliveries` con una columna `provider` nueva; el worker de entregas elige el parser por `provider` | El cuerpo de Instagram tiene otra forma (`object: "instagram"`, `entry[].messaging[]`) y otra identidad de cuenta (`entry[].id` = id de la cuenta de Instagram). Una ruta por producto evita un `switch` por `object` en el único endpoint caliente, y Meta deja configurar un callback por producto |
| 7 | Estados de entrega | Saliente: `Sent` al 2xx de Graph; `Read` al `messaging_seen`; **nunca** `Delivered`. Entrante: `Delivered` fijo (D-M5) | Instagram no manda `sent`/`delivered`; sólo «visto». El frontend ya dibuja por estado; sólo cambia qué estados aparecen |
| 8 | Echo | Lo que el negocio responde desde la app de Instagram llega con `is_echo: true` y se guarda como `Outbound` **sin `sentBy`**, con `Sent`. Si el `mid` ya existe (lo mandó QEP), no inserta nada | Es el `smb_message_echoes` que WhatsApp todavía no suscribe (D-M13), pero en Instagram viene en el mismo campo `messages` y no se puede apagar. Ignorarlo dejaría el hilo incompleto |
| 9 | Ventana cerrada | Mismo `messaging.window_closed`; el compositor en Instagram no ofrece plantillas, explica que sólo se puede responder cuando la persona vuelva a escribir | No hay plantillas en Instagram (§3). `customerWindowExpiresAt` se calcula igual |
| 10 | Clientes | Todo el que escribe por Instagram queda atado a un cliente, incompleto si no existe (spec 2026-10-10). Customers gana `instagram_user_id` (IGSID, único por tenant) e `instagram_username`. Sin teléfono, no hay emparejamiento automático con un cliente existente: la asesora lo vincula desde el hilo si ya lo tiene | El IGSID es estable por cuenta de Instagram del negocio; el `username` cambia. Sin la columna, reconectar la cuenta crearía clientes repetidos (D-M11 aplicado a Instagram) |
| 11 | Adjuntos salientes | Imagen JPEG/PNG (se recomprime como hoy) y **sólo PDF** como documento, hasta 25 MB; `doc`, `xls` y notas de voz se rechazan con 422 `messaging.media.unsupported_format` en una conversación de Instagram | Es la tabla de tipos de Meta para Instagram (§3). La política pasa a ser por canal (`OutboundMediaPolicy.For(channel)`) |
| 12 | Cómo le llega el adjunto a Meta | Meta **descarga** el adjunto desde una URL que QEP le da; no hay subida previa como `POST /{phone_number_id}/media`. Se usa una URL prefirmada de R2 de 10 minutos sobre la clave privada que ya existe; si la prueba contra Meta real falla (§3, V-4), se cae a una copia pública con clave aleatoria como la del PDF de cotizaciones | La imagen por URL está documentada; el id de adjunto subido es reciente (marzo 2026) y sólo para imágenes |
| 13 | Texto | Tope de **1000 caracteres** en Instagram (4096 sigue en WhatsApp), validado por canal | Límite de Meta para Instagram (§3, con una duda sobre 1000 vs 2000: V-3) |
| 14 | Marcar leído | `POST …/read` deja `unread_count` en 0 siempre; el acuse a Meta (`sender_action: mark_seen`) se manda sólo si V-5 lo confirma para Instagram; si no, no se manda y nada más cambia | Hoy el acuse es un *best effort* con 5 s de timeout; en Instagram puede no existir |
| 15 | Pipeline | `channel` viaja en la lectura de conversaciones que Pipeline hace por `MessagingDbContext`; el triage no cambia | Un negocio nace igual de un DM que de un WhatsApp |

## 3. Lo que se verificó en la guía oficial de Instagram (2026-10-14)

Leído de la documentación de Meta para desarrolladores a través de resultados de búsqueda
restringidos a `developers.facebook.com`: este entorno no pudo abrir las páginas directo. Cada fila
dice su fuente; lo que no se pudo cerrar queda como **V-n (a verificar)** y tiene un paso en §12 que
lo cierra **antes** de escribir el código que dependa de ello.

| Tema | Lo verificado | Fuente |
| --- | --- | --- |
| Dos variantes de la API | *Instagram API with Instagram Login* (token de Instagram User, host `graph.instagram.com`, **no** exige Página de Facebook) e *Instagram API with Facebook Login* (token de Página, host `graph.facebook.com`, exige Página vinculada y que quien conecta tenga rol de admin). «Una sola variante por app»: si se quieren las dos, son dos apps | https://developers.facebook.com/docs/instagram-platform/overview/ y https://developers.facebook.com/docs/instagram-platform/instagram-api-with-instagram-login/ |
| Permisos | `instagram_business_basic` e `instagram_business_manage_messages` (reemplazan a `business_basic` y `business_manage_messages`). Con la app en modo desarrollo sólo se puede escribir a personas con rol en la app; salir a público exige **App Review** | https://developers.facebook.com/docs/instagram-platform/create-an-instagram-app/ |
| Business Login | La ventana de autorización devuelve un `code` (vale 1 hora, un solo uso) al `redirect_uri`. `POST https://api.instagram.com/oauth/access_token` lo canjea por un token **corto** (1 hora) más el id de usuario de Instagram y los permisos; `GET https://graph.instagram.com/access_token?grant_type=ig_exchange_token` (con el app secret, del lado del servidor) lo cambia por uno **largo de 60 días** | https://developers.facebook.com/documentation/instagram-platform/instagram-api-with-instagram-login/business-login y https://developers.facebook.com/docs/instagram-platform/reference/access_token/ |
| Refresco | `GET https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token`: sólo si el token tiene **más de 24 h**, no venció y la persona concedió `instagram_business_basic`. El nuevo vale 60 días desde el refresco. Uno que pase 60 días sin refrescar vence y ya no se refresca | https://developers.facebook.com/docs/instagram-platform/reference/refresh_access_token/ |
| Cuenta | Cuenta **profesional** (business o creator) | Overview |
| Quién abre la conversación | **La persona.** «Your app has 24 hours to respond to any message sent from an Instagram user». No hay plantillas de inicio; *One-Time Notifications* y *Sponsored Messages* **no existen** en Instagram | https://developers.facebook.com/docs/instagram-platform/instagram-api-with-instagram-login/messaging-api/ y https://developers.facebook.com/documentation/business-messaging/messenger-platform/policy |
| `HUMAN_AGENT` | `messaging_type: "MESSAGE_TAG"`, `tag: "HUMAN_AGENT"`: una **persona** responde hasta 7 días después, sólo para asuntos que no se resolvieron en 24 h, sin contenido promocional ni automatizado. Exige pedir el permiso *Human Agent* en el dashboard | Misma política y https://developers.facebook.com/docs/messenger-platform/reference/send-api/ |
| Enviar | `POST /<IG_ID>/messages` (o `/me/messages`) con `recipient: { id: <IGSID> }` y `message: { text }` o `message: { attachment: { type: image \| audio \| video \| file, payload: { url } } }`; hasta 10 imágenes por envío; respuesta `{ recipient_id, message_id }` | Send Messages |
| Adjuntos | Audio `aac`, `m4a`, `wav`, `mp4` (25 MB); imagen `png`, `jpeg` (8 MB); video `mp4`, `ogg`, `avi`, `mov`, `webm` (25 MB); archivo **sólo PDF** (25 MB) | Send Messages |
| Reacciones salientes | `sender_action: "react"` con `payload: { message_id, reaction }` | Send Messages |
| Destinatario | Siempre el **IGSID** (Instagram-scoped ID) que llega en el webhook; no existe mandar a un `@usuario` | Send Messages |
| Webhook: forma | `object: "instagram"`, `entry[].id` = id de la cuenta profesional, `entry[].time`, `entry[].messaging[]` con `sender.id`, `recipient.id`, `timestamp`, y `message` o `postback` o `read` o `reaction` | https://developers.facebook.com/docs/graph-api/webhooks/reference/instagram y https://developers.facebook.com/documentation/instagram-platform/self-messaging.md |
| Webhook: `message` | `mid`, `text`, `attachments[]{type, payload.url}`, `is_echo`, `is_deleted`, `quick_reply`, `reply_to`, `story{url, id}`. Tipos de adjunto: `audio`, `file`, `image`, `video`, `fallback`, `reel`, `ig_reel`, `post`, `ig_post`, `appointment_booking`. Desde el 2025-11-03 una publicación compartida llega con `share` **y** `ig_post`; `share` se retira el 2026-02-01 | Webhooks Reference: Instagram y changelog |
| Webhook: campos | `messages`, `messaging_optins`, `messaging_postbacks`, `messaging_reactions`, `messaging_referrals`, `messaging_seen`, `message_edit`. **Los echo vienen dentro de `messages`** (no hay `message_echoes` en Instagram). `messaging_seen` es sólo de Instagram | Webhooks Reference y Messenger webhooks |
| Firma y verificación | `X-Hub-Signature-256` (HMAC-SHA256 del cuerpo con el app secret), `hub.challenge` en el `GET` de verificación; si la entrega falla una hora seguida, Meta **desuscribe** la app y hay que resuscribir | https://developers.facebook.com/documentation/business-messaging/messenger-platform/webhooks |
| Suscripción | Con Instagram Login los campos se eligen en el **dashboard** de la app (`/{app-id}/subscriptions` no sirve para Instagram) | Webhooks - Instagram Platform |
| Límites | Send API: 300 llamadas/s por cuenta profesional para texto, enlaces, reacciones y stickers (changelog; la página vieja dice 100); audio y video, 10/s; Conversations API, 2/s | https://developers.facebook.com/documentation/business-messaging/messenger-platform/overview/rate-limiting y changelog |
| Perfil del usuario | *User Profile API* por IGSID: nombre, foto, seguidores, verificado, si sigue al negocio y si el negocio lo sigue; `username` desde Graph v14 | Changelog 2021-09-14 y posteriores |
| Errores | `{ error: { message, type, code, error_subcode, fbtrace_id } }`; `190` = token vencido o revocado (como en WhatsApp) | Spec de WhatsApp §3 |

**A verificar antes de codificar lo que depende de cada uno:**

| Id | Duda | Quién la cierra | Si sale distinto |
| --- | --- | --- | --- |
| V-1 | Si la app de Meta que ya tiene WhatsApp puede sumar el producto *Instagram* con **Instagram Login**, y si el *Instagram App ID / App Secret* que aparecen ahí son **distintos** del `Meta:App:AppId/AppSecret` (la documentación los muestra como ids propios). Decide con qué secreto se firma `/api/webhooks/instagram` | Owner, en el dashboard (§12 paso 0) | Sección `Meta:Instagram:*` con sus propias claves; si son las mismas, se reusa `Meta:App` |
| V-2 | Cómo se suscribe **cada cuenta** conectada al webhook con Instagram Login: `POST /<IG_ID>/subscribed_apps?subscribed_fields=messages,messaging_seen,messaging_postbacks,messaging_reactions` con el token de la cuenta, o basta con la suscripción del dashboard. Y si la persona tiene que activar «Permitir acceso a mensajes» en la configuración de su Instagram (*Message controls → Connected tools*) | Prueba manual con la primera cuenta real (§12 paso 0) | Un paso más en el handler de conexión y un texto en la pantalla de conexión que lo pida |
| V-3 | Tope del texto: 1000 caracteres (tabla de Instagram) o 2000 (Send API de Messenger) | Prueba manual | Una constante |
| V-4 | Si Meta descarga una **URL prefirmada de R2** (query string de SigV4) como adjunto. Con WhatsApp vía Zenvia no pudo (`IQuotationPdfStorage.PublishAsync`) | Prueba manual con imagen y PDF | Copia pública con clave aleatoria y lifecycle, como las cotizaciones |
| V-5 | Si `sender_action: mark_seen` (y `typing_on`) aplica a Instagram con Instagram Login | Prueba manual | Decisión 14: sin acuse |
| V-6 | Si Meta exige `https` en el `redirect_uri` también para `localhost` | Dashboard | Túnel para probar en local (`ngrok` o similar) |
| V-7 | Si `GET /<IGSID>?fields=name,username,profile_pic` responde con el token de Instagram User (hay reportes de `username` ausente según versión) | Prueba manual | `profile_name` se llena con lo que venga; `username` opcional |

## 4. Trazabilidad: historias → componentes

| # | Historia | Componentes |
| --- | --- | --- |
| 1 | Como admin, conecto la cuenta de Instagram de mi empresa desde Integraciones con el login de Meta y la veo `Active` con su `@usuario` | `IntegrationProviders.Instagram`, `ProviderOnboarding.InstagramLogin`, `ConnectInstagramHandler`, `IInstagramLoginGateway` + `InstagramGraphClient` (Integrations.Infrastructure), ruta `(instagram, igUserId)`, frontend `integrations/` |
| 2 | Como admin, pruebo, pauso, reanudo y elimino la conexión como las demás | `InstagramConnectionTester`, lifecycle existente |
| 3 | Como plataforma, el token se refresca solo; si no se puede, la conexión queda `NeedsAttention` | `InstagramTokenRefreshWorker` (Integrations.Infrastructure), `tokenExpiresAt` |
| 4 | Como asesora, un DM a la cuenta conectada aparece en la bandeja como conversación, con nombre y `@usuario`, atado a un cliente | `/api/webhooks/instagram`, `InstagramWebhookPayloadParser`, `InstagramInboundIngestion` (misma SQL con `channel`), `IMessagingCustomerDirectory.EnsureAsync` (Instagram), `IInstagramClient.GetProfileAsync` |
| 5 | Como asesora, veo texto, imágenes, videos, audios, PDF, publicaciones y reels compartidos, respuestas a historias y reacciones | Mapa de tipos de Instagram → `MessageKind` (+ `Share` nuevo), `details`, copia de medios a R2 con el worker existente (por URL, sin token) |
| 6 | Como asesora, respondo con texto, imagen o PDF mientras la ventana esté abierta; fuera de ella el compositor me lo explica | `SendMessageHandler` + `IInstagramClient.SendTextAsync/SendAttachmentAsync`, `OutboundMediaPolicy.For(channel)`, `messaging.window_closed`, frontend `message-composer` |
| 7 | Como asesora, veo cuándo la persona vio mi mensaje | `messaging_seen` → `Read` sobre salientes anteriores o iguales a `read.mid` |
| 8 | Como asesora, veo en el hilo lo que un compañero respondió desde la app de Instagram | Echo → `Outbound` sin `sentBy` |
| 9 | Como asesora, la lista y el hilo me dicen por qué canal es cada conversación, y filtro por canal | `channel` en `ConversationSummary`, filtro `channel=` en la lista, frontend `conversation-list-item`, `thread-header` |
| 10 | Como asesora, no me ofrecen iniciar conversación, plantillas ni notas de voz en Instagram | `GET /messaging/connections` con `channel`; `POST /conversations` → 422 `messaging.channel_unsupported` con una conexión de Instagram; compositor por canal |
| 11 | Como asesora, un mensaje que la persona borró se ve como «Mensaje eliminado» | `is_deleted` → `details.deleted = true`; burbuja |
| 12 | Como operador, asignación, resolver/reabrir, no leído, búsqueda, respuestas rápidas y Pipeline funcionan igual en Instagram | Sin cambios de código: la conversación es la misma entidad |

## 5. Contratos HTTP

Base: `/api/v1/tenants/{tenantId}`. Enums por nombre. Errores en ProblemDetails con `code`, como
todo el repo. **Lo que no se nombra acá no cambia.**

### 5.1 Integrations

**`GET /integrations/catalog`** — gana un proveedor:

```json
{ "key": "instagram", "displayName": "Instagram (Meta)", "category": "Messaging",
  "fields": [
    { "key": "username", "label": "Usuario", "kind": "Text", "required": false, "maxLength": 64 },
    { "key": "accountName", "label": "Nombre de la cuenta", "kind": "Text", "required": false, "maxLength": 256 }
  ],
  "maxConnections": 5, "connectionCount": 0,
  "onboarding": { "kind": "InstagramLogin", "appId": "…", "authorizeUrl": "https://www.instagram.com/oauth/authorize",
                  "scopes": ["instagram_business_basic", "instagram_business_manage_messages"] } }
```

`appId` es el *Instagram App ID* (V-1). El `redirect_uri` lo arma el SPA con su propio origen
(`/settings/integrations/instagram/callback`) y el backend lo exige **idéntico** en el canje: Meta
rechaza el `code` si no coincide con el de la autorización.

**`ConnectionResponse` de una conexión `instagram`:**

```json
{ "id": "…", "providerKey": "instagram", "name": "Instagram Ventas", "status": "Active",
  "fields": { "username": "origenbotanico", "accountName": "Origen Botánico",
              "igUserId": "17841400000000000", "tokenExpiresAt": "2026-12-13T10:00:00Z" },
  "secrets": { "accessToken": { "configured": true, "updatedAt": "…", "readable": true } },
  "lastVerifiedAt": "…", "lastFailureAt": null, "lastFailureCode": null, … }
```

`igUserId` y `tokenExpiresAt` son internos (los escribe el backend; `POST`/`PUT` los rechazan).
`lastFailureCode` posibles: `token_expired`, `account_not_professional`, `permissions_missing`.

**`POST /integrations/instagram/connect`** — permiso `integrations.connection.manage`.

```json
{ "name": "Instagram Ventas", "code": "AQB…", "redirectUri": "https://qep.qcode.co/settings/integrations/instagram/callback" }
```

- 201 `ConnectionResponse` con la conexión `Active`.
- 422: `integrations.instagram.code_exchange_failed`, `integrations.instagram.account_not_professional`,
  `integrations.instagram.permissions_missing` (la persona desmarcó un permiso en la ventana de Meta),
  `integrations.instagram.account_already_connected` (índice único de `connection_routes`: una cuenta
  no vive en dos tenants), `integrations.connection.limit_reached`, `integrations.connection.name_taken`,
  `validation.failed` (`name`, `code`, `redirectUri`).

**Webhook de Instagram** — fuera de `/api/v1/tenants/*`, sin sesión:

- `GET /api/webhooks/instagram?hub.mode=subscribe&hub.verify_token=…&hub.challenge=…` → 200 con el
  `hub.challenge` si el token coincide; 403 si no.
- `POST /api/webhooks/instagram` con `X-Hub-Signature-256` → 200 vacío si la firma es válida; 401 si no.
  Campos suscritos: `messages`, `messaging_seen`, `messaging_postbacks`, `messaging_reactions`.

### 5.2 Messaging

**`ConversationSummary`** gana `"channel": "WhatsApp" | "Instagram"`. En Instagram, `contact` viaja
`{ "userId": "<IGSID>", "waId": null, "username": "laura.perez", "profileName": "Laura Pérez" }`.
**BFF:** `channel` viaja aunque se pueda deducir de `connectionId` porque la fila de la lista dibuja el
ícono del canal y el compositor decide qué ofrecer, y ninguno de los dos tiene la conexión a mano.

**`GET /messaging/conversations`** acepta `channel=WhatsApp|Instagram` (opcional; ausente = todas).
`counts` no cambia.

**`GET /messaging/connections`** — cada conexión gana `"channel"`; las de Instagram traen
`"username"` en vez de `"displayPhoneNumber"`:

```json
{ "id": "…", "name": "Instagram Ventas", "channel": "Instagram", "displayPhoneNumber": null, "username": "origenbotanico" }
```

**`Message`** gana el `kind` **`Share`** (publicación, reel o historia compartida) y, en `media`, el
mismo `url` servido por QEP cuando el medio se copió. Un `Share` trae `text` con la URL que Meta
manda y `media` `null` (D-I5). Un mensaje borrado por la persona viaja con `"deleted": true` (campo
nuevo, `false` en los demás).

**`POST …/conversations/{id}/messages`** en Instagram: `text` hasta **1000** caracteres (`validation.failed`
en `text` si pasa). `replyTo` se acepta y se guarda, pero **no viaja a Meta** (Instagram no cita en el
envío); la burbuja sí lo dibuja.

**`POST …/messages/media/upload-session`** y **`POST …/messages/media`** en Instagram: sólo `.jpg`,
`.jpeg`, `.png` y `.pdf`, 25 MB; lo demás 422 `messaging.media.unsupported_format` o `too_large`.

**`POST …/messages/audio`** en Instagram → 422 `messaging.channel_unsupported`.

**`POST /conversations`**, **`POST …/messages/template`** y campañas con una conexión de Instagram →
422 `messaging.channel_unsupported`. `GET …/connections/{id}/templates` → `[]`.

**Nuevo código:** `messaging.channel_unsupported` («Esta acción no está disponible en Instagram.»).

## 6. Diseño por módulo

### 6.1 Integrations

- **Catálogo.** `IntegrationProviders.Instagram` (`"instagram"`, categoría `Messaging`,
  `ConsumingModules = [messaging]`, tope 5, `onboarding: InstagramLogin`). Campos públicos `username`,
  `accountName`; internos `igUserId`, `tokenExpiresAt`; secreto interno `accessToken`. Migración que
  cambia `CK_connections_provider_key` a `('zenvia','whatsapp-cloud','instagram')` (D10 del spec de
  Integrations).
- **`ProviderOnboarding.InstagramLogin`** y su DTO en el catálogo (`appId`, `authorizeUrl`,
  `scopes`). Sale sólo si `Meta:Instagram` está configurado (como `whatsapp-cloud` con `Meta:App`,
  D-M3).
- **`ConnectInstagramHandler`** (Application), pasos: validador → tenant y permiso → tope y nombre →
  `IInstagramLoginGateway.ExchangeCodeAsync(code, redirectUri)` (token corto + `user_id` + permisos;
  si faltan los dos permisos → `permissions_missing`) → `ExchangeLongLivedAsync` (60 días) →
  `GetMeAsync` (`id, username, name, account_type`; `account_type` distinto de `BUSINESS`/`MEDIA_CREATOR`
  → `account_not_professional`) → `SubscribeAsync` (V-2) → `IntegrationConnection.Create` +
  `IntegrationConnectionRoute.Create("instagram", igUserId, accountId: null)` en la misma
  transacción (el índice único traduce a `account_already_connected`) → auditoría
  `integrations.connection.created` con `onboarding: instagram_login`.
- **`InstagramGraphClient`** (Infrastructure): cliente con nombre `integrations.instagram-graph`,
  base `https://graph.instagram.com/`, mismo patrón que `MetaGraphClient` (sin redirecciones, 10 s,
  sin logs de `HttpClient`). El canje del `code` va contra `https://api.instagram.com/oauth/access_token`
  en `application/x-www-form-urlencoded` con `client_secret`: **nunca** en un log.
- **`InstagramConnectionTester`**: `GET /me?fields=id,username,name` con el token; refresca `username` y
  `accountName`; `190` → `token_expired`. Registrado en `ConnectionTesterRegistry`.
- **`InstagramTokenRefreshWorker`**: cada `Integrations:Instagram:RefreshIntervalMinutes` (default
  1440), toma las conexiones `instagram` `Active` o `NeedsAttention` con `tokenExpiresAt` a menos de
  15 días y más de 24 h desde su emisión, llama `refresh_access_token`, reemplaza el secreto y
  `tokenExpiresAt`; una falla `190` reporta `token_expired` por `IConnectionHealthReporter`. Mismo
  molde que `ConnectionSecretRekeyWorker`.
- **Opciones.** Sección `Meta:Instagram` (`AppId`, `AppSecret`, `GraphApiVersion`) con validador
  estricto en `Production`. Si V-1 dice que son las mismas claves de `Meta:App`, la sección sobra y se
  reusa.
- **`PUT`** de una conexión `instagram` sólo cambia `name` (como `whatsapp-cloud`, D-M4).

### 6.2 Messaging

- **Dominio.** `Channel` (`WhatsApp`, `Instagram`) en `Conversation`, con `StartWithUserId` que recibe
  el canal; `IsValidUserId` pasa a ser por canal: BSUID en WhatsApp, `^[0-9]{1,64}\z` en Instagram.
  `MessageKind.Share` (14). `WindowLength` sigue en 24 h para los dos.
- **Puertos nuevos.** `IInstagramClient` (`SendTextAsync`, `SendAttachmentAsync(type, url)`,
  `MarkSeenAsync` si V-5, `GetProfileAsync(igsid)`), con `InstagramSender(IgUserId, AccessToken)`.
  `IMessagingConnectionDirectory` gana `FindInstagramRouteAsync(igUserId)` y
  `ResolveInstagramSenderAsync(tenantId, connectionId)`, y `ListActiveAsync` devuelve el canal y el
  `username`. `IMessagingCustomerDirectory.EnsureAsync` recibe el canal: en Instagram empareja por
  `instagram_user_id` y, si no hay, crea el cliente incompleto con nombre = `profileName` o `@username`.
- **Webhook.** `InstagramWebhookEndpoints` (`/api/webhooks/instagram`), reusa `WebhookBodyReader`,
  `IWebhookSignatureVerifier` (con el secreto de V-1) y `IWebhookDeliveries.EnqueueAsync(provider:
  "instagram", body)`. `webhook_deliveries` gana `provider` (`'whatsapp'` por default para las filas
  existentes). `WebhookDeliveryProcessor` despacha por `provider` a `WebhookPayloadParser` o a
  `InstagramWebhookPayloadParser`.
- **Parser de Instagram** (`InstagramWebhookPayloadParser`, tolerante como el de WhatsApp): por cada
  `entry[].messaging[]`:
  - `message` sin `is_echo` → `InboundMessage` con `userId = sender.id`, `wamid = mid`, `occurredAt =
    timestamp`, `text`, adjuntos → el primero decide el `kind` (`image`, `video`, `audio`, `file` →
    `Document`; `share`, `ig_post`, `ig_reel`, `reel`, `post` → `Share`; `fallback` y lo demás →
    `Unsupported`) y los demás adjuntos van a `details`. `story` → `Text` con `details.story`.
    `quick_reply` → `Interactive`. `is_deleted` → `details.deleted = true` (y si el `mid` ya existe,
    `UPDATE details`). `reply_to.mid` → `reply_to_wamid`.
  - `message` con `is_echo` → `OutboundEcho` (`recipient.id` = la persona, `mid`, `text`, adjuntos).
  - `read` → `SeenUpdate(mid)`. `reaction` → `InboundMessage` `Reaction` (`react`) o un `UPDATE` que
    quita la reacción (`unreact`). `postback` → `Interactive` con `details.postback`.
  - La cuenta es `entry[].id` (id de la cuenta profesional del tenant): es la **ruta**.
- **Ingesta.** La misma transacción de tres sentencias (spec 2026-10-09 §7.5) con `channel` en el
  `INSERT` de la conversación y `wa_id = NULL`. Antes de la primera inserción de una conversación
  nueva se pide el perfil (`GetProfileAsync`, V-7) para `profile_name` y `username`; si Meta no
  responde, entra sin nombre y el probador lo rellena después. El echo inserta `Outbound`, `Sent`,
  `sent_by_member_id = NULL`, y mueve `last_message_*` sin tocar `unread_count` ni la ventana; `ON
  CONFLICT (connection_id, wamid) DO NOTHING` lo hace idempotente contra nuestro propio envío.
- **Medios entrantes.** `payload.url` de Meta se descarga **sin token** (es una URL pública con
  vencimiento) con el `MediaCopyWorker` existente; `sha256` no viene → se guarda `sin verificar` con el
  warning que ya existe. `message_media.meta_media_id` guarda la URL truncada o el `mid`; el worker
  recibe la fuente como URL en vez de `GET /<MEDIA_ID>`.
- **Envío.** `SendMessageHandler` y `SendMediaMessageHandler` resuelven el canal de la conversación y
  despachan a `IWhatsAppCloudClient` o `IInstagramClient`; `OutboundGuards` no cambia (ventana,
  asignación, reclamo, idempotencia son iguales). `SendTarget` gana `SendToInstagramUser(igsid)`.
  El validador de texto lee el tope por canal (`SendMessageValidator` recibe el canal desde la guarda,
  o el handler valida el largo después de cargar la conversación: se decide en el plan, D-I3).
  En Instagram `SendMediaMessageHandler` **no sube** a Meta: firma la URL de R2 (decisión 12) y la
  manda como `attachment.payload.url`. `SendAudioMessageHandler` responde `channel_unsupported`.
- **Estados.** `messaging_seen` marca `Read` todos los `Outbound` de la conversación con `occurred_at
  <=` el del `read.mid` (si el `mid` no está, todos los anteriores al `timestamp` del evento). Mismo
  `UPDATE` monótono que `StatusIngestion`.
- **Lecturas.** `ConversationSummaryBuilder` y `MessageQueries` copian `channel`; el filtro `channel=`
  entra al índice `IX_conversations_tenant_status_activity` con una condición más (si el plan de
  consulta se degrada, índice parcial por canal: §7).
- **Guardas por canal.** `StartConversationHandler`, `SendTemplateMessageHandler`, campañas y
  `ListTemplatesHandler` comprueban el proveedor de la conexión y responden `channel_unsupported` o
  lista vacía.
- **`MessageFailureReasons`** gana los códigos de Instagram que se confirmen en la prueba (al menos
  `190` token, `551` persona no disponible, `10` permiso/ventana, `100` parámetro). `MetaErrorCodes`
  agrega los que decidan algo (ventana cerrada en Instagram no es `131047`).

### 6.3 Customers (delta)

`instagram_user_id` (hasta 64, único por tenant, `IX_customers_tenant_instagram_user_id`) e
`instagram_username` (hasta 64, `trgm` para la búsqueda de la lista). Los pone la ingesta de Messaging
por `IMessagingCustomerDirectory`; **nunca** viajan por el `PUT` del cliente (como `WhatsAppUserId`).
`CustomerDto` los expone en lectura para que la ficha muestre el `@usuario` y enlace a
`instagram.com/<username>`. El Excel de clientes gana la columna `Instagram` en la exportación; la
importación no la lee (es un dato que sólo Meta da).

### 6.4 Api / Bootstrapper

- `MapInstagramWebhook` con el mismo `RateLimiterPolicies.Webhook` y la misma exención de CSRF por
  ruta (`RequireCsrfHeaderMiddleware` recibe la lista de rutas exentas).
- Adaptadores: `MessagingConnectionDirectory` resuelve los dos proveedores; `MessagingCustomerDirectory`
  gana el emparejamiento por `instagram_user_id`.
- `AddAuthorization`: sin políticas nuevas (se reusan `messaging.conversation.*` e
  `integrations.connection.*`).

### 6.5 Frontend (`qep-frontend`)

| Dónde | Cambio |
| --- | --- |
| `features/integrations/` | `onboarding.kind === 'InstagramLogin'` dibuja «Conectar con Instagram»: abre la URL de autorización en una ventana (`window.open`, sin SDK), con `state` aleatorio guardado en `sessionStorage`. Ruta nueva `routes/_authenticated/settings/integrations/instagram/callback.tsx`: lee `code` y `state`, llama `connectInstagram`, cierra la ventana y refresca catálogo y lista. Tarjeta de conexión con `@usuario` y vencimiento del token. Estados: carga, Meta canceló, `permissions_missing`, `account_not_professional`, `account_already_connected`, éxito |
| `features/messaging/types/messaging.ts` | `channel` en `ConversationSummary` y `MessagingConnection`; `MESSAGE_KINDS` + `'Share'`; `Message.deleted`; `CHANNEL_LABELS` |
| `conversation-list-item.tsx`, `thread-header.tsx` | Ícono del canal junto al nombre; en Instagram, `@usuario` en vez de teléfono; `showConnection` cuando hay más de una conexión de **cualquier** canal |
| `conversation-list.tsx` / `inbox-layout.tsx` | Filtro por canal (chips Todas · WhatsApp · Instagram) sobre `channel=` |
| `message-composer.tsx` | Por canal: sin micrófono, sin plantillas, tope 1000 con contador, adjuntos sólo imagen y PDF; aviso de ventana cerrada propio («Solo puedes responder cuando la persona vuelva a escribir») |
| `message-bubble.tsx` | `Share` con enlace a la publicación; «Mensaje eliminado»; `Read` como «Visto» en Instagram; sin `Delivered` |
| `new-conversation-dialog.tsx`, campañas | Sólo conexiones `channel === 'WhatsApp'` |
| `messaging.api.ts` | `channel` en `fetchConversations`; `messaging.channel_unsupported` en `describeMessagingFailure` |
| Pruebas | Cada pantalla tocada cubre carga, vacío, error y éxito con el canal nuevo; `render-route` para el callback |

## 7. Base de datos

| Tabla | Cambio |
| --- | --- |
| `messaging.conversations` | `channel text NOT NULL DEFAULT 'WhatsApp'` + `CK_conversations_channel IN ('WhatsApp','Instagram')`; el default se quita en una migración posterior (mismo truco que `completeness`). Índice parcial `IX_conversations_tenant_channel_activity (tenant_id, channel, status, last_activity_at DESC, id)` sólo si el filtro lo necesita |
| `messaging.messages` | `CK_messages_kind` admite `14` (`Share`). `details.deleted`, `details.story`, `details.postback`, `details.attachments` en `jsonb`: sin columnas |
| `messaging.message_media` | `source_url text NULL` para el entrante de Instagram (Meta no da `MEDIA_ID`) |
| `messaging.webhook_deliveries` | `provider text NOT NULL DEFAULT 'whatsapp'`; la deduplicación por SHA-256 sigue siendo global |
| `integrations.connections` | `CK_connections_provider_key` con `'instagram'` |
| `customers.customers` | `instagram_user_id`, `instagram_username` e índices |

Migraciones: `AddInstagramProvider` (Integrations), `AddConversationChannel` (Messaging),
`AddCustomerInstagram` (Customers). Las tres con el factory de diseño (CLAUDE.md).

## 8. Flujos

### 8.1 Conectar Instagram

1. SPA: «Conectar con Instagram» → `window.open(authorizeUrl?client_id&redirect_uri&response_type=code&scope&state)`.
2. Meta: la persona entra con su Instagram profesional y acepta los dos permisos → redirige a
   `redirect_uri?code=…&state=…`.
3. SPA (ruta `callback`): comprueba `state`, `POST /integrations/instagram/connect { name, code, redirectUri }`.
4. Backend: canje corto → largo → `/me` → suscripción (V-2) → conexión + ruta + auditoría → 201.
5. SPA: cierra la ventana, refresca; la conexión aparece `Active` con `@usuario`.

Un `code` vencido o reusado → `code_exchange_failed`; la pantalla ofrece volver a intentar (abre de
nuevo la ventana).

### 8.2 Recibir un DM

1. Meta → `POST /api/webhooks/instagram` → firma → `webhook_deliveries(provider='instagram')` → 200.
2. Worker: parser de Instagram → ruta por `entry[].id` (`FindInstagramRouteAsync`, con la caché de 60 s)
   → `Paused` o módulo apagado descarta entrantes y aplica `read` (decisión 7 del spec de WhatsApp) →
   cliente (`EnsureAsync` por `instagram_user_id`) → perfil si la conversación es nueva → ingesta SQL
   con `channel = 'Instagram'` → medios a la cola de copia.

### 8.3 Responder

1. `POST …/messages` → guardas (ventana 24 h, asignación, reclamo) → `IInstagramClient.SendTextAsync`
   (`POST graph.instagram.com/{v}/{igUserId}/messages`, `recipient.id = user_id`) → `Sent` con `wamid =
   message_id` → 201.
2. Imagen o PDF: sesión de subida y confirmación como hoy; en vez de `POST /media` a Meta, URL
   prefirmada de R2 (decisión 12) en `attachment.payload.url`.
3. `messaging_seen` → `Read`.

### 8.4 Refrescar el token

Worker diario: `refresh_access_token` con el token vigente → secreto nuevo + `tokenExpiresAt`; `190` →
`NeedsAttention` (`token_expired`) y un aviso en la tarjeta de la conexión; al reconectar, la misma
cuenta cae en la misma conexión (misma ruta) y sus conversaciones siguen.

## 9. Configuración

| Clave | Dónde | Secreto |
| --- | --- | --- |
| `Meta:Instagram:AppId` | ConfigMap | no |
| `Meta:Instagram:AppSecret` | `prod-secret.yaml` / user-secrets | **sí** |
| `Meta:Instagram:GraphApiVersion` | ConfigMap (`v24.0`) | no |
| `Meta:App:WebhookVerifyToken` | ya existe; se reusa para el `GET` de verificación | sí |
| `Integrations:Instagram:RefreshIntervalMinutes` | `appsettings.json` (1440) | no |

Si V-1 concluye que el *Instagram App Secret* es el mismo `Meta:App:AppSecret`, las dos primeras
claves desaparecen. `ConfigurationExampleTests` exige que `appsettings.example.json` las traiga.

## 10. Errores

| Código | HTTP | Cuándo |
| --- | --- | --- |
| `integrations.instagram.code_exchange_failed` | 422 | Meta rechazó el `code` o el canje largo |
| `integrations.instagram.permissions_missing` | 422 | Faltó `instagram_business_basic` o `instagram_business_manage_messages` |
| `integrations.instagram.account_not_professional` | 422 | `account_type` no es business ni creator |
| `integrations.instagram.account_already_connected` | 422 | La cuenta ya está en otra conexión (de cualquier tenant) |
| `messaging.channel_unsupported` | 422 | Plantilla, campaña, inicio de conversación o nota de voz en Instagram |
| `messaging.window_closed` | 422 | Igual que WhatsApp |

`failureReason` en español para los códigos de Instagram que la prueba contra Meta confirme (§6.2);
el resto cae en el genérico «Instagram no pudo entregar el mensaje.».

## 11. Seguridad

- El `client_secret` sólo viaja en el canje y en el refresco, del backend a Meta, y nunca aparece en
  logs ni trazas: el canje va en el **cuerpo** del `POST` (no en el query), así que la redacción de
  OpenTelemetry del query no es la única barrera.
- El token de Instagram User se cifra con `AesGcmSecretProtector` como el de WhatsApp; `readable: true`
  en la respuesta sólo dice que descifra, nunca el valor.
- `state` en el OAuth del SPA contra CSRF del callback; el backend además exige que `redirectUri`
  pertenezca a un origen permitido (`Meta:Instagram:AllowedRedirectOrigins`, con el de producción y
  `localhost:3002`).
- Las URLs de medios de Meta (`payload.url`) nunca salen por la API: se sirven desde R2 con la sesión,
  como hoy. Las URLs prefirmadas que se le dan a Meta duran 10 minutos.
- El webhook de Instagram queda fuera del limitador `Public` y con el de concurrencia `Webhook`, por
  la misma razón que el de WhatsApp: un 429 hace que Meta reintente y, tras una hora de fallas,
  desuscriba la app.

## 12. Plan de ejecución (TDD: RED antes que GREEN, con evidencia literal)

| Paso | Qué | Cierra |
| --- | --- | --- |
| 0 | **Prueba manual con Meta real, antes de cualquier código**: agregar el producto Instagram a la app en modo desarrollo, conectar la cuenta del owner con Business Login, recibir un DM en un webhook temporal (el `GET` de verificación ya existe), mandar texto, imagen por URL prefirmada y PDF, probar `mark_seen` y `GET /<IGSID>`. Documentar en el handoff lo que se vio | V-1 a V-7 |
| 1 | Integrations: proveedor, onboarding, `ConnectInstagramHandler`, gateway, probador, ruta, migración. Pruebas: unitarias del handler con `FakeMetaGraphHandler` de Instagram, de integración del endpoint (`IntegrationsApiHarness`), fuga de secretos (`ConnectionSecretLeakTests` persigue el token) | Historias 1–2 |
| 2 | Worker de refresco con reloj controlado; `NeedsAttention` al fallar | Historia 3 |
| 3 | Messaging: `channel` en dominio, migración, DTOs, filtro; `channel_unsupported` en las guardas de plantillas, campañas, inicio y audio | Historias 9–10 |
| 4 | Webhook de Instagram: endpoint, `provider` en entregas, parser (pruebas por tipo de adjunto, echo, `read`, `reaction`, `postback`, `is_deleted`, cuerpo malformado), ingesta con canal, cliente por `instagram_user_id`, perfil | Historias 4, 5, 8, 11 |
| 5 | Envío: `IInstagramClient`, despacho por canal en texto y media, política de adjuntos por canal, `messaging_seen` → `Read` | Historias 6–7 |
| 6 | Customers: columnas, directorio, DTO, exportación | Historia 4 |
| 7 | Frontend: integraciones (conectar + callback), bandeja por canal, compositor por canal, burbujas | Todas |
| 8 | README § Mensajería gana «Instagram»; `docs/superpowers/plans/…-mensajeria-instagram-handoff-meta.md` con lo que hace el owner en Meta (App Review de los dos permisos, callback, campos suscritos) | — |
| 9 | Despliegue: variables `META_INSTAGRAM_APP_ID` y `META_INSTAGRAM_APP_SECRET` (si V-1), migraciones, backend antes que frontend, verificar el webhook en Meta, prender la conexión del primer tenant sólo con el frontend arriba (mismo orden que WhatsApp) | — |

Arquitectura: `MessagingLayerTests` e `IntegrationsLayerTests` siguen valiendo (Messaging no
referencia Integrations ni Customers; lo nuevo entra por los puertos existentes y sus adaptadores en
Bootstrapper). `ConfigurationExampleTests` cubre las claves nuevas.

**Tamaño estimado** (sin App Review ni el paso 0, que dependen de Meta y del owner):

| Parte | Estimación |
| --- | --- |
| Paso 0 | 1 día del owner con una asesora de apoyo |
| Backend (pasos 1–6) | 3 a 4 semanas |
| Frontend (paso 7) | 2 semanas |
| App Review de Meta | 1 a 3 semanas de espera, en paralelo desde el paso 0 |

## 13. Riesgos y decisiones

### Decisiones tomadas sin el owner (a ratificar)

| # | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D-I1 | Instagram Login y no Facebook Login (decisión 1) | Otro gateway, otro login en el SPA y tokens de Página; las conversaciones no cambian |
| D-I2 | `channel` en `conversations` en vez de un módulo aparte (decisión 4) | — |
| D-I3 | El tope de texto por canal se valida en el handler después de cargar la conversación (el validador de FluentValidation no conoce el canal); el error sigue saliendo como `validation.failed` en `text` | Mover la regla al validador con un lookup previo |
| D-I4 | Un `Share` guarda la URL en `text` y no copia el medio | Un `kind` con media y copia |
| D-I5 | `replyTo` se guarda pero no se manda a Meta | Nada: Meta no lo admite en el envío |
| D-I6 | Echo sin `sentBy` y sin subir `unread_count` | Un `MemberRef` «Desde la app de Instagram» si se quiere distinguir |
| D-I7 | `messaging_seen` marca `Read` por `occurred_at <=` el del `mid` | Un `UPDATE` sólo del `mid` |
| D-I8 | Una cuenta de Instagram vive en un solo tenant (índice único de rutas) | Permitirlo rompería el enrutamiento |
| D-I9 | Sin `HUMAN_AGENT` en v1 | Un slice: permiso en App Review, `messaging_type` + `tag` en el envío, ventana de 7 días en el DTO y copy del compositor |
| D-I10 | Sin notas de voz en Instagram | Transcodificar OGG/Opus a `m4a` en el backend (otra dependencia) |

### Riesgos

- **App Review.** Sin aprobación, sólo se puede escribir a personas con rol en la app. El tiempo y el
  resultado no dependen de QEP. Se pide desde el paso 0, con el video de uso que Meta exige.
- **Token de 60 días.** Si el worker falla en silencio, los tenants pierden el canal sin aviso. Alerta
  sobre `tokenExpiresAt < now + 7 días` en el dashboard de operador y en el log.
- **Echo sin apagar.** Un negocio que responde mucho desde el celular genera tráfico de webhook que
  QEP ingiere y guarda; es el comportamiento deseado, pero sube el volumen de `messages`.
- **Documentación contradictoria.** Las páginas de Instagram Login y las de Messenger Platform describen
  el mismo envío con diferencias (tope de texto, `sender_action`). El paso 0 es lo que decide, no la
  lectura.
- **Zenvia.** No participa: la bandeja va directo contra Meta, como WhatsApp Cloud. La conexión
  `zenvia` sigue siendo sólo para el envío de cotizaciones.

## 14. Entregables

1. Este spec ratificado (decisiones de §2 y §13) y el resultado del paso 0 en un handoff.
2. Backend: migraciones, proveedor, webhook, parser, ingesta, envío, worker, pruebas (unitarias, de
   integración con el harness, de arquitectura, de configuración), README.
3. Frontend: conexión por Instagram Login, bandeja y compositor por canal, pruebas.
4. Handoff a Meta: permisos, callback, campos suscritos, cómo verificar la primera cuenta.

## Historial de revisión

| Fecha | Cambio |
| --- | --- |
| 2026-10-14 | Primera versión, reemplaza el análisis escrito desde `main` sin el módulo Messaging |

## Fuentes

- https://developers.facebook.com/docs/instagram-platform/overview/
- https://developers.facebook.com/docs/instagram-platform/instagram-api-with-instagram-login/
- https://developers.facebook.com/docs/instagram-platform/instagram-api-with-instagram-login/messaging-api/
- https://developers.facebook.com/documentation/instagram-platform/instagram-api-with-instagram-login/business-login
- https://developers.facebook.com/docs/instagram-platform/reference/access_token/
- https://developers.facebook.com/docs/instagram-platform/reference/refresh_access_token/
- https://developers.facebook.com/docs/instagram-platform/create-an-instagram-app/
- https://developers.facebook.com/docs/graph-api/webhooks/reference/instagram
- https://developers.facebook.com/docs/instagram-api/webhooks
- https://developers.facebook.com/documentation/business-messaging/messenger-platform/webhooks
- https://developers.facebook.com/documentation/business-messaging/messenger-platform/policy
- https://developers.facebook.com/docs/messenger-platform/reference/send-api/
- https://developers.facebook.com/documentation/business-messaging/messenger-platform/overview/rate-limiting
- https://developers.facebook.com/docs/messenger-platform/changelog/
- https://developers.facebook.com/documentation/instagram-platform/self-messaging.md
