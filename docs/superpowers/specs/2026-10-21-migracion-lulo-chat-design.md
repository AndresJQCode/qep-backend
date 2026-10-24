# Migración de lulo-chat a QEP (módulo Messaging)

- **Estado:** borrador para revisar con el developer. Las preguntas que sólo él puede responder están en §4;
  las decisiones que tomó el diseño para poder cerrarse están marcadas «mi decisión» y se ratifican o se cambian
  en la revisión.
- **Fecha:** 2026-10-21.
- **Repos:** `lulo-chat-backend` (exportador, sólo lectura sobre Mongo) y `qep-backend` (importador, en
  Bootstrapper). Sin cambios en `qep-frontend`.
- **Verificado contra el código el 2026-10-21:** `lulo-chat-backend` en `main` (último commit 2026-09-05) y
  `qep-backend` en `develop`. Donde este documento y el código discrepen, gana el código.

## 1. Por qué

Los clientes de lulo-chat van a pasar a QEP y van a usar la bandeja del módulo Messaging. Para que el cambio no
les cueste el historial, lo que tienen en lulo tiene que aparecer en QEP: sus contactos, las etiquetas con las
que los clasifican, las respuestas rápidas que usan a diario y las conversaciones con sus mensajes.

El spec base de Messaging (2026-10-09 §1) dejó fuera de alcance «la sincronización del historial de
coexistencia», así que hoy **no existe ningún camino para crear mensajes históricos**: los únicos escritores de
`messaging.messages` son la ingesta del webhook y el envío saliente. Este trabajo abre ese camino, acotado a una
migración por tenant, corrida por el operador, idempotente y reportada.

## 2. Objetivos, no objetivos, anti-objetivos

- **Objetivo.** Un asesor que entra a QEP el primer día encuentra a sus contactos como clientes, con sus
  etiquetas, y abre cualquier conversación de lulo con sus mensajes y adjuntos en orden.
- **Objetivo.** Las respuestas rápidas compartidas del tenant funcionan en QEP con `/atajo`.
- **Objetivo.** La corrida se puede repetir sin duplicar nada y deja un reporte de lo que entró, lo que se
  descartó y por qué.
- **No objetivo.** Migrar usuarios con contraseña (lulo guarda AES reversible, no un hash; los asesores entran
  a QEP por invitación), departamentos, horarios, chatbots, campañas pasadas, plantillas (lulo no guarda su
  contenido: lo pide a Meta), integraciones de ecommerce, estadísticas, Messenger como canal (QEP no lo tiene).
- **No objetivo.** Sincronización continua entre los dos sistemas. Es un corte: se exporta, se importa, se
  mueve el número. Una segunda corrida incremental cubre lo que entró entre el export y el corte (§7).
- **Anti-objetivo.** Un importador que pase por los handlers de la API: dispararía auditoría por fila, eventos
  de outbox, correos, SignalR y el triage del embudo sobre miles de conversaciones viejas. El precedente es
  `ExportLoadSeeder`: SQL masivo que no pasa por los handlers.
- **Anti-objetivo.** Que QEP dependa de Mongo, del driver o del modelo de lulo. QEP lee un paquete neutral; lo
  que sabe de Mongo vive en el exportador, en el repo de lulo.

## 3. Lo verificado en los dos códigos

### 3.1 lulo-chat (Mongo + Mongoose)

| Tema | Lo que hay | Archivo |
| --- | --- | --- |
| Tenant | No hay colección de tenant: **el tenant es el usuario admin**, `tenantId = _id` propio. Es `string` en casi todo y `ObjectId` en `messages` | `auth.service.ts:55-61` |
| Contacto | `firstName?`, `lastName?`, `phones[] {indicativeNumber, number (E.164 sin `+`), isMobile, isWhatsAppCapable}`, `emails[]?`, `labels[] → labels`, `source[]` (`whatsapp\|instagram\|messenger\|lulochat`), `receiveMessages` (`pending\|allowed\|rejected`), `birthDate?`, `disabledAt?`, `messages[] → messages._id` | `contacts/schemas/contact.schema.ts` |
| Contacto de Instagram/Messenger | El IGSID o PSID va en `phones[0].number`, con `isWhatsAppCapable: false` | `instagram-webhook.service.ts:225-237` |
| Etiqueta | `name`, `color` (hex), única por `(tenantId, name)`. Sólo cuelga del contacto, nunca de la conversación | `labels/schema/label.schema.ts` |
| Respuesta rápida | `title`, `message`, `itsForGroup` (compartida o personal), `userId`. Sin índices | `quick-messages/schemas/quick-message.schema.ts` |
| Conversación | **No es una entidad.** Es el `conversationId` (UUID) de los mensajes. Estado y asignado se escriben con `updateMany` **en todos los mensajes** de la conversación | `messaging-service.service.ts:702, 923-995` |
| Mensaje | `operator` (`prospect\|agent\|bot`), `from`, `to`, `messageId` (wamid / mid / uuid interno, único), `type`, `text`, `mediaUrl`, `messageDate`, `status` (`pending\|read\|archived\|scheduled\|status_change`), `advisorId` (el **asignado**, no el autor), `repliedToMessage` (copia embebida con `id` y `messageId`), `conversationId`, `source`, `fullName` | `messaging-service/schemas/message.schema.ts` |
| Tipos | `text, audio, document, image, sticker, video, location, contacts, interactive, template, reaction, change_status, service, campaign, button`. Las reacciones y los acuses (`statuses`) **no se persisten**; `location` llega sin coordenadas | `webhooks.controller.ts:24-36` |
| Cierre | El motivo queda como texto `"Cerrado: <motivo>"` en un mensaje `change_status`; `closurereasons` es sólo un catálogo | `messaging-service.service.ts:44` |
| Medios | URL pública de S3 (`https://s3.us-east-2.amazonaws.com/qcode.co/<carpeta>/prospects/<uuid>.<ext>`, `public-read`). **No se guarda mime ni tamaño**; el nombre del documento va en `text` | `webhooks.service.ts:44-91`, `core/s3/s3.service.ts` |
| Número del negocio | `whatsappnumbers.numberId` es el `phone_number_id` de Meta; `number` es `parseInt` de los dígitos del `display_phone_number` (`whatsapp-integration.service.ts:43-44`), mientras `messages.to` lleva el `display_phone_number` crudo del webhook: se comparan **por dígitos** | `whatsapp-integration/schema/whatsapp-number.schema.ts` |
| Instagram | `instagram_integrations.instagramId` es el id de la cuenta y aparece en `messages.from/to` | `instagram-integration/schema` |
| Asesores | Son `users` con rol `Asesor` y `tenantId` del admin; `email` no es único en la base; contraseña AES reversible | `users/schemas/user.schema.ts` |
| Volumen | Única pista: «1M msgs ≈ 24MB», «evita cargar millones de docs en memoria». Hay **mensajes huérfanos** (sin contacto) | `CLEANUP_ORPHAN_MESSAGES_PLAN.md` |
| Tooling | No hay export ni migraciones. `src/recovery/` copia mensajes entre dos Mongo por `messageDate` y es el patrón más cercano a un extractor | `recovery/recovery.service.ts` |

Datos sucios conocidos (hay endpoints de arreglo para cada uno): `conversationId` duplicados para el mismo par,
teléfonos con `"undefined"`, indicativos mal puestos (`'57'` quemado), contactos duplicados.

### 3.2 QEP (Postgres, `develop`)

| Tema | Lo que hay | Archivo |
| --- | --- | --- |
| Conversación | `(connection_id, user_id)` único; **fila sólo con teléfono permitida** (`user_id NULL`, `wa_id` dígitos, índice `IX_conversations_connection_wa_legacy`) que el primer entrante con BSUID adopta. `channel` `WhatsApp\|Instagram`. `status` `Open\|Resolved`, `resolution_reason_id`, `assigned_member_id` + `assigned_at` (los dos o ninguno), `unread_count`, `last_inbound_at`, `last_message_*` | `MessagingDbContext.cs`, `Conversation.cs` |
| Mensaje | `direction` 1 Inbound / 2 Outbound / 3 System; `kind` 1..14 (Text, Image, Video, Audio, Document, Sticker, Location, Contacts, Reaction, Interactive, Template, Unsupported, Event, Share); `status` 1 Sent / 2 Delivered / 3 Read / 4 Failed; `wamid` único por `(connection_id, wamid)`; `client_id` único por conversación; `reply_to_message_id`; `search_vector` generado | `MessageRecord.cs`, migración `InitialMessaging` |
| Checks | entrante nunca `Failed`; `direction = 3 ⇔ kind = 13`; un evento lleva `wamid NULL`, `client_id NULL`, `status = 2` y `details` con la forma de `ConversationEventType` | ídem |
| Medios | `message_media` (PK `message_id`): `mime_type` NOT NULL, `meta_media_id` NOT NULL (`''` si no hay), `storage_key`, `stored_at`, `source_url`. `MediaCopyWorker` reclama las filas con `stored_at NULL` para copiar el archivo de Meta a R2, y **no intenta la copia si el mensaje tiene más de 7 días** (`MetaRetention`: Meta conserva el medio 7 días; pasado eso no hay qué bajar), también en el camino por URL. No borra nada: lo que ya está en R2 queda | `MediaTransfer.cs:58, 130, 196` |
| Clave R2 | `messaging/{tenantId}/{messageId}`, bucket privado | `MessagingMediaKeys.cs` |
| Cliente | `Customer.CreateIncomplete` (exige BSUID), `CreateIncompleteFromInstagram` (IGSID), `CreateIncompleteFromExternalSale` (teléfono/correo opcionales, **descarta** los inválidos). Índices únicos por BSUID e IGSID; `phone_e164` **no** es único | `Customer.cs`, `CustomersDbContext.cs` |
| Enlace por teléfono | `CustomerWhatsAppDirectory.EnsureAsync`: BSUID → si no, el **más antiguo por `phone_e164`** y le pega el BSUID → si no, crea incompleto | `CustomerWhatsAppDirectory.cs` |
| Etiquetas | Viven en **Customers** (`tags`, `customer_tags`): nombre ≤ 40 único por `lower(name)`, color en `gray\|red\|orange\|yellow\|green\|teal\|blue\|purple`, **100 por tenant, 20 por cliente**. La bandeja filtra por etiqueta a través del cliente | `Tag.cs`, `CustomerTags.cs` |
| Consentimiento | `Customer.MarketingOptOut` + `OptOutOfMarketing(source)` | `Customer.cs:274, 773` |
| Respuesta rápida | `quick_replies`: `shortcut` `^[a-z0-9-]{2,32}$` único por tenant, `body` ≤ 4096 con variables sólo `{nombre}`, `{asesor}`, `{empresa}`. Globales del tenant, sin dueño ni título | `QuickReply.cs` |
| Motivo de resolución | `resolution_reasons`: `name` ≤ 60, único por `lower(name)` entre los no borrados | migración `AddResolutionReasons` |
| Asignado | `assigned_member_id` y `sent_by_member_id` son **ids de membresía** (`tenancy.memberships`), referencia blanda | `Conversation.cs`, `MessagingAssignees.cs` |
| Conexión | `integrations.connections` + `IntegrationConnectionRoute.AccountId` = `phone_number_id` (WhatsApp) o id de la cuenta (Instagram) | `IntegrationConnectionRoute.cs` |
| Embudo | El triage es **una consulta**: toda conversación `Open` con `customer_id` y sin negocio abierto aparece en la cola del triage | `PipelineConversationDirectory.cs`, `PipelineRules.cs` |
| Precedentes | `ExportLoadSeeder` (SQL masivo en Bootstrapper, idempotente, fuera de los handlers); `ExportLoadSeedWorker` (corre después de que la API levanta, nunca en el arranque); `ICustomerProvisioning` (crear cliente sin sesión) | `src/Bootstrapper/Seeding/` |

## 4. Preguntas para ti (cambian lo que se construye)

Ordenadas por lo que más pesa. Para cada una digo qué asumí mientras tanto.

1. **¿Qué tenants y cuántos datos?** Cuántos negocios se migran, y para el más grande cuántos contactos,
   mensajes y adjuntos. Decide si el importador corre en minutos desde tu máquina o necesita correr como Job en
   el clúster con reanudación. *Asumo:* pocos tenants, cientos de miles de mensajes el mayor, y lo diseño
   reanudable por si acaso (§6.7).
2. **Conversaciones abiertas: ¿`Open` o `Resolved` al llegar?** Si llegan `Open` con cliente, **todas** entran a la
   cola del triage del embudo el primer día (§3.2). *Asumo (mi decisión D-1):* queda `Open` la que en lulo estaba
   abierta **y** tuvo actividad en los últimos 30 días; el resto llega `Resolved` sin motivo. El umbral es un
   parámetro de la corrida.
3. **Respuestas rápidas personales** (`itsForGroup: false`): QEP no tiene respuestas por asesor. ¿Se importan como
   globales, o sólo las compartidas? *Asumo (D-2):* sólo las compartidas; las personales van al reporte con su dueño
   para que cada asesor decida si la recrea.
4. **Messenger.** QEP no tiene el canal. *Asumo (D-3):* el contacto se importa como cliente (sin canal); sus
   conversaciones de Messenger **no**, y van al reporte con el conteo.
5. **Asesores: ¿quién los invita?** El importador casa el `users.email` de lulo con la membresía activa de QEP por
   correo. *Asumo (D-4):* las invitaciones las haces antes desde la pantalla de miembros (72 h de vigencia, el
   asesor tiene que aceptar); lo que no case llega sin asignar y al reporte. La alternativa es que el importador
   invite, pero manda correos y deja membresías `Invited` que no se pueden asignar hasta que acepten.
6. **Medios: ¿todo el historial o una ventana?** Cada adjunto es una descarga de S3 y una subida a R2 (costo y
   tiempo; el bucket de lulo tiene que seguir público hasta terminar). *Asumo (D-5):* se copian todos; el
   parámetro `--media-since` permite acotarlo. Un mensaje cuyo adjunto no se copia conserva el texto y queda sin
   adjunto, reportado.
7. **Dónde corre el importador.** *Asumo (D-6):* un **modo de consola del `Api`** (`dotnet Api.dll import
   lulo-chat --tenant … --package …`), que levanta el contenedor de servicios y corre sin HTTP. Se corre local
   contra la base por port-forward o como `Job` de Kubernetes con la imagen del Api. Sin endpoint, sin permiso
   nuevo, sin tabla de jobs. La alternativa es un endpoint de plataforma con tabla de corridas; cuesta más y da
   progreso en pantalla, que para pocos tenants no hace falta.
8. **El corte del número.** En QEP una conexión de WhatsApp sólo nace por Embedded Signup y la de Instagram por
   Instagram Login (`IntegrationProviders.cs:137, 170`), y `conversations.connection_id` es obligatorio: **el
   número tiene que estar conectado en QEP antes de importar**. Meta entrega el webhook a todas las apps
   suscritas, así que desde ese momento lulo y QEP reciben los dos, durante todo el import. ¿Quién apaga el
   webhook de lulo y cuándo? *Asumo (D-7):* la secuencia de §7: Embedded Signup en QEP → export → import → export
   incremental → import (idempotente) → apagar lulo. Mientras tanto, los asesores siguen contestando desde lulo.
10. **Clientes que ya existen en QEP.** Si el tenant ya opera en QEP y un contacto de lulo casa por teléfono con
    un cliente de QEP (quizá `Complete`, con CUC y cotizaciones), ¿el importador le puede cambiar algo? *Asumo
    (D-10):* a un cliente que ya existía sólo se le **agregan etiquetas**; nombre, correo, estado y consentimiento
    no se tocan. Lo demás de §6.3 aplica sólo a los clientes que la importación crea.
9. **`birthDate` del contacto.** `Customer` no lo tiene. *Asumo (D-8):* se descarta y va al reporte. Agregarlo es
   un cambio de Customers aparte.

## 5. Arquitectura

Dos piezas y un paquete entre ellas.

```
lulo-chat-backend                      paquete por tenant                 qep-backend
┌──────────────────────┐   zip   ┌───────────────────────────┐   zip   ┌──────────────────────────────┐
│ scripts/export-tenant│ ──────► │ manifest.json              │ ──────► │ Api import lulo-chat         │
│ (sólo lectura Mongo) │         │ advisors.ndjson            │         │  LuloChatImporter            │
│                      │         │ labels.ndjson              │         │  (Bootstrapper, SQL masivo)  │
│                      │         │ closure_reasons.ndjson     │         │   ├─ catálogo (tags, motivos,│
│                      │         │ quick_messages.ndjson      │         │   │   respuestas rápidas)    │
│                      │         │ contacts.ndjson            │         │   ├─ clientes                │
│                      │         │ conversations.ndjson       │         │   ├─ conversaciones+mensajes │
│                      │         │ messages.ndjson            │         │   ├─ medios (S3 → R2)        │
│                      │         │ report-export.json         │         │   └─ report-import.json      │
└──────────────────────┘         └───────────────────────────┘         └──────────────────────────────┘
```

### 5.1 Exportador (repo `lulo-chat-backend`)

- **Qué es.** Un script de Node (`scripts/export-tenant.ts`), con el driver de Mongo directo, fuera de la app de
  Nest: no necesita Kafka, Redis ni S3 para correr, y no toca nada (sólo `find` con cursor, por lotes).
- **Entrada.** `--tenant <adminUserId>`, `--out <carpeta>`, `--since <fecha>` opcional (sólo mensajes con
  `messageDate >= since`, para la corrida incremental), `--mongo <uri>` (o `DATABASE_URL`).
- **Lo que resuelve del lado de lulo**, porque ahí está el contexto para hacerlo:
  - **El tenant.** Filtra `tenantId` como string **y** como `ObjectId` (los dos existen, §3.1).
  - **Las conversaciones.** Se guía por `messages.tenantId` (no por `contacts.messages[]`, que deja fuera los
    huérfanos) y agrupa por el **par (negocio, persona)**, no por `conversationId`: lulo tiene `conversationId`
    duplicados para el mismo par (`findDuplicatedConversations`) y en QEP sólo cabe una conversación por
    `(connection_id, wa_id)` o `(connection_id, user_id)`. Varios `conversationId` del mismo par se funden en
    una, con sus mensajes en orden de fecha. El negocio es el de `from`/`to` que coincide **por dígitos** con
    `whatsappnumbers.number` (que es `parseInt` del `display_phone_number`, no el texto crudo que va en `to`) o
    con `instagram_integrations.instagramId`; el otro es la persona. De cada grupo saca: canal (`source` de un
    mensaje que **no** sea `change_status`: los `change_status` se escriben siempre con `whatsapp`, también en
    Instagram, `messaging-service.service.ts:960`), el `numberId` del negocio, el estado (del último mensaje que
    no sea `change_status`), el asesor (`advisorId` del último mensaje), el motivo de cierre (del último
    `change_status` que empiece por `Cerrado: `), y el contacto (por teléfono o IGSID dentro del tenant). Un
    grupo que no resuelve negocio va al reporte y no se exporta.
  - **Los mensajes.** Salen tal cual, con `_id`, `conversationId`, `operator`, `type`, `text`, `mediaUrl`,
    `messageDate`, `messageId`, `status`, `repliedToMessage.id` y `fullName`. Los `change_status` **no** van: ya
    se usaron para derivar la conversación (§6.4). Los `reaction` tampoco (lulo nunca los persistió).
  - **Los asesores.** `users` del tenant con rol `Asesor` más el admin: `_id`, `email`, `firstName`, `lastName`,
    `disabledAt`. Sin contraseña ni tokens.
- **Salida.** Una carpeta con los NDJSON, `manifest.json` (tenant, fecha, `since`, conteos, versión del formato)
  y `report-export.json` (lo descartado y por qué). El operador la comprime y la deja donde el importador la lea.
- **Volumen.** Cursor con `batchSize`, escritura en streaming: nunca carga una colección entera.

### 5.2 Paquete

NDJSON, una línea por registro, UTF-8, con los ids de lulo como strings. El formato es contrato entre los dos
repos y lleva `formatVersion` en el manifiesto; el importador rechaza una versión que no conoce. El detalle
campo a campo queda al constructor, con una regla: **el paquete lleva los datos de lulo sin transformar**; la
traducción a QEP la hace el importador, que es donde están las reglas de QEP para validarla.

### 5.3 Importador (repo `qep-backend`)

- **Dónde.** `src/Bootstrapper/Import/LuloChat/` (toca Messaging, Customers, Tenancy e Integrations: por eso
  Bootstrapper, como `ExportLoadSeeder`). Sin referencias nuevas entre módulos. `ArchitectureTests` sigue igual.
- **Cómo se dispara.** D-6: modo de consola del `Api`. Parámetros: `--tenant <qepTenantId>`, `--package
  <carpeta o zip>`, `--open-window-days 30`, `--media-since <fecha>`, `--dry-run`, `--skip-media`.
- **Qué usa del dominio y qué va en SQL.** Lo que tiene factoría y repositorio y cabe en volumen razonable pasa
  por el dominio (etiquetas, motivos, respuestas rápidas, clientes: `ICustomerProvisioning`-style, con el
  normalizador de teléfonos de Customers). Conversaciones, mensajes y medios van en SQL sobre
  `MessagingDbContext`, con las mismas sentencias que `InboundIngestion`, `OutboundMessages` y
  `ConversationOpener` ya usan (`ON CONFLICT … DO NOTHING`, el `COALESCE` del snapshot). El importador **no**
  llama a `IMessagingNotifier`, ni publica outbox, ni escribe auditoría por fila: una sola entrada de auditoría
  por corrida con los conteos.
- **Prechequeos, antes de escribir nada.** Tenant existe y tiene el módulo `messaging` prendido; existe una
  conexión activa de WhatsApp cuya ruta tiene `AccountId = numberId` por cada número del paquete (y de Instagram
  por cada `instagramId`), lo que obliga a conectar el número **antes** de importar (§7, D-7); las etiquetas del
  paquete más las existentes no pasan de 100; el formato del paquete es el esperado. Cualquiera que falle detiene la corrida con el motivo. `--dry-run` corre los prechequeos y
  la traducción completa, y escribe sólo el reporte.
- **Salida.** `report-import.json` con conteos por entidad (creado, ya existía, descartado) y la lista de
  descartes con id de lulo y motivo. Y el log.

## 6. Mapeo, entidad por entidad

### 6.1 Asesores → membresías (sin crear nada)

Por correo, `lower(email)`. No hay búsqueda de usuario por correo (`IUserDirectory` sólo tiene
`GetEmailAsync(userId)`): el cruce recorre las membresías del tenant y resuelve el correo de cada una, como
`MessagingAssignees.ListAsync`. Sólo casa una membresía **activa**; una `Invited` o `Suspended` cuenta como no
encontrada. El resultado es un diccionario `luloUserId → membershipId` que usan las
conversaciones. Lo que no casa va al reporte. (D-4)

### 6.2 Etiquetas → `customers.tags`

| lulo | QEP | Regla |
| --- | --- | --- |
| `name` | `name` | recortado a 40; igualdad por `lower(name)`: si ya existe, se reutiliza |
| `color` (hex) | `color` (8 valores) | por el matiz más cercano; sin hex válido → `gray` |

Dos etiquetas de lulo que colapsan al mismo nombre en QEP se funden (reporte). Si tenant + paquete superan 100,
el prechequeo falla: elegir cuáles se dejan es decisión humana.

### 6.3 Contactos → clientes

- **Identidad y dedupe**, en este orden: por IGSID (`instagram_user_id`) si es contacto de Instagram; por
  teléfono E.164 contra el cliente **más antiguo** con ese `phone_e164` (la misma regla que
  `CustomerWhatsAppDirectory`); si no hay, se crea. Varios contactos de lulo con el mismo teléfono se funden
  en un cliente: unión de etiquetas, el nombre del más reciente.
- **Cliente que ya existía en QEP (D-10).** Sólo recibe etiquetas. Nombre, correo, teléfono, estado y
  consentimiento quedan como están: pueden ser datos reales de una ficha completa con cotizaciones. El resto
  de esta sección aplica a los clientes que la importación crea.
- **Teléfono.** `+` + `number`. Lo que el normalizador de Customers rechace (`"undefined"`, vacío, demasiado
  corto) deja al cliente sin teléfono y, si tenía conversaciones de WhatsApp, esas no se importan (no hay
  `wa_id` válido): reporte.
- **Nombre.** `firstName + ' ' + lastName`; vacío → el `fullName` del último entrante de sus conversaciones →
  el teléfono. Correo: el primero válido de `emails[]`.
- **Ficha.** `Incomplete`, con una factoría nueva en `Customer` del estilo de `CreateIncompleteFromExternalSale`
  que acepte además el IGSID y el momento de creación (`createdAt` de lulo), porque la ficha debe conservar la
  antigüedad. Sin BSUID: lulo es anterior a los BSUID y no los tiene. Lo que haga falta en la ficha se completa
  después, como con cualquier incompleto.
- **Consentimiento.** `receiveMessages = rejected` → `OptOutOfMarketing` con un `MarketingConsentSource`
  nuevo, `Imported`. No basta el enum: `CK_customers_marketing_opt_out_source` sólo admite `manual|meta|button`
  (`CustomersDbContext.cs:64-66`), así que es también **una migración de Customers**. `allowed` y `pending` no
  tocan nada.
- **Estado.** `disabledAt` presente → cliente inactivo. `birthDate` se descarta (D-8).
- **Etiquetas del cliente.** Hasta 20; las que sobren, al reporte. `assigned_by_user_id`: el del operador que
  corre la importación (parámetro `--actor`), porque la columna es NOT NULL y nadie de lulo la asignó en QEP.

### 6.4 Conversaciones

Una por grupo (negocio, persona) del paquete (§5.1), salvo Messenger (D-3) y los grupos sin contacto resoluble.

| Campo QEP | Origen |
| --- | --- |
| `connection_id` | la conexión cuya ruta tiene `AccountId = numberId` (WhatsApp) o `= instagramId` (Instagram) |
| `channel` | `source` |
| `user_id` | WhatsApp: `NULL` (sin BSUID); Instagram: el IGSID |
| `wa_id` | WhatsApp: los dígitos del teléfono de la persona; Instagram: `NULL` |
| `customer_id` | el cliente de §6.3 |
| `profile_name` | `fullName` del último entrante |
| `status` | D-1: `Open` si en lulo no estaba `archived` **y** `last_activity_at ≥ hoy − ventana`; si no, `Resolved` |
| `resolution_reason_id` | el motivo de §6.6 cuando el estado es `Resolved` y lulo tenía `Cerrado: <motivo>`; si no, `NULL` |
| `assigned_member_id`, `assigned_at` | la membresía de §6.1 y la fecha del último mensaje; sin membresía, los dos `NULL` |
| `unread_count` | si `Open`: cantidad de entrantes con `status = pending` después del último saliente; si `Resolved`: 0 |
| `last_inbound_at`, `last_inbound_wamid` | del último entrante; el wamid sólo si es un id real de Meta |
| `last_activity_at`, `last_message_*` | del último mensaje importado (no hay eventos) |
| `created_at` | `messageDate` del primer mensaje |

**Si la conversación ya existe en QEP** (el tenant conectó el número antes de importar y la persona ya
escribió), se busca en este orden, como `ConversationOpener`: por `customer_id` en esa conexión, por `wa_id`,
por IGSID. Si existe, se le cuelgan los mensajes históricos y **no se tocan** su estado, su asignación ni su
`unread_count`; sólo `created_at` retrocede si el primer mensaje importado es anterior.

**Eventos del sistema.** Los `change_status` de lulo no se convierten en eventos de QEP: `details` tiene la forma
de `ConversationEventType` (actor, destinatario, motivo por id) y lulo sólo tiene texto libre. Su información
(estado, asignado, motivo) ya quedó en la fila de la conversación. **Mi decisión (D-9).**

### 6.5 Mensajes

| Campo QEP | Origen |
| --- | --- |
| `id` | UUID v5 determinista a partir del `_id` de lulo (`ON CONFLICT (id) DO NOTHING`: la idempotencia) |
| `direction` | `prospect` → Inbound; `agent` → Outbound (`bot` existe en el enum pero nada lo persiste: las respuestas automáticas se guardan como `agent`; si apareciera, Outbound) |
| `status` | Inbound → `Delivered` (2); Outbound → `Sent` (1). Lulo no guarda acuses: no se inventan |
| `kind` | `text` → Text; `image/video/audio/document/sticker` → su kind; `contacts` → Contacts; `interactive`, `button` → Interactive; `template`, `campaign` → Template; `location` → **Unsupported** (lulo no guardó coordenadas); `service` → Unsupported |
| `text` / `caption` | Text, Interactive, Template, Unsupported: `text` → `text`. Con adjunto: `text` → `caption` (y para `document`, `text` es el nombre del archivo: va a `file_name`, no a `caption`) |
| `wamid` | `messageId` cuando es de Meta (WhatsApp: empieza por `wamid.`; Instagram: el `mid`); un uuid interno → `NULL`. Si `(connection_id, wamid)` ya existe (QEP lo recibió por webhook), **se omite** y el existente manda |
| `client_id`, `sent_by_member_id` | `NULL`: lulo no guarda el autor del saliente |
| `reply_to_message_id` | el id determinista de `repliedToMessage.id`, si ese mensaje está en el paquete |
| `occurred_at` | `messageDate` |
| `created_at` | `now()` de la corrida |

`timestamp` de lulo no se usa (unidades inconsistentes, §3.1).

### 6.6 Motivos de cierre → `resolution_reasons`

`closureReason` recortado a 60, por `lower(name)`; se reutiliza el que exista. Además, los motivos que aparezcan
en `Cerrado: <motivo>` y no estén en el catálogo de lulo se crean igual, para que la conversación los pueda
referenciar.

### 6.7 Medios

- **Fase A, con los mensajes.** Cada mensaje con `mediaUrl` inserta su fila de `message_media` con `mime_type`
  provisional por extensión, `meta_media_id = ''`, `source_url = mediaUrl`, `file_name` (documentos),
  `stored_at NULL`, `next_attempt_at = '9998-01-01'` y `last_error = 'import_pending'`. Con `next_attempt_at`
  en el futuro **`MediaCopyWorker` nunca la reclama** (sólo toma `next_attempt_at <= now`; y si la reclamara,
  la daría por vencida: §3.2). No se usa `9999-01-01`: es `MediaCopyProcessor.GiveUpAt`, el centinela de las
  filas que el worker **abandonó**, y las pendientes de importar no deben contarse con ésas.
- **Fase B, después.** El importador recorre sus filas `import_pending` del tenant, baja de S3 (sin credenciales:
  el bucket es público), sube a R2 con `IMessagingMediaStore.UploadAsync` en `MessagingMediaKeys.For(tenant,
  messageId)`, y completa `mime_type` (del `Content-Type` de S3), `size_bytes`, `sha256`, `storage_key`,
  `stored_at`, `last_error = NULL`. Concurrencia acotada (parámetro, 4 por defecto). Un 404 o un archivo mayor a
  `MaxBytes` deja la fila con `last_error = 'import_failed:<motivo>'` y el mensaje sin adjunto visible; reporte.
- **Reanudable.** Volver a correr retoma las `import_pending`. `--skip-media` corre sólo la fase A.
- **Vencimiento.** Lulo no borra de S3, así que no hay prisa, pero el bucket tiene que seguir público hasta que
  la fase B termine en todos los tenants (§7).

### 6.8 Respuestas rápidas → `quick_replies`

Sólo las `itsForGroup: true` (D-2). `title` → `shortcut`: minúsculas, sin acentos, lo que no sea `[a-z0-9-]`
pasa a `-`, recortado a 32, mínimo 2; si choca con uno existente, sufijo `-2`, `-3`. `message` → `body`: si
pasa de 4096 o contiene `{…}` que no sea `{nombre}`, `{asesor}` o `{empresa}`, **se descarta** con reporte (no se
adivina el texto). Por `QuickReply.Create`, que es quien valida.

## 7. Prerrequisitos y secuencia del corte

Por tenant:

1. **En QEP, antes:** tenant creado, módulo `messaging` prendido, dueño registrado, asesores invitados **y con la
   invitación aceptada** (D-4), los permisos de messaging en sus roles, y el **número conectado por Embedded
   Signup** (Instagram por Instagram Login). Desde acá QEP recibe el webhook, y lulo también: los asesores
   siguen trabajando en lulo hasta el paso 6.
2. **Export completo** desde lulo (`--tenant`). Comprimir y dejar el paquete donde el importador lo lea.
3. **Import** con `--dry-run`; revisar el reporte; import real. Fase B de medios puede quedar corriendo.
4. **Export incremental** (`--since` = fecha del paso 2 menos un margen) e **import** otra vez: lo que QEP ya
   recibió por webhook se omite por `wamid`; lo que sólo tiene lulo (lo que los asesores respondieron desde
   lulo) entra.
5. Verificar en la bandeja: una conversación vieja con adjunto, una abierta con asignado, una etiqueta en el
   filtro, una respuesta rápida.
6. **Apagar lulo** para ese número (quitar la suscripción del webhook o desactivar el número) y pasar a los
   asesores a QEP. Si entre 4 y 6 hubo respuestas desde lulo, un incremental más.

**Lo que pasa mientras los dos reciben (pasos 1 a 6).** Es el caso normal, no la excepción: el import llega
cuando QEP ya tiene conversaciones con BSUID para quien escribió después del paso 1. Por eso §6.4 busca primero
la existente (por cliente, teléfono o IGSID) y le cuelga el historial sin tocar su estado. Una persona nueva
(sin historial) escribe: QEP le crea conversación y cliente por BSUID; el incremental la casa por teléfono o
cliente. Una persona con historial que escribe **después** del import: la fila importada sólo con teléfono es
adoptada por el BSUID (`IX_conversations_connection_wa_legacy`), como diseñó el spec 2026-10-10. Lo que un asesor
responde desde lulo en ese lapso QEP no lo ve hasta el incremental.

## 8. Pruebas

- **Exportador:** pruebas con un Mongo efímero (`mongodb-memory-server` o el `compose.dev.yaml`): tenant como
  string y como `ObjectId`; grupo con negocio no resoluble; `--since`; huérfanos incluidos.
- **Importador (IntegrationTests de Bootstrapper, contra el esquema migrado):** idempotencia (correr dos veces
  deja los mismos conteos); conversación que ya existía en QEP conserva estado y asignación; `wamid` repetido se
  omite; `Open`/`Resolved` según la ventana; `unread_count`; etiqueta por encima de 20; respuesta rápida con
  `{x}` descartada; medio en fase A no reclamado por `MediaCopyWorker`; fase B completa la fila y el `GET` del
  medio responde; `--dry-run` no escribe. Las pruebas acotadas al módulo tocado: no se corre la suite completa.
- **Checks de la base:** cada fila que el importador inserta cumple los `CK_*` de §3.2; una prueba por check
  con un paquete adrede inválido.

## 9. Lo que queda al constructor

El formato exacto de cada NDJSON y del manifiesto; el `MarketingConsentSource` nuevo si hace falta; el nombre de
la factoría de `Customer`; cómo el `Api` detecta el modo de consola sin arrancar Kestrel; el tamaño de lote de
las transacciones (una por conversación es el punto de partida); la tabla de colores hex → 8 colores; el algoritmo
de slug; el formato del reporte. Todo lo demás está decidido arriba.

## 10. Riesgos

- **Contactos duplicados por teléfono en lulo** (hay un endpoint `remove-duplicates` deshabilitado): se funden
  (§6.3). Si en lulo tenían etiquetas distintas, el cliente queda con la unión.
- **`conversationId` duplicados para el mismo par** se funden en una conversación (§5.1); el estado y el
  asignado salen del último mensaje entre todos los grupos. Si lulo tenía dos hilos vivos para la misma persona
  con asesores distintos, en QEP queda uno, con el asesor del último mensaje.
- **`phone_e164` no es único en QEP:** si el tenant ya tiene dos clientes con el mismo teléfono, el importador
  elige el más antiguo, igual que la ingesta. Reporte.
- **Mensajes huérfanos en lulo:** entran, porque la conversación se arma por `conversationId` y no por el
  contacto; si no tienen contacto resoluble, se descartan con conteo.
- **Triage del embudo** con conversaciones `Open` importadas (pregunta 2).
- **El bucket de S3 cambia a privado** antes de terminar la fase B: las filas quedan `import_failed:403`. Se
  rehace con el bucket abierto.
- **Orden del hilo:** QEP ordena por `occurred_at` y luego `id`; dos mensajes de lulo con el mismo `messageDate`
  salen en orden de id determinista, no en el de lulo. Aceptable.
