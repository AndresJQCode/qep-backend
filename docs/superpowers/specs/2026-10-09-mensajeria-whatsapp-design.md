# Mensajería por WhatsApp (Meta Cloud API) y conexión por Embedded Signup

**Fecha:** 2026-10-09
**Módulos:** Messaging (nuevo: Domain, Application, Infrastructure, Api; esquema `messaging`),
Integrations (proveedor `whatsapp-cloud`, `onboarding`, Embedded Signup, tabla de rutas, probador),
Tenancy (módulo `messaging`), Authorization (dos permisos en `admin` y `advisor`), Customers
(`phone_e164`), Storage (lectura y escritura por stream), Api/Bootstrapper (webhook, limitador,
excepción de CSRF, adaptadores de los puertos, políticas).
**Estado:** decisiones 1–12 aprobadas por el owner en conversación el 2026-10-09 (la 12, búsqueda
en el historial, en una segunda vuelta el mismo día); las DECISIÓN-PENDIENTE de la primera versión
quedaron cerradas con decisiones a ratificar (§13). Pendiente su lectura final.
**Depende de:** `2026-10-08-integraciones-design.md` (módulo Integrations, `IIntegrationConnections`,
`IConnectionHealthReporter`, `ConnectionResponse`, permisos `integrations.connection.*`) y del
spec de módulos por tenant (`TenantModuleKeys`, `TenantModuleGuard`, `tenancy.module_not_enabled`).
**Contrato:** `qep-frontend`, rama `origin/feature/mensajeria-whatsapp`,
`docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-backend-prompt.md` (historias 1–16 y
contratos HTTP) y su spec `docs/superpowers/specs/2026-10-09-mensajeria-whatsapp-design.md`. El
frontend ya está construido y probado contra ese contrato: rutas, campos, enums y códigos se copian
**tal cual**. Si algo no se puede cumplir, se pregunta antes de cambiarlo (sección «Riesgos y
decisiones pendientes»).

## 1. Contexto y alcance

QEP manda cotizaciones por WhatsApp pero no escucha: lo que el cliente responde se pierde en el
teléfono de alguien. El owner tiene una app de Meta aprobada y quiere mensajería oficial: cada
tenant conecta **su** número con el flujo de Meta (Embedded Signup), lo que llega por webhook se lee
en una bandeja dentro de QEP y se responde desde ahí.

**Una corrección al prompt del frontend.** El prompt dice que Integrations «todavía no existe en
`develop`». Sí existe: se mergeó en `develop` local con `ecdcfb1` (Merge branch
`feature/integraciones`), sin push al momento de escribir este spec. Las historias **1 a 4**
(catálogo, conectar Zenvia, probar/pausar/reanudar/editar/eliminar con `If-Match`, sólo lectura)
**ya están entregadas** por ese merge. Este spec agrega sólo los deltas sobre Integrations y el
módulo nuevo.

### Dentro de alcance

- Historias 5–16 del prompt: conectar WhatsApp con Embedded Signup, webhook firmado y sin
  duplicados, bandeja con lista, hilo, envío de texto, estados de entrega, acuse de lectura,
  ventana de 24 horas, resolver/reabrir, polling, módulo `messaging` y sus permisos.
- Medios **entrantes**: copiarlos a R2 y servirlos con la sesión de QEP.
- Emparejar la conversación con el cliente de QEP por teléfono.
- Historia 17 (owner, 2026-10-09): búsqueda por palabra en todo el historial de mensajes del
  tenant, rápida aunque el historial sea enorme (§7.3, §8.8).

### Fuera de alcance (tal cual el prompt)

Plantillas fuera de la ventana de 24 horas, medios salientes, asignación de conversaciones,
sincronización del historial de coexistencia, Messenger e Instagram, notificaciones en tiempo
real. Si algo de esto hace falta para cumplir una historia, se pregunta antes.

## 2. Decisiones del owner (2026-10-09)

| # | Decisión | Elegido | Por qué |
| --- | --- | --- | --- |
| 1 | Frontera entre módulos | **Integrations** es dueño del proveedor `whatsapp-cloud`, de `onboarding` en el catálogo, del handler de Embedded Signup, del probador y del enrutamiento entre tenants. **Messaging** (módulo nuevo) es dueño de conversaciones, mensajes, webhook, workers, envío, acuse de lectura y medios | La conexión es una credencial del tenant, como la de Zenvia; lo que se hace con ella es del consumidor (spec 2026-10-08, «Objetivo»). Se descartaron «todo en Integrations» (Integrations dejaría de ser sólo conexiones) y «Messaging crea conexiones» (dos dueños para la misma credencial) |
| 2 | Configuración de Meta | **Una sección `Meta:App`** (`AppId`, `AppSecret`, `ConfigId`, `GraphApiVersion`, `WebhookVerifyToken`) que leen los dos módulos. `AppSecret` y `WebhookVerifyToken` son secretos (user-secrets en local, `prod-secret.yaml` en k8s); el resto en ConfigMap. Validador estricto en `Production` con `ValidateOnStart` | Es **una** app de Meta para toda la plataforma: el mismo secreto firma el webhook y canjea el `code` |
| 3 | Cliente HTTP de Graph | **Uno por módulo**, con nombre (`integrations.meta-graph`, `messaging.meta-graph`), con el patrón de `ZenviaConnectionTester`: sin redirecciones automáticas, 10 s de timeout, `RemoveAllLoggers`, y un `HttpMessageHandler` falso en pruebas | Un módulo nunca referencia la infraestructura de otro; el token viaja en `Authorization` y un 3xx no debe llevarlo a otro host |
| 4 | Módulo por tenant | `messaging`, **último** de `TenantModuleKeys.All`, sin dependencias. `whatsapp-cloud` tiene `ConsumingModules = [messaging]` | Contrato. El catálogo lo filtra con la regla de visibilidad que ya existe |
| 5 | Permisos | `messaging.conversation.read` y `messaging.conversation.manage` en `admin` y `advisor`, **de núcleo** (sin `RequiredModules`), y **cada handler de Messaging llama `TenantModuleGuard.EnsureEnabledAsync`** | El contrato exige 403 **con código** `tenancy.module_not_enabled` con el módulo apagado; ése lo da `TenantModuleGuard`, no la máscara de permisos |
| 6 | Webhook | `GET/POST /api/webhooks/whatsapp`, anónimo, **exento de CSRF por ruta**, **sin** el limitador `Public`, con un limitador **global de concurrencia** propio. El POST sólo valida la firma, guarda el cuerpo deduplicado y responde 200; todo lo demás es asíncrono | Ver §8.2 |
| 7 | Conexión `Paused` | Mensajes entrantes **se descartan**; los estados de mensajes ya enviados **sí** se aplican. `NeedsAttention` sigue guardando. Conexión borrada o desconocida: se descarta y se registra | Decisión del owner: pausar es «no quiero recibir por aquí» |
| 8 | Base para alto volumen | §7 completa | El owner insistió: muchos mensajes y tiene que ser rápido |
| 9 | Medios entrantes | Se copian a R2 (bucket privado) al llegar, con un job aparte; la bandeja los sirve desde R2 | La URL de Meta dura 5 minutos y exige el token; el medio vive 7 días en Meta (§3) |
| 10 | Cliente de QEP | Por **`phone_e164`** calculado en Customers con `libphonenumber-csharp` y el país del cliente; **sin** suponer «10 dígitos = Colombia». Se resuelve **al leer**, no se guarda en la conversación | Un cliente editado o borrado no deja un id viejo en la conversación |
| 11 | Envío | Idempotente por `(conversación, clientId)`; un `Failed` se reenvía | Historia 12: reintentar no duplica |
| 12 | Búsqueda en el historial | Full-text en PostgreSQL: columna generada `search_vector` (`tsvector`, español sin acentos) en `messages`, índice GIN por tenant, endpoint `GET /messaging/messages/search` | Prioridad del owner: un tenant que busca en su historial tiene que obtener respuesta rápida. Agregar la columna generada **después**, con la tabla llena, reescribe toda la tabla bajo `ACCESS EXCLUSIVE` (horas a 100 M de filas); hoy es gratis |

## 3. Lo que se verificó en la documentación de Meta (2026-10-09)

Nada de esto se supone: cada fila tiene su fuente. Lo que no se pudo verificar quedó cerrado con
una decisión a ratificar en §13.

| Tema | Lo verificado | Fuente |
| --- | --- | --- |
| Vida del `code` | «The exchangeable token code has a time-to-live of 30 seconds» | https://developers.facebook.com/docs/whatsapp/embedded-signup/implementation |
| Eventos del popup | `FINISH`, `FINISH_ONLY_WABA`, `FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING`, `FINISH_OBO_MIGRATION`, `FINISH_GRANT_ONLY_API_ACCESS`, `CANCEL`; datos `phone_number_id`, `waba_id`, `business_id` | misma |
| Canje del `code` | `GET https://graph.facebook.com/<versión>/oauth/access_token?client_id=<APP_ID>&client_secret=<APP_SECRET>&code=<CODE>` → token de *business integration system user* | https://developers.facebook.com/docs/whatsapp/embedded-signup/onboarding-customers-as-a-tech-provider |
| Suscribir la app a la WABA | `POST /<WABA_ID>/subscribed_apps` con el token del negocio | misma |
| Registrar el número | `POST /<PHONE_NUMBER_ID>/register` con `{ "messaging_product": "whatsapp", "pin": "<6 dígitos>" }`. El PIN crea la verificación en dos pasos si no existe; **si ya existe, tiene que ser el actual**. Límite: 10 registros por número en 72 h; al pasarlo, error `133016` | https://developers.facebook.com/docs/whatsapp/cloud-api/reference/registration |
| Coexistencia | Con `FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING` sólo llega `waba_id`; el partner **se salta el registro** («the number is already registered»); 20 mensajes/s fijos. Meta pide además suscribir `history`, `smb_app_state_sync` y `smb_message_echoes` y sincronizar en 24 h (ver D-M13) | https://developers.facebook.com/docs/whatsapp/embedded-signup/custom-flows/onboarding-business-app-users |
| Números de la WABA | `GET /<WABA_ID>/phone_numbers` → `id`, `display_phone_number`, `verified_name`, `quality_rating`, `code_verification_status`. `GET /<PHONE_NUMBER_ID>` agrega `status`, `throughput`, `name_status`. `quality_rating` ∈ `GREEN`, `YELLOW`, `RED`, `UNKNOWN`, `NA`. «Business phone numbers must have a status of 'connected' in order to send and receive messages via the API» | https://developers.facebook.com/docs/whatsapp/business-management-api/manage-phone-numbers |
| Enviar texto | `POST /<PHONE_NUMBER_ID>/messages` con `messaging_product: "whatsapp"`, `recipient_type: "individual"`, `to`, `type: "text"`, `text.body` (hasta 4096 caracteres), `text.preview_url` opcional. Respuesta `contacts[].{input, wa_id}` y `messages[].id` | https://developers.facebook.com/docs/whatsapp/cloud-api/messages/text-messages |
| Dato de correlación | `biz_opaque_callback_data` en la petición de envío vuelve en los webhooks de `statuses` de ese mensaje (máximo 512 caracteres) | https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status |
| Marcar leído | `POST /<PHONE_NUMBER_ID>/messages` con `{ "messaging_product": "whatsapp", "status": "read", "message_id": "<wamid>" }`. Marca leídos también los anteriores de la conversación. Sólo dentro de 30 días desde la recepción | https://developers.facebook.com/docs/whatsapp/cloud-api/guides/mark-message-as-read |
| Medios | `GET /<MEDIA_ID>` → `url`, `mime_type`, `sha256`, `file_size`, `id`. La `url` **vence a los 5 minutos** y se baja con `Authorization: Bearer <token>`. Un medio recibido por webhook está disponible **7 días**. Tope de un medio entrante: 100 MB (documentos 100 MB, audio y video 16 MB, imagen 5 MB, sticker 100–500 KB) | https://developers.facebook.com/docs/whatsapp/cloud-api/reference/media |
| Verificación del webhook | `GET` con `hub.mode=subscribe`, `hub.challenge` (entero a devolver) y `hub.verify_token` | https://developers.facebook.com/docs/graph-api/webhooks/getting-started |
| Firma | `X-Hub-Signature-256: sha256=<firma>`: HMAC-SHA256 del cuerpo con el app secret como llave | misma |
| Reintentos y tamaño | Cualquier respuesta distinta de 200 se reintenta «with decreasing frequency … for up to 7 days», con duplicados posibles. **Payloads de hasta 3 MB**. Lotes de hasta 1000 actualizaciones | https://developers.facebook.com/docs/whatsapp/cloud-api/guides/set-up-webhooks y la de Graph de arriba |
| Mensaje entrante | `entry[].changes[].value` con `metadata.{display_phone_number, phone_number_id}`, `contacts[].{profile.name, wa_id}`, `messages[].{from, id, timestamp, type, …}`. Un error de mensaje entrante llega con `type: "unsupported"` y `errors` | https://developers.facebook.com/docs/whatsapp/cloud-api/webhooks/components y https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/ |
| Imagen y ubicación | `image.{id, mime_type, sha256, caption, url}` (`url` en despliegue gradual desde 2025-11-12); `location.{latitude, longitude, name, address, url}` | https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/image/ y …/location/ |
| Estados | `statuses[].{id, status, timestamp, recipient_id, errors[]}`; `status` ∈ `sent`, `delivered`, `read`, `failed`, **`played`**; `errors[].{code, title, message, error_data.details}`. Si el mensaje se lee al llegar, **no** llega `delivered` | https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/messages/status/ |
| `account_update` | `entry.id` es el **id de la WABA**. `value.event` ∈ `ACCOUNT_DELETED`, `DISABLED_UPDATE` (con `ban_info.waba_ban_state` ∈ `DISABLE`, `REINSTATE`, `SCHEDULE_FOR_DISABLE`), `ACCOUNT_VIOLATION`, `ACCOUNT_RESTRICTION`, `PARTNER_ADDED`, `PARTNER_REMOVED`, `PARTNER_APP_INSTALLED`, `PARTNER_APP_UNINSTALLED`, `ACCOUNT_OFFBOARDED`, `ACCOUNT_RECONNECTED`, entre otros | https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/reference/account_update/ |
| Errores de Graph | `{ "error": { "message", "type", "code", "error_subcode", "fbtrace_id" } }`. `190` = token vencido, revocado o inválido (subcódigos 463, 460, 467) | https://developers.facebook.com/docs/graph-api/guides/error-handling |
| Códigos de la Cloud API | tabla de §10.3 | https://developers.facebook.com/docs/whatsapp/cloud-api/support/error-codes |
| Permisos de la app | `whatsapp_business_management` y `whatsapp_business_messaging`, con Advanced Access. `business_management` sólo hace falta a un Solution Partner que comparte línea de crédito | https://developers.facebook.com/documentation/business-messaging/whatsapp/solution-providers/overview/ |

**Dos correcciones a lo conversado, por la documentación:**

1. **Tope del cuerpo del webhook: 4 MiB, no 1 MB.** Meta documenta payloads de hasta 3 MB; con
   1 MB rechazaríamos lotes legítimos y Meta los reintentaría siete días.
2. **`statuses` trae `played`** (mensajes de voz). El contrato sólo tiene `Sent | Delivered |
   Read | Failed`; `played` se guarda como `Read` (escuchar implica leer). Ver D-M9.

## 4. Trazabilidad: historias → componentes

| Historia | Componentes |
| --- | --- |
| 1–4 | Ya entregadas (`ecdcfb1`). Deltas: `onboarding` en el catálogo, campos internos, `PUT` que no edita internos (§6.1) |
| 5 | `POST /integrations/whatsapp/embedded-signup` → `CompleteWhatsAppSignupHandler` (Integrations.Application) + `MetaGraphClient` (Integrations.Infrastructure) |
| 6 | Mismo handler: lee número, nombre verificado y calidad; crea la conexión `Active`; 201 `ConnectionResponse` |
| 7 | Códigos `integrations.whatsapp.code_exchange_failed`, `registration_failed`, `number_already_connected`, `limit_reached`, `name_taken`, `validation.failed`; índice único de `integrations.connection_routes` |
| 8 | `whatsapp-cloud.ConsumingModules = [messaging]` + `IsVisibleFor` existente |
| 9 | `WhatsAppWebhookEndpoints` (Messaging.Api), `WebhookSignatureVerifier`, `messaging.webhook_deliveries`, `WebhookDeliveryWorker` |
| 10 | `ListConversationsHandler`, índices de `conversations`, `IMessagingCustomerDirectory` (adaptador sobre Customers), `IMessagingConnectionDirectory` (adaptador sobre Integrations) |
| 11 | `ListMessagesHandler`, `message_media`, `MediaCopyWorker`, `GET /messaging/media/{messageId}`, `MarkConversationReadHandler` + acuse a Meta |
| 12 | `SendMessageHandler`, índice único `(conversation_id, client_id)`, procesamiento de `statuses`, `failureReason` (§10.3) |
| 13 | `customerWindowExpiresAt = last_inbound_at + 24 h`; `messaging.window_closed` |
| 14 | `ResolveConversationHandler`, `ReopenConversationHandler` con `If-Match`; reapertura automática en la ingesta |
| 15 | Polling del frontend (lista 15 s, hilo 5 s, `CONVERSATIONS_POLL_INTERVAL_MS` y `MESSAGES_POLL_INTERVAL_MS` en `src/features/messaging/types/messaging.ts:201-202`); consultas de §7.6 |
| 16 | Módulo `messaging` + `TenantModuleGuard` en cada handler + permiso `read` |
| 17 (owner, 2026-10-09): «Como asesor, busco en el historial de mensajes de mi organización por palabra y obtengo rápido los mensajes que coinciden con su conversación» | `GET /messaging/messages/search` → `SearchMessagesHandler`; `messages.search_vector` generada, configuración `messaging.es_unaccent`, índice GIN `IX_messages_tenant_search` (§7.3, §8.8) |

## 5. Contratos HTTP (copiados del prompt del frontend)

Base: `/api/v1/tenants/{tenantId}`. Enums por nombre. Errores en ProblemDetails con `code` (y
`errors` por campo en los 422 de validación), como todo el repo.

### 5.1 Módulo y permisos

- Módulo por tenant nuevo: `messaging` (último de la lista, sin dependencias).
- Permisos: `integrations.connection.read`, `integrations.connection.manage` (en `admin`);
  `messaging.conversation.read`, `messaging.conversation.manage` (en `admin` y `advisor`).
- Con `messaging` apagado, todo `/messaging/*` y el proveedor `whatsapp-cloud` responden 403
  `tenancy.module_not_enabled`.

### 5.2 Integrations

Los endpoints, cuerpos y códigos del spec 2026-10-08 sin cambios. Dos agregados:

**`GET /integrations/catalog`** — cada proveedor trae además `onboarding`:

```json
{ "providers": [
  { "key": "whatsapp-cloud", "displayName": "WhatsApp Business (Meta)", "category": "Messaging",
    "fields": [
      { "key": "displayPhoneNumber", "label": "Número", "kind": "Phone", "required": false, "maxLength": 32 },
      { "key": "verifiedName", "label": "Nombre verificado", "kind": "Text", "required": false, "maxLength": 512 }
    ],
    "maxConnections": 5, "connectionCount": 1,
    "onboarding": { "kind": "MetaEmbeddedSignup", "appId": "…", "configId": "…", "graphApiVersion": "v24.0" } },
  { "key": "zenvia", "displayName": "Zenvia (WhatsApp)", "category": "Messaging",
    "fields": [
      { "key": "apiToken", "label": "API token", "kind": "Secret", "required": true, "maxLength": 512 },
      { "key": "fromNumber", "label": "Número emisor", "kind": "Phone", "required": true, "maxLength": 15 }
    ],
    "maxConnections": 20, "connectionCount": 0,
    "onboarding": { "kind": "Form" } }
] }
```

`onboarding` ausente equivale a `{ "kind": "Form" }`. `whatsapp-cloud` es consumido por el módulo
`messaging`; sus campos los llena el backend, nunca un formulario.

**`ConnectionResponse` de una conexión `whatsapp-cloud`** (misma forma del spec):

```json
{ "id": "…", "providerKey": "whatsapp-cloud", "name": "Ventas", "status": "Active",
  "fields": { "displayPhoneNumber": "+57 300 123 4567", "verifiedName": "Origen Botánico",
              "phoneNumberId": "111", "wabaId": "222", "qualityRating": "GREEN" },
  "secrets": { "accessToken": { "configured": true, "updatedAt": "…", "readable": true } },
  "lastVerifiedAt": "…", "lastFailureAt": null, "lastFailureCode": null,
  "createdAt": "…", "createdBy": { "memberId": "…", "displayName": "…" }, "updatedAt": "…", "version": 1 }
```

`lastFailureCode` posibles, además de los del spec: `token_expired`, `number_unregistered`.

**`POST /integrations/whatsapp/embedded-signup`** — permiso `manage`. Lo manda el frontend en
cuanto la ventana de Meta cierra; el `code` vence en 30 segundos.

```json
{ "name": "Ventas",
  "path": "existing_business_app" | "new_number",
  "event": "FINISH" | "FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING",
  "code": "AQB…",
  "wabaId": "222",
  "phoneNumberId": "111" | null,
  "businessId": "333" | null }
```

- `event` es lo que Meta reportó: `FINISH` es un número registrado en la Cloud API;
  `FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING` es coexistencia (el número sigue en la app del
  teléfono) y llega **sin** `phoneNumberId`. `path` es lo que la persona eligió en pantalla, para
  auditoría.
- Respuesta: **201 `ConnectionResponse`** con la conexión `Active` y los campos llenos.
- 422: `integrations.whatsapp.code_exchange_failed`, `integrations.whatsapp.number_already_connected`,
  `integrations.whatsapp.registration_failed`, `integrations.connection.limit_reached`,
  `integrations.connection.name_taken`, `validation.failed`.

**Webhook de Meta** — fuera de `/api/v1/tenants/*`, sin sesión:

- `GET /api/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=…&hub.challenge=…` → 200 con el
  `hub.challenge` en texto plano si el token coincide; 403 si no.
- `POST /api/webhooks/whatsapp` con `X-Hub-Signature-256` → 200 vacío si la firma es válida (y se
  procesa después), 401 si no. Campos suscritos: `messages` (mensajes entrantes y `statuses`) y
  `account_update`.

### 5.3 Messaging

**`GET /messaging/conversations?status=Open|Resolved&search=&page=&pageSize=`** — permiso `read`.
`search` ≤ 100 caracteres, por nombre de perfil, nombre del cliente o número. Orden: actividad más
reciente primero. `counts` cuenta todo el tenant, no la búsqueda.

```json
{ "items": [ ConversationSummary ], "total": 4, "page": 1, "pageSize": 30,
  "counts": { "open": 4, "unread": 3 } }
```

```json
ConversationSummary
{ "id": "…", "connectionId": "…", "connectionName": "Ventas",
  "contact": { "waId": "573001234567", "profileName": "Laura Pérez" | null },
  "customer": { "id": "…", "name": "Droguería Central" } | null,
  "status": "Open" | "Resolved",
  "unreadCount": 2,
  "lastMessage": { "direction": "Inbound" | "Outbound",
                   "kind": "Text" | "Image" | …,
                   "preview": "¿Tienen disponible…?" | null,
                   "status": "Sent" | "Delivered" | "Read" | "Failed",
                   "at": "…" } | null,
  "customerWindowExpiresAt": "…" | null,
  "updatedAt": "…", "version": 2 }
```

`customerWindowExpiresAt` es el último mensaje de la persona más 24 horas; `null` si nunca
escribió. `preview` es el texto, o la leyenda de un medio; `null` cuando no hay.

**`GET /messaging/conversations/{id}`** — permiso `read`. `ConversationSummary`. 404
`messaging.conversation.not_found` dentro del tenant.

**`GET /messaging/conversations/{id}/messages?limit=50&before={messageId}`** — permiso `read`. Sin
`before`, los `limit` más nuevos; con `before`, los `limit` anteriores a ese mensaje. `items` en
orden cronológico.

```json
{ "items": [ Message ], "hasMore": true }
```

```json
Message
{ "id": "…",
  "direction": "Inbound" | "Outbound",
  "kind": "Text" | "Image" | "Video" | "Audio" | "Document" | "Sticker" | "Location" |
          "Contacts" | "Reaction" | "Interactive" | "Template" | "Unsupported",
  "text": "…" | null,
  "media": { "url": "/api/v1/tenants/{tenantId}/messaging/media/{messageId}",
             "mimeType": "image/jpeg", "fileName": "orden.pdf" | null, "caption": "…" | null } | null,
  "location": { "latitude": 4.6, "longitude": -74.1, "name": "…" | null, "address": "…" | null } | null,
  "status": "Sent" | "Delivered" | "Read" | "Failed",
  "failureReason": "…" | null,
  "at": "…",
  "sentBy": { "memberId": "…", "displayName": "Andrés" } | null,
  "clientId": "…" | null }
```

`media.url` la sirve el backend con la sesión de QEP (**`GET /messaging/media/{messageId}`**,
permiso `read`, responde el archivo con su `Content-Type`): la URL de Meta es efímera y exige el
token. `failureReason` en español cuando el motivo de Meta sea conocido.

**`GET /messaging/messages/search?q=&conversationId=&from=&to=&limit=50&before={messageId}`** —
permiso `read` (agregado por el owner el 2026-10-09, historia 17; no está en el prompt del
frontend). Busca por palabra en el texto y las leyendas de todos los mensajes del tenant, sin
acentos ni flexiones («drogueria» encuentra «Droguería»; «pedido» encuentra «pedidos»), con
prefijo en cada palabra de 3 o más caracteres. `q` obligatorio, 2–100 caracteres. `conversationId`,
`from` y `to` (fechas ISO, por `at`) son filtros opcionales. `limit` 1–100, default 50. `before`
pagina hacia atrás desde ese mensaje, que tiene que ser del tenant (404
`messaging.message.not_found`). `items` del más nuevo al más viejo.

```json
{ "items": [ MessageHit ], "hasMore": true }
```

```json
MessageHit = Message + { "conversationId": "…",
                         "contact": { "waId": "573001234567", "profileName": "Laura Pérez" | null },
                         "customer": { "id": "…", "name": "Droguería Central" } | null,
                         "connectionName": "Ventas" }
```

Lleva quién y por dónde porque la lista de resultados tiene que dibujarlos sin pedir cada
conversación aparte (BFF, CLAUDE.md); se resuelven por página como en la lista (§8.7). 422
`validation.failed` con `errors` (`q`, `conversationId`, `from`, `to`, `limit`, `before`). Un
término que no deja ninguna palabra buscable (sólo símbolos u operadores) responde 200 con `items`
vacío, nunca 500.

**`POST /messaging/conversations/{id}/messages`** — permiso `manage`.

```json
{ "clientId": "uuid", "text": "Sí, tenemos 12 unidades." }
```

- Respuesta: **201 `Message`** con `status: "Sent"` y el mismo `clientId`.
- Idempotente por `(conversación, clientId)`: repetir el envío devuelve el mismo mensaje.
- 422: `messaging.window_closed`, `messaging.connection_unavailable` (conexión pausada o con
  atención pendiente), `messaging.conversation.not_open`, `messaging.message.rejected`,
  `validation.failed` (vacío o más de 4096 caracteres).

**`POST /messaging/conversations/{id}/read`** — permiso `manage`. 204. Deja `unreadCount` en 0 y
manda a Meta el acuse de lectura. Sin `If-Match`.

**`POST /messaging/conversations/{id}/resolve`** y **`/reopen`** — permiso `manage`, `If-Match`
obligatorio con `version` entre comillas (428 sin él, 412 desactualizado). Responden
`ConversationSummary`. 422: `messaging.conversation.already_resolved`,
`messaging.conversation.already_open`.

**Lo que el webhook debe reflejar en estos contratos:**

- Un mensaje entrante crea la conversación si no existe (por conexión y `waId`), la reabre si
  estaba resuelta, sube `unreadCount`, mueve `customerWindowExpiresAt` y guarda el nombre de perfil
  que Meta manda.
- Cada tipo de mensaje de Meta cae en un `kind` de la lista; uno que no exista en la lista es
  `Unsupported`, nunca se descarta.
- Los `statuses` de Meta actualizan el `status` del mensaje saliente (`sent`, `delivered`, `read`,
  `failed` con su motivo).
- Un mensaje repetido por Meta no se duplica.

### 5.4 Ambigüedades del contrato resueltas con el código del frontend

| Pregunta | Respuesta | Evidencia (`qep-frontend`, `origin/feature/mensajeria-whatsapp`) |
| --- | --- | --- |
| ¿Qué es `counts.unread`? | **La suma de `unreadCount` de todas las conversaciones del tenant** (mensajes sin leer), no cuántas conversaciones tienen alguno. Cualquier estado, sin filtro de búsqueda | `src/routes/_authenticated/messaging.test.tsx:33-47`: una conversación con `unreadCount: 2` y otra con 0 responden `counts: { open: 2, unread: 2 }`; con «conversaciones con no leídos» sería 1. `src/features/messaging/components/conversation-list.tsx:59-61` lo dibuja como «N sin leer», el mismo texto que la fila usa para los mensajes de una conversación (`conversation-list-item.tsx:37`) |
| ¿`counts.open` respeta la búsqueda? | No: todo el tenant (contrato). La pestaña muestra el número (`conversation-list.tsx:100-102`) | contrato |
| `pageSize` y `limit` por defecto | 30 conversaciones, 50 mensajes | `src/features/messaging/types/messaging.ts:197-198` |
| ¿Qué `status` lleva un mensaje **entrante**? | `Delivered` fijo. La burbuja sólo dibuja el ícono de estado en salientes (`message-bubble.tsx:64`), pero **sí** pinta como fallido cualquier `Failed` (`message-bubble.tsx:38`, `:69`): un entrante nunca puede ser `Failed` | código |
| `status` desconocido | El SPA lo degrada a `Sent`; `direction` y el `status` de la conversación desconocidos son error de contrato | spec del frontend, «Reglas del SPA ante la forma» |
| `lastFailureCode` que el SPA sabe dibujar | `credentials_rejected`, `provider_unreachable`, `token_expired`, `number_unregistered`. Un código que no conoce **lo muestra crudo**, no con un texto genérico: `labelFor` devuelve `labels[value] ?? value` | `src/features/integrations/types/integrations.ts:121-126` y `:193-198`; lo usa `connection-list.tsx:124-125` |

## 6. Diseño por módulo

### 6.1 Integrations (deltas)

**Catálogo.** `FieldDefinition` gana `Internal` (bool, default `false`). Un campo interno:

- **no** sale en `GET /catalog` (`ProviderFieldResponse` sólo lista los no internos);
- **sí** sale en `ConnectionResponse.fields` (o en `secrets`, si es `Secret`), porque el contrato lo
  muestra ahí;
- **no** se puede escribir por `POST /connections` ni `PUT /connections/{id}`: viene → 422
  `validation.failed` con `errors["fields.<key>"]` (o `secrets.<key>`). Lo escribe sólo el handler
  de Embedded Signup.

`IntegrationProvider` gana `Onboarding` (`ProviderOnboarding.Form` o
`ProviderOnboarding.MetaEmbeddedSignup`). `ProviderResponse` gana `onboarding`:

```csharp
// BFF: la tarjeta del catálogo decide si abre un formulario o el popup de Meta sin conocer el
// proveedor. appId y configId son públicos (viajan en la URL del popup); el secreto nunca sale.
public sealed record ProviderOnboardingResponse(string Kind, string? AppId, string? ConfigId, string? GraphApiVersion);
```

Con `Kind = "Form"` los otros tres viajan `null` y se omiten en el JSON (`JsonIgnore` cuando es
`null`), para que la forma sea exactamente `{ "kind": "Form" }`. `appId`, `configId` y
`graphApiVersion` salen de `MetaAppOptions`, no del dominio.

**Proveedor nuevo:**

| Key | Display | Categoría | Consumen | Máx | Campos |
| --- | --- | --- | --- | --- | --- |
| `whatsapp-cloud` | WhatsApp Business (Meta) | Messaging | `messaging` | 5 | `displayPhoneNumber` (Phone, opcional, 32), `verifiedName` (Text, opcional, 512), `phoneNumberId` (Text, interno, 32), `wabaId` (Text, interno, 32), `qualityRating` (Text, interno, 16), `accessToken` (Secret, interno, 2048) |

`displayPhoneNumber` **no** lleva el patrón E.164 sin «+» de Zenvia: Meta lo devuelve formateado
(`+57 300 123 4567`) y se guarda tal cual. `IntegrationProvider` hoy exige al menos un campo y
claves únicas; nada cambia ahí.

**Reglas para un proveedor con `MetaEmbeddedSignup`:**

- `POST /connections` con `providerKey: "whatsapp-cloud"` → 422 `validation.failed` con
  `errors["providerKey"]` («Este proveedor se conecta desde el flujo de Meta»). Sin esto, el
  formulario genérico crearía una conexión sin token.
- `PUT /connections/{id}` sólo cambia `name`: `displayPhoneNumber` y `verifiedName` los llena el
  backend (contrato), así que también son de sólo lectura ahí. Un `PUT` que traiga cualquier campo
  o secreto → 422 `validation.failed` en esa clave. Como no cambian campos, `PUT` no vuelve a
  probar (regla del spec 2026-10-08).
- Sin `Meta:App` configurado (sólo posible fuera de `Production`, §9), `whatsapp-cloud` **no sale
  en el catálogo** y `embedded-signup` responde 422 `integrations.whatsapp.code_exchange_failed`,
  que es el código del contrato para «no se pudo canjear». Se elige así para no inventar un código
  503 nuevo; el log dice la causa real (D-M3).

**Migraciones de Integrations:**

1. `AddWhatsAppCloudProvider`: cambia `CK_connections_provider_key` a `('zenvia','whatsapp-cloud')`
   (D10 del spec anterior: proveedor nuevo = migración, a propósito).
2. `AddConnectionRoutes`:

```sql
CREATE TABLE integrations.connection_routes (
    provider_key   varchar(32)  NOT NULL,
    external_id    varchar(64)  NOT NULL,  -- phone_number_id de Meta
    account_id     varchar(64)  NULL,      -- waba_id: account_update llega por WABA
    tenant_id      uuid         NOT NULL,
    connection_id  uuid         NOT NULL REFERENCES integrations.connections(id) ON DELETE CASCADE,
    CONSTRAINT "PK_connection_routes" PRIMARY KEY (connection_id),
    CONSTRAINT "IX_connection_routes_provider_external" UNIQUE (provider_key, external_id)
);
CREATE INDEX "IX_connection_routes_provider_account"
    ON integrations.connection_routes (provider_key, account_id) WHERE account_id IS NOT NULL;
```

- El **único** `(provider_key, external_id)` es lo que impide que un número quede conectado en dos
  tenants (o dos veces en el mismo). `IntegrationsUnitOfWork` traduce la violación **por nombre de
  índice** (`IX_connection_routes_provider_external`) a `integrations.whatsapp.number_already_connected`,
  como hoy traduce `IX_connections_tenant_provider_name` (CLAUDE.md: discriminar por nombre de
  índice, no sólo por `23505`).
- `account_id` es un **delta sobre lo conversado**: `account_update` identifica la cuenta por
  `entry.id` = WABA (§3), no por número. Una WABA puede tener varios números, así que no es único.
- `ON DELETE CASCADE`: al eliminar la conexión la ruta desaparece en la misma sentencia, y el
  número queda libre para conectarse de nuevo.
- La fila se crea en la **misma transacción** que la conexión.

**Puertos nuevos en `Modules.Integrations.Application`** (cruzan tenants a propósito; los usa sólo
el webhook, que no tiene tenant hasta resolver la ruta):

```csharp
public sealed record ConnectionRoute(Guid TenantId, Guid ConnectionId, ConnectionStatus Status);

public interface IConnectionRoutes
{
    // Cross-tenant: el webhook no sabe de qué tenant es el número. Null = número desconocido o conexión borrada.
    Task<ConnectionRoute?> FindAsync(string providerKey, string externalId, CancellationToken ct);
    Task<IReadOnlyList<ConnectionRoute>> FindByAccountAsync(string providerKey, string accountId, CancellationToken ct);
}

// Nombre y estado de todas las conexiones de un proveedor del tenant (no sólo Active):
// la bandeja muestra connectionName también en conversaciones de una conexión pausada.
Task<IReadOnlyList<ConnectionListing>> IIntegrationConnections.ListByProviderAsync(Guid tenantId, string providerKey, CancellationToken ct);
```

`ResolveAsync` sigue igual: sólo `Active` y visible, con el token en claro en memoria.

**Probador `whatsapp-cloud`** (`WhatsAppCloudConnectionTester`, `IProviderConnectionTester`):
`GET /{graphVersion}/{phoneNumberId}?fields=display_phone_number,verified_name,quality_rating,status`
con `Authorization: Bearer <accessToken>`.

| Respuesta | Resultado | `last_failure_code` |
| --- | --- | --- |
| 200 (cualquier `status`) | `Ok`; además refresca `displayPhoneNumber`, `verifiedName`, `qualityRating`. `status` se registra en el log, no decide | — |
| error Graph `code = 190` | `CredentialsRejected` con código `token_expired` | `token_expired` |
| error Graph `code = 133010` | `CredentialsRejected` con código `number_unregistered` | `number_unregistered` |
| otro 400/401/403 | `CredentialsRejected` | `credentials_rejected` |
| timeout, 5xx, red | `Unreachable` | `provider_unreachable` |

Un 200 vale aunque `status` no sea `CONNECTED` (D-M14): no se pudo verificar qué `status` reporta
un número de coexistencia, y marcarlo `number_unregistered` en falso dejaría la conexión en
`NeedsAttention` sin motivo. `number_unregistered` sale sólo del `133010` de Meta, acá o al enviar
(§8.3).

`ConnectionTestResult` gana un `FailureCode` opcional; sin él, el mapeo de hoy
(`ConnectionFailureCodes`). `ConnectionFailureCodes` gana `TokenExpired = "token_expired"`,
`NumberUnregistered = "number_unregistered"` y `AccountDisabled = "account_disabled"` (§8.2). El
refresco de campos al probar es nuevo: el probador devuelve `RefreshedFields` y el handler los
aplica con un método del agregado que sólo toca campos internos o de sólo lectura del proveedor.

**`CompleteWhatsAppSignupHandler`** (detalle en §8.1). Auditoría
`integrations.connection.created` con `changedFields` por clave y además `path` y `event` en los
metadatos de la entrada — **nunca** valores de campos (regla del spec 2026-10-08).

**Errores nuevos** en `IntegrationsErrorCodes`: `WhatsAppCodeExchangeFailed`,
`WhatsAppRegistrationFailed`, `WhatsAppNumberAlreadyConnected` (los tres 422, `DomainException`).

### 6.2 Tenancy

- `TenantModuleKeys.Messaging = Define("messaging")`, **último** en `All`, sin entrada en
  `Dependencies`.
- **No** entra en `DefaultForNewTenants`: el código de hoy deja fuera `pos`
  (`TenantModuleKey.cs:48-50`, «Los seis de hoy, sin `pos`») y la regla es que lo nuevo y vendible
  se prende por tenant. `TenantModuleKeysTests.DefaultForNewTenantsIsEverythingButPos` pasa a decir
  «sin `pos` ni `messaging`», y `AllKeepsTheContractOrder` gana la clave al final.
- Migración `AddMessagingModuleKey` que cambia `CK_tenant_modules_module_key` (otro módulo = otra
  migración, a propósito, `TenantModuleKey.cs:32-33`).

### 6.3 Authorization

- `MessagingPermissions.ConversationRead = "messaging.conversation.read"` y
  `ConversationManage = "messaging.conversation.manage"` en `Modules.Messaging.Application`.
- `PermissionDefinition` de los dos con `RequiredModules: []` (núcleo) y categoría `Messaging`.
  Si se declararan con `RequiredModules: [messaging]`, la máscara quitaría el permiso y el 403
  saldría como `authorization.denied`, sin el código que el contrato pide; por eso cada handler
  revalida con `TenantModuleGuard.EnsureEnabledAsync(…, TenantModuleKeys.Messaging, …)` —
  decisión 5.
- Las **dos mitades**: constantes y políticas en `AddAuthorization`
  (`QepServiceCollectionExtensions`), más los dos en los `RoleDefinition` de `admin` y `advisor`.
  Sin la política el síntoma es 500, no 403 (CLAUDE.md).
- `CompositionRootTests.PermissionDiscoveryFindsTheFortySixConstants` pasa a 48 (se renombra) y
  gana una prueba hermana de `TheAdminRoleCarriesTheIntegrationsPermissionsAsCore` para
  `admin` y `advisor` con los de Messaging.

### 6.4 Messaging (módulo nuevo)

`src/Modules/Messaging/Modules.Messaging.{Domain,Application,Infrastructure,Api}` con sus
`packages.lock.json`, `MessagingDbContext` (esquema `messaging`, `MessagingDbContextFactory` para
`dotnet ef`), `MessagingUnitOfWork` y `tests/ArchitectureTests/MessagingLayerTests.cs`.

Referencias permitidas: `Messaging.Application` → `Tenancy.Application` (`ITenantModules`,
`TenantModuleGuard`, `IExecutionContext`) y `Audit.Domain`; `Messaging.Domain` → `Tenancy.Domain`.
**Nunca** `Modules.Integrations.*` ni `Modules.Customers.*` ni `Modules.Storage.*`:
`MessagingLayerTests` lo verifica, y la regla de `IntegrationsLayerTests` («ningún `Modules.*` de
negocio referencia `Modules.Integrations.Application`») ya lo cubre del otro lado.

**Puertos que Messaging declara (adaptadores en `src/Bootstrapper`):**

| Puerto (Messaging.Application) | Adaptador (Bootstrapper) sobre | Para qué |
| --- | --- | --- |
| `IMessagingConnectionDirectory` | `IConnectionRoutes`, `IIntegrationConnections`, `IConnectionHealthReporter` | `FindRouteAsync(phoneNumberId)` y `FindByAccountAsync(wabaId)` cruzando tenants, con **caché en memoria de 60 s**; `ResolveSenderAsync(tenantId, connectionId)` → `phoneNumberId` + token (sólo `Active`); `ListNamesAsync(tenantId)`; `ReportRejectedAsync(tenantId, connectionId, failureCode)` |
| `IMessagingCustomerDirectory` | puerto nuevo de Customers | `MatchAsync(tenantId, waIds)` y `FindPhonesByNameAsync(tenantId, term, cap)` (§6.5) |
| `IMessagingMediaStore` | `IObjectStorage` de Storage (bucket privado) | `UploadAsync(key, stream, length, contentType)` y `OpenReadAsync(key)` (§6.6) |

La caché de rutas es por pod y vence a los 60 s: pausar una conexión puede tardar hasta un minuto
en empezar a descartar mensajes. Se acepta (ver §13).

**Auditoría.** Camino atómico propio (`IMessagingAuditRecorder`, proyección de `audit.entries` en
`MessagingDbContext`, como Integrations): `messaging.conversation.resolved|reopened`, recurso
`conversation`. Un envío **no** se audita aparte: el mensaje mismo guarda `sent_by_member_id`, y
una entrada de auditoría por mensaje duplicaría la tabla más grande. No hay eventos de outbox en
esta versión.

**Permiso por endpoint:**

| Método y ruta | Permiso | Handler |
| --- | --- | --- |
| `GET /messaging/conversations` | read | `ListConversationsHandler` |
| `GET /messaging/conversations/{id}` | read | `GetConversationHandler` |
| `GET /messaging/conversations/{id}/messages` | read | `ListMessagesHandler` |
| `POST /messaging/conversations/{id}/messages` | manage | `SendMessageHandler` |
| `POST /messaging/conversations/{id}/read` | manage | `MarkConversationReadHandler` |
| `POST /messaging/conversations/{id}/resolve` | manage | `ResolveConversationHandler` |
| `POST /messaging/conversations/{id}/reopen` | manage | `ReopenConversationHandler` |
| `GET /messaging/messages/search` | read | `SearchMessagesHandler` |
| `GET /messaging/media/{messageId}` | read | `GetMediaHandler` |
| `GET /api/webhooks/whatsapp` | anónimo | `VerifyWebhookHandler` |
| `POST /api/webhooks/whatsapp` | anónimo, firma | `ReceiveWebhookHandler` |

Cada handler de tenant, en este orden: tenant de la ruta = tenant de la sesión y permiso (403
`authorization.denied`), `TenantModuleGuard` (403 `tenancy.module_not_enabled`), y sólo después el
repositorio, que **siempre** recibe `tenantId`. Un id que existe en otro tenant, pedido bajo la
ruta propia, no se encuentra (404) — la misma respuesta que un id inexistente, así que no confirma
nada.

**Validadores (FluentValidation):**

| Comando | Reglas | Clave en `errors` |
| --- | --- | --- |
| `SendMessage` | `text` no vacío tras `Trim`, ≤ 4096, sin `\0`; `clientId` GUID no vacío | `text`, `clientId` |
| `ListConversations` | `status` ∈ `Open`, `Resolved` (default `Open`); `search` ≤ 100; `page` ≥ 1; `pageSize` 1–50 (default 30) | `status`, `search`, `page`, `pageSize` |
| `ListMessages` | `limit` 1–100 (default 50); `before` GUID de un mensaje **de esa conversación** | `limit`, `before` |
| `SearchMessages` | `q` 2–100 caracteres tras `Trim`, sin `\0`; `conversationId` GUID o ausente; `from` ≤ `to`; `limit` 1–100 (default 50); `before` GUID | `q`, `conversationId`, `from`, `to`, `limit`, `before` |

### 6.5 Customers (delta)

- Columna `phone_e164 varchar(16) NULL` en `customers.customers` (E.164 con `+`, máximo 15
  dígitos). Índice `IX_customers_tenant_phone_e164 (tenant_id, phone_e164) WHERE phone_e164 IS NOT NULL`.
- Se calcula **en el dominio** en `Customer.Create` y `Customer.Update`, con
  `libphonenumber-csharp`:
  - teléfono que empieza con `+` → se interpreta como internacional;
  - si no, como número **nacional** del `Country` del cliente (ISO-3166 alpha-2, obligatorio desde
    `AddCustomerCountry`; el frontend lo elige de una lista en
    `src/features/customers/components/customer-form.tsx:396`). La librería maneja los prefijos de
    larga distancia;
  - si no parsea o no es válido → `null`. **Nunca** falla la escritura del cliente: un teléfono que
    hoy se guarda se sigue guardando.
- El dominio recibe un `IPhoneNumberNormalizer` (puerto de Customers.Domain, implementación en
  Customers.Infrastructure sobre la librería) para no amarrar el dominio a un paquete externo y
  poder probar con un doble.
- **Backfill** idempotente al arrancar (`CustomerPhoneBackfillWorker`): recorre en lotes de 500 las
  filas con `phone IS NOT NULL AND phone_e164 IS NULL` y las recalcula. Las que no parsean vuelven
  a intentarse en cada arranque; son pocas y el costo es acotado.
- Dependencia nueva: `libphonenumber-csharp` en `Directory.Packages.props` y los
  `packages.lock.json` **regenerados** con `dotnet restore --force-evaluate` en el mismo commit
  (si no, el `docker build` falla con `NU1004`, CLAUDE.md). La versión se fija al implementar, la
  última estable de ese momento, y se revisa el diff de locks por arrastre transitivo.
- Puerto de Customers para Messaging (`ICustomerPhoneDirectory`, Customers.Application):
  - `MatchAsync(tenantId, IReadOnlyCollection<string> e164)` → por cada número, **un** cliente:
    una consulta por página (`WHERE tenant_id = @t AND phone_e164 = ANY(@phones)`, ≤ 30 valores).
    **Regla con duplicados:** gana el creado primero (`created_at` ascendente) y, si empatan, el de
    `id` menor. Determinista y estable entre polls.
  - `FindPhonesByNameAsync(tenantId, term, cap = 200)` → los `phone_e164` de clientes cuyo nombre
    contiene `term` (usa `IX_customers_name_trgm`). Con más de 200 coincidencias, la búsqueda por
    nombre de cliente queda incompleta; se registra en el log.
- **Match:** los dígitos de `phone_e164` == `waId` exacto (Meta manda `wa_id` en dígitos, sin `+`).

### 6.6 Storage (delta)

`IObjectStorage` hoy sólo lee y escribe `byte[]` (`DownloadAsync`, `UploadAsync`). Un documento de
WhatsApp puede pesar 100 MB (§3), así que se agregan dos métodos:

```csharp
Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken ct);
Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken ct); // Stream + ContentType + Length; null si no está
```

`R2ObjectStorage` los implementa con el mismo `PutObjectRequest` de hoy
(`UseChunkEncoding = false`, `DisablePayloadSigning = true`) y `ContentLength` explícito: con R2 la
subida en chunks firmados falla (memoria del proyecto «AWSSDK v4 es incompatible con R2»), así que
el largo tiene que conocerse antes de subir. Meta lo da en `file_size`.

### 6.7 Api / Bootstrapper

- `RateLimiterPolicies.Webhook = "webhook"`: un `ConcurrencyLimiter` **global** (una sola
  partición), `PermitLimit` 64 y `QueueLimit` 256 configurables en
  `Messaging:Webhook:{ConcurrencyLimit, QueueLimit}`, `OldestFirst`. Ver §8.2 por qué no `Public`.
- `RequireCsrfHeaderMiddleware`: excepción **explícita por ruta** para `/api/webhooks/` (prefijo
  ordinal). Es exactamente lo que el comentario del middleware pide
  (`src/Bootstrapper/Csrf/RequireCsrfHeaderMiddleware.cs:27-32`): la excepción no se ata a
  `DisableAntiforgery`, y el endpoint no acepta la cookie de sesión — se autentica con la firma.
- `MapWhatsAppWebhook` en `Program.cs`, `AllowAnonymous()`, `RequireRateLimiting(Webhook)`.
- Los tres adaptadores de §6.4 y el nuevo puerto de Customers se registran en Bootstrapper.

## 7. Base de datos (esquema `messaging`)

Objetivo: que los cuatro caminos calientes —la lista cada 15 s, el hilo cada 5 s, los contadores y
el estado por `wamid`— sean **una búsqueda por índice acotada** sin importar cuántos mensajes tenga
la tabla, y que la ingesta sume sin conflictos aunque varios pods procesen la misma conversación.

### 7.1 Convenciones y una excepción documentada

- Ids `uuid` **v7** (`Guid.CreateVersion7()`): ordenados por tiempo, así que el B-tree de la PK
  crece por la derecha (sin divisiones de página aleatorias como con v4).
- **Excepción a la convención de enums como texto:** en `messages`, `direction`, `kind` y `status`
  son `smallint` con `CHECK`. Son 2 bytes contra 6–12 de un `varchar` por columna en la tabla que
  no tiene techo; a 100 M de filas es más de 1 GB de heap y de índices. **La API sigue mandando
  nombres** (`Inbound`, `Image`, `Read`): la traducción vive en un solo mapa del Infrastructure con
  prueba de ida y vuelta. `conversations.status` sigue la convención (`varchar` con `CHECK`): la
  tabla es órdenes de magnitud más chica.

| Columna | Valores |
| --- | --- |
| `direction` | 1 `Inbound`, 2 `Outbound` |
| `kind` | 1 `Text`, 2 `Image`, 3 `Video`, 4 `Audio`, 5 `Document`, 6 `Sticker`, 7 `Location`, 8 `Contacts`, 9 `Reaction`, 10 `Interactive`, 11 `Template`, 12 `Unsupported` |
| `status` | 1 `Sent`, 2 `Delivered`, 3 `Read`, 4 `Failed` (el orden numérico **es** el de avance; §7.5) |

### 7.2 `messaging.conversations`

```sql
CREATE TABLE messaging.conversations (
    id                      uuid         NOT NULL,
    tenant_id               uuid         NOT NULL,
    connection_id           uuid         NOT NULL,
    wa_id                   varchar(20)  NOT NULL,   -- dígitos, sin '+'
    profile_name            varchar(256) NULL,
    status                  varchar(16)  NOT NULL,
    unread_count            integer      NOT NULL DEFAULT 0,
    last_inbound_at         timestamptz  NULL,       -- ventana = + 24 h
    last_inbound_wamid      text         NULL,       -- acuse de lectura
    last_activity_at        timestamptz  NOT NULL,
    last_message_id         uuid         NULL,
    last_message_direction  smallint     NULL,
    last_message_kind       smallint     NULL,
    last_message_preview    varchar(200) NULL,
    last_message_status     smallint     NULL,
    last_message_at         timestamptz  NULL,
    created_at              timestamptz  NOT NULL,
    updated_at              timestamptz  NOT NULL,
    version                 bigint       NOT NULL,
    CONSTRAINT "PK_conversations" PRIMARY KEY (id),
    CONSTRAINT "CK_conversations_status" CHECK (status IN ('Open','Resolved')),
    CONSTRAINT "CK_conversations_unread" CHECK (unread_count >= 0)
) WITH (fillfactor = 80);
```

**Por qué la «foto» del último mensaje vive en la conversación:** la lista se consulta cada 15 s por
cada persona con la bandeja abierta. Si tuviera que buscar el último mensaje de 30 conversaciones
en `messages` serían 30 búsquedas más (o un `LATERAL`) por poll. Con la foto, la lista **nunca toca
`messages`**. El costo es mantenerla en la ingesta y en los estados, que ya escriben esa fila.

| Índice | Definición | Por qué |
| --- | --- | --- |
| `IX_conversations_connection_wa` | `UNIQUE (connection_id, wa_id)` | Una conversación por persona y número de la organización; es el blanco del `ON CONFLICT` de la ingesta |
| `IX_conversations_tenant_status_activity` | `(tenant_id, status, last_activity_at DESC, id DESC)` | La lista: igualdad en tenant y estado, orden por actividad. `id` desempata y vuelve la paginación estable. El `count(*)` de `total` sin búsqueda sale del mismo índice |
| `IX_conversations_tenant_open` | `(tenant_id) WHERE status = 'Open'` | `counts.open`: index-only scan sobre un índice parcial chico |
| `IX_conversations_tenant_unread` | `(tenant_id) INCLUDE (unread_count) WHERE unread_count > 0` | `counts.unread` = `SUM(unread_count)`: sólo recorre las que tienen algo, sin ir al heap |
| `IX_conversations_profile_name_trgm` | `GIN (profile_name gin_trgm_ops)` | Búsqueda «contiene» por nombre de perfil |
| `IX_conversations_wa_id_trgm` | `GIN (wa_id gin_trgm_ops)` | Búsqueda por fragmento de número |

`fillfactor = 80`: la fila se actualiza en cada mensaje y en cada estado del último mensaje; el
espacio libre deja que las actualizaciones que **no** tocan columnas indexadas (la foto del estado)
sean HOT. Las que mueven `last_activity_at` o `unread_count` no lo son, y se acepta: es una fila
por conversación, no por mensaje.

`pg_trgm` vive en el esquema `public`: la migración hace `CREATE EXTENSION IF NOT EXISTS pg_trgm;`
sin cláusula `SCHEMA`, igual que `AddCustomersNameTrigramIndex` y `AddProductsTrigramIndexes`. En
una base sin `public` falla con `3F000` (memoria del proyecto «pg_trgm necesita el schema public»);
no se le pone `SCHEMA messaging`, porque sacaría `gin_trgm_ops` del `search_path`.

### 7.3 `messaging.messages`

```sql
CREATE TABLE messaging.messages (
    id                 uuid         NOT NULL,
    conversation_id    uuid         NOT NULL,
    tenant_id          uuid         NOT NULL,
    connection_id      uuid         NOT NULL,
    occurred_at        timestamptz  NOT NULL,   -- hora de Meta (timestamp del webhook) o del envío
    direction          smallint     NOT NULL,
    kind               smallint     NOT NULL,
    status             smallint     NOT NULL,
    text               text          NULL,
    caption            varchar(1024) NULL,      -- leyenda de un medio (Meta: caption); aquí y no en message_media para que la busque search_vector
    details            jsonb         NULL,      -- location / contacts / reaction / interactive
    wamid              text          NULL,
    client_id          uuid          NULL,
    sent_by_member_id  uuid          NULL,
    failure_code       integer       NULL,      -- código de Meta; -1 = envío sin confirmar (§8.3)
    failure_title      text          NULL,      -- título de Meta en inglés, para soporte; nunca sale por HTTP
    created_at         timestamptz   NOT NULL,
    search_vector      tsvector GENERATED ALWAYS AS (
                           to_tsvector('messaging.es_unaccent',
                                       coalesce(text, '') || ' ' || coalesce(caption, ''))
                       ) STORED,
    CONSTRAINT "PK_messages" PRIMARY KEY (id),
    CONSTRAINT "FK_messages_conversation" FOREIGN KEY (conversation_id)
        REFERENCES messaging.conversations (id) ON DELETE CASCADE,
    CONSTRAINT "CK_messages_direction" CHECK (direction IN (1, 2)),
    CONSTRAINT "CK_messages_kind" CHECK (kind BETWEEN 1 AND 12),
    CONSTRAINT "CK_messages_status" CHECK (status BETWEEN 1 AND 4),
    CONSTRAINT "CK_messages_inbound_not_failed" CHECK (direction = 2 OR status <> 4)
) WITH (fillfactor = 90);
```

| Índice | Definición | Por qué |
| --- | --- | --- |
| `PK_messages` | `(id)` | v7: inserción al final |
| `IX_messages_thread` | `(conversation_id, occurred_at DESC, id DESC)` | El hilo y su paginación hacia atrás por *keyset*: `WHERE conversation_id = @c AND (occurred_at, id) < (@t, @id) ORDER BY occurred_at DESC, id DESC LIMIT @n + 1`. La fila `n + 1` sólo dice `hasMore`. Ordena por la **hora de Meta**, no la de llegada: un reenvío tardío de Meta cae en su lugar y no desordena el hilo. `id` desempata los segundos repetidos (Meta manda segundos) |
| `IX_messages_connection_wamid` | `UNIQUE (connection_id, wamid) WHERE wamid IS NOT NULL` | Dos trabajos con un índice: deduplicar mensajes entrantes (`ON CONFLICT DO NOTHING`) y encontrar el saliente que un `status` actualiza. Por conexión y no global: el `wamid` es de Meta, y acotarlo a la conexión evita que un tenant choque con otro |
| `IX_messages_conversation_client` | `UNIQUE (conversation_id, client_id) WHERE client_id IS NOT NULL` | Idempotencia del envío (§8.3). Parcial: los entrantes no lo pagan |
| `IX_messages_tenant_search` | `USING GIN (tenant_id, search_vector)` | La búsqueda en el historial (historia 17). `tenant_id` entra en el GIN gracias a `btree_gin`; el *fast scan* del GIN intersecta la lista de un término raro con la del tenant sin leer el heap de los demás |

**Lo que no se indexa, a propósito:** no hay índice B-tree sólo por `tenant_id` (ninguna consulta
caliente recorre los mensajes de un tenant por fecha; el aislamiento lo da la conversación, que ya
se validó contra el tenant; el GIN de búsqueda lo lleva como primera clave para otra cosa). No se
guarda el payload crudo por mensaje: el cuerpo vive en `webhook_deliveries` y se purga a los 7
días.

**Búsqueda en el historial (`search_vector`, decisión 12).**

- **Columna generada hoy, no mañana.** Una `GENERATED … STORED` que se agrega a una tabla con datos
  reescribe **toda** la tabla bajo `ACCESS EXCLUSIVE`: a 100 M de filas son horas sin lecturas ni
  escrituras en `messages`. Creada con la tabla vacía no cuesta nada, y cada `INSERT` la calcula por
  su cuenta (unos microsegundos por mensaje).
- `to_tsvector(regconfig, text)` con la configuración como **literal** es `IMMUTABLE`, que es lo que
  PostgreSQL exige en una columna generada; la variante de un argumento depende de
  `default_text_search_config` y no se admite. La configuración va **calificada con esquema**
  (`'messaging.es_unaccent'`) en la expresión: así no depende del `search_path` de quien inserte.
- **Configuración `messaging.es_unaccent`.** En la migración `InitialMessaging`, antes de la tabla:

```sql
CREATE EXTENSION IF NOT EXISTS unaccent;    -- en public, mismo precedente que pg_trgm (§7.2)
CREATE EXTENSION IF NOT EXISTS btree_gin;   -- idem: tenant_id dentro de un GIN
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_ts_config c JOIN pg_namespace n ON n.oid = c.cfgnamespace
        WHERE n.nspname = 'messaging' AND c.cfgname = 'es_unaccent')
    THEN
        CREATE TEXT SEARCH CONFIGURATION messaging.es_unaccent (COPY = pg_catalog.spanish);
        ALTER TEXT SEARCH CONFIGURATION messaging.es_unaccent
            ALTER MAPPING FOR hword, hword_part, word, asciiword, asciihword, hword_asciipart
            WITH unaccent, spanish_stem;
    END IF;
END $$;
```

  Sólo las palabras pasan por `unaccent` y el *stemmer* español («Droguería» → `drogueri`,
  «pedidos» → `pedid`). Números, correos, URLs y partes con dígitos **conservan el mapeo por
  defecto** de `spanish`: un número de pedido o un documento siguen siendo un token exacto. El
  `DO … IF NOT EXISTS` la hace idempotente: `CREATE TEXT SEARCH CONFIGURATION` no tiene `IF NOT
  EXISTS`, y la migración corre en bases locales que pueden tenerla a medias.
- **El índice.** `CREATE INDEX "IX_messages_tenant_search" ON messaging.messages USING GIN
  (tenant_id, search_vector)`. Con `fastupdate` en su valor por defecto (prendido): los inserts
  dejan las entradas en la lista pendiente y el GIN se consolida al llegar a
  `gin_pending_list_limit` (4 MB por defecto) o en el `autovacuum`. Con mucho tráfico conviene
  fijar `gin_pending_list_limit` en el índice (`WITH (gin_pending_list_limit = 2048)`, en KB) y
  dejar el `autovacuum` de la tabla más agresivo (`autovacuum_vacuum_scale_factor = 0.02`), para
  que la lista pendiente —que se recorre entera en cada búsqueda— no crezca.
- **La consulta** (`SearchMessagesHandler`, §8.8):

```sql
SET LOCAL statement_timeout = '2s';
SELECT … FROM messaging.messages
WHERE tenant_id = @t
  AND search_vector @@ to_tsquery('messaging.es_unaccent', @q)
  [AND conversation_id = @c]
  [AND occurred_at BETWEEN @from AND @to]
  [AND (occurred_at, id) < (@beforeAt, @beforeId)]
ORDER BY occurred_at DESC, id DESC
LIMIT @limit + 1;
```

- **Cómo se arma `@q`.** `websearch_to_tsquery` no admite prefijos, así que el servidor construye
  la `tsquery` él mismo: `Trim`, tope de 100 caracteres, separa por espacios, **quita** los
  operadores de `tsquery` (`& | ! : * ( ) ' \ <`) y los caracteres de control de cada token,
  descarta los tokens vacíos, se queda con los **primeros 8**, y cada uno pasa por
  `to_tsvector('messaging.es_unaccent', token)` para obtener su lexema normalizado; los de 3 o más
  caracteres van como `lexema:*` (prefijo) y los más cortos exactos; todo unido con `&`. Un token
  que no deja lexema (una *stop word* como «de», o sólo símbolos) se omite; si no queda ninguno, la
  respuesta es `items: []` sin consultar. Así ningún texto de la persona llega a `to_tsquery`
  como sintaxis.
- **El límite honesto.** El GIN devuelve rápido *qué filas* contienen el término, pero el `ORDER BY`
  por fecha obliga a ordenar **todas** las coincidencias del tenant antes de cortar en `LIMIT`. Con
  un término raro son decenas de filas; con uno muy común («hola») en un tenant con millones de
  mensajes son millones, y la consulta tarda. Mitigación: el `statement_timeout` de 2 s con `SET
  LOCAL` (la respuesta es 422 `validation.failed` en `q` con «Busca con una palabra más específica
  o acota las fechas»), y el rango `from`/`to`, que recorta por la columna del `ORDER BY`. Si un
  tenant lo necesita de verdad, el camino es un motor externo (OpenSearch/Meilisearch) alimentado
  desde la ingesta; el índice de hoy no se tira, se deja para los filtros.
- **Descartado y por qué:** `pg_trgm` sobre `text` (índice 2–3 veces más grande, sin `tenant_id`
  adentro, y «contiene» no entiende de flexiones); RUM (no viene en `postgres:18-alpine`, la imagen
  del `compose.yaml`, ni en la base administrada); ordenar por `ts_rank` (obliga a puntuar todas las
  coincidencias; el orden por fecha es lo que una bandeja espera).

`fillfactor = 90`: el cambio de `status` (columna **no** indexada) es la única actualización de la
fila; con espacio libre en la página es HOT y no toca ningún índice.

**Particionado: todavía no.** Particionar por mes obliga a meter la clave de partición
(`occurred_at`) en todo índice único, y entonces `UNIQUE (connection_id, wamid)` ya no deduplica:
el mismo `wamid` reenviado con otra hora caería en otra partición. Umbral para revisarlo: cuando
`messages` pase de ~200 M de filas o el `VACUUM` de la tabla deje de terminar en su ventana. El
camino será particionar por mes y mover la deduplicación a una tabla chica
`(connection_id, wamid) → message_id` con su propia purga.

### 7.4 `messaging.message_media` y `messaging.webhook_deliveries`

```sql
CREATE TABLE messaging.message_media (
    message_id       uuid         NOT NULL,   -- 1:1, sólo mensajes con medio
    mime_type        varchar(128) NOT NULL,
    file_name        varchar(256) NULL,
    meta_media_id    varchar(64)  NOT NULL,
    size_bytes       bigint       NULL,
    sha256           varchar(64)  NULL,       -- el que manda Meta; se verifica al copiar
    storage_key      varchar(256) NULL,       -- messaging/{tenantId}/{messageId}
    stored_at        timestamptz  NULL,
    attempts         integer      NOT NULL DEFAULT 0,
    next_attempt_at  timestamptz  NOT NULL,
    last_error       varchar(256) NULL,
    CONSTRAINT "PK_message_media" PRIMARY KEY (message_id),
    CONSTRAINT "FK_message_media_message" FOREIGN KEY (message_id)
        REFERENCES messaging.messages (id) ON DELETE CASCADE
);
CREATE INDEX "IX_message_media_pending" ON messaging.message_media (next_attempt_at)
    WHERE stored_at IS NULL;

CREATE TABLE messaging.webhook_deliveries (
    id             bigint GENERATED ALWAYS AS IDENTITY,
    body_sha256    bytea        NOT NULL,
    payload        jsonb        NOT NULL,
    received_at    timestamptz  NOT NULL,
    claimed_until  timestamptz  NULL,
    attempts       integer      NOT NULL DEFAULT 0,
    processed_at   timestamptz  NULL,
    last_error     varchar(512) NULL,
    CONSTRAINT "PK_webhook_deliveries" PRIMARY KEY (id),
    CONSTRAINT "IX_webhook_deliveries_body_sha256" UNIQUE (body_sha256)
);
CREATE INDEX "IX_webhook_deliveries_pending" ON messaging.webhook_deliveries (id)
    WHERE processed_at IS NULL;
```

- `message_media` aparte: sólo una fracción de los mensajes trae medio, y sus 11 columnas no
  engordan la fila de cada texto. El índice parcial de pendientes queda casi vacío en régimen. La
  leyenda **no** vive aquí: está en `messages.caption` para que `search_vector` la indexe (§7.3).
- `webhook_deliveries.id` es `bigint` identity y no UUID: la tabla es una cola, su orden es el de
  llegada y nunca sale por HTTP. El único por `body_sha256` hace que un reenvío **idéntico** de
  Meta no cree otra fila; un reenvío con bytes distintos sí la crea y lo absorbe la deduplicación
  por `wamid` (§8.2).
- `payload` como `jsonb`: la firma ya se validó contra los bytes crudos antes de guardar.

### 7.5 Ingesta de un mensaje entrante: SQL atómico, sin cargar y guardar

Por cada mensaje de un `change`, en **una** transacción, tres sentencias (no se pasa por el
agregado, a propósito: dos pods sobre la misma conversación suman bien sin reintentos de
concurrencia):

```sql
-- 1. La conversación (crear si no existe). DO NOTHING + SELECT y no DO UPDATE: un DO UPDATE
--    escribiría una tupla nueva aunque no cambie nada, y reabriría una conversación resuelta
--    con un reenvío viejo de Meta.
INSERT INTO messaging.conversations (id, tenant_id, connection_id, wa_id, profile_name, status,
       unread_count, last_activity_at, created_at, updated_at, version)
VALUES (@newId, @tenantId, @connectionId, @waId, @profileName, 'Open', 0, @occurredAt, @now, @now, 1)
ON CONFLICT (connection_id, wa_id) DO NOTHING
RETURNING id;
-- vacío → SELECT id FROM messaging.conversations WHERE connection_id = @connectionId AND wa_id = @waId;
-- (en una sentencia aparte: así ve la fila que otro pod acaba de commitear)

-- 2. El mensaje. Si Meta lo reenvía, no inserta nada.
INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at,
       direction, kind, status, text, caption, details, wamid, created_at)
VALUES (@messageId, @conversationId, @tenantId, @connectionId, @occurredAt,
        1, @kind, 2, @text, @caption, @details, @wamid, @now)   -- search_vector se calcula solo
ON CONFLICT (connection_id, wamid) WHERE wamid IS NOT NULL DO NOTHING
RETURNING id;

-- 3. Sólo si (2) insertó: contadores, reapertura, ventana y foto. Las expresiones del SET leen
--    los valores VIEJOS de la fila (semántica de UPDATE en PostgreSQL).
UPDATE messaging.conversations SET
    unread_count       = unread_count + 1,
    status             = 'Open',
    profile_name       = COALESCE(@profileName, profile_name),
    last_inbound_wamid = CASE WHEN last_inbound_at IS NULL OR @occurredAt >= last_inbound_at
                              THEN @wamid ELSE last_inbound_wamid END,
    last_inbound_at    = GREATEST(last_inbound_at, @occurredAt),
    last_activity_at   = GREATEST(last_activity_at, @occurredAt),
    last_message_id        = CASE WHEN last_message_at IS NULL OR @occurredAt >= last_message_at THEN @messageId  ELSE last_message_id END,
    last_message_direction = CASE WHEN last_message_at IS NULL OR @occurredAt >= last_message_at THEN 1           ELSE last_message_direction END,
    last_message_kind      = CASE WHEN last_message_at IS NULL OR @occurredAt >= last_message_at THEN @kind       ELSE last_message_kind END,
    last_message_preview   = CASE WHEN last_message_at IS NULL OR @occurredAt >= last_message_at THEN @preview    ELSE last_message_preview END,
    last_message_status    = CASE WHEN last_message_at IS NULL OR @occurredAt >= last_message_at THEN 2           ELSE last_message_status END,
    last_message_at        = GREATEST(last_message_at, @occurredAt),
    updated_at         = @now,
    version            = version + 1
WHERE id = @conversationId;
```

Si el mensaje trae medio, la fila de `message_media` (con `next_attempt_at = now`) entra en la
misma transacción. `GREATEST` ignora `NULL` en PostgreSQL, así que la primera vez toma el valor
nuevo.

**`version` sube en la ingesta** (decisión del owner): un `resolve` con la versión que la persona
vio antes de que llegara un mensaje nuevo recibe 412, que es lo correcto — está resolviendo algo
que ya cambió. `resolve`, `reopen` y `read` sí pasan por el agregado con EF y `version` como token
de concurrencia (`IsConcurrencyToken`).

**Estados (`statuses`).** Un `UPDATE` monótono por índice único:

```sql
UPDATE messaging.messages
SET status = @new, failure_code = @code, failure_title = @title,
    wamid = COALESCE(wamid, @wamid)
WHERE connection_id = @connectionId
  AND (wamid = @wamid OR id = @callbackMessageId)   -- @callbackMessageId: de biz_opaque_callback_data
  AND direction = 2
  AND (
        (@new IN (2, 3) AND (status < @new OR (status = 4 AND failure_code = -1)))
     OR (@new = 1       AND status = 4 AND failure_code = -1)
     OR (@new = 4       AND status = 1)
  )
RETURNING id, conversation_id;
```

- `Sent(1) < Delivered(2) < Read(3)`: un `delivered` que llega después de un `read` no retrocede
  (Meta no garantiza el orden y a veces ni manda `delivered`, §3).
- `Failed(4)` sólo pisa un `Sent`: un mensaje entregado o leído no puede volverse fallido.
- `failure_code = -1` es «envío sin confirmar» (timeout con Meta, §8.3): cualquier estado real de
  Meta lo corrige.
- `played` se guarda como `Read` (3).
- Si la sentencia actualizó y el mensaje es el último de su conversación:
  `UPDATE messaging.conversations SET last_message_status = @new WHERE id = @conv AND last_message_id = @id`.
  **No** sube `version` ni `updated_at`: el estado de entrega no es algo que la persona edite, y
  subirlo convertiría cada acuse en un 412 al resolver. Esa actualización es HOT
  (`last_message_status` no está en ningún índice).

### 7.6 Las consultas calientes y su plan esperado

| Consulta | Frecuencia | SQL (resumen) | Plan esperado |
| --- | --- | --- | --- |
| Lista sin búsqueda | cada 15 s por persona con la bandeja abierta, por página cargada | `WHERE tenant_id = @t AND status = @s ORDER BY last_activity_at DESC, id DESC LIMIT 30 OFFSET @o` + `count(*)` | Index scan sobre `IX_conversations_tenant_status_activity`, 30 filas de heap. El `count(*)` es index-only scan del mismo índice: lineal en conversaciones **del tenant y estado**, no en mensajes |
| Contadores | con cada lista | `count(*) … WHERE tenant_id = @t AND status = 'Open'` y `SUM(unread_count) … WHERE tenant_id = @t AND unread_count > 0` | Index-only scan de los dos parciales |
| Nombres y clientes | con cada lista | `IMessagingConnectionDirectory.ListNamesAsync` (≤ 5 conexiones por tenant) y una consulta a Customers por `phone_e164 = ANY(@≤30)` | Index scan de `IX_customers_tenant_phone_e164` |
| Lista con búsqueda | al teclear (debounce 400 ms) | igual + `AND (profile_name ILIKE @p OR wa_id LIKE @d OR wa_id = ANY(@phonesDeClientes))` | Con ≥ 3 caracteres, bitmap scan de los GIN de trigramas combinado (`BitmapOr`) y filtro por tenant; con menos, el índice del tenant y filtro en memoria — acotado por las conversaciones del tenant |
| Hilo | cada 5 s por hilo abierto | `WHERE conversation_id = @c ORDER BY occurred_at DESC, id DESC LIMIT 51` | Index scan de `IX_messages_thread`, 51 filas. Independiente del tamaño total de la tabla |
| Hilo hacia atrás | al subir | igual + `AND (occurred_at, id) < (@t, @id)` | El mismo índice, por comparación de fila |
| Estado por `wamid` | por cada acuse de Meta | `UPDATE … WHERE connection_id = @c AND wamid = @w` | Una búsqueda en `IX_messages_connection_wamid`, actualización HOT |
| Medios pendientes | cada pocos segundos | `WHERE stored_at IS NULL AND next_attempt_at <= now() ORDER BY next_attempt_at LIMIT 20 FOR UPDATE SKIP LOCKED` | Index scan del parcial, casi vacío |
| Búsqueda en el historial | al pedirla una persona (sin poll) | `WHERE tenant_id = @t AND search_vector @@ to_tsquery(…) ORDER BY occurred_at DESC, id DESC LIMIT 51` | Bitmap index scan de `IX_messages_tenant_search` (intersección tenant ∧ términos dentro del GIN), bitmap heap scan de las coincidencias y un `Sort` con `LIMIT` (top-N heapsort). Costo proporcional a las **coincidencias del tenant**, no a la tabla; acotado por `statement_timeout` de 2 s (§7.3) |

### 7.7 Tamaños aproximados (orden de magnitud, para planear)

Estimación por fila, con texto promedio de ~60 bytes y `wamid` de ~60:

| Objeto | Bytes por fila | 10 M mensajes | 100 M mensajes |
| --- | --- | --- | --- |
| heap de `messages` (con `fillfactor` 90, `search_vector` incluido: ~60 B por fila con ~8 lexemas) | ~340 | ~3,4 GB | ~34 GB |
| `PK_messages` | ~32 | ~0,3 GB | ~3 GB |
| `IX_messages_thread` | ~56 | ~0,6 GB | ~5,6 GB |
| `IX_messages_connection_wamid` | ~90 | ~0,9 GB | ~9 GB |
| `IX_messages_conversation_client` (≈ 40 % salientes) | ~20 efectivos | ~0,2 GB | ~2 GB |
| `IX_messages_tenant_search` (GIN; listas comprimidas, ~30–50 B por fila) | ~40 | ~0,4 GB | ~3–5 GB |
| **Total `messages`** | **~580** | **~5,8 GB** | **~58 GB** |

`conversations` (~350 bytes con sus índices por fila) queda en decenas o cientos de MB aun con
millones de contactos. `webhook_deliveries` se purga a 7 días y su tamaño depende del tráfico de
una semana. Si el índice de `wamid` pesa demasiado, la salida es guardar un hash de 16 bytes del
`wamid` en vez del texto; no hace falta hoy.

## 8. Flujos

### 8.1 Conectar WhatsApp (Embedded Signup)

`POST /integrations/whatsapp/embedded-signup`, permiso `manage`. El `code` vence a los 30 s, así que
**antes del canje sólo hay validaciones de milisegundos**, nada contra Meta:

1. Tenant y permiso (403); `whatsapp-cloud` visible para el tenant, o 403
   `tenancy.module_not_enabled`.
2. FluentValidation: `name` (las reglas del agregado), `path` ∈ los dos valores, `event` ∈ los dos
   valores, `code` no vacío (≤ 1024), `wabaId` dígitos (≤ 32), `phoneNumberId` dígitos y
   **obligatorio con `FINISH`**, `null` con coexistencia, `businessId` dígitos o `null` →
   `validation.failed`.
3. Una consulta: tope de 5 por proveedor (`limit_reached`), nombre libre (`name_taken`) y, si llegó
   `phoneNumberId`, que no tenga ruta (`number_already_connected`). Es una lectura indexada; la
   garantía final la dan los índices únicos al guardar.
4. **Canje:** `GET /{v}/oauth/access_token?client_id&client_secret&code`. Error o sin token →
   `integrations.whatsapp.code_exchange_failed`.
5. Si `event = FINISH`: `POST /{phoneNumberId}/register` con `{ messaging_product: "whatsapp",
   pin }`, con un **PIN aleatorio de 6 dígitos** (`RandomNumberGenerator`) que **no se guarda**.
   Guardarlo agregaría una clave a `secrets` y rompería el contrato de `ConnectionResponse`; si un
   día hace falta volver a registrar, ese registro fija un PIN nuevo. Error →
   `integrations.whatsapp.registration_failed` (incluido el `133016` de 10 registros en 72 h, y el
   caso de un número que ya tenía verificación en dos pasos con otro PIN: ver §13).
6. Si es coexistencia: `GET /{wabaId}/phone_numbers` para resolver el número. Con uno solo, ése.
   Con varios, el que no tenga ruta en QEP; si queda más de uno **no se elige ninguno** →
   `registration_failed` con `detail` «La cuenta tiene varios números sin conectar; deja uno solo
   o conéctalo desde el número nuevo», y se registra en el log (D-M15). **No** se llama
   `/register` (§3).
7. `POST /{wabaId}/subscribed_apps` con el token. Error → `registration_failed`.
8. `GET /{phoneNumberId}?fields=display_phone_number,verified_name,quality_rating,status`.
9. Crea la conexión `Active` con `displayPhoneNumber`, `verifiedName`, `phoneNumberId`, `wabaId`,
   `qualityRating` y el secreto `accessToken` cifrado (`ISecretProtector`, AAD por conexión y
   campo), `last_verified_at = now`, la fila de `connection_routes` y la auditoría, en **una**
   transacción. Una carrera con otro tenant sobre el mismo número la resuelve el índice único →
   `number_already_connected`.
10. 201 `ConnectionResponse`.

**Si algo falla después del canje** (pasos 5–9), Meta pudo quedar con el número registrado o la
app suscrita. No se deshace: volver a intentar exige otro `code` (otro popup) y los pasos 5 y 7 son
seguros de repetir dentro de los límites de Meta. Un `number_already_connected` en el paso 9 deja la
app suscrita a la WABA del otro intento; los webhooks de ese número siguen yendo a la conexión que
ya existe, que es la correcta.

Los pasos 4–8 usan el `HttpClient` `integrations.meta-graph` (10 s, sin redirecciones, sin
loggers). Se registran código HTTP, `error.code`, `error_subcode` y `fbtrace_id`; **nunca** el
`code`, el token, el PIN ni el `client_secret`.

### 8.2 Webhook: recibir rápido, procesar después

**Por qué no el limitador `Public`.** `Public` es una ventana fija de 120 por minuto **por IP** y
sin cola (`Program.cs:56-70`). Meta manda desde pocas IPs compartidas y en ráfagas (lotes de hasta
1000 actualizaciones, §3); un 429 hace que Meta reintente con espera creciente hasta 7 días y puede
terminar deshabilitando el webhook. En su lugar, `Webhook`: concurrencia global con cola, que
protege al pod sin rechazar ráfagas normales.

**GET (verificación):** `hub.mode == "subscribe"` y `hub.verify_token` igual a
`Meta:App:WebhookVerifyToken` comparado en tiempo constante (`CryptographicOperations.FixedTimeEquals`
sobre los bytes UTF-8) → 200 `text/plain` con `hub.challenge` tal cual, más
`X-Content-Type-Options: nosniff`. Cualquier otra cosa → 403. Sin `Meta:App` configurado → 403.

**POST:**

1. Lee el cuerpo **crudo** con tope de 4 MiB (`Messaging:Webhook:MaxBodyBytes`, §3: Meta manda
   hasta 3 MB). Más → 413.
2. `X-Hub-Signature-256` = `sha256=` + hex. HMAC-SHA256 del cuerpo crudo con `Meta:App:AppSecret`;
   compara los 32 bytes en tiempo constante. Falta, mal formada o distinta → **401 sin tocar la
   base**.
3. `INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at) … ON CONFLICT
   (body_sha256) DO NOTHING`.
4. 200 con cuerpo vacío.

El tiempo del POST es una HMAC y un INSERT; nada depende de Integrations, de Customers ni de Meta.

**Worker (`WebhookDeliveryWorker`, reclamos como `IdentityInboxClaims`):** toma un lote de pendientes
(`IX_webhook_deliveries_pending`), reclama cada una con una sentencia que se commitea sola
(`claimed_until = now + lease[n]`, `attempts + 1`, sólo si no está procesada y el reclamo venció) y
la procesa en su propio scope. Varios pods pueden correrlo a la vez: el reclamo da un solo ganador.

Procesar una entrega = recorrer `entry[].changes[]`; cada `change` en su propia transacción e
idempotente, así que reprocesar una entrega a medias no duplica nada:

- `field = "messages"`: ruta por `value.metadata.phone_number_id` en
  `IMessagingConnectionDirectory.FindRouteAsync` (caché 60 s).
  - Sin ruta (conexión borrada o número desconocido) → se descarta y se registra (phone number id
    y cantidad, sin contenido).
  - `Paused`, **o módulo `messaging` apagado para ese tenant** (la ruta trae el tenant; se
    consulta `ITenantModules` con la misma caché de 60 s; D-M18) → **mensajes entrantes
    descartados**; `statuses` sí se aplican.
  - `Active` o `NeedsAttention` → `messages[]` por §7.5 y `statuses[]` por §7.5.
  - Por cada mensaje: `wa_id` = `contacts[]` del mismo `wa_id` que `from`; `profile_name` =
    `contacts[].profile.name` (truncado a 256); `occurred_at` = `timestamp` (segundos Unix).
- `field = "account_update"`: rutas por `entry.id` (WABA) en `FindByAccountAsync`. Con
  `DISABLED_UPDATE` + `ban_info.waba_ban_state = "DISABLE"`, `ACCOUNT_DELETED`, `PARTNER_REMOVED`,
  `PARTNER_APP_UNINSTALLED` o `ACCOUNT_OFFBOARDED` → `ReportRejectedAsync(…, "account_disabled")`
  para cada conexión (pasa de `Active` a `NeedsAttention` por el `IConnectionHealthReporter` que ya
  existe; si no está `Active`, lo ignora). `account_disabled` es un `lastFailureCode` nuevo
  (D-M16, §10.2). Los demás eventos se registran y nada más; `REINSTATE` y `ACCOUNT_RECONNECTED`
  **no** reactivan solos: la persona prueba la conexión.
- Otro `field` (incluidos `history`, `smb_app_state_sync` y `smb_message_echoes`, que no se
  suscriben en este slice, D-M13) → se registra y se ignora.

**La carrera del `sent` antes que el `wamid`.** Meta puede mandar el `sent` antes de que el envío
haya commiteado el `wamid` (§8.3 mantiene la fila sin commitear mientras espera la respuesta de
Meta). Cada envío de QEP lleva `biz_opaque_callback_data = "qep:{messageId}"` (§3), así que:

- `status` **con** callback de QEP y sin fila todavía → la entrega queda pendiente y se reintenta
  con la curva de leases; después de **N = 8** intentos (~1 h) se deja, se marca procesada con
  `last_error` y se registra una advertencia.
- `status` **sin** callback y sin fila → no es un mensaje de QEP (p. ej. uno enviado desde la app
  del teléfono en coexistencia): se descarta en el acto, sin reintentos inútiles.

**Purga diaria:** `DELETE … WHERE processed_at < now() - interval '7 days'` en lotes. Una entrega
que agotó sus intentos se marca procesada y también se purga.

### 8.3 Enviar un mensaje

`POST /messaging/conversations/{id}/messages`. Orden de chequeos: validador (`validation.failed`) →
tenant y permiso → `TenantModuleGuard` → conversación del tenant (404
`messaging.conversation.not_found`) → **idempotencia** → `status = Open`
(`messaging.conversation.not_open`) → ventana (`last_inbound_at + 24 h > now`; si no, o si es
`null`, `messaging.window_closed`) → conexión `Active` por `ResolveSenderAsync` (si no,
`messaging.connection_unavailable`) → Meta.

**Idempotencia sin que la fila a medio enviar se vea**, en **una** transacción:

1. `INSERT INTO messaging.messages (…, client_id, status = 1, …) ON CONFLICT (conversation_id,
   client_id) WHERE client_id IS NOT NULL DO NOTHING RETURNING id`.
   - Si otro request con el mismo `clientId` está en vuelo, PostgreSQL hace **esperar** este
     `INSERT` hasta que aquél termine (conflicto con una fila sin commitear). Al terminar, este no
     inserta.
2. Si no insertó: `SELECT … FOR UPDATE` de la fila existente.
   - `Sent`, `Delivered` o `Read` → **201 con ese mismo mensaje**, sin llamar a Meta (y antes de
     mirar la ventana o el estado de la conversación: lo que ya salió, ya salió).
   - `Failed` → se reenvía sobre la misma fila (no se entregó, así que no se duplica).
3. `POST /{phoneNumberId}/messages` con `{ messaging_product: "whatsapp", recipient_type:
   "individual", to: waId, type: "text", text: { body }, biz_opaque_callback_data:
   "qep:{messageId}" }`.
4. Según la respuesta:

| Meta responde | Fila | HTTP |
| --- | --- | --- |
| 2xx con `messages[0].id` | `status = Sent`, `wamid`, foto de la conversación (`last_activity_at`, `last_message_*`) | **201 `Message`** |
| error Graph `190` | no se guarda la fila (rollback); `ReportRejectedAsync(…, "token_expired")` | 422 `messaging.connection_unavailable` |
| error `133010` | rollback; `ReportRejectedAsync(…, "number_unregistered")` | 422 `messaging.connection_unavailable` |
| error `131047` | `Failed` con el código | 422 `messaging.window_closed` (la ventana se cerró del lado de Meta) |
| otro 4xx | `Failed`, `failure_code`, `failure_title` | 422 `messaging.message.rejected` |
| timeout, 5xx, red | `Failed` con `failure_code = -1` («sin confirmar») | 422 `messaging.message.rejected` |

La transacción mantiene un candado sobre **una fila de mensaje** mientras Meta responde (≤ 10 s).
Bloquea sólo a otro request con el mismo `clientId`, nunca la conversación; los envíos son al ritmo
de una persona y el pool aguanta. Si el pod muere a mitad, la fila no queda (nunca se commiteó) y el
reintento la crea de nuevo.

**Riesgo residual documentado:** con timeout, Meta pudo haber aceptado el mensaje. Si llega su
`sent` con el callback, §7.5 corrige la fila (`-1` → `Sent`) y un reintento posterior devuelve ese
mensaje sin reenviar. Si la persona reintenta **antes** de que llegue el acuse, puede salir dos
veces. Es la única ventana de duplicado, y sólo con una falla de red justo en el envío.

`occurred_at` del saliente = hora del servidor al recibir el 2xx; `sent_by_member_id` = la
persona; `clientId` vuelve tal cual en la respuesta.

### 8.4 Marcar leído

`POST /messaging/conversations/{id}/read` (manage, sin `If-Match`): por el agregado, `unreadCount =
0`, `updated_at = now`, `version + 1`, commit. **Después** del commit, si hay `last_inbound_wamid` y
el último entrante tiene menos de 30 días (§3), `POST /{phoneNumberId}/messages` con `{
messaging_product: "whatsapp", status: "read", message_id }` — uno solo basta, Meta marca también
los anteriores. **Best effort:** timeout corto (5 s), una falla se registra y la respuesta igual es
**204**. Con `unreadCount` ya en 0 no se llama a Meta. Con la conexión no `Active` tampoco.

### 8.5 Resolver y reabrir

Por el agregado `Conversation` con `If-Match` (428 sin él, 412 con versión vieja). `Resolve` sobre
`Resolved` → 422 `messaging.conversation.already_resolved`; `Reopen` sobre `Open` → 422
`messaging.conversation.already_open`. `updated_at`, `version + 1`, auditoría en la misma
transacción. Responden `ConversationSummary`. Un entrante nuevo reabre solo (§7.5).

### 8.6 Medios entrantes

**Copia (`MediaCopyWorker`):** reclama pendientes con `FOR UPDATE SKIP LOCKED` y
`next_attempt_at = now + lease`, y por cada uno:

1. `ResolveSenderAsync(tenantId, connectionId)` → token. Sin conexión `Active` → reintento más
   tarde.
2. `GET /{meta_media_id}` → `url`, `mime_type`, `sha256`, `file_size`. Más de 100 MB → se rinde.
3. `GET url` con `Authorization: Bearer` (la URL vence a los 5 minutos: se pide justo antes) y se
   hace **stream** a `IMessagingMediaStore.UploadAsync("messaging/{tenantId}/{messageId}", stream,
   file_size, mime_type)` calculando SHA-256 al pasar. Si no coincide con el de Meta → falla e
   intento siguiente.
4. `stored_at`, `storage_key`, `size_bytes`.

Reintentos con espera creciente (1 min, 5 min, 30 min, 2 h, 6 h…). Meta guarda el medio **7 días**:
pasado ese plazo se deja de intentar y queda `last_error`. El token no se registra nunca.

**Servir (`GET /messaging/media/{messageId}`, read):** tenant, permiso y módulo; el mensaje es del
tenant y tiene `message_media` con `stored_at` → stream desde R2 con su `Content-Type`,
`Content-Length`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: sandbox;
default-src 'none'` y `Cache-Control: private, max-age=3600`. `Content-Disposition`: `inline` sólo
para `image/*` (salvo `image/svg+xml`), `audio/*` y `video/*`; `attachment` con el nombre del
archivo para todo lo demás. Así un HTML o un SVG que alguien mande por WhatsApp no se ejecuta en el
origen de la API.

Mientras no se copió (o el mensaje no tiene medio, o no existe en el tenant) → **404 sin código**:
no se inventa uno (contrato). El frontend lo trata como medio no disponible y el poll lo vuelve a
pedir.

### 8.7 Leer la lista y el hilo

- `customerWindowExpiresAt = last_inbound_at + 24 h`, o `null`.
- `customer`: `IMessagingCustomerDirectory.MatchAsync` con los `waId` de la página (§6.5).
- `connectionName`: `ListNamesAsync`; si la conexión se borró, `"Conexión eliminada"` (D-M17).
- `lastMessage`: la foto; `null` si no hay. `preview` = `text`, o `caption` en un medio.
- Búsqueda: `search` se normaliza (trim). Busca `profile_name ILIKE '%term%'`, `wa_id LIKE '%dígitos%'`
  (sólo si el término tiene dígitos) y `wa_id = ANY(phones)` con los dígitos de
  `FindPhonesByNameAsync`.
- Hilo: `kind` → `text` (columna `text`; en un medio es `null`), `media` (si hay `message_media`;
  `media.caption` sale de `messages.caption` y la `url` es la ruta de §8.6 aunque todavía no esté
  copiado), `location` desde `details`, `failureReason` por §10.3, `sentBy` con el nombre del
  miembro (lookup por página, como Quotations con el asesor).

**Mapa de tipos de Meta a `kind`:**

| `type` de Meta | `kind` | `text` | `details` |
| --- | --- | --- | --- |
| `text` | `Text` | `text.body` | — |
| `image`, `video`, `audio`, `document`, `sticker` | el homónimo | `null`; la leyenda (`caption`) va a `messages.caption` | — (`id`, `mime_type`, `sha256` y `filename` del documento van a `message_media`) |
| `location` | `Location` | — | `latitude`, `longitude`, `name`, `address` |
| `contacts` | `Contacts` | nombres formateados, separados por coma | el arreglo |
| `reaction` | `Reaction` | el emoji | `message_id`, `emoji` |
| `interactive` | `Interactive` | título de `button_reply` o `list_reply` | el objeto |
| `button` | `Interactive` | `button.text` | el objeto |
| cualquier otro (`unsupported`, `order`, `system`, desconocidos) | `Unsupported` | — | `type` original y `errors` si vienen |

`Template` queda reservado para salientes; esta versión no envía plantillas. Que `button` caiga en
`Interactive` y no en `Unsupported` es D-M6.

### 8.8 Buscar en el historial

`GET /messaging/messages/search` (read, historia 17), `SearchMessagesHandler`:

1. Validador (`validation.failed`) → tenant y permiso (403) → `TenantModuleGuard` (403 con código).
2. Si viene `conversationId`, la conversación es del tenant o 404 `messaging.conversation.not_found`.
   Si viene `before`, el mensaje es del tenant (`SELECT occurred_at, id … WHERE id = @before AND
   tenant_id = @t`) o 404 `messaging.message.not_found`; de ahí salen `@beforeAt` y `@beforeId`.
3. Se arma la `tsquery` como dice §7.3. Sin lexemas → `{ "items": [], "hasMore": false }` sin
   consultar.
4. La consulta de §7.3 con `SET LOCAL statement_timeout = '2s'` en la misma transacción, `LIMIT
   limit + 1`; la fila extra sólo decide `hasMore`. Un timeout (`57014`) se traduce en
   `MessagingUnitOfWork` a `ValidationException` con `errors["q"]` («Busca con una palabra más
   específica o acota las fechas»), nunca 500.
5. Por página: las conversaciones de los resultados (una consulta por `id = ANY(…)`), `customer`
   por `MatchAsync` con sus `waId`, `connectionName` por `ListNamesAsync`, `sentBy` por el lookup
   de miembros. Cada `MessageHit` se arma como un `Message` (§8.7) más esos cuatro campos.

No hay resaltado de coincidencias ni `ts_rank`: el orden es por fecha, y la pantalla marca el
término por su cuenta si lo quiere.

## 9. Configuración

| Clave | Dónde | Notas |
| --- | --- | --- |
| `Meta:App:AppId` | ConfigMap `Meta__App__AppId` | Público (viaja en el popup) |
| `Meta:App:ConfigId` | ConfigMap `Meta__App__ConfigId` | Configuración de Embedded Signup |
| `Meta:App:GraphApiVersion` | ConfigMap `Meta__App__GraphApiVersion` | `v24.0` (contrato); patrón `^v\d+\.\d+$` |
| `Meta:App:AppSecret` | Secret `Meta__App__AppSecret: "#{META_APP_SECRET}#"` | Canje del `code` y firma del webhook |
| `Meta:App:WebhookVerifyToken` | Secret `Meta__App__WebhookVerifyToken: "#{META_WEBHOOK_VERIFY_TOKEN}#"` | Aleatorio, ≥ 32 caracteres |
| `Messaging:Webhook:MaxBodyBytes` | default 4194304 | §3 |
| `Messaging:Webhook:ConcurrencyLimit` / `QueueLimit` | default 64 / 256 | §8.2 |
| `Messaging:Workers:*` | defaults en código | intervalos de entregas, medios y purga |

`MetaAppOptionsValidator` con `ValidateOnStart`: en `Production` exige las cinco claves (y la forma
de la versión); sin ellas el pod no arranca, a propósito, como `SecretProtectionOptionsValidator`.
Fuera de producción el módulo arranca sin ellas: `whatsapp-cloud` no sale en el catálogo, el
webhook responde 403 al GET y 401 al POST, y el resto de Messaging funciona contra lo que haya en la
base. En local van por user-secrets y se verifican **contando**, nunca listando
(`dotnet user-secrets list | Select-String -Pattern "Meta:App" | Measure-Object`).

`appsettings.example.json` gana las claves no secretas con valores de ejemplo;
`ConfigurationExampleTests` lo verifica.

## 10. Errores

### 10.1 Códigos HTTP (mapeo central en `ApiExceptionHandler`; ningún handler arma un status)

| Código | HTTP | Cuándo |
| --- | --- | --- |
| `validation.failed` con `errors` | 422 | FluentValidation (§6.4, §8.1); campos internos o de sólo lectura en `POST`/`PUT` de conexiones; `q` de la búsqueda fuera de 2–100 o búsqueda que excede los 2 s (`errors["q"]`) |
| `integrations.whatsapp.code_exchange_failed` | 422 | Meta no canjeó el `code` (vencido, usado, app mal configurada) o falta `Meta:App` |
| `integrations.whatsapp.registration_failed` | 422 | `/register`, `phone_numbers` o `subscribed_apps` fallaron; número ambiguo en coexistencia |
| `integrations.whatsapp.number_already_connected` | 422 | ruta existente, o `IX_connection_routes_provider_external` al guardar |
| `integrations.connection.limit_reached` | 422 | 5 por tenant para `whatsapp-cloud` |
| `integrations.connection.name_taken` | 422 | índice único de nombre |
| `messaging.window_closed` | 422 | ventana vencida, nunca escribió, o Meta respondió `131047` |
| `messaging.connection_unavailable` | 422 | conexión `Paused`, `NeedsAttention`, borrada u oculta; o Meta respondió `190`/`133010` |
| `messaging.conversation.not_open` | 422 | enviar a una `Resolved` |
| `messaging.message.rejected` | 422 | Meta rechazó el mensaje o no se pudo confirmar |
| `messaging.conversation.already_resolved` / `already_open` | 422 | resolve/reopen sin cambio |
| `messaging.conversation.not_found` | 404 | id inexistente **dentro del tenant** (también el `conversationId` de la búsqueda). Un `before` de otra conversación en el hilo es `validation.failed` |
| `messaging.message.not_found` | 404 | `before` de la búsqueda que no es un mensaje del tenant |
| 404 sin código | 404 | medio no copiado, mensaje sin medio o inexistente |
| `tenancy.module_not_enabled` | 403 | `messaging` apagado (`/messaging/*`, `embedded-signup`, conexiones de `whatsapp-cloud`) |
| `authorization.denied` | 403 | otro tenant o sin permiso. **Nunca 404** para otro tenant |
| `concurrency.conflict` / precondición | 412 / 428 | `If-Match` en resolve/reopen |
| — | 401 | webhook con firma inválida |
| — | 403 | verificación del webhook con token distinto |
| — | 413 | cuerpo del webhook sobre el tope |
| — | 429 | cola del limitador `Webhook` llena (Meta reintenta) |

### 10.2 `lastFailureCode` de una conexión `whatsapp-cloud`

| Código | Cuándo | Lo dibuja el frontend |
| --- | --- | --- |
| `token_expired` | Graph `190` al probar o al enviar | Sí (`integrations.ts:125`) |
| `number_unregistered` | Graph `133010` al probar o al enviar | Sí (`integrations.ts:126`) |
| `credentials_rejected` | otro 400/401/403 al probar | Sí |
| `provider_unreachable` | timeout, 5xx, red al probar | Sí |
| `account_disabled` | `account_update` de cuenta deshabilitada, borrada, app desinstalada o partner quitado (§8.2; D-M16) | **No todavía**: `labelFor` muestra el código crudo (`integrations.ts:193-198`). El frontend agrega la etiqueta «Meta deshabilitó la cuenta de WhatsApp Business o quitó el acceso de QEP» en su slice |

### 10.3 `failureReason` en español

Se calcula **al leer** desde `failure_code` (cambiar un texto no pide migración). Sólo códigos
verificados en https://developers.facebook.com/docs/whatsapp/cloud-api/support/error-codes; todo
otro código → texto genérico.

| Código | Texto |
| --- | --- |
| `131047` | Pasaron más de 24 horas desde el último mensaje de la persona: WhatsApp sólo acepta plantillas aprobadas. |
| `131026` | WhatsApp no pudo entregar el mensaje: puede que el número no use WhatsApp o tenga una versión desactualizada. |
| `131051` | WhatsApp no admite este tipo de mensaje. |
| `131049` | WhatsApp no entregó el mensaje para cuidar la experiencia de la persona. Espera al menos 24 horas antes de volver a intentarlo. |
| `131050` | La persona dejó de recibir mensajes de marketing de tu organización. |
| `131021` | No puedes enviarle un mensaje al mismo número que lo envía. |
| `131031` | La cuenta de WhatsApp Business está restringida o no pasó una verificación. |
| `131042` | Hay un problema con el método de pago de la cuenta de WhatsApp Business. |
| `131056` | Enviaste demasiados mensajes a esta persona en poco tiempo. Espera un momento y vuelve a intentarlo. |
| `133010` | El número de tu organización no está registrado en WhatsApp Business. |
| `368` | La cuenta de WhatsApp Business está restringida o deshabilitada por incumplir políticas. |
| `131005` | QEP perdió los permisos sobre tu cuenta de WhatsApp. Vuelve a conectar el número. |
| `131016` | WhatsApp no está disponible en este momento. Intenta de nuevo en unos minutos. |
| `131000` | WhatsApp no pudo enviar el mensaje por un error desconocido. Intenta de nuevo. |
| `-1` (QEP) | No pudimos confirmar el envío con WhatsApp. Intenta de nuevo. |
| cualquier otro | WhatsApp no pudo entregar el mensaje. |

`failure_title` (inglés, de Meta) se guarda para soporte y **nunca** sale por HTTP.

## 11. Seguridad

- **Firma:** HMAC-SHA256 sobre los bytes crudos, comparación en tiempo constante, 401 antes de
  tocar la base. Sin firma válida, nada entra a `webhook_deliveries`.
- **Secretos nunca en un log:** `AppSecret`, `WebhookVerifyToken`, el `code`, el token de acceso, el
  PIN y la URL firmada de un medio. Los `HttpClient` de Graph usan `RemoveAllLoggers`; se registran
  status, `error.code`, `error_subcode` y `fbtrace_id`. El cuerpo de un webhook tampoco se registra
  (trae mensajes de personas).
- **El token nunca llega al frontend:** `accessToken` viaja sólo como `{ configured, updatedAt,
  readable }`, como todo secreto de Integrations. La prueba de fuga de Integrations
  (sentinela en logs, respuestas, auditoría y outbox) se extiende a Embedded Signup y al envío.
- **Medios:** servidos con `nosniff`, CSP `sandbox` y `attachment` para lo que no es imagen, audio
  o video (§8.6).
- **Aislamiento:** doble capa (ruta + política; handler revalida tenant y permiso). El webhook
  cruza tenants **sólo** por `IConnectionRoutes`, que devuelve tenant y conexión desde un id de
  Meta que vino firmado.
- **CSRF:** la excepción es por ruta y el webhook no lee la cookie de sesión (§6.7).

## 12. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias:**

- Messaging.Domain: `Conversation` (resolve/reopen y sus 422, ventana, `MarkRead`), mapa de
  `kind`/`status` de ida y vuelta, `failureReason`, mapa de tipos de Meta.
- Integrations: `FieldDefinition.Internal` (catálogo sin internos, `PUT`/`POST` los rechazan),
  `onboarding` (`Form` sin claves extra), `CompleteWhatsAppSignupHandler` con dobles de Graph
  (FINISH, coexistencia con uno y con varios números, cada error), `WhatsAppCloudConnectionTester`
  (200 con `CONNECTED` y 200 con otro `status` → los dos `Ok`; `190`; `133010`; 5xx; timeout).
- Messaging: armado de la `tsquery` (operadores quitados, tope de 8 tokens, prefijo sólo con 3 o
  más caracteres, *stop words* omitidas, sin lexemas → vacío).
- Customers: `phone_e164` (con `+`, nacional por país, prefijo troncal, inválido → `null` sin
  romper la escritura), regla de duplicados.
- Tenancy: orden de `All`, `DefaultForNewTenants` sin `messaging`.
- Validadores: claves de `errors`.

**Integración (Testcontainers + `HttpMessageHandler` falso de Meta):**

- Webhook: firma válida → 200 y una fila; inválida o ausente → 401 y **cero** filas; GET con token
  bueno y malo; cuerpo sobre el tope → 413.
- **Carga:** N POSTs firmados concurrentes con `wamid` repetidos → **cero 429**, un mensaje por
  `wamid`, `unreadCount` exacto, una conversación por `waId`.
- Ingesta: reapertura de una resuelta, reenvío viejo que no reabre ni sube contadores, foto del
  último mensaje con mensajes fuera de orden, `Paused` y módulo apagado descartan entrantes y
  aplican estados, ruta desconocida descarta, `account_update` de cuenta deshabilitada deja la
  conexión en `NeedsAttention` con `account_disabled`, un `field` desconocido se ignora.
- Estados: monotonía (`read` antes que `delivered`), `failed` sólo sobre `Sent`, `played` →
  `Read`, callback que llega antes del `wamid` (reintento y éxito), status ajeno sin callback
  descartado sin reintentos.
- Envío: idempotencia por `clientId` (dos concurrentes → una llamada a Meta), `Failed` se reenvía,
  `190` → `NeedsAttention` + `connection_unavailable`, `131047` → `window_closed`, timeout → `-1`.
- Lista: orden, `counts` (suma de `unreadCount`), búsqueda por perfil, número y cliente.
- Hilo: paginación con `before` y `hasMore`, orden cronológico.
- Búsqueda en el historial: tres mensajes repartidos en dos tenants → sólo los del tenant;
  «drogueria» encuentra «Droguería» y «pedido» encuentra «pedidos» (acentos y flexiones); prefijo
  («drog»); una leyenda de imagen se encuentra; paginación por `before` con `hasMore`; filtro
  `conversationId`; entrada con operadores (`&|!:*()'`) → 200 vacío; `q` de un carácter → 422
  `validation.failed` en `q`; `before` de otro tenant → 404 `messaging.message.not_found`; la
  migración es idempotente (correrla dos veces sobre la misma base no falla).
- Módulo apagado → 403 con código en cada endpoint; sin permiso → 403; otro tenant → 403; 412/428.
- Medios: copia con almacén falso, `sha256` que no coincide, 404 sin código antes de copiar, headers
  de seguridad al servir.
- Embedded Signup: carrera por el mismo número en dos tenants → `number_already_connected`; borrar
  la conexión libera la ruta.
- Fuga de secretos con sentinela (`AppSecret`, token, `code`).

**Arquitectura:** `MessagingLayerTests` (capas; Messaging no referencia Integrations, Customers ni
Storage), conteo de permisos a 48, roles de sistema con los permisos de Messaging como núcleo,
`ConfigurationExampleTests`.

**Corridas:** por tarea, sólo las clases tocadas más `ArchitectureTests`. La suite completa **una
vez al final**, comparada por nombre contra `develop` con un script, y `dotnet build` sin warnings.

## 13. Riesgos y decisiones

### Decisiones tomadas sin el owner

| # | Decisión | Si está mal, cuesta |
| --- | --- | --- |
| D-M1 | Tope del webhook 4 MiB (Meta: 3 MB), no 1 MB | Una constante |
| D-M2 | `account_id` (WABA) en `connection_routes` para `account_update` | Una columna |
| D-M3 | `whatsapp-cloud` fuera del catálogo si falta `Meta:App` (sólo fuera de producción); `embedded-signup` → `code_exchange_failed` | Un código nuevo si se prefiere 503 |
| D-M4 | `PUT` de `whatsapp-cloud` sólo cambia `name`; `POST /connections` lo rechaza | Abrir campos en el validador |
| D-M5 | Entrantes con `status = Delivered` fijo | Nada en la pantalla (sólo dibuja estado en salientes) |
| D-M6 | `button` de Meta → `Interactive` | Mover una fila del mapa |
| D-M7 | Duplicados de cliente: gana el más viejo, luego `id` menor | Cambiar el `ORDER BY` |
| D-M8 | `biz_opaque_callback_data = "qep:{messageId}"` para correlacionar estados | Nada: campo opcional de Meta |
| D-M9 | `played` → `Read` | Un caso del mapa |
| D-M10 | Al eliminar una conexión no se llama a Meta (ni `deregister` ni desuscribir) | El número sigue registrado en la Cloud API; `deregister` tiene el mismo límite de 10 en 72 h |
| D-M11 | Las conversaciones de una conexión eliminada se conservan, de sólo lectura (enviar → `connection_unavailable`). Reconectar el mismo número crea una conexión nueva y, con ella, conversaciones nuevas | Una migración que reasigne `connection_id` |
| D-M12 | `REINSTATE`/`ACCOUNT_RECONNECTED` no reactivan solos | La persona prueba la conexión |

Las siete que siguen cierran las DECISIÓN-PENDIENTE de la primera versión. Se tomaron sin el owner
el 2026-10-09 y quedan **a ratificar** en su lectura:

| # | Decisión (a ratificar) | Cierra | Si está mal, cuesta |
| --- | --- | --- | --- |
| D-M13 | Los campos de coexistencia (`history`, `smb_app_state_sync`, `smb_message_echoes`) **no se suscriben** en este slice; el worker ignora cualquier `field` desconocido. El HANDOFF lo dice: lo que la persona responda desde la app del teléfono **no aparece en QEP** hasta un slice posterior que suscriba `smb_message_echoes` | DP-1 | Ese slice: un `field` más en el worker y un saliente sin `sentBy` |
| D-M14 | El probador acepta el número si `GET /{phoneNumberId}` responde 200, **sin mirar `status`**; sólo un error de Graph marca falla | DP-2 | Si un número de verdad desconectado responde 200, se descubre al enviar (`133010`) |
| D-M15 | Coexistencia con varios números sin conectar en la WABA: no se elige ninguno → 422 `integrations.whatsapp.registration_failed` con `detail` que lo explica; el HANDOFF lo anota | DP-3 | Que el frontend mande el número elegido |
| D-M16 | Cuenta deshabilitada, borrada, app desinstalada o partner quitado → `lastFailureCode` nuevo `account_disabled` | DP-4 | Una etiqueta en el frontend (hoy muestra el código crudo, §10.2) |
| D-M17 | `connectionName` de una conexión eliminada = `"Conexión eliminada"` | DP-5 | Guardar el último nombre en la conversación |
| D-M18 | Módulo `messaging` apagado con mensajes llegando: se descartan como con `Paused`; los `statuses` sí se aplican | DP-6 | Guardarlos para que aparezcan al reactivar |
| D-M19 | `business_management` **no se pide** en App Review: Meta lo exige sólo a un Solution Partner que comparte línea de crédito (§3) | DP-7 | Pedirlo después, con otra revisión de Meta |

### Riesgos

- **PIN aleatorio y verificación en dos pasos previa.** Si el número ya tenía verificación en dos
  pasos con otro PIN, `/register` falla (§3) → `registration_failed`. La persona tiene que quitar
  ese PIN en WhatsApp Manager y reintentar; el mensaje de error del frontend debería decirlo.
- **Caché de rutas de 60 s:** pausar o borrar tarda hasta un minuto en reflejarse en la ingesta.
- **Duplicado por timeout en el envío** (§8.3): sólo si la persona reintenta antes del acuse.
- **Polling:** con muchas personas con la bandeja abierta, la lista es la consulta más frecuente.
  Está acotada por índice; si crece, el siguiente paso es un ETag por `max(updated_at)` del tenant
  para responder 304.
- **Números de México y Argentina:** el `wa_id` de Meta para móviles de esos países puede no
  coincidir dígito a dígito con el E.164 que da libphonenumber. **No verificado**; hoy los clientes
  son colombianos. Si aparece, se agrega una normalización por país en el match.
- **Medios después de 7 días:** si una conexión queda `NeedsAttention` más de una semana, sus
  medios entrantes ya no se pueden copiar.
- **Búsqueda con un término muy común** en un tenant enorme: ordena todas las coincidencias y
  puede chocar con los 2 s (§7.3). Es el límite conocido del enfoque; el siguiente paso es un motor
  externo.
- **Lista pendiente del GIN:** con `fastupdate`, una lista pendiente grande se recorre entera en
  cada búsqueda. Se vigila con `pgstatginindex` y se ajusta `gin_pending_list_limit` si crece.

### DECISIÓN-PENDIENTE

Ninguna abierta. Las siete de la primera versión (DP-1 a DP-7) se cerraron con D-M13 a D-M19, a
ratificar por el owner.

## 14. Entregables

- Código y pruebas de §6–§12, en `feature/mensajeria-whatsapp`, con las migraciones:
  `AddWhatsAppCloudProvider` y `AddConnectionRoutes` (Integrations), `AddMessagingModuleKey`
  (Tenancy), `AddCustomerPhoneE164` (Customers) e `InitialMessaging` (Messaging; incluye las
  extensiones `pg_trgm`, `unaccent` y `btree_gin`, la configuración `messaging.es_unaccent` y el
  índice GIN de búsqueda).
- `Directory.Packages.props` con `libphonenumber-csharp` y todos los `packages.lock.json` regenerados
  con `dotnet restore --force-evaluate`, en el mismo commit.
- README: secciones «Integraciones» (Embedded Signup, `Meta:App`, rutas) y «Mensajería»
  (configuración, secretos, webhook, workers, purga, cómo probar el webhook en local con un cuerpo
  firmado en archivo y `curl.exe -d "@archivo.json"`).
- `CLAUDE.md`: Messaging existe; son **15** módulos, cada uno con su `<Modulo>LayerTests.cs`.
- `appsettings.example.json` con las claves no secretas; `k8s/prod-configMap.yaml` y
  `k8s/prod-secret.yaml` con las de §9; variables `META_APP_SECRET` y
  `META_WEBHOOK_VERIFY_TOKEN` en el grupo `Backend-prod`.
- `HANDOFF` para el owner en Meta:
  1. App en **Live**, con verificación del negocio y App Review.
  2. Permisos con **Advanced Access**: `whatsapp_business_management` y
     `whatsapp_business_messaging`. `business_management` **no** se pide (D-M19).
  3. `qep.qcode.co` en «Allowed Domains for the JavaScript SDK» y en «Valid OAuth Redirect URIs».
  4. Configuración de Facebook Login for Business para **WhatsApp Embedded Signup v4**; su id va a
     `Meta__App__ConfigId`.
  5. Webhook: URL `https://<host de la API>/api/webhooks/whatsapp`, el token de verificación
     (el mismo valor que `META_WEBHOOK_VERIFY_TOKEN`) y **sólo** los campos `messages` y
     `account_update`.
  6. Desplegar el backend **antes** de verificar el webhook: Meta hace el GET al guardarlo.
  7. **Lo que hay que saber de la coexistencia (D-M13, D-M15):** lo que alguien responda desde la
     app de WhatsApp Business en el teléfono **no aparece en QEP** en esta versión; entra con un
     slice posterior que suscriba `smb_message_echoes`. Y si la cuenta de WhatsApp Business tiene
     varios números sin conectar, el flujo de coexistencia no puede saber cuál se eligió y responde
     `registration_failed`: se conecta un número por vez, o se usa el camino de número nuevo.

## Historial de revisión

- 2026-10-09: spec escrito sobre las decisiones 1–11 del owner, el contrato del frontend y la
  documentación de Meta verificada (§3). Correcciones por la documentación: tope de 4 MiB, estado
  `played`, `account_update` por WABA. Pendiente la lectura del owner y DP-1 a DP-7.
- 2026-10-09 (segunda vuelta): decisión 12 del owner, búsqueda por palabra en el historial
  (`messages.caption` y `search_vector`, configuración `messaging.es_unaccent`, índice GIN por
  tenant, `GET /messaging/messages/search`, historia 17). DP-1 a DP-7 cerradas con D-M13 a D-M19,
  a ratificar. El probador ya no mira `status`; `account_disabled` nuevo.
