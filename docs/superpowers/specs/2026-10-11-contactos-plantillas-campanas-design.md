# Ficha de contacto, etiquetas, plantillas y campañas de WhatsApp

**Fecha:** 2026-10-11
**Módulos:** Customers (notas, etiquetas, consentimiento de marketing), Messaging (plantillas,
conversación nueva, campañas), Bootstrapper (adaptadores y permisos). **Frontend:** `qep-frontend`,
`features/customers/`, `features/messaging/` y `features/campaigns/` (nueva).
**Estado:** el owner pidió el alcance el 2026-10-11 («clic en el contacto abre una modal para editarlo y
agregar notas», «tags a contactos», «campañas masivas por tag o contacto individual», «iniciar una
conversación con plantilla, con `/` desde el chat») y delegó el diseño («planifica e implementa»,
«prioriza buenas prácticas», «basado en argumentos y en las apps similares»). **Las decisiones de la §3
las tomó el asistente y quedan para revisión del owner.**
**Es un delta sobre** `2026-10-09-mensajeria-whatsapp-design.md` («el spec base»),
`2026-10-10-mensajeria-asignacion-bsuid-design.md` («el spec de asignación») y
`2026-10-11-notas-de-voz-asesor-design.md`. Las plantillas y las campañas estaban **fuera de alcance**
en los dos primeros («se pregunta antes»): este spec es esa pregunta, respondida por el pedido del owner.
**Ramas:** `feature/contactos-plantillas-campanas` desde `feature/notas-de-voz` (`71335f7`) en los dos
repos. Parte de ahí y no de `develop` porque reutiliza `OutboundGuards` y `SendResult`, que todavía no
están en `develop`.

## 1. Contexto

- **No existe una entidad «contacto».** `ContactDto` es una proyección de columnas de `Conversation`
  (BSUID, teléfono, usuario, nombre de perfil), que Meta sobrescribe. El dueño real de la persona es el
  **`Customer`**: es por tenant, abarca todas las conexiones y la ingesta lo crea `Incomplete` si no
  existe (`ICustomerWhatsAppDirectory.EnsureAsync`).
- Hoy completar un cliente incompleto es `PUT /customers/{id}`, que exige todos los campos y emite el CUC.
  El dominio **no** permite editar parcialmente uno incompleto, y eso no cambia.
- `IWhatsAppCloudClient` envía texto y audio, **no plantillas**. Fuera de la ventana de 24 h Meta sólo
  acepta plantillas aprobadas (131047). Sin plantillas no hay campañas ni manera de retomar una
  conversación vencida.
- Las conversaciones las crea sólo la ingesta. Una fila creada sólo con teléfono (`user_id NULL`,
  `wa_id`) **la adopta la ingesta** cuando llega el BSUID (`InboundIngestion.ResolveConversationAsync`,
  paso 1a). Eso permite abrir conversaciones desde la plataforma sin cambiar el modelo.

## 2. Referencias: lo que hacen Meta y las apps similares

Fuentes, el 2026-10-11: documentación de Meta (Cloud API, plantillas, BSUID, códigos de error, límites de
mensajería, política de WhatsApp Business), respond.io, WATI, Kommo, Zoko, Zendesk e Intercom.

| Tema | Hecho | Fuente / quién |
| --- | --- | --- |
| Listar plantillas | `GET /{waba_id}/message_templates`: `name`, `language`, `status`, `category`, `components`, `parameter_format` (`NAMED`/`POSITIONAL`); paginado por cursor | Meta |
| Enviables | Sólo `APPROVED`; el mismo nombre puede existir en varios idiomas | Meta |
| Enviar | `type: "template"`, `template.name`, `language.code`, `components[{type:"body", parameters:[{type:"text", text, parameter_name?}]}]`. Si no coincide la cantidad de parámetros: 132000 | Meta |
| Destinatario | `recipient` (BSUID) sirve para plantillas, excepto las de autenticación; si vienen `to` y `recipient`, gana `to` | Meta (BSUID) |
| Consentimiento | Hace falta opt-in y respetar todo opt-out. La persona puede apagar el marketing de una empresa: Meta acepta el envío y no lo entrega (131050) | Política, Meta |
| Errores no reintentables | 131049 (tope de marketing por usuario), 131050 (opt-out), 131026 (no entregable), 132001 (plantilla inexistente o de otro idioma), 132015 (pausada), 132016 (deshabilitada) | Meta |
| Reintentables | 130429 (throughput), 131056 (tasa por par emisor–destinatario), errores 5xx y timeouts | Meta |
| Throughput y límites | 80 mps por número; tope de usuarios únicos fuera de la ventana por portafolio y en 24 h: 250 → 2K → 10K → 100K | Meta |
| Ficha en el chat | Los campos del contacto se editan desde un panel de la conversación | WATI, respond.io |
| Notas internas | «Comentarios» que sólo ve el equipo, con autor y hora | respond.io |
| Etiquetas | Nombre y color. El asesor **crea en línea** al etiquetar; renombrar y borrar es de manager u owner | respond.io |
| Ventana cerrada | La caja se bloquea y aparece «Elegir plantilla», con vista previa y variables | Zendesk, Intercom |
| `/` | En casi todas abre **respuestas rápidas**; las plantillas tienen su propio selector | Help Scout, Zendesk |
| Campañas | Audiencia por etiqueta o segmento, sin duplicados, confirmación con conteo, programar, cancelar, estado por destinatario (enviado, entregado, leído, respondido, fallido) con motivo | respond.io, Kommo, WATI, Zoko |
| Opt-out | Etiqueta o bandera reservada que excluye de marketing | Zoko |

## 3. Decisiones (tomadas por el asistente, para revisión)

1. **Notas, etiquetas y consentimiento viven en `Customers`**, no en `Messaging`. Si vivieran en la
   conversación quedarían partidas por conexión y desaparecerían de la ficha del cliente. Messaging las
   consume por puertos con adaptador en Bootstrapper, como ya hace con `IMessagingCustomerDirectory`.
2. **La modal del contacto reusa el formulario de cliente**: no hay «edición parcial» de un incompleto.
   Completar la ficha sigue siendo `PUT /customers/{id}`. Inventar un estado intermedio rompería la
   invariante de `CK_customers_complete_fields`, que hoy protege a cotizaciones y reportes.
3. **Etiquetas como respond.io:** catálogo por tenant con nombre y color de una paleta fija. Quien gestiona
   clientes **crea en línea**; renombrar, cambiar color y borrar requieren un permiso nuevo de admin.
   Así no se multiplican etiquetas casi iguales.
4. **Notas:** cualquiera que gestione clientes crea; el autor edita y borra las suyas; el admin borra
   cualquiera. Sin menciones ni adjuntos (fuera de alcance, §11).
5. **`/` abre plantillas**, como pidió el owner, porque hoy no hay respuestas rápidas que compitan por esa
   tecla. Además, con la ventana cerrada la caja se bloquea y muestra «Elegir plantilla», como Zendesk e
   Intercom. Si mañana se agregan respuestas rápidas, `/` las lista juntas, en dos grupos.
6. **Plantillas leídas de Meta, no administradas acá.** Se crean y aprueban en WhatsApp Manager. La
   plataforma lista sólo las `APPROVED`, con caché de 5 minutos por conexión y botón «Actualizar». Copiar
   su ciclo de aprobación sería un segundo sistema de verdad.
7. **Soporte de plantillas v1:** encabezado de texto (sin variables) o sin encabezado, cuerpo con
   variables nombradas o posicionales, pie, y botones de respuesta rápida o URL **estática**. Las de
   encabezado multimedia, URL dinámica, autenticación, carrusel o flujos se listan como
   **no enviables**, con motivo. Mostrarlas grises evita el «¿por qué no aparece mi plantilla?».
8. **Destinatario:** `to` con el teléfono si el cliente lo tiene; si no, `recipient` con el BSUID. Es la
   regla de Meta (gana `to`) dicha explícitamente.
9. **Conversación nueva con plantilla.** «Nueva conversación» en la bandeja: eliges cliente, conexión
   (si hay más de una) y plantilla. Si ya existe la conversación de ese cliente en esa conexión, se envía
   ahí. Si no, se crea **asignada a quien la inició**: quien inicia es quien espera la respuesta.
10. **Consentimiento de marketing como dato.** Hay una bandera `marketing_opt_out` en el cliente. La
    prende o apaga una persona en la ficha, y se prende sola cuando Meta responde 131050. Las campañas con
    plantilla `MARKETING` excluyen a esos clientes; las `UTILITY` no, porque el opt-out de Meta es sólo de
    marketing. Las palabras clave (STOP, BAJA) quedan fuera de v1 por falsos positivos en español
    («no me den de baja el pedido»): registrado como §12-P1.
11. **Campañas en `Messaging`, envío asíncrono con worker**, sin reusar el camino síncrono del texto. Ese
    camino tiene una transacción abierta durante la llamada a Meta: aceptable para un mensaje, no para
    miles. El worker copia `MediaCopyWorker`: `FOR UPDATE SKIP LOCKED`, lease y reintento con
    backoff.
12. **Entrega como máximo una vez.** Un destinatario cuyo envío quedó sin resultado (caída a mitad de la
    llamada) se marca `Failed` con `unknown_outcome` y **no se reenvía**. Un duplicado de marketing
    cuesta plata y quejas; un faltante se ve en el detalle y se reintenta con una campaña nueva.
13. **Audiencia congelada al empezar el envío**, no al crear la campaña. Una campaña programada para
    mañana le llega a quien tenga la etiqueta mañana. Así funcionan los segmentos de respond.io.
14. **Ritmo:** 20 mensajes por segundo por conexión, configurable (`Messaging:Campaigns:MessagesPerSecond`).
    Es un cuarto del mínimo de Meta (80 mps), así que deja aire para la bandeja en el mismo número.
15. **Una plantilla pausada o deshabilitada** (132015/132016), una inexistente (132001) o credenciales
    rechazadas **detienen la campaña**: estado `Failed` con motivo, y los pendientes quedan `Skipped`.
    Seguir enviando sólo junta fallas.
16. **Permiso propio para campañas:** `messaging.campaign.manage`, de fábrica sólo en admin. Una campaña
    llega a miles de personas en nombre de la empresa, no es tarea diaria del asesor, y el costo es
    por mensaje.
17. **No hay tope propio de frecuencia ni control de tier.** Nadie del benchmark lo documenta: se apoyan
    en el 131049 de Meta. La pantalla de confirmación sí avisa cuando la audiencia supera 250, el tier
    de un número nuevo.

## 4. Customers: notas, etiquetas y consentimiento

### 4.1 Modelo

| Tabla | Columnas | Restricciones |
| --- | --- | --- |
| `customers.customer_notes` | `id`, `tenant_id`, `customer_id`, `body` (1–2000), `author_user_id`, `created_at`, `updated_at`, `version` | índice `(tenant_id, customer_id, created_at DESC)` |
| `customers.tags` | `id`, `tenant_id`, `name` (1–40, sin espacios en los extremos), `color`, `created_at`, `updated_at`, `version` | único `IX_tags_tenant_name` sobre `(tenant_id, lower(name))`; tope de 100 por tenant |
| `customers.customer_tags` | `customer_id`, `tag_id`, `tenant_id`, `assigned_at`, `assigned_by_user_id` | PK `(customer_id, tag_id)`; índice `(tenant_id, tag_id)`; tope de 20 por cliente; borrar la etiqueta borra sus asignaciones |
| `customers.customers` (columna nueva) | `marketing_opt_out bool NOT NULL DEFAULT false`, `marketing_opt_out_at timestamptz NULL`, `marketing_opt_out_source text NULL` (`manual`/`meta`) | — |

`color` ∈ `gray, red, orange, yellow, green, teal, blue, purple`. Viaja por nombre; el frontend lo pinta.

Las notas son su propio agregado (`CustomerNote`), no una colección dentro de `Customer`. Así una nota no
le sube la `version` al cliente: con un 412 de concurrencia, quien edita la ficha perdería su cambio por
una nota que escribió otro.

### 4.2 Contrato HTTP

Todo bajo `/api/v1/tenants/{tenantId:guid}/customers`. El aislamiento por tenant es en dos capas, y otro
tenant da **403, nunca 404** (CLAUDE.md).

| Método y ruta | Permiso | Cuerpo | Respuesta |
| --- | --- | --- | --- |
| `GET /{customerId}/notes` | `customers.customer.read` | — | `200 CustomerNoteDto[]`, de la más nueva a la más vieja |
| `POST /{customerId}/notes` | `customers.customer.manage` | `{ body }` | `201 CustomerNoteDto` |
| `PUT /{customerId}/notes/{noteId}` | `customers.customer.manage` y ser el autor | `{ body, expectedVersion }` | `200 CustomerNoteDto` |
| `DELETE /{customerId}/notes/{noteId}` | autor con `customers.customer.manage`, o `customers.note.moderate` | — | `204` |
| `GET /tags` | `customers.customer.read` | — | `200 TagDto[]` por nombre, con `customerCount` |
| `POST /tags` | `customers.customer.manage` | `{ name, color }` | `201 TagDto`; nombre repetido: `409 customers.tag.name_taken` |
| `PUT /tags/{tagId}` | `customers.tag.manage` | `{ name, color, expectedVersion }` | `200 TagDto` |
| `DELETE /tags/{tagId}` | `customers.tag.manage` | — | `204` |
| `GET /{customerId}/tags` | `customers.customer.read` | — | `200 TagDto[]` |
| `PUT /{customerId}/tags` | `customers.customer.manage` | `{ tagIds: Guid[] }` (0–20, sin repetir, todos del tenant) | `200 TagDto[]`: la colección entera, en orden (BFF) |
| `PUT /{customerId}/marketing-consent` | `customers.customer.manage` | `{ optedOut: bool }` | `200 MarketingConsentDto` |

- `CustomerNoteDto`: `{ id, body, author: { userId, displayName }, createdAt, updatedAt, canEdit, canDelete, version }`.
  `canEdit`/`canDelete` vienen calculados para que la pantalla no repita la regla (BFF). Si el autor ya
  no existe, `displayName` es «Usuario eliminado».
- `TagDto`: `{ id, name, color, customerCount, version }`.
- `MarketingConsentDto`: `{ optedOut, optedOutAt, source }`.
- `CustomerDto` (el existente) suma `tags: TagDto[]` y `marketingConsent: MarketingConsentDto`, para que la
  ficha y la modal se dibujen con un solo GET.
- Errores de dominio: `customers.note.not_found` (404), `customers.note.not_author` (403),
  `customers.tag.not_found` (404), `customers.tag.limit_reached` (422), `customers.customer.tag_limit_reached`
  (422), `customers.tag.name_taken` (409, traducido en Infrastructure por **nombre de índice**). Los textos
  libres llevan validador de FluentValidation, que da el 422 con el mapa `errors`.
- Auditoría: crear, editar y borrar nota; crear, editar y borrar etiqueta; cambiar etiquetas de un
  cliente; cambiar consentimiento. Por el camino de auditoría que ya use Customers, en la misma
  transacción.

### 4.3 Permisos nuevos

| Permiso | Roles de fábrica | Motivo |
| --- | --- | --- |
| `customers.tag.manage` | admin | Renombrar o borrar cambia lo que ven todos y vacía campañas (decisión 3) |
| `customers.note.moderate` | admin | Borrar notas ajenas (decisión 4) |

Cada uno necesita las dos mitades: la constante y su política en `AddAuthorization`. Sin la política, el
síntoma es un 500 y no un 403.

### 4.4 Puertos para Messaging

`ICustomerWhatsAppDirectory` (Customers) y su adaptador `IMessagingCustomerDirectory` (Bootstrapper) suman:

- `FindContactAsync(tenantId, customerId)` → `{ customerId, name, phoneE164?, whatsAppUserId?, isActive, marketingOptOut }` o `null`.
- `ListAudienceAsync(tenantId, tagIds, customerIds)` → la misma forma, **sin duplicados**, por lotes
  (paginado por id, de a 500).
- `MarkMarketingOptOutAsync(tenantId, customerId, source: meta)`, idempotente.
- `CountAudienceAsync(tenantId, tagIds, customerIds)` → `{ total, inactive, optedOut, withoutWhatsApp }`.

## 5. Messaging: plantillas

### 5.1 Graph

- El puerto `IMessagingConnectionDirectory` suma `ResolveTemplateSourceAsync(tenantId, connectionId)`, que
  devuelve `{ wabaId, accessToken }` sólo si la conexión está activa. El adaptador lee
  `WhatsAppCloudFieldKeys.WabaId`.
- `IWhatsAppCloudClient` suma:
  - `ListTemplatesAsync(source)`: `GET {ver}/{wabaId}/message_templates?fields=name,language,status,category,components,parameter_format&limit=100`.
    Sigue `paging.next` hasta 10 páginas.
  - `SendTemplateAsync(sender, target, template, callbackData)`: el payload de la §2 por el
    `MessagePayload` existente.
- El normalizador puro `TemplateCatalog` convierte cada plantilla en
  `MessageTemplateDto { name, language, category, parameterFormat, header?, body, footer?, buttons[], parameters[], sendable, unsupportedReason? }`.
  - `parameters` son las claves en orden de aparición: `"1"`, `"2"` o `"first_name"`.
  - `unsupportedReason` ∈ `media_header`, `dynamic_url_button`, `authentication`, `carousel`, `flow`, `other`.
  - Tiene pruebas unitarias con los JSON de ejemplo de Meta.
- Caché: `IMemoryCache` por `(tenantId, connectionId)`, 5 minutos. `refresh=true` la salta.

### 5.2 Contrato HTTP

Bajo `/api/v1/tenants/{tenantId:guid}/messaging`.

| Método y ruta | Permiso | Cuerpo | Respuesta |
| --- | --- | --- | --- |
| `GET /connections` | `messaging.conversation.read` | — | `200 MessagingConnectionDto[]`: `{ id, name, displayPhoneNumber }`, sólo las conexiones `whatsapp-cloud` **activas** del tenant, por nombre. Lo usan la conversación nueva y las campañas. Sin él, la pantalla tendría que leer Integrations, cuyo permiso es de admin (agregado el 2026-10-11 al implementar E2) |
| `GET /connections/{connectionId}/templates?refresh=` | `messaging.conversation.read` | — | `200 MessageTemplateDto[]`: sólo `APPROVED`, ordenadas por nombre e idioma. `buttons[]` = `{ type: "QuickReply"\|"Url"\|"PhoneNumber"\|"Other", text, url? }` |
| `POST /conversations/{conversationId}/messages/template` | `messaging.conversation.manage` | `{ clientId, name, language, parameters: { [key]: string } }` | `201 MessageDto` |
| `POST /conversations` | `messaging.conversation.manage` | `{ clientId, connectionId, customerId, template: { name, language, parameters } }` | `201 { conversation: ConversationSummary, message: MessageDto }` |

Reglas del envío de plantilla, iguales a las del texto salvo las indicadas:

- Mismo `OutboundGuards`: permiso, módulo, membresía activa, idempotencia por `clientId`, sólo el
  asignado, autoasignación y conversación `Open`.
- **No exige ventana abierta.** Esa es la razón de existir de la plantilla.
- La plantilla tiene que estar en el catálogo, `APPROVED` y `sendable`: si no,
  `422 messaging.template.not_available`. Las claves de `parameters` tienen que ser **exactamente**
  las de la plantilla: si no, `422 messaging.template.parameters_mismatch`.
- Cada valor: 1–1024 caracteres, sin saltos de línea ni tabulaciones, sin más de 4 espacios seguidos. Es
  la regla de Meta (132018): validarla acá da el campo exacto en el 422 en lugar de una falla tardía.
- Se guarda `kind = Template`. `text` lleva lo que verá la persona (encabezado, cuerpo con variables
  sustituidas y pie, separados por línea en blanco), así la burbuja y la vista previa de la lista no
  necesitan conocer la plantilla. `details` lleva `{ template: { name, language, category, parameters } }`.
  `MessageDto` suma `template: { name, language } | null`.

Reglas de `POST /conversations` (decisión 9):

- El cliente tiene que existir y estar activo (`404 messaging.customer.not_found`) y tener teléfono o
  BSUID (`422 messaging.customer.unreachable`).
- La conexión tiene que ser del tenant y estar activa.
- Si ya hay una conversación del cliente en esa conexión (por `customer_id`, por `user_id` o por
  `wa_id`), se envía ahí con las mismas reglas de arriba y se responde `200`.
- Si no la hay, `ConversationOpener` (Infrastructure, SQL) la inserta con `user_id` = BSUID del cliente
  si lo tiene, si no `user_id NULL` y `wa_id` = dígitos del teléfono; `customer_id`; asignada a quien
  llama; `Open`. Usa `ON CONFLICT DO NOTHING` más un `SELECT`, como la ingesta. Después envía.
- Registra el evento `Assigned` de la conversación, como `take`.

### 5.3 Fallas de Meta

`MessageFailureReasons` suma 131049, 131050, 131026, 132000, 132001, 132015, 132016 y 132018, con su texto
para la burbuja, en tuteo. 131050 además llama a `MarkMarketingOptOutAsync`.

## 6. Messaging: campañas

### 6.1 Modelo

`messaging.campaigns`:

- `id`, `tenant_id`, `connection_id`, `name` (1–120), `template_name`, `template_language`,
  `template_category`.
- `parameters jsonb`: `{ key: { source: "text"|"customerName", value? } }`.
- `audience_tag_ids uuid[]`, `audience_customer_ids uuid[]` (máximo 500).
- `status`, `failure_reason`, `scheduled_at`, `started_at`, `completed_at`.
- `created_by_member_id`, `created_at`, `updated_at`, `version`.
- Índice `(tenant_id, created_at DESC)`.

`messaging.campaign_recipients`:

- `id`, `campaign_id`, `tenant_id`, `customer_id`, `customer_name`, `wa_id`, `user_id`.
- `status`, `failure_code`, `failure_reason`, `conversation_id`, `message_id`.
- `attempts`, `next_attempt_at`, `locked_until`, `sent_at`.
- Único `(campaign_id, customer_id)`. Índice parcial `(next_attempt_at) WHERE status IN (Pending, Sending)`.

| Campaña | De | A |
| --- | --- | --- |
| `Draft` | crear | `Scheduled` (lanzar con fecha futura), `Sending` (lanzar ya), borrada |
| `Scheduled` | — | `Sending` (llega la hora), `Cancelled` |
| `Sending` | — | `Completed` (sin pendientes), `Cancelled`, `Failed` (decisión 15) |

Destinatario: `Pending → Sending → Sent | Failed`, o `Skipped`. `Sent` es «Meta lo aceptó». Entregado y
leído salen del `status` del mensaje, que ya actualiza el webhook. Respondido es: hay un entrante en esa
conversación después de `sent_at`.

Los motivos de `Skipped`: `opted_out`, `inactive`, `unreachable`, `campaign_cancelled`, `campaign_failed`.

### 6.2 Contrato HTTP

Bajo `/api/v1/tenants/{tenantId:guid}/messaging/campaigns`. Todos con `messaging.campaign.manage`.

| Método y ruta | Cuerpo | Respuesta |
| --- | --- | --- |
| `GET /?page=&pageSize=` | — | `200 { items: CampaignSummaryDto[], total, page, pageSize }`, de la más nueva a la más vieja |
| `GET /{id}` | — | `200 CampaignDetailDto` |
| `GET /{id}/recipients?status=&page=&pageSize=` | — | `200 { items: CampaignRecipientDto[], total, page, pageSize }` |
| `POST /audience-preview` | `{ tagIds, customerIds, templateCategory }` | `200 { total, reachable, optedOut, inactive, withoutWhatsApp }` |
| `POST /` | `CampaignInput` | `201 CampaignDetailDto` (`Draft`) |
| `PUT /{id}` | `CampaignInput` y `expectedVersion` | `200 CampaignDetailDto`; sólo en `Draft` |
| `DELETE /{id}` | — | `204`; sólo en `Draft` |
| `POST /{id}/launch` | `{ expectedVersion }` | `200 CampaignDetailDto` |
| `POST /{id}/cancel` | `{ expectedVersion }` | `200 CampaignDetailDto` |

- `CampaignInput`: `{ name, connectionId, template: { name, language }, parameters, audience: { tagIds, customerIds }, scheduledAt? }`.
  - `scheduledAt` tiene que estar en el futuro y a no más de 30 días.
  - La audiencia no puede ir vacía.
  - La plantilla se valida contra el catálogo como en §5.2.
  - Las claves de `parameters` tienen que ser exactamente las de la plantilla, y un `text` lleva valor
    válido según §5.2.
- `CampaignSummaryDto`:
  `{ id, name, status, failureReason, connection: { id, name }, template: { name, language, category }, scheduledAt, startedAt, completedAt, createdBy: { memberId, displayName }, counts, createdAt, updatedAt, version }`.
  - `counts`: `{ total, pending, sent, delivered, read, replied, failed, skipped }`. Viaja **completo,
    incluso en cero** (BFF).
- `CampaignDetailDto` = summary + `parameters` + `audience: { tags: [{id,name,color}], customers: [{id,name}] }`.
  Una etiqueta o un cliente borrados no aparecen.
- `CampaignRecipientDto`: `{ customerId, customerName, status, failureReason, sentAt, conversationId, messageStatus }`.
- Errores: `messaging.campaign.not_found` (404), `messaging.campaign.not_editable` (409),
  `messaging.campaign.not_cancellable` (409), concurrencia 412 como el resto.
- Auditoría por el publisher de Messaging: creada, lanzada, cancelada, terminada o fallida.

### 6.3 Worker `CampaignDispatchWorker`

Copia `MediaCopyWorker`: `PeriodicTimer` de 2 s, scope por lote y opciones en `MessagingWorkerOptions`.
Cada tick hace:

1. **Activar.** Las `Scheduled` con `scheduled_at <= now` pasan a `Sending`, en una sentencia con
   `version` para que dos pods no la activen dos veces.
2. **Congelar.** Para cada campaña `Sending` sin destinatarios todavía: `ListAudienceAsync`, una fila por
   cliente. `Skipped` si está inactivo, no tiene teléfono ni BSUID, o tiene opt-out y la plantilla es
   `MARKETING`. Si no, `Pending`. Se hace en lotes, con `ON CONFLICT DO NOTHING`, así repetirlo es
   inocuo.
3. **Enviar.** Toma hasta `MessagesPerSecond × 2` destinatarios `Pending` vencidos, con
   `FOR UPDATE SKIP LOCKED`, los pasa a `Sending` con un lease de 2 minutos y procesa cada uno:
   1. Abre la conversación con `ConversationOpener`, sin asignar.
   2. Inserta el mensaje saliente `Template` con `sent_by_member_id` = creador de la campaña y guarda
      `message_id` en el destinatario, **en la misma transacción**.
   3. Llama a Meta y liquida el mensaje y el destinatario. Errores reintentables (§2): backoff de
      30 s · 2^n, hasta 5 intentos, después `Failed`. No reintentables: `Failed`, y 131050 marca el
      opt-out. Los de la decisión 15 detienen la campaña.
   4. Un token bucket en memoria por conexión mantiene el ritmo de la decisión 14.
4. **Recuperar.** Un destinatario `Sending` con lease vencido y `message_id` puesto pasa a
   `Failed unknown_outcome` (decisión 12). Uno sin mensaje vuelve a `Pending`.
5. **Cerrar.** Una campaña `Sending` sin `Pending` ni `Sending` pasa a `Completed`.

Cancelar pasa los `Pending` a `Skipped campaign_cancelled`. Lo que ya está en vuelo termina.

La campaña **no** asigna conversaciones ni cambia su estado. Una respuesta entra por la ingesta normal y
cae en «Sin asignar» si nadie la tenía, que es justo donde un asesor la toma.

### 6.4 Permiso

`MessagingPermissions.CampaignManage = "messaging.campaign.manage"`, de fábrica en admin (decisión 16).
Lleva constante, política y la entrada en el catálogo de permisos de roles.

### 6.5 Notas de la implementación (E3, 2026-10-11)

Lo que el spec no fija y el código decidió, o lo que se agregó sin cambiar el contrato HTTP de la §6.2:

- **La versión viaja en el cuerpo** (`expectedVersion`) en `PUT`, `launch` y `cancel`, no en `If-Match`: así lo implementó la pantalla. Falta o
  `<= 0` es `422` en `expectedVersion`; una versión vieja es `412 concurrency.conflict`.
- **`failureCode` y `failureReason`** (en la campaña y en cada destinatario) los calcula el backend en español: el código estable
  (`opted_out`, `131049`, `unknown_outcome`, `unconfirmed`, `connection_unavailable`…) y su texto (`CampaignFailureReasons`; los numéricos
  reusan `MessageFailureReasons`). Las columnas `failure_reason` guardan el texto; el DTO cae al cálculo si falta.
- **Contadores.** `sent` son los `Sent` («Meta lo aceptó»); `delivered` y `read` son subconjuntos de `sent` (leído implica entregado);
  `replied` cuenta los `Sent` con un entrante posterior; `pending` suma `Pending` y `Sending`; `total = pending + sent + failed + skipped`.
  `messageStatus` de un destinatario sólo viaja cuando está `Sent`.
- **Columnas extra** en `messaging.campaigns`: `failure_code` y `audience_frozen_at`. La segunda marca que el worker terminó de escribir los
  destinatarios: sin ella, una audiencia que se cayó a medias parecería terminada y la campaña se cerraría como `Completed`. No sube
  `version` (no es algo que la pantalla edite), así que el «Cancelar» de quien mira no choca con un `412`.
- **`launch` con una fecha que ya pasó** desde que se guardó el borrador es `422` en `scheduledAt`: no se manda de golpe lo que alguien
  había agendado. La plantilla se valida de nuevo contra el catálogo al lanzar.
- **Worker.** El mensaje saliente nace `Failed` con `failure_code = -1` (sin confirmar) y pasa a `Sent` solo cuando Meta acepta: si el
  proceso cae con la llamada en vuelo, la burbuja ya dice la verdad y la recuperación deja al destinatario `Failed unknown_outcome`.
  `client_id` del mensaje es el id del destinatario, así `(conversation_id, client_id)` garantiza un mensaje por destinatario aunque dos
  pods lleguen al mismo punto. La llamada a Meta no retiene transacción y se liquida aunque el proceso esté cerrando. Hasta 8 envíos en
  paralelo por tick, cada uno en su scope; el ritmo lo fija el cubo, no el paralelismo.
- **Riesgo aceptado de la decisión 12.** Un timeout o un 5xx (Meta pudo haber aceptado el mensaje) se reintenta como pide §6.3; eso puede
  duplicar un envío en ese caso puntual. Lo que cae a mitad de llamada (proceso muerto) sí queda `unknown_outcome` y no se reenvía.
- **Una plantilla o una conexión que dejan de servir** a mitad del envío detienen la campaña como los códigos de Meta de la decisión 15:
  `132001` (plantilla fuera del catálogo o con otras variables) o `connection_unavailable`. Un destinatario reclamado cuya campaña se
  canceló o falló mientras esperaba su turno no se envía: queda `Skipped campaign_cancelled` o `campaign_failed`.
- **Configuración.** `Messaging:Campaigns:MessagesPerSecond` (1 a 80, por defecto 20; se valida al arrancar) y
  `Messaging:Workers:CampaignPollSeconds` (2).
- **Permiso.** `messaging.campaign.manage` es el 51.º permiso del catálogo; de núcleo, de fábrica sólo en `admin`.

## 7. Frontend

UI en español, tuteando. Componentes de `components/ui`, formularios con react-hook-form y zod, TanStack
Query, y parsers a mano como en `messaging.api.ts`. Cada archivo vive en la feature que lo usa
(SDD-ADR-07).

### 7.1 Ficha de contacto (`features/messaging` + `features/customers`)

- En `thread-header.tsx`, el nombre del contacto pasa a ser un botón (accesible, `aria-haspopup="dialog"`)
  que abre `ContactDialog`.
- `ContactDialog`:
  - **Encabezado:** nombre, teléfono, `@usuario` y badge «Ficha incompleta».
  - **Pestaña Ficha:** el `CustomerForm` existente en modo editar o completar, más el interruptor
    «No quiere recibir promociones» (consentimiento).
  - **Pestaña Notas:** lista con autor y hora relativa, caja para escribir, editar y borrar según
    `canEdit`/`canDelete`.
  - **Pestaña Etiquetas:** `CustomerTagPicker`, un combobox con búsqueda, chips de color y «Crear «x»»
    si no existe.
  - Si la conversación no tiene cliente, la modal lo dice y no ofrece pestañas.
  - Lo de solo lectura se ve sin `customers.customer.manage`; lo editable se oculta.
- `CustomerNotesPanel`, `CustomerTagPicker` y `TagManagerDialog` viven en `features/customers`. La ficha
  del cliente (`/customers/$customerId`) también los muestra.
- `TagManagerDialog` sirve para renombrar, cambiar color y borrar. Se abre desde la lista de clientes y
  pide `customers.tag.manage`.

### 7.2 Plantillas en la caja (`features/messaging`)

- `/` como primer carácter de la caja abre `TemplatePicker`, un popover sobre la caja.
  - Filtra por nombre con lo que se escribe después de la barra y se navega con el teclado
    (↑ ↓ Enter Esc).
  - Las no enviables aparecen deshabilitadas con su motivo.
  - Elegir una abre `TemplateComposer`: una entrada por variable, vista previa en vivo que sustituye las
    variables y «Enviar».
- **Ventana cerrada:** la caja de texto se deshabilita con el aviso «Pasaron más de 24 h desde el último
  mensaje del cliente. Solo puedes escribirle con una plantilla.» y el botón «Elegir plantilla». También
  se puede escribir `/`.
- El envío va por la cola en orden con `clientId` y burbuja optimista, como el texto. La burbuja de una
  plantilla muestra un rótulo «Plantilla · nombre».
- **«Nueva conversación»** en la cabecera de la bandeja abre un diálogo:
  1. Buscar cliente (API de clientes).
  2. Elegir conexión, si hay más de una.
  3. Elegir plantilla, llenar variables y enviar.
  4. Navegar a la conversación.

### 7.3 Campañas (`features/campaigns`, nueva)

- Rutas `/campaigns`, `/campaigns/new` y `/campaigns/$campaignId`. Entrada «Campañas» en el sidebar con
  `messaging.campaign.manage`, debajo de Mensajería.
- **Lista:** nombre, estado, plantilla, programada o enviada, y una barra de progreso con enviados,
  fallidos y pendientes.
- **Nueva o editar borrador:** nombre, conexión, plantilla (`TemplatePicker` reutilizado) y variables, cada
  una «Texto fijo» o «Nombre del cliente».
  - **Audiencia:** etiquetas (multiselección) y clientes sueltos (búsqueda), con el conteo de
    `audience-preview` en vivo: «Le llegará a N de M. X no quieren promociones, Y sin WhatsApp, Z
    inactivos.»
  - **Envío:** ahora o programado.
  - **Confirmación:** modal con el conteo, aviso de costo por mensaje y aviso si N > 250 (decisión 17).
- **Detalle:** contadores (enviados, entregados, leídos, respondidos, fallidos, omitidos) y tabla de
  destinatarios filtrable por estado, con motivo y enlace a la conversación. Mientras está
  `Scheduled` o `Sending` se actualiza cada 5 s, y ahí está el botón «Cancelar».

## 8. Pruebas

- **Backend, unitarias:**
  - Dominio: `CustomerNote`, `Tag`, la asignación con su tope, `Campaign` con sus transiciones y el
    consentimiento.
  - Validadores y handlers con fakes (`MessagingTestBed`).
  - `TemplateCatalog` con JSON de Meta.
  - El payload de `SendTemplateAsync` (`WhatsAppCloudClientTests`).
  - El worker: ritmo, clasificación de errores, recuperación de leases, cierre y la detención de la
    decisión 15.
- **Backend, integración** (Testcontainers):
  - Endpoints de notas, etiquetas y consentimiento con 403 de otro tenant.
  - `IX_tags_tenant_name` → 409.
  - Plantilla en conversación, ventana cerrada → 201.
  - Conversación nueva y su adopción por la ingesta cuando llega el BSUID.
  - Campaña de punta a punta contra `FakeMetaGraphHandler`, el `FOR UPDATE SKIP LOCKED` con dos workers
    y la auditoría en la misma transacción.
- **ArchitectureTests:** Messaging sigue sin referenciar Customers ni Integrations.
- **Frontend:** Vitest + Testing Library + msw:
  - Parsers.
  - `ContactDialog` con sus pestañas y permisos.
  - El picker con `/` y su teclado, la ventana cerrada y la nueva conversación.
  - El formulario de campaña con su preview y la confirmación, y el detalle con su polling.
- Por tarea corren sólo las clases tocadas. La suite completa corre una vez al final de cada repo y se
  compara por nombre contra la línea base.

## 9. Implementación por entregas

Cada entrega toca los dos repos, con un commit por repo y por entrega, en conventional commits sin
atribución de IA.

| Entrega | Backend | Frontend |
| --- | --- | --- |
| **E1 Ficha** | §4 completo | §7.1 |
| **E2 Plantillas** | §5 | §7.2 |
| **E3 Campañas** | §6 | §7.3 |

E2 depende de E1 (`FindContactAsync`); E3 depende de E1 y E2. Dentro de cada entrega, backend y frontend
se hacen en paralelo contra el contrato de este documento.

## 10. Despliegue

- Hay tres migraciones nuevas: Customers (notas, etiquetas y consentimiento) y Messaging (campañas). No
  hay datos que migrar.
- `Messaging:Campaigns:MessagesPerSecond` es opcional (por defecto 20).
- **Antes de usar campañas en producción:** la plantilla de marketing debería tener el botón «Detener
  promociones», y el negocio necesita opt-in de sus clientes (política de Meta).
- Orden de despliegue: backend → frontend.

## 11. Fuera de alcance

- Respuestas rápidas.
- Crear o editar plantillas desde la plataforma.
- Plantillas con encabezado multimedia, URL dinámica o carrusel.
- Menciones y adjuntos en notas.
- Importar audiencias por CSV.
- Exportar destinatarios.
- Campañas recurrentes.
- Pruebas A/B.
- Retargeting por resultado.
- Webhook `user_preferences`.
- Marketing Messages API.

## 12. Decisiones pendientes

- **P1.** ¿Se marca el opt-out con palabras clave entrantes (STOP, BAJA) o con el botón de respuesta
  rápida «Detener promociones»? Lo segundo es más seguro: requiere leer el `button.payload` del entrante.
- **P2.** ¿Las campañas deberían estar también en el rol advisor?
- **P3.** ¿El tope de 20 mps alcanza para el volumen real del primer tenant?
