# Mensajería: gestor de plantillas de WhatsApp

**Fecha:** 2026-10-14
**Módulos:** Messaging (tabla nueva, agregado, comandos, endpoints, permiso nuevo, campo nuevo del webhook,
llamadas nuevas a Graph). Authorization y Bootstrapper (catálogo de permisos, rol de sistema y política).
**Frontend:** `qep-frontend`, slice aparte (§7).
**Estado:** alcance, enfoque (espejo local con webhook) y las nueve secciones del diseño aprobados por el
owner en conversación el 2026-10-14. **Corregido al implementar (2026-10-15)** para que diga lo que hace el
código (CLAUDE.md: gana el código): el reintento versionado de la plantilla de cotización y la firma de la señal
(decisiones del owner del 2026-10-15), el PDF generado, el índice único parcial y la carrera de los primeros
llenados. Cada cambio lleva la marca «corregido al implementar» o «agregado al implementar».
**Es un delta sobre** `2026-10-11-contactos-plantillas-campanas-design.md` (§5, el catálogo de plantillas)
y `2026-10-09-mensajeria-whatsapp-design.md` (el webhook, §8.2). Lo que este documento no toca sigue como
está ahí.
**Ramas:** `feature/gestor-de-plantillas` desde `develop` `56aeb105` en `qep-backend`, en un worktree
aparte. La homónima en `qep-frontend` cuando le toque.

## 1. Contexto y alcance

Hoy las plantillas de WhatsApp se crean y se borran en la consola de Meta. QEP sólo las **lista**
(`GET /{waba}/message_templates`, caché de 5 minutos, sólo `APPROVED`) y las envía desde la bandeja y las
campañas. El spec del 2026-10-11 dejó «crear o editar plantillas desde la plataforma» fuera de alcance
(§11). Este spec lo trae.

Hay además una razón de orden: el envío de cotizaciones por la Cloud API de Meta (el «consumidor
Quotations», spec pendiente) necesita una plantilla con encabezado de documento **por tenant**, y nadie
debería tener que entrar a Meta para que su tenant pueda enviar cotizaciones.

**Dentro:**

- Un **espejo local** de las plantillas de cada conexión `whatsapp-cloud`, con todos los estados y el
  motivo de rechazo, alimentado por la creación, el webhook `message_template_status_update` y un
  refresco manual contra Meta.
- **Plantillas libres**: el admin crea plantillas de texto (`MARKETING` o `UTILITY`) con encabezado de
  texto, cuerpo con variables con nombre, pie y botones; las manda a aprobar, ve su estado y las borra.
- **La plantilla de cotización aprovisionada por QEP**: nombre reservado, cuerpo fijo que el backend
  conoce, encabezado de documento con un PDF de ejemplo. La crea un botón, una por conexión.
- La **señal** «esta conexión tiene su plantilla de cotización aprobada, y se llama así», como puerto de
  Messaging para que el consumidor Quotations la lea (§4.3).
- El catálogo de envío (bandeja y campañas) pasa a leer del espejo.

**Fuera** (§8): editar plantillas, encabezado multimedia en las libres, `AUTHENTICATION`, carrusel,
flujos, los webhooks de calidad y de categoría, el guard en `SendQuotation`, el resolver de canal de
Quotations y la pantalla del frontend.

## 2. Decisiones

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-T1 | Libres **y** de cotización aprovisionada, en la misma tabla y la misma pantalla. | Decisión del owner. El envío de cotizaciones necesita variables exactas que un humano no debería poder cambiar; marketing y campañas necesitan libertad. |
| D-T2 | La de cotización conserva el PDF como **encabezado de documento**, igual que hoy con Zenvia. | Decisión del owner. Paridad con lo que el cliente final ya recibe. Cuesta la Resumable Upload del PDF de ejemplo (§4.4). |
| D-T3 | Se aprovisiona **a pedido**, con un botón en la pantalla de plantillas de la conexión. El signup embebido no cambia. | Decisión del owner. El signup ya encadena varias llamadas a Graph y un fallo ahí deja la conexión sin número; un rechazo de Meta tiene que verse en una pantalla, no esconderse en un flujo que el admin ya cree terminado. |
| D-T4 | **Espejo local con webhook** (tabla `messaging.message_templates`), no passthrough a Meta ni job periódico. | Decisión del owner. El estado es local e inmediato, sobrevive a Meta caída y la señal de cotización es un query. Un passthrough dependería del caché de 5 minutos para decidir si se puede enviar una cotización. |
| D-T5 | Sólo el admin gestiona: permiso nuevo `messaging.template.manage`, de núcleo y de fábrica sólo en `admin`. Leer el catálogo de envío sigue con `conversation.read`. | Lo que la empresa manda en su nombre y lo que Meta cobra lo decide quien administra, como `CampaignManage` y `QuickReplyManage`. |
| D-T6 | **Editar queda fuera.** Cambiar el texto es crear otra plantilla y borrar la vieja. | Meta permite editar con límites y vuelve a mandar la plantilla a revisión; nadie lo pidió. Mismo criterio que rige con Zenvia en el CLAUDE.md. |
| D-T7 | Las plantillas creadas desde la consola de Meta aparecen (`kind = External`) y se pueden borrar. El refresco las recoge. | El espejo tiene que ser la verdad completa del WABA o la pantalla miente. |
| D-T8 | El catálogo de envío (`IMessageTemplates`) lee del espejo las `APPROVED`. El caché en memoria de 5 minutos desaparece. | Una sola fuente. `refresh=true` pasa a sincronizar el espejo contra Meta. |
| D-T9 | Variables **con nombre** (`parameter_format: NAMED`) en las libres; el encabezado de texto no lleva variables. | Más legible para el admin y para el asesor que llena los valores. Posicionales siguen soportadas al **enviar** las `External` que ya existan. |
| D-T10 | No se manda `allow_category_change`. | Si Meta decide que el texto es marketing, mejor un rechazo visible que una plantilla que cobra como marketing sin que el admin lo sepa. |
| D-T11 | Borrar usa `hsm_id` **y** `name`: sólo ese idioma. | La identidad de QEP es nombre más idioma (campañas, `details` de los mensajes). Sin `hsm_id` Meta borra todos los idiomas con ese nombre. |
| D-T12 | Borrar se niega si una campaña `Scheduled` o `Sending` de esa conexión referencia nombre e idioma. | La campaña guarda la plantilla por nombre e idioma y fallaría al enviar. |
| D-T13 | La fila se **elimina** al borrar (no hay borrado lógico). | Las campañas ya guardan nombre, idioma y categoría en sus columnas, y los mensajes enviados guardan la plantilla en `details`. Nada referencia la fila. |
| D-T14 | Los cambios de estado que trae el webhook **no se auditan**; crear, aprovisionar y borrar sí, atómico con `IMessagingAuditRecorder`. | El webhook es del sistema y puede llegar repetido; queda en el log con tenant y conexión. |
| D-T15 | Los motivos de rechazo y los estados viajan con el nombre que Meta les da (`REJECTED`, `INVALID_FORMAT`). | El diccionario en español lo tiene el frontend, como el resto de los enums. |

## 3. Lo que hay que confirmar contra Meta

Lo que este documento da por cierto de la Graph API y **no** se ha ejercitado todavía en este repo. El plan
lo contrasta con la documentación vigente; lo que sólo se puede ver con tráfico real queda en el handoff.

| Supuesto | Dónde se usa | Si resulta distinto |
| --- | --- | --- |
| `POST /{wabaId}/message_templates` acepta `parameter_format: NAMED` con `body_text_named_params` en el `example` del `BODY`. | Crear libres (§4.4) | Se cambia el constructor de componentes; el contrato HTTP no cambia. |
| La Resumable Upload (`POST /{appId}/uploads` y `POST /{uploadId}` con `file_offset: 0`) funciona con el **token de negocio de la conexión**, no sólo con un token de usuario. | Aprovisionar la de cotización (§4.5) | Sale `messaging.template.upload_failed` en el primer intento real. Alternativa: token de la app (`appId|appSecret`), que exige que Messaging conozca el `AppSecret`, cosa que ya hace para la firma del webhook. |
| El `header_handle` devuelto no vence antes de la llamada de creación que sigue inmediatamente. | §4.5 | Se sube y se crea en el mismo handler; no se guarda el handle. |
| `DELETE /{wabaId}/message_templates?hsm_id=&name=` borra sólo ese idioma; un nombre borrado queda bloqueado un tiempo antes de poder reutilizarse. | Borrar (§4.4), reintentar la de cotización (§4.5) | *(Corregido al implementar, 2026-10-15.)* La cuarentena se resuelve con el nombre versionado: el reintento de la de cotización no recrea `qep_cotizacion`, crea `qep_cotizacion_v2`, `_v3`… y salta de versión si Meta dice que el nombre ya existe (§4.5). En una libre, el 422 `name_taken` no distingue cuarentena de nombre en uso; el cuerpo exacto que Meta devuelve para un nombre en cuarentena queda a confirmar en producción (handoff). |
| El valor de `message_template_status_update` trae `event`, `message_template_id`, `message_template_name`, `message_template_language` y `reason`, y el `entry.id` es el WABA. | Webhook (§4.4) | El parser es tolerante: lo que falte deja la fila sin tocar y un log. |
| `error_user_title` y `error_user_msg` de Graph distinguen «nombre ya existe» y «tope de plantillas» del resto de los 400. **`error_subcode` no sirve:** Meta lo declaró obsoleto y no lo devuelve desde la v16. | Traducción de errores (§4.8) | Caen en `rejected_by_meta` con el `error_user_msg` de Meta. La pantalla sigue siendo útil. |
| `GET /{wabaId}/message_templates` acepta `id` y `rejected_reason` en `fields`. | Refrescar (§4.4) | Sin `rejected_reason` el espejo sólo sabría el motivo por el webhook. |

## 4. Backend

### 4.1 Modelo

Dos tablas nuevas en el schema `messaging`, migración `AddMessageTemplates`, y un índice en una segunda migración,
`AddQuotationTemplateIndex` (agregado al implementar, 2026-10-15).

**`messaging.message_templates`** — el espejo:

| Columna | Tipo | Qué guarda |
| --- | --- | --- |
| `id` | uuid | v7, PK |
| `tenant_id`, `connection_id` | uuid | La conexión dueña. Todo query filtra por las dos. |
| `meta_template_id` | varchar(64) | El `id` de Meta, al crear o al listar. |
| `name` | varchar(512) | Tal cual Meta. |
| `language` | varchar(16) | `es`, `es_CO`… |
| `category` | varchar(32) | `MARKETING`, `UTILITY`, `AUTHENTICATION`. Texto. |
| `parameter_format` | varchar(16) | `NAMED` o `POSITIONAL`. |
| `status` | varchar(32) | El nombre de Meta: `PENDING`, `APPROVED`, `REJECTED`, `PAUSED`, `DISABLED`, `IN_APPEAL`, `PENDING_DELETION`, `LIMIT_EXCEEDED`. Texto, sin `CHECK`: Meta agrega estados sin avisar. |
| `status_reason` | varchar(64) null | `rejected_reason` del listado o `reason` del webhook. |
| `components` | jsonb | Los componentes crudos de Meta; lo que hoy normaliza `TemplateCatalog`. |
| `kind` | varchar(16) | `Free`, `Quotation`, `External`. `CHECK`. |
| `created_by_member_id` | uuid null | Quién la creó desde QEP. `NULL` en `External`. |
| `created_at`, `updated_at`, `synced_at` | timestamptz | `synced_at` null hasta que un refresco la confirme contra Meta. |

Índices: `UX_message_templates_meta_id` único en `(tenant_id, connection_id, meta_template_id)`;
`UX_message_templates_name_language` único en `(tenant_id, connection_id, name, language)`;
`IX_message_templates_status` en `(tenant_id, connection_id, status)`; y `UX_message_templates_quotation`, único
**parcial** en `(tenant_id, connection_id) WHERE kind = 'Quotation'`: a lo sumo una plantilla de cotización por
conexión. *(Agregado al implementar, 2026-10-15: dos primeros aprovisionamientos a la vez dejaban dos filas
`Quotation`; §4.5.)*

**`messaging.template_mirror_syncs`** — cuándo se sincronizó cada conexión:

| Columna | Tipo |
| --- | --- |
| `tenant_id`, `connection_id` | uuid, PK compuesta |
| `synced_at` | timestamptz |

Sin esta fila no hay forma de distinguir «un WABA sin plantillas» de «un espejo que nunca se llenó», y
la segunda situación obliga a sincronizar antes de servir el catálogo de envío (§4.3). También le da a la
pantalla el «última sincronización».

### 4.2 Dominio

- `MessageTemplate`: entidad con las columnas de arriba. Fábricas `Pending(...)` (creada desde QEP, en
  `PENDING`, con `kind` `Free` o `Quotation`) y `Mirrored(...)` (traída por el refresco, `kind = External`).
  Métodos: `ApplyStatus(status, reason, now)`, `SyncFrom(status, reason, category, parameterFormat, components,
  now)`, `AdoptAsQuotation(now)` (sólo desde `External`), `IsApproved`, `IsLive`.
- `MessageTemplateKind` (`Free`, `Quotation`, `External`) y `MessageTemplateStatuses` (constantes de
  texto; `IsLive(status)` = `PENDING`, `APPROVED` o `IN_APPEAL`, lo que cuenta como «ya existe» para la de
  cotización).
- `MessageTemplateName`: la regla del nombre (`^[a-z0-9_]{1,512}$`) y la reserva de `qep_cotizacion` y
  `qep_cotizacion_*` (`messaging.template.name_reserved`). Vive en Domain porque la usa también la
  adopción (§4.5).
- Códigos nuevos en `MessagingErrorCodes`, todos `messaging.template.*`: `name_reserved`,
  `name_taken`, `limit_reached`, `rejected_by_meta`, `upload_failed`, `quotation_exists`,
  `quotation_conflict`, `in_use_by_campaign`, `not_found` y `name_invalid` *(agregado al implementar,
  2026-10-15: el dominio rechaza un nombre fuera de `^[a-z0-9_]{1,512}$`; es la última línea de defensa, el
  validador ya lo marca en `name`)*.

### 4.3 Aplicación

**Comandos y consultas**, cada uno con `MessagingAuthorization.EnsureAsync` (`TemplateManage`) y el
`TenantModuleGuard` de `messaging`:

| Caso de uso | Entrada | Salida |
| --- | --- | --- |
| `GetTemplateManagementQuery` | tenant, conexión | `TemplateManagementDto` |
| `CreateTemplateCommand` | tenant, conexión, `name`, `language`, `category`, `header?`, `body`, `footer?`, `buttons[]`, `examples{}` | `ManagedTemplateDto` |
| `ProvisionQuotationTemplateCommand` | tenant, conexión | `ManagedTemplateDto` |
| `DeleteTemplateCommand` | tenant, conexión, `templateId` | — |
| `SyncTemplatesCommand` | tenant, conexión | `TemplateManagementDto` |

**`CreateTemplateCommand`**: valida (§4.8), resuelve la conexión con `ResolveTemplateSourceAsync`
(`null` → `messaging.connection_unavailable`), comprueba el nombre reservado y que no exista ya
`(name, language)` en el espejo (`name_taken` local, sin llamar a Meta), arma los componentes con
`TemplateComponentsBuilder` (puro, en Application), llama `IWhatsAppCloudClient.CreateTemplateAsync`,
inserta la fila `Pending` con el `id` que Meta devolvió y la categoría que Meta devolvió, audita
`messaging.template.created`, y guarda. Si Meta falla, no se escribe nada.

**`ProvisionQuotationTemplateCommand`**: §4.5.

**`DeleteTemplateCommand`**: busca la fila por `templateId` en esa conexión (`not_found`); pregunta a
`IMessageTemplateRepository.IsReferencedByActiveCampaignAsync(tenant, connection, name, language)`
(`in_use_by_campaign`); llama `DeleteTemplateAsync(source, metaTemplateId, name)`; con éxito **o con
«no existe»** de Meta, elimina la fila, audita `messaging.template.deleted` y guarda.

**`SyncTemplatesCommand`** y el refresco implícito usan el mismo servicio de Application,
`TemplateMirrorSync.RunAsync(tenant, connection)`: lista todo en Meta (`ListTemplatesAsync`, con `id` y
`rejected_reason`), hace upsert por `meta_template_id` (`SyncFrom` en las conocidas, `Mirrored` en las
nuevas), elimina las filas que Meta ya no lista, escribe `template_mirror_syncs` y guarda. Es una sola
transacción por conexión. Las filas `Quotation` conservan su `kind` al sincronizar. Un elemento del listado sin
`id`, nombre o idioma se salta. Si el listado falla, el token vencido o un permiso faltante (§4.8) reportan la
conexión y son `messaging.connection_unavailable`; **cualquier otra** falla es `messaging.templates.unavailable`:
un 400 del listado no es un nombre repetido ni un rechazo, así que el listado no pasa por la traducción de
crear y borrar. *(Corregido al implementar, 2026-10-15.)*

**Catálogo de envío.** `IMessageTemplates.ListAsync(tenant, connection, refresh)` conserva su firma y su
contrato (`messaging.connection_unavailable`, `messaging.templates.unavailable`). Su implementación nueva,
`MirroredTemplateCatalog`: si `refresh` **o** no hay fila en `template_mirror_syncs` para esa conexión,
corre `TemplateMirrorSync`; luego devuelve `TemplateCatalog.Normalize` sobre los `components` de las
filas `APPROVED`, ordenadas como hoy. `RequireSendable` y `BuildSend` no cambian. `ListTemplatesQuery`
sigue pidiendo `ConversationManage` con `refresh=true` (R8 del spec 2026-10-11). La única excepción a
R8 es el primer llenado: un `GET` con `ConversationRead` sobre una conexión nunca sincronizada le pega a
Meta una vez. Ocurre una sola vez por conexión y la alternativa, un catálogo vacío hasta que un admin
entre a la pantalla, dejaría la bandeja sin plantillas tras el despliegue.

**Dos primeros llenados a la vez** *(agregado al implementar, 2026-10-15)*: dos destinatarios de una campaña o
dos asesores abriendo la bandeja tras el despliegue sincronizan la misma conexión nunca sincronizada. Los dos
insertan el marcador y las mismas filas; el segundo choca con `PK_template_mirror_syncs` o con
`UX_message_templates_meta_id`, y `MessagingUnitOfWork` lo traduce por nombre a `concurrency.conflict` (412)
después de soltar del contexto las filas del espejo que quedaron pendientes (`DiscardMirrorChanges`; sin eso, el
siguiente `SaveChanges` del mismo request, el de la campaña, las reinsertaría). El llenado implícito —el catálogo
sin `refresh` y el `GET` de la pantalla— atrapa ese 412 y lee el espejo que llenó el otro; `refresh=true` y el
`POST …/sync` explícito lo dejan subir. El marcador se escribe con EF y no con un upsert en SQL crudo porque
tiene que confirmarse en la misma transacción que las filas.

El catálogo viejo hacía *single-flight* y recordaba 30 s una falla de Meta; eso desapareció con el caché. Con una
conexión nunca sincronizada y Meta caído, cada destinatario de una campaña espera su propio timeout una vez.

**Puertos nuevos:**

- `IMessageTemplateRepository`: `FindAsync(tenant, connection, id)`, `FindByMetaIdAsync(tenant,
  connection, metaTemplateId)`, `FindByNameAsync(tenant, connection, name, language)`,
  `ListAsync(tenant, connection)`, `ListApprovedAsync(tenant, connection)`,
  `FindQuotationAsync(tenant, connection)`, `Add`, `Remove`, `IsReferencedByActiveCampaignAsync(...)`,
  `MarkSyncedAsync(tenant, connection, now)`, `LastSyncAsync(tenant, connection)`.
- `IQuotationTemplateReadiness`: `Task<ApprovedQuotationTemplate?> FindApprovedAsync(Guid tenantId, Guid
  connectionId, ct)`, con `ApprovedQuotationTemplate(string Name, string Language)` en Application. Devuelve el
  nombre y el idioma de la fila `Quotation` en `APPROVED` de esa conexión, o `null`. Es la señal para el
  consumidor Quotations (§5). La implementa Infrastructure (`QuotationTemplateReadiness`, scoped, una lectura
  sin caché que va por `UX_message_templates_quotation`); el adaptador hacia Quotations va en Bootstrapper
  cuando ese spec llegue. *(Corregido al implementar, 2026-10-15, decisión del owner: era `Task<bool>
  IsApprovedAsync`. Con el reintento versionado el nombre vigente es el de la fila, no una constante, y el
  sender lo necesita.)*
- `IWhatsAppCloudClient` gana `CreateTemplateAsync`, `DeleteTemplateAsync` y `UploadTemplateExampleAsync`
  (§4.4). `ListTemplatesAsync` no cambia de firma; cambia los `fields`.

**Webhook.** `WebhookPayloadParser` gana `TemplateStatusChange(WabaId, MetaTemplateId, Name, Language,
Event, Reason)` para `field = "message_template_status_update"`. `WebhookDeliveryProcessor` lo procesa como
`account_update` *(corregido al implementar, 2026-10-15: no es `WebhookHandlers`)*: `FindByAccountAsync(wabaId)`
da las conexiones de esa cuenta; por cada una, busca la fila por `meta_template_id` en `MessagingDbContext` y
`ApplyStatus(event, reason, now)`. Sin fila: log a nivel information con tenant,
conexión y `meta_template_id`, y nada más —el refresco la va a traer. Un `event` que no está en
`MessageTemplateStatuses` se guarda igual, como texto: la pantalla lo muestra crudo antes de que
inventemos qué significa.

**DTOs** (BFF; el motivo de cada forma, en el DTO):

```
TemplateManagementDto {
  connection: { id, name, displayPhoneNumber },
  quotationTemplate: { templateId, status, statusReason } | null,
  lastSyncedAt: DateTimeOffset | null,
  templates: ManagedTemplateDto[] }

ManagedTemplateDto {
  id, metaTemplateId, name, language, category, parameterFormat,
  status, statusReason, kind,
  header, body, footer, buttons: TemplateButtonDto[], parameters: string[],
  sendable, unsupportedReason,
  createdBy: MemberRefDto | null, createdAt, updatedAt }
```

`quotationTemplate` es `null` cuando no se ha aprovisionado y la pantalla muestra el botón de crear; en
`REJECTED`, `PAUSED` o `DISABLED` muestra el motivo y el botón de reintentar, que es el mismo
`POST …/quotation`. `header`, `body`, `footer`, `buttons`, `parameters`, `sendable` y
`unsupportedReason` salen de `TemplateCatalog` sobre `components`, resueltos para que la pantalla no
conozca el formato de Meta, igual que `MessageTemplateDto`. `templates` va ordenado por `name` y
`language`, todas las filas, sin paginar: un WABA tiene decenas de plantillas, no miles.

### 4.4 Infrastructure

Todo en `Modules.Messaging.Infrastructure/Meta`, en `WhatsAppCloudClient`:

- **`ListTemplatesAsync`**: `TemplateFields` pasa a
  `id,name,language,status,category,components,parameter_format,rejected_reason`.
- **`CreateTemplateAsync(source, JsonObject payload)`**: `POST {ver}/{wabaId}/message_templates` con
  `name`, `language`, `category`, `parameter_format` y `components`. El payload lo arma Application
  (`TemplateComponentsBuilder.CreatePayload`, `QuotationTemplateDefinition.Payload`). Devuelve
  `MessagingGraphResult<CreatedTemplate(Id, Status, Category)>`. *(Corregido al implementar, 2026-10-15: no hay
  `TemplateCreateRequest`.)*
- **`DeleteTemplateAsync(source, metaTemplateId, name)`**: `DELETE
  {ver}/{wabaId}/message_templates?hsm_id={id}&name={name}`. Un 400 cuyo error diga que la plantilla no
  existe se devuelve como éxito con `AlreadyGone = true`.
- **`UploadTemplateExampleAsync(source, bytes, mimeType, fileName)`**: el `appId` no entra por parámetro, sale de
  `MessagingMetaOptions` (sin él, la falla `no_app_id`). Dos requests.
  `POST {ver}/{appId}/uploads?file_length=&file_type=&file_name=` devuelve `{ id: "upload:…" }`, que en la
  práctica trae la firma: `upload:…?sig=…`. El id se valida contra esa forma y va sin escapar; la `?sig=` viaja
  como query del segundo request, no escapada dentro de la ruta.
  `POST {ver}/{uploadId}` con header `file_offset: 0`, `Authorization: OAuth {token}` y el cuerpo binario
  devuelve `{ h }`. Timeout de 30 s; el PDF pesa unos KB. Un token rechazado sigue el camino de §4.8; cualquier
  otro fallo → `upload_failed`. *(Corregido al implementar, 2026-10-15.)*
- `MessagingMetaOptions` gana `AppId` (la sección `Meta:App` ya lo tiene; Integrations lo valida).

**Traducción de errores de Graph** (§4.8) en una clase propia, `TemplateGraphErrors`, que discrimina por
`error_user_title` y `error_user_msg`, nunca sólo por `code = 100` (y no por `error_subcode`, que Meta ya no
devuelve): el mismo criterio que el nombre de índice en los `23505`. Vive en Application, no en
Infrastructure: traduce un `MessagingGraphFailure` ya parseado, sin EF ni HTTP, y el handler necesita
reaccionar al token vencido (`ReportRejectedAsync`) en el mismo lugar. El token vencido o un permiso faltante
(HTTP 401 o 403, `code` 190, 10 o 200–299; §4.8) sigue el camino que ya tienen los envíos:
`ReportRejectedAsync(token_expired)` y `messaging.connection_unavailable`. *(Corregido al implementar,
2026-10-15: decía sólo 401 o `code = 190`.)*

**Persistencia:** no hay carpeta `Persistence/Configurations`: el mapeo de las dos tablas va en
`MessagingDbContext` (`ConfigureMessageTemplate` y `ConfigureTemplateMirrorSync`), como el resto del módulo
*(corregido al implementar, 2026-10-15)*. `MessageTemplateRepository` con las consultas de §4.3.
`MessagingUnitOfWork` traduce por **nombre de índice**: `UX_message_templates_name_language` →
`name_taken`; `UX_message_templates_quotation` → `quotation_exists`; `UX_message_templates_meta_id` y
`PK_template_mirror_syncs` → `concurrency.conflict` (412), soltando antes las filas pendientes del espejo
(§4.3).
`IsReferencedByActiveCampaignAsync` es un `EXISTS` sobre `messaging.campaigns` por `connection_id`,
`template_name`, `template_language` y `status IN ('Scheduled','Sending')`. `MessageTemplateCatalog` (el del
`IMemoryCache`) se elimina junto con su registro.

**Webhook:** el parser y `WebhookDeliveryProcessor` (§4.3). El handoff de Meta
(`docs/superpowers/plans/2026-10-09-mensajeria-whatsapp-handoff-meta.md`, paso 5) gana el campo
`message_template_status_update` en la suscripción de la app.

### 4.5 La plantilla de cotización aprovisionada

`QuotationTemplateDefinition` (Application) es la **única** fuente del nombre, el idioma, la categoría,
el texto, las variables y sus ejemplos:

- `Name = "qep_cotizacion"`, `Language = "es"`, `Category = "UTILITY"`, `ParameterFormat = NAMED`.
- Versiones *(agregado al implementar, 2026-10-15, decisión del owner)*: `Name` es la versión 1.
  `NameFor(version)` da `qep_cotizacion` para la 1 y `qep_cotizacion_v{n}` desde la 2; `VersionOf(name)` es su
  inverso y devuelve `null` para cualquier otro nombre (también `_v1`, `_v0` o `_v02`, que `NameFor` nunca
  produce). Todas caen dentro del prefijo reservado. `Draft(name)` y `Payload(handle, name)` reciben el nombre.
- `HEADER`: `format: DOCUMENT`, `example.header_handle: [handle]`.
- `BODY`: `¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta
  el *{{valid_until}}*.`, con `body_text_named_params`: `fullname → Juan Perez`, `order_number →
  COT-000123`, `total → 2.450.000 COP`, `valid_until → 30 de septiembre de 2026`.
- `FOOTER`: `Este mensaje fue generado automáticamente.`
- `Matches(components)`: compara cuerpo, pie, formato del encabezado y nombres de variables de unos
  `components` de Meta con la definición. No mira el nombre. Lo usa la adopción.
- El PDF de ejemplo **se genera en Application**: `QuotationTemplateDefinition.ExamplePdf()` sobre `MinimalPdf`,
  un PDF 1.4 de una página con una línea y su tabla `xref`. No es un recurso embebido. *(Corregido al
  implementar, 2026-10-15, decisión del owner.)*

Es el mismo texto que hoy tiene la plantilla de Zenvia (README § Plantilla de WhatsApp), para que el
cliente final no note el cambio de canal. Las versiones las usa el reintento; un cambio de texto algún día
es otra definición que también crea la versión siguiente, aprobada antes de que el envío la use. El sender
de Quotations lee el nombre vigente de la fila `Quotation` (`FindApprovedAsync`, §4.3), no de una constante, y
cambia en el **mismo commit** que las variables.

**`ProvisionQuotationTemplateCommand`**, en orden. *(Corregido al implementar, 2026-10-15: el reintento
recreaba `qep_cotizacion` borrando antes la rechazada, pero Meta bloquea un tiempo el nombre de una plantilla
borrada. Decisión del owner: el reintento crea la versión siguiente, y crea antes de borrar.)*

1. Autoriza, guard de módulo, resuelve la conexión.
2. `FindQuotationAsync`: si la fila `Quotation` está viva (`IsLive`: `PENDING`, `APPROVED` o `IN_APPEAL`) →
   422 `quotation_exists`. Si está en cualquier otro estado (`REJECTED`, `PAUSED`, `DISABLED`,
   `LIMIT_EXCEEDED`…), es el «reintentar» y se sigue con ella como la anterior.
3. Sin fila `Quotation`, `FindByNameAsync("qep_cotizacion", "es")`: si existe, la **adopta** (sólo el nombre
   base): si `Matches(components)`, `AdoptAsQuotation`, audita `messaging.template.quotation_provisioned` con
   `changed_fields = ["kind"]`, guarda y responde 201 sin tocar Meta; si no coincide, 422 `quotation_conflict`
   con el nombre en el mensaje, para que el admin la borre desde la pantalla. `AuditEntry` no tiene columna de
   metadatos: `changed_fields = ["kind"]` es la marca «adoptada» que antes se llamaba `adopted = true`, y es lo
   que distingue en la auditoría una adoptada de una creada.
4. El nombre a crear es la versión siguiente a la mayor `VersionOf(name)` entre **todas** las filas del espejo
   de esa conexión en `es` (cualquier `kind`); sin ninguna, la 1 (`qep_cotizacion`). Un reintento tras
   `qep_cotizacion` crea `qep_cotizacion_v2`.
5. Sube el PDF de ejemplo (`UploadTemplateExampleAsync`) → `handle`.
6. `CreateTemplateAsync` con los componentes de la definición, el nombre y el `handle`. Si Meta responde que el
   nombre ya existe (`name_taken`; por ejemplo, uno en cuarentena que el espejo ya no conoce), prueba la versión
   siguiente **con el mismo handle**, hasta 5 creaciones en total; agotadas, sube el `name_taken`. Cualquier
   otra falla sube como en §4.8.
7. Con la nueva creada, y sólo si había una anterior, la borra en Meta (`DeleteTemplateAsync(source,
   metaTemplateId, name)`, idempotente). Si el borrado falla por el token, 422 `connection_unavailable` y no se
   escribe nada: la nueva ya quedó en Meta sin fila y el siguiente refresco la trae como `External`. Si falla por
   otra cosa, queda un warning con tenant, conexión y `meta_template_id` (nunca el token) y se sigue: la fila
   vieja se quita igual y el siguiente refresco la traerá como `External` si Meta todavía la tiene.
8. Quita la fila anterior, inserta la `Pending` con `kind = Quotation` y el nombre usado, audita
   `quotation_provisioned` (sin `changed_fields`) y guarda, en una sola transacción. 201.

Crear antes de borrar hace que un fallo al subir o al crear deje la anterior intacta en Meta y en el espejo,
sin ningún `DELETE`, y que el siguiente intento calcule la misma versión.

**Dos primeros aprovisionamientos a la vez** *(agregado al implementar, 2026-10-15)*: calculan la misma
versión; Meta crea una y al otro le dice «ya existe», así que salta a la siguiente y también la crea. El índice
`UX_message_templates_quotation` (§4.1) deja guardar una sola fila `Quotation` por conexión y
`MessagingUnitOfWork` lo traduce por nombre a `quotation_exists`. La versión que alcanzó a crear el que pierde
queda en Meta y vuelve como `External` en el siguiente refresco. No hay comprobación previa en el handler: sólo
el índice cierra la carrera, como el triage de Pipeline.

**Espejo atrasado** *(agregado al implementar, 2026-10-15)*: si alguien creó a mano en Meta una
`qep_cotizacion` viva que el espejo todavía no conoce, el paso 3 no la ve, Meta responde `name_taken` en el
paso 6 y el aprovisionamiento crea `qep_cotizacion_v2` en vez de adoptarla. La de mano vuelve como `External`
con el siguiente refresco. Sincronizar antes de aprovisionar lo evita.

El nombre `qep_cotizacion` y el prefijo `qep_cotizacion_` quedan reservados para `CreateTemplateCommand`
(`name_reserved`). Borrar la `Quotation` desde la pantalla **sí** se permite —es la salida cuando algo
quedó mal— y el frontend avisa que deja de poderse enviar cotizaciones por esa conexión.

### 4.6 Permiso y auditoría

- `MessagingPermissions.TemplateManage = "messaging.template.manage"`. De núcleo (`RequiredModules`
  vacío, como los demás de Messaging) y de fábrica sólo en `admin`. Entra en los **tres** lugares de
  `QepServiceCollectionExtensions` donde ya está `CampaignManage`: catálogo de permisos, rol de sistema
  `admin` y la política de `AddAuthorization`. Sin la tercera, `RequireAuthorization` no resuelve y el
  síntoma es 500.
- `IMessagingAuditRecorder.RecordTemplate(tenantId, actorId, action, templateId, occurredAt)` y las
  acciones `MessagingAuditActions.TemplateCreated = "messaging.template.created"`,
  `TemplateQuotationProvisioned = "messaging.template.quotation_provisioned"`,
  `TemplateDeleted = "messaging.template.deleted"`. Atómicas con la transacción que escribe el espejo.
- El token de la conexión no se imprime: `MessagingTemplateSource.ToString` ya lo oculta; las llamadas
  nuevas no loguean headers ni cuerpos de request.

### 4.7 Contratos HTTP

Bajo `/api/v1/tenants/{tenantId}/messaging`. El `GET /connections/{connectionId}/templates?refresh=` **no
cambia de forma**; sólo cambia de dónde lee (D-T8).

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /connections/{connectionId}/template-management` | `template.manage` | 200 `TemplateManagementDto` |
| `POST /connections/{connectionId}/template-management/templates` | `template.manage` | 201 `ManagedTemplateDto`, `Location` del `GET` de la pantalla |
| `POST /connections/{connectionId}/template-management/quotation` | `template.manage` | 201 `ManagedTemplateDto`, `Location` del `GET` de la pantalla |
| `DELETE /connections/{connectionId}/template-management/templates/{templateId}` | `template.manage` | 204 |
| `POST /connections/{connectionId}/template-management/sync` | `template.manage` | 200 `TemplateManagementDto` |

**Cuerpo de crear una libre** (`CreateTemplateRequest`):

```json
{
  "name": "promo_octubre",
  "language": "es",
  "category": "MARKETING",
  "header": "Oferta de octubre",
  "body": "Hola {{first_name}}, este mes {{product}} tiene {{discount}} de descuento.",
  "footer": "Responde DETENER para no recibir promociones.",
  "buttons": [
    { "type": "QuickReply", "text": "Detener promociones" },
    { "type": "Url", "text": "Ver catálogo", "url": "https://ejemplo.co/catalogo" },
    { "type": "PhoneNumber", "text": "Llamar", "phoneNumber": "+573001234567" }
  ],
  "examples": { "first_name": "Ana", "product": "Silla Nova", "discount": "20 %" }
}
```

Los `type` de botón son los que ya usa `TemplateButtonDto` (`QuickReply`, `Url`, `PhoneNumber`). Las
variables se declaran en `body` como `{{nombre}}`; `examples` trae **exactamente** esas claves.
`header` y `footer` admiten `null`. No hay `clientId`: la idempotencia natural es `(name, language)`,
que responde `name_taken` al repetir.

### 4.8 Validación y errores

**`CreateTemplateCommandValidator`** (FluentValidation, 422 `validation.failed` con el mapa `errors`):

| Campo | Regla |
| --- | --- |
| `name` | obligatorio; `^[a-z0-9_]{1,512}$`; no reservado (`qep_cotizacion`, `qep_cotizacion_*`) |
| `language` | obligatorio; `^[a-z]{2,3}(_[A-Z]{2})?$`; hasta 16 |
| `category` | `MARKETING` o `UTILITY` |
| `header` | hasta 60; sin `{{` |
| `body` | obligatorio; hasta 1024; cada `{{…}}` con nombre `^[a-z0-9_]+$`; al menos un carácter que no sea variable |
| `footer` | hasta 60 |
| `buttons` | hasta 10; `text` obligatorio hasta 25; `Url` con `url` HTTPS absoluta hasta 2000; `PhoneNumber` con `phoneNumber` `^\+?[0-9]{7,20}$`; las `QuickReply` todas contiguas, al principio o al final |
| `examples` | claves iguales al conjunto de variables del cuerpo, ni una más ni una menos; valores no vacíos hasta 1024 |

Lo que Meta valida y nosotros **no duplicamos** (proporción de variables frente a texto, variable al
final del cuerpo, contenido) vuelve de Meta y se traduce en Infrastructure. Duplicarlo sería mantener dos
copias de reglas que Meta cambia sin avisar.

**Mapa de errores**, todos por el `ApiExceptionHandler` central, ningún status a mano:

| Situación | Código | HTTP |
| --- | --- | --- |
| Validación del comando | `validation.failed` + `errors` | 422 |
| Nombre reservado | `validation.failed` con `errors.name` desde el validador; `messaging.template.name_reserved` si llega al dominio | 422 |
| Nombre fuera de `^[a-z0-9_]{1,512}$` que llega al dominio *(agregado al implementar, 2026-10-15)* | `messaging.template.name_invalid` (el validador ya lo marca en `name`) | 422 |
| `(name, language)` ya existe, en el espejo o en Meta | `messaging.template.name_taken` (sin mapa `errors`; el frontend lo pinta en `name`, como hace con `quick_reply.shortcut_taken`) | 422 |
| Tope de plantillas del WABA | `messaging.template.limit_reached` | 422 |
| Cualquier otro rechazo de Graph al crear | `messaging.template.rejected_by_meta`, con el `error_user_msg` de Meta en el detalle: «WhatsApp no aceptó la plantilla: …» | 422 |
| Falló la subida del PDF de ejemplo | `messaging.template.upload_failed` | 422 (reintentar es seguro, como `media.upload_failed`) |
| Ya hay plantilla de cotización viva, u otro aprovisionamiento simultáneo de la misma conexión ganó (`UX_message_templates_quotation`) | `messaging.template.quotation_exists` | 422 |
| Existe `qep_cotizacion` externa con otro cuerpo | `messaging.template.quotation_conflict` | 422 |
| Campaña `Scheduled` o `Sending` la usa | `messaging.template.in_use_by_campaign` | 422 |
| `templateId` no es de esa conexión | `messaging.template.not_found` | 404 (uuid local; tenant y conexión ya se revalidaron, no filtra nada) |
| Conexión ajena, inactiva o sin `wabaId` | `messaging.connection_unavailable` | 422, nunca 404 |
| Token vencido o permiso faltante: HTTP 401 o 403, o `code` 190 (token), 10 o 200–299 (permisos de Graph) *(corregido al implementar, 2026-10-15)* | `messaging.connection_unavailable` tras `ReportRejectedAsync(token_expired)` | 422 |
| Meta no contesta o 5xx; o el listado del refresco falla por cualquier otra cosa | `messaging.templates.unavailable` | 422 (el que ya existe; reintentar es seguro) |
| Dos refrescos explícitos de la misma conexión a la vez *(agregado al implementar, 2026-10-15)* | `concurrency.conflict` (el llenado implícito lo atrapa, §4.3) | 412 |
| Sin permiso o módulo apagado | `authorization.denied` / `tenancy.module_not_enabled` | 403 |

Ningún código nuevo toca `src/Api/ApiExceptionHandler.cs`: todos son `MessagingDomainException` (422) o
`ResourceNotFoundException` (404), que el mapa central ya conoce.

### 4.9 Pruebas

TDD: RED antes que GREEN, con evidencia literal de ambos.

**Unitarias** (`Modules.Messaging.UnitTests`):

- `QuotationTemplateDefinitionTests`: componentes exactos con el handle y los ejemplos; `Matches` acepta
  el propio resultado y rechaza otro cuerpo, otro pie, otro formato de encabezado u otras variables.
- `TemplateComponentsBuilderTests`: cuerpo con `body_text_named_params` en el orden de aparición;
  encabezado y pie opcionales; los tres tipos de botón; sin `components` vacíos.
- `CreateTemplateCommandValidatorTests`: campo por campo, incluido el agrupamiento de botones, el nombre
  reservado y `examples` con una clave de más y una de menos.
- `MessageTemplateTests` (dominio): `ApplyStatus`, `SyncFrom` conserva `kind`, `AdoptAsQuotation` sólo
  desde `External`, `IsLive`.
- `WebhookPayloadParserTests`: `message_template_status_update` completo, sin `reason`, con `event`
  desconocido; otros campos de plantilla siguen en `UnknownChange`.
- `TemplateGraphErrorsTests`: cada fila del mapa de §4.8 a partir de cuerpos de error de Graph; un 400
  sin `error_user_title` conocido cae en `rejected_by_meta` con el `error_user_msg`.
- `ManagedTemplateMappingTests`: fila → DTO, con `createdBy` null en `External`.

**Integración** (`Modules.Messaging.IntegrationTests`, con `FakeMetaGraphHandler` y `MessagingApiHarness`):

- Crear una libre: Meta recibe el cuerpo esperado; queda fila `PENDING` `Free` con auditoría
  `template.created`; repetir el nombre da `name_taken` sin segunda llamada a Meta; sin
  `X-Permissions: messaging.template.manage` → 403 por el permiso.
- Aprovisionar: dos requests de subida y uno de creación, en ese orden, con el `header_handle` del fake;
  fila `PENDING` `Quotation`; repetir → `quotation_exists`; con la anterior en `REJECTED` → crea
  `qep_cotizacion_v2` y **después** borra la vieja; un reintento que Meta rechaza no borra nada y deja la
  `REJECTED`; `name_taken` salta a la versión siguiente con una sola subida; con una `External` igual → adopta
  sin llamar a Meta; con una `External` distinta → `quotation_conflict`. *(Corregido al implementar,
  2026-10-15.)*
- Webhook `APPROVED`: la fila cambia y el `GET /templates` de envío empieza a devolverla; `REJECTED`
  conserva `reason`; un `message_template_id` desconocido no escribe nada; la entrega queda procesada.
- Borrar: `DELETE` a Meta con `hsm_id` y `name`; fila eliminada; auditoría; Meta «no existe» → 204 igual;
  con una campaña `Scheduled` → `in_use_by_campaign` y la fila sigue.
- Sincronizar: adopta una `External` nueva, actualiza estado y categoría de una conocida, elimina la que
  Meta ya no lista, conserva `kind = Quotation`, escribe `template_mirror_syncs`.
- Catálogo de envío: sin fila de sincronización, el primer `GET /templates` sincroniza; con ella, no
  llama a Meta; `refresh=true` sí.
- `IQuotationTemplateReadiness.FindApprovedAsync` devuelve nombre e idioma sólo con la `Quotation` en
  `APPROVED` de esa conexión (`null` antes de aprovisionar, en `PENDING`, en `REJECTED` y para otra conexión), y
  devuelve `qep_cotizacion_v2` cuando la aprobada es la del reintento. *(Corregido al implementar,
  2026-10-15.)*

**Regresión**: `CampaignApiTests`, `CampaignDispatchTests` y las de envío de plantilla pasan hoy por el
caché de 5 minutos y el fake de `message_templates`; se barren a mano para que sigan en verde leyendo del
espejo. Es el caso de «el síntoma no se parece a la causa» del CLAUDE.md.

**Arquitectura**: `MessagingLayerTests` sin cambios. No hay módulo nuevo y Messaging sigue sin
referenciar Integrations ni Quotations.

## 5. Lo que este spec le deja al consumidor Quotations

El consumidor Quotations es un spec aparte. Este documento le entrega el puerto
`IQuotationTemplateReadiness` (§4.3): `FindApprovedAsync` dice si se puede enviar por Meta y, cuando sí, con
qué nombre e idioma. El sender de Meta usa **ese** nombre, no `QuotationTemplateDefinition.Name`: tras un
reintento la vigente es `qep_cotizacion_v2` o una posterior. *(Corregido al implementar, 2026-10-15.)* Y
registra, para no perderlas, las reglas que el owner fijó el 2026-10-14 en esta conversación:

- **Canal por conexión del tenant.** Sólo Zenvia configurado → envía por Zenvia. Sólo `whatsapp-cloud`
  activa → envía por Meta. **Las dos** → envía por Meta.
- **Con Meta, el envío exige la plantilla de cotización `APPROVED` en esa conexión.** Si no lo está, el
  botón «Enviar» de la cotización no está disponible y el backend lo rechaza con un 422 propio de
  Quotations. Zenvia no cambia: sigue sin depender de ninguna plantilla de Meta.
- **El resolver es un Strategy por tenant** (`IQuotationChannelResolver`), con los puertos de
  Integrations que ya existen (`IConnectionRoutes`, `IIntegrationConnections`) y el adaptador en
  Bootstrapper. No se diseña una abstracción de canal genérica para la bandeja ni para las campañas:
  esos son modelos de WhatsApp Cloud (BSUID, ventana de 24 h, plantillas, ritmo por 130429) y otro canal,
  como Instagram, entraría como un cliente por `ProviderKey` detrás de una fábrica, con su propio parser
  de webhook, cuando exista.
- **Pendiente para ese spec:** qué pasa con un tenant sin ninguna conexión (hoy envía por la credencial
  global de Zenvia de QEP).

## 6. Entregas y despliegue

1. Backend, `feature/gestor-de-plantillas` desde `develop` `56aeb105`, en un worktree. Migraciones
   `AddMessageTemplates` y `AddQuotationTemplateIndex`. Reinicio de la API y recarga de sesión por el permiso
   nuevo.
2. Handoff de Meta: suscribir `message_template_status_update` en la app (paso 5 del handoff). Hasta que
   esté, los estados sólo cambian con el refresco.
3. Frontend, slice aparte: la pantalla de plantillas de la conexión (`template-management`), el
   formulario de crear, el botón de la plantilla de cotización con su estado y su reintento, el borrado
   con aviso, y el diccionario en español de estados y motivos de rechazo.

Hasta que el frontend tenga la pantalla, el espejo de cada conexión se llena solo con el primer
`GET /templates` de la bandeja o de las campañas (§4.3), así que el catálogo de envío no se queda vacío
al desplegar. Ese primer llenado es uno por conexión y ya no tiene *single-flight*: con Meta caído, cada
destinatario de una campaña sobre una conexión nunca sincronizada espera su propio timeout una vez.

## 7. Riesgos

- Los supuestos de §3, en especial el token que acepta la Resumable Upload. Se confirman con el primer
  aprovisionamiento real; el handoff gana una nota «a confirmar en producción».
- El espejo puede quedar atrás si el webhook no está suscrito o se pierde; el refresco manual y el
  refresco implícito del primer `GET` lo corrigen. No hay job periódico (D-T4).
- Un admin puede borrar la plantilla de cotización y dejar su conexión sin poder enviar cotizaciones por
  Meta; la pantalla avisa y el aprovisionamiento la vuelve a crear.
- Versiones huérfanas en el WABA *(agregado al implementar, 2026-10-15)*: el perdedor de dos aprovisionamientos
  simultáneos, o un reintento cuyo borrado de la versión vieja Meta rechaza, dejan una versión en Meta sin fila
  `Quotation`. El siguiente refresco la trae como `External` y el admin la borra desde la pantalla.

## 8. Fuera de alcance

- Editar plantillas (D-T6).
- Encabezado multimedia, URL dinámica, `COPY_CODE` y variables en el encabezado en las libres.
- Categoría `AUTHENTICATION`, carrusel, flujos, Marketing Messages API.
- Webhooks `message_template_quality_update` y `template_category_update`; el refresco recoge el cambio
  de categoría.
- `allow_category_change` (D-T10).
- El guard en `SendQuotation`, el resolver de canal y el sender de Meta en Quotations (§5).
- La pantalla del frontend (§6).
