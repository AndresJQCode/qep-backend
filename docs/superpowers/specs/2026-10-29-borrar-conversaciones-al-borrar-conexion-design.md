# Borrar las conversaciones al borrar la conexión

**Fecha:** 2026-10-29
**Módulos:** Messaging (consumidor del evento, borrado de conversaciones, mensajes, medios y objetos de R2), Bootstrapper
(adaptador que limpia Automations y Pipeline), Integrations (sin cambios: ya publica el evento).
**Repos:** `qep-backend`. El aviso en el diálogo de borrado de `qep-frontend` es un slice aparte (§10, DP-7).
**Estado:** borrador para revisión. Las reglas de §2 son del owner (conversación del 2026-10-29). Las que toma el spec están en §9
(D-1…), a ratificar. Lo que no se pudo decidir con el código está en §11 como `DECISIÓN-PENDIENTE`.

## 1. Contexto

Hoy, cuando un tenant borra una conexión de WhatsApp o Instagram, sus conversaciones quedan huérfanas: siguen en
`messaging.conversations` con un `connection_id` que ya no existe, con sus mensajes y con sus adjuntos en R2. Verificado en el código y
en la base local (`dev_qep`) el 2026-10-29:

| Hecho | Dónde |
|---|---|
| Borrar la conexión elimina la fila, audita `integrations.connection.deleted` y escribe en el outbox el evento `integrations.connection-deleted.v1`, en el mismo `SaveChanges` | `DeleteConnectionHandler.HandleAsync` (`DeleteConnection.cs:21-33`); nombre del evento en `ConnectionEvents.Deleted` (`ConnectionEvents.cs:13`); acción de auditoría en `ConnectionAuditActions.Deleted` (`ConnectionWriteSupport.cs:38`) |
| El payload es `{ tenantId, connectionId, providerKey, occurredAt }`, en minúscula, sin actor | `IntegrationsEventPublisher` (`IntegrationsEventPublisher.cs:10-23`) |
| El evento **no tiene consumidor**: «Sin consumidores en este spec» | `ConnectionEvents.cs:7-8`; `rg "connection-deleted\|ConnectionEvents.Deleted" src` sólo da el publicador |
| Sólo `integrations.connection_routes` y `connection_secrets` tienen FK a `integrations.connections` (`ON DELETE CASCADE`). `messaging.conversations.connection_id` no tiene FK | `pg_constraint` en `dev_qep` |
| La única FK hacia `messaging.conversations` es `messages(conversation_id)` `ON DELETE CASCADE`, y la única hacia `messages` es `message_media(message_id)` `ON DELETE CASCADE` | `FK_messages_conversation`, `FK_message_media_message` |
| Al borrar la conexión, el webhook ya no la encuentra y descarta lo que llegue (`unknown-route`), y el envío no resuelve remitente | `WebhookDeliveryProcessor.cs:117-121`; `IMessagingConnectionDirectory.FindRouteAsync` / `ResolveSenderAsync` (`IMessagingConnectionDirectory.cs:43-50`) |

Los medios viven en el bucket privado bajo una clave **determinística**: `messaging/{tenantId}/{messageId}`
(`MessagingMediaKeys.For`, `MessagingMediaKeys.cs:7`). La usan los entrantes de WhatsApp e Instagram (`MediaTransfer.KeyFor`,
`MediaTransfer.cs:69`), los adjuntos y notas de voz del asesor y la copia del PDF de una cotización enviada por plantilla
(`QuotationTemplateOutbound.cs:126`). En `dev_qep` las 203 filas de `message_media` con `storage_key` cumplen esa forma, sin excepción.
El `source_url` de Instagram es una URL de Meta: no hay nada nuestro que borrar ahí. El puerto para borrar ya existe:
`IMessagingMediaStore.DeleteAsync(key, ct)` (`IMessagingMediaStore.cs:16`), con adaptador en Bootstrapper sobre el `IObjectStorage`
de Storage (`MessagingMediaStore.cs:18`).

Tablas que guardan un id de conversación o de mensaje **sin FK** (por eso el borrado en cascada no las alcanza):

| Tabla | Columna | Nulable | Notas |
|---|---|---|---|
| `messaging.automation_inbox` | `conversation_id`, `connection_id`, `message_id` | no | Cola del bot; las procesadas se purgan a los 7 días |
| `messaging.campaign_recipients` | `conversation_id`, `message_id` | sí | Foto de un envío de campaña |
| `automations.conversation_runs` | `conversation_id`, `connection_id`, `last_inbound_message_id` | no / sí | «La corrida no se purga nunca» (`AutomationsDbContext.cs:281`) |
| `automations.run_units` | `run_id`, `inbound_message_id` | — / sí | Sin FK a la corrida; retención de 3 días (`RunDetailRetentionDays`) |
| `automations.http_calls` | `run_id` | sí | `connection_id` es la conexión de **API externa** del bloque HTTP, no la de mensajería; retención de 30 días (`HttpCallRetentionDays`) |
| `pipeline.triage_decisions` | `conversation_id` | no | Índice único `IX_triage_decisions_conversation_inbound` |
| `pipeline.deals` | `origin_conversation_id` | sí | **No estaba en el inventario inicial**: un negocio nacido del triage apunta a su conversación |
| `customers.data_consents` | `conversation_id`, `prompt_message_id`, `replied_to_message_id`, `reply_message_id` | sí | Prueba legal, sólo se inserta |

## 2. Reglas del owner (2026-10-29)

- **O1.** Borrar una conexión borra sus conversaciones, los mensajes de esas conversaciones y sus adjuntos en R2.
- **O2.** `customers.data_consents` **no se toca**. Su `conversation_id` queda como referencia histórica colgante, sin migración.
  No se puede poner en `NULL`: `CK_data_consents_identity` lo exige en `Accepted`/`Rejected` y en `Revoked` + `ThisConversation`.
- **O3.** El disparador es un consumidor en Messaging del evento que ya publica Integrations, con el patrón inbox/outbox del repo.
  Integrations no referencia Messaging.
- **O4.** Orden: juntar las claves de R2 y borrar las filas en una transacción; borrar los objetos de R2 después. Una falla de R2 deja
  objetos huérfanos en el bucket, **nunca** filas rotas.

## 3. Objetivos, no-objetivos y antiobjetivos

**Objetivos**

1. Que después de borrar una conexión no quede ninguna conversación, mensaje ni medio de esa conexión en la base, ni referencias vivas
   (bot, triage, cola) que apunten a ellos.
2. Que el borrado sea idempotente: el evento se puede entregar dos veces, o cortarse a mitad, y el resultado es el mismo.
3. Que una conexión con decenas de miles de mensajes se borre sin transacciones gigantes ni bloqueos largos.

**No-objetivos**

- Limpiar las otras tablas con `connection_id` (campañas, espejo de plantillas, horarios, presupuestos de envío, canal de cotización,
  chatbots, Shopify). Se analizan en §8 y se dejan como `DECISIÓN-PENDIENTE`.
- Borrar clientes creados por la ingesta (`Customer` `Incomplete`): son del tenant, no de la conexión.
- Borrar las entradas de `audit.entries` que nombran esas conversaciones: la auditoría no se reescribe.
- Deshacer el borrado. El borrado de la conexión ya es físico (D9 del spec 2026-10-08) y esto también.

**Antiobjetivos**

- Que Messaging referencie Integrations, Automations o Pipeline (`MessagingLayerTests`).
- Que una falla de R2 revierta o bloquee el borrado de filas.
- Una FK nueva entre esquemas.

## 4. Flujo

```
DELETE /connections/{id}  (Integrations, sin cambios)
  └─ misma transacción: borra la conexión + audit + outbox «integrations.connection-deleted.v1»

ConnectionDeletedWorker (Messaging.Infrastructure, BackgroundService)
  └─ ConnectionDeletedProcessor.ProcessBatchAsync      ← copia de ShopifyClaimedOutboxConsumer
       └─ por mensaje reclamado en messaging.inbox_messages:
            1. una vez:     limpia referencias de nivel conexión (§5.2)
            2. por lote:    transacción { elige N conversaciones FOR UPDATE SKIP LOCKED
                                          junta las claves de medios
                                          IConversationPurgeReferences.DeleteAsync (Automations, Pipeline)
                                          borra automation_inbox de esas conversaciones
                                          DELETE conversations  → cascada messages → message_media }
                            commit
                            después: IMessagingMediaStore.DeleteAsync por clave (best effort)
            3. sin más conversaciones: transacción { audit (System) + inbox processed_at } commit
```

## 5. Diseño

### 5.1 Consumidor e inbox (Messaging)

- **Inbox propio.** Messaging no tiene `inbox_messages` (en `dev_qep` existen las de audit, identity, notifications, pipeline,
  platform, shopify y storage). Migración `AddMessagingInbox` en `MessagingDbContext`: `messaging.inbox_messages (consumer, message_id,
  processed_at, claimed_until, attempts)`, PK `(consumer, message_id)`, nombres de columna a mano porque los reclamos se escriben en SQL
  crudo. Proyección de sólo lectura de `platform.outbox_messages` con `ExcludeFromMigrations`, como `PipelineDbContext.cs:280-289`.
- **Sin efecto retroactivo (D-12).** La misma migración `AddMessagingInbox`, en su `Up`, inserta en `messaging.inbox_messages` una fila
  con `consumer = 'messaging.connection-deleted'` y `processed_at = now()` por cada `integrations.connection-deleted.v1` que ya esté en
  `platform.outbox_messages` (`ON CONFLICT DO NOTHING`). El consumidor nunca los ve: el borrado aplica sólo a las conexiones borradas
  desde el despliegue. Va en la migración y no en un filtro por `occurred_at` contra una fecha de configuración, para no depender del
  reloj ni de una variable que alguien olvide en `Backend-prod`. El `Down` borra esas filas junto con la tabla.
- **Reclamo.** `MessagingInboxClaims.TryClaimAsync`, copia de `ShopifyInboxClaims` (`ShopifyInbox.cs:38-63`): una sentencia
  `INSERT … ON CONFLICT … RETURNING attempts` que se commitea sola, así el intento queda contado aunque el lote falle. Ningún módulo
  referencia la infraestructura de otro, así que es una copia más, igual que la de Shopify.
- **Consumidor.** `ConnectionDeletedProcessor` (singleton) con `Consumer = "messaging.connection-deleted"` y una sola entrada en
  `eventNames`. Curva de reintento `[5 s, 30 s, 5 min]` y log en Error desde el intento 3, como `CitySynonymEventsProcessor.cs:21-24`.
  Un payload ilegible se loguea y se marca procesado sin efecto (mismo criterio).
- **El nombre y el payload son contrato con Integrations.** Messaging los declara en su Application
  (`MessagingConnectionEventNames.Deleted = "integrations.connection-deleted.v1"`) y lee el payload por nombre
  (`ConnectionDeletedPayloads.TryRead`: `tenantId`, `connectionId`). Mismo costo que `ShopifyWebhookTopics` y
  `geography.city_synonym.*`: dos copias que se cambian juntas, amarradas por pruebas (§10).
- **Sin filtro por proveedor.** El evento sale para toda conexión (`whatsapp-cloud`, `instagram`, Shopify, API externa). El consumidor no
  mira `providerKey`: busca conversaciones por `tenant_id` y `connection_id`, y si no hay, termina. Filtrar por proveedor obligaría a
  copiar las claves de proveedor de Integrations, sin ganar nada: `IX_conversations_connection_user` empieza por `connection_id`.

### 5.2 Referencias de nivel conexión (una vez por mensaje, idempotentes)

Antes de los lotes, en su propia transacción:

- `messaging.campaign_recipients`: `UPDATE … SET conversation_id = NULL, message_id = NULL WHERE tenant_id = @t AND campaign_id IN
  (SELECT id FROM messaging.campaigns WHERE tenant_id = @t AND connection_id = @c) AND (conversation_id IS NOT NULL OR message_id IS
  NOT NULL)`. Usa `IX_campaign_recipients_campaign_*`; `campaign_recipients` no tiene índice por `conversation_id`. Un destinatario
  sólo puede tener una conversación de la conexión de su campaña, así que el filtro por campaña es exacto. La campaña y la foto del envío
  (nombre, estado, `sent_at`) quedan (D-6).

### 5.3 Lote (una transacción por lote)

1. `SELECT id FROM messaging.conversations WHERE tenant_id = @t AND connection_id = @c ORDER BY id LIMIT @n FOR UPDATE SKIP LOCKED`.
   Lote de **100 conversaciones** (D-8).
2. Claves de medios: `SELECT m.id, mm.storage_key FROM messaging.messages m JOIN messaging.message_media mm ON mm.message_id = m.id
   WHERE m.conversation_id = ANY(@ids)`. La clave es `storage_key` si no es nula y, si lo es (copia pendiente o rendida),
   `MessagingMediaKeys.For(tenantId, messageId)`: borrar en R2 una clave que no existe no falla, y así se cubre una copia que llega
   tarde (§7.3). Se guardan en memoria; nunca se loguean.
3. `IConversationPurgeReferences.DeleteAsync(tenantId, conversationIds, ct)` — puerto en Messaging.Application, adaptador
   `ConversationPurgeReferences` en Bootstrapper (§5.4). Corre sobre la conexión del `MessagingDbContext`, dentro de la transacción.
4. `DELETE FROM messaging.automation_inbox WHERE tenant_id = @t AND conversation_id = ANY(@ids)`.
5. `DELETE FROM messaging.conversations WHERE tenant_id = @t AND id = ANY(@ids)`: la cascada se lleva `messages` y `message_media`.
6. Commit. Recién ahí, `IMessagingMediaStore.DeleteAsync` por cada clave del lote (§5.5).
7. Si el `SELECT` del paso 1 devolvió menos de `@n`, se cierra (§5.6).

`SKIP LOCKED` hace que dos réplicas (o un reclamo vencido que otra toma) procesen lotes distintos y no se esperen. Hoy hay una sola
réplica (`replicas-multiples-postergado`), pero el diseño no lo supone.

### 5.4 Automations y Pipeline: adaptador en Bootstrapper (D-3)

`ConversationPurgeReferences(MessagingDbContext messaging) : IConversationPurgeReferences`, en `src/Bootstrapper/`, con el mismo
criterio que `CustomerMergeReferences` (`CustomerMergeReferences.cs:7-27`): SQL crudo por nombre, sobre el contexto del dueño de la
transacción, todo valor como parámetro, filtrando siempre por `tenant_id`. En orden fijo:

| Sentencia | Por qué |
|---|---|
| `DELETE FROM automations.run_units WHERE tenant_id = @t AND run_id IN (SELECT id FROM automations.conversation_runs WHERE tenant_id = @t AND conversation_id = ANY(@ids))` | El detalle de la corrida no tiene sentido sin la corrida; `IX_run_units_tenant_run` |
| `DELETE FROM automations.conversation_runs WHERE tenant_id = @t AND conversation_id = ANY(@ids)` | Una corrida `Running`/`Waiting` de una conversación borrada ocuparía `IX_conversation_runs_live` y el worker de esperas la seguiría tocando; las terminadas guardan `variables` y `trace` con datos del contacto, y la corrida no se purga nunca. `IX_conversation_runs_tenant_conversation_started` |
| `DELETE FROM pipeline.triage_decisions WHERE tenant_id = @t AND conversation_id = ANY(@ids)` | Una decisión es «qué se hizo con este entrante de esta conversación»: sin la conversación no se puede mostrar ni chocar con nada. `IX_triage_decisions_conversation_inbound` empieza por `(tenant_id, conversation_id)` |
| `UPDATE pipeline.deals SET origin_conversation_id = NULL WHERE tenant_id = @t AND origin_conversation_id = ANY(@ids)` | El negocio **se conserva**: tiene cliente, responsable, monto, cotizaciones y su historial, y es dato comercial del tenant, no de la conexión. Sólo se suelta la referencia, que `DealDetailBuilder.cs:62` expone y la pantalla convertiría en un enlace roto. `source` queda `Conversation` (no hay CHECK que lo ate a la columna). **Sin `version++`**, como la fusión: un negocio abierto en otra pestaña no debe dar 412 por esto |

`automations.http_calls` **no se toca** (D-5): no tiene índice por `run_id` y su retención de 30 días la vacía sola
(`HttpCallLog.PurgeBeforeAsync`).

**Por qué no un consumidor por módulo.** Pipeline tiene inbox, pero `triage_decisions` y `deals` no tienen `connection_id`: necesitan los
ids de conversación, que dejan de existir cuando Messaging borra. Dependerían de correr antes que Messaging, y el outbox no ordena entre
consumidores. Automations, además, no tiene inbox. Con el adaptador, todo lo que apunta a una conversación desaparece en la misma
transacción que la conversación, o nada.

**El costo, el mismo de `CustomerMergeReferences`:** Bootstrapper conoce por nombre tablas de Automations y Pipeline. Renombrar una
columna compila y el lote responde `42703` en el worker (se reintenta para siempre, con log en Error). Lo amarran las pruebas de §10.

### 5.5 R2 después del commit (O4)

- Por cada clave del lote, `DeleteAsync` con hasta **3 intentos** en el proceso (espera corta: 0,5 s y 2 s). Un objeto que no existe no es
  error.
- Si después de eso falla, se loguea en Warning **la cantidad** de claves fallidas, el `tenantId` y el `connectionId`; nunca la clave
  ni el contenido. El objeto queda huérfano en el bucket privado: nadie lo puede pedir, porque `GET /messaging/media/{messageId}` lee
  la fila (`MessagingSupport.MediaUrl`), que ya no existe. Es el mismo criterio que `SendMediaMessage.DiscardAsync`
  (`SendMediaMessage.cs:325-337`).
- Un reintento del evento **no** recupera esas claves: las filas ya no están. Si el owner quiere que no quede ningún huérfano, ver
  DP-1 (cola durable).
- Las subidas de `messaging/staging/…` del asesor que no se confirmaron no se buscan: el lifecycle del prefijo las borra al día.

### 5.6 Cierre y auditoría (D-7)

Cuando un lote devuelve menos de `@n` conversaciones, en una transacción:

- Una sola entrada de auditoría, actor `System` (`Guid.Empty`, como `RecordBySystem`): acción nueva
  `messaging.connection.conversations_deleted`, `resource_type = "integration_connection"` (el mismo `ConnectionAuditActions.ResourceType`
  de Integrations, para que en la auditoría quede junto al `integrations.connection.deleted` de la persona), `resource_id =
  connectionId`, `outcome = "success"`, `changed_fields = []`. Método nuevo en `IMessagingAuditRecorder`:
  `RecordConnectionPurge(Guid tenantId, Guid connectionId, DateTimeOffset occurredAt)`.
- Quién borró la conexión ya lo dice la entrada de Integrations; el payload no trae actor y no se agrega.
- **No** una entrada por conversación: una conexión puede tener miles, y la auditoría no es un inventario.
- Las cantidades (conversaciones, mensajes, medios, claves de R2 fallidas) van al log en Information al cerrar, no a `changed_fields`,
  que en todo el repo lleva claves de campo.
- `processed_at` del inbox en el mismo `SaveChanges` que la auditoría: un mensaje cerrado nunca queda sin su entrada, ni al revés.

Si el reclamo vence a mitad de una conexión grande, el siguiente reclamo (de esta réplica o de otra) sigue desde donde quedó: los lotes
ya borrados no se vuelven a encontrar. Por eso la idempotencia no necesita guardar progreso.

## 6. Volumen

- Lote de 100 conversaciones: con el promedio local (379 conversaciones, 4.004 mensajes, 203 medios en la única conexión de `dev_qep`)
  son ~1.000 mensajes por transacción. Una conversación excepcional de 20.000 mensajes cabe igual en una transacción; si el `EXPLAIN`
  del plan muestra lo contrario, el lote baja por **mensajes**, no por conversaciones (D-8).
- Pausa de 200 ms entre lotes, para no acaparar el pool ni el WAL frente al tráfico de la bandeja.
- El borrado de R2 va por clave (no hay borrado por prefijo útil: la clave es `messaging/{tenant}/{message}`, sin conexión). Con 20
  borrados en paralelo por lote.
- Nada se materializa más allá del lote: los ids y las claves de 100 conversaciones.

## 7. Carreras y bordes

1. **Llega algo nuevo para la conexión borrada.** No crea conversación: el webhook descarta por `unknown-route`
   (`WebhookDeliveryProcessor.cs:117-121`) y los envíos no resuelven remitente. Una entrega guardada antes del borrado y procesada después
   pasa por el mismo `FindRouteAsync` al procesarse. Por eso el consumidor puede cerrar cuando ya no encuentra conversaciones.
2. **El bot está a mitad de una unidad.** Si el motor escribe un mensaje en una conversación ya borrada, `FK_messages_conversation` lo
   rechaza y la unidad falla; su fila de `automation_inbox` ya no existe, así que no se reintenta. Lo que **sí** puede pasar es que el
   motor confirme una corrida (sin FK) después del lote. Para cerrarlo, el lote toma el mismo candado por conversación que el motor
   (`pg_try_advisory_xact_lock`, CLAUDE.md, «Esperas con tope») y salta las ocupadas, que caen en el lote siguiente. **Verificar** la
   función de la clave del candado en `RunProcessor` antes de implementar; si no se puede compartir sin referenciar Automations, ver DP-6.
3. **La copia de un medio entrante termina después del lote.** `MediaCopyProcessor.CopyOneAsync` sube a R2 y después hace un `UPDATE`
   de `message_media` (`MediaCopyProcessor.cs:45-52`): con la fila borrada actualiza cero filas y el objeto queda huérfano. Se cambia
   para que, si el `UPDATE` afecta cero filas, borre la clave que acaba de subir (best effort, como `DiscardMismatchAsync`) (D-9).
4. **El evento se entrega dos veces o se reprocesa después de cerrado.** El inbox lo frena (`processed_at`). Si igual corre —un
   reclamo vencido—, no encuentra conversaciones y no escribe nada: la auditoría y el cierre se hacen sólo si el inbox todavía no
   estaba procesado (se relee bajo el reclamo), así que nunca quedan dos entradas de auditoría.
5. **Eventos viejos en el outbox.** Los `integrations.connection-deleted.v1` de conexiones borradas antes de esta feature **no** se
   procesan (D-12, cierra DP-2): la migración los marca procesados sin efecto. Quien borró una conexión lo hizo cuando borrar no tocaba
   las conversaciones y sin aviso en la pantalla; borrarle el historial ahora cambiaría la regla a sus espaldas, y es irreversible. Las
   conversaciones huérfanas viejas siguen como hoy; limpiarlas, si se quiere, es una operación aparte y deliberada por tenant, con el
   conteo previo de DP-2. En `dev_qep` no hay ningún evento de este tipo (la base se restauró de otro equipo).
6. **La bandeja abierta en otra pestaña.** La conversación desaparece al recargar; mientras tanto, abrirla da 404 del endpoint del hilo.
   No se empuja nada por tiempo real en v1 (DP-8).
7. **El reporte de mensajería** deja de contar esas conversaciones también en periodos pasados: es consecuencia de O1, no un bug. Se
   anota en el handoff.

## 8. Las otras tablas con `connection_id`

Después del borrado de §5 quedan filas que nombran la conexión borrada. Ninguna rompe nada que se haya verificado, pero tampoco sirven:

| Tabla | Qué pasa hoy si queda | Recomendación |
|---|---|---|
| `messaging.campaigns` (+ `campaign_recipients` por cascada) | Una campaña terminada queda como historial; una `Scheduled`/`Sending` falla al despachar sin remitente | D-13: conservar las terminadas (con §5.2), **cancelar** las `Scheduled`/`Sending` con `Campaign.Cancel` + `SaveCancelledAsync`, y dejar los `Draft` |
| `messaging.message_templates`, `messaging.template_mirror_syncs` | Espejo de plantillas de Meta de una conexión que ya no existe; nadie lo lee sin conexión | En alcance si el owner quiere: borrar por `(tenant_id, connection_id)` en el cierre. DP-4 |
| `messaging.connection_business_hours` | Asignación huérfana; ya se tolera: «para los horarios es una asignación huérfana, no una opción» (`IMessagingConnectionDirectory.cs:59-61`) | Borrarla en el cierre (es de Messaging y no tiene valor). DP-4 |
| `messaging.connection_send_budgets` | Contador sin conexión | Borrarlo en el cierre. DP-4 |
| `quotations.tenant_whatsapp_channels` | El canal de envío de cotizaciones apuntaría a una conexión borrada | DP-5: decide Quotations (otro consumidor o un adaptador más); fuera de este spec |
| `automations.chatbot_connections` | El chatbot sigue «asignado» a una conexión que no existe | DP-5: decide Automations; fuera de este spec |
| `shopify.stores`, `shopify.orders` | Sólo aplica a conexiones de Shopify, que no tienen conversaciones | Fuera de alcance |
| `automations.http_calls.connection_id` | Es la conexión de API externa; se va sola a los 30 días | Fuera de alcance |

## 9. Decisiones del spec (a ratificar)

- **D-1.** El evento es `integrations.connection-deleted.v1` (`ConnectionEvents.Deleted`), no `integrations.connection.deleted`, que es la
  acción de auditoría. El consumidor se suscribe al primero.
- **D-2.** El consumidor vive en Messaging.Infrastructure con inbox propio (`messaging.inbox_messages`, migración `AddMessagingInbox`),
  copia del patrón de Pipeline y Shopify.
- **D-3.** Automations y Pipeline se limpian por un puerto de Messaging con adaptador en Bootstrapper, en la misma transacción del lote,
  no con un consumidor por módulo (§5.4).
- **D-4.** Los negocios del embudo se conservan; sólo se suelta `origin_conversation_id`, sin subir `version`.
- **D-5.** `automations.http_calls` no se borra: lo vacía su retención.
- **D-6.** Las campañas no se borran en este spec; sus destinatarios pierden `conversation_id` y `message_id`.
- **D-7.** Auditoría: una entrada `System` por conexión al cerrar, `messaging.connection.conversations_deleted`; cantidades al log.
- **D-8.** Lotes de 100 conversaciones, `FOR UPDATE SKIP LOCKED`, una transacción por lote, pausa de 200 ms; si el `EXPLAIN` lo pide,
  el lote se mide en mensajes.
- **D-9.** `MediaCopyProcessor` borra lo que subió si su fila ya no existe.
- **D-10.** Sin filtro por `providerKey`; una conexión sin conversaciones cierra en el primer lote.
- **D-11.** Ningún endpoint, permiso, código de error ni status HTTP nuevo: `DELETE /connections/{id}` sigue respondiendo 204 en el acto y
  el borrado de conversaciones es asíncrono (segundos).
- **D-12.** Sin efecto retroactivo: `AddMessagingInbox` marca procesados los `connection-deleted.v1` que ya están en el outbox (§5.1,
  §7.5). Orden de despliegue: primero el aviso del diálogo de borrado en el frontend (DP-7), después el backend con el consumidor, para
  que nadie borre una conexión con la regla nueva sin saberlo. Decisión del owner, 2026-10-29.
- **D-13.** Campañas de la conexión borrada (cierra DP-3), en el cierre, antes de la auditoría de D-7:
  - `Completed`, `Failed` y `Cancelled` se conservan como historial; sus destinatarios ya perdieron `conversation_id`/`message_id` (§5.2).
    Un «Reintentar» posterior lo rechaza la API: `RetryCampaignHandler` valida la plantilla contra el catálogo, que lanza
    `messaging.connection_unavailable` para una conexión que no existe (`Campaigns.cs:244-246`).
  - `Scheduled` y `Sending` se cancelan por el mismo camino del usuario: `Campaign.Cancel(now)` + `ICampaignRepository.SaveCancelledAsync`
    (los pendientes pasan a `Skipped` con `campaign_cancelled`; lo que ya está en vuelo termina) y
    `RecordCampaign(tenantId, Guid.Empty, bySystem: true, CampaignCancelled, …)`, como hace `CampaignDispatchStore` con `CampaignFailed`.
    No se pasa por `CancelCampaignHandler`: exige `IExecutionContext`, permiso y `ExpectedVersion`, y aquí no hay persona.
  - `Draft` **no** se toca: el dominio no lo deja cancelar (`IsCancellable` = `Scheduled | Sending`) y no envía nada. Sigue editable, y
    `Update` deja cambiarle la conexión. Lanzarlo sin cambiarla responde `messaging.connection_unavailable`, por el mismo catálogo.
  - Un choque de versión (el worker movió la campaña entre la lectura y el guardado) se reintenta en el siguiente reclamo; el estado final
    es el mismo. Decisión del owner, 2026-10-29.

## 10. Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias — `tests/Modules/Messaging/Modules.Messaging.UnitTests`**

- `ConnectionDeletedPayloadsTests.ReadsTheIntegrationsPayload` — con el literal `{"tenantId":…,"connectionId":…,"providerKey":"whatsapp-cloud","occurredAt":…}`.
- `ConnectionDeletedPayloadsTests.AMalformedPayloadIsNotRead`.
- `ConnectionPurgeTests.MediaKeyFallsBackToTheDeterministicKeyWhenStorageKeyIsNull`.

**Integración — `tests/Modules/Messaging/Modules.Messaging.IntegrationTests` (Testcontainers)**

- `ConnectionDeletedProcessorTests.DeletesTheConversationsMessagesAndMediaOfTheConnection`
- `…ConversationsOfAnotherConnectionAndAnotherTenantSurvive`
- `…DataConsentsSurviveTheirConversation` — la fila de `customers.data_consents` sigue igual, byte por byte, con su `conversation_id`.
- `…RedeliveringTheEventIsANoOp` — idempotencia: segundo reclamo, cero efectos, una sola entrada de auditoría.
- `…AFailureAfterTheFirstBatchResumesOnTheNextClaim` — un doble de `IConversationPurgeReferences` que falla en el segundo lote; el reintento
  termina y el resultado es el mismo que sin falla.
- `…MediaIsDeletedOnlyAfterTheBatchCommits` — el doble de `IMessagingMediaStore` registra el orden; ninguna clave antes del commit.
- `…AnR2FailureLeavesTheRowsDeletedAndTheMessageProcessed`
- `…AConnectionWithoutConversationsIsProcessedAndAudited`
- `…CampaignRecipientsLoseTheirConversationAndMessage`
- `…ScheduledAndSendingCampaignsAreCancelledBySystem` (D-13) — `Cancelled`, pendientes `Skipped` con `campaign_cancelled`, una entrada
  `CampaignCancelled` con actor vacío y `System`.
- `…FinishedCampaignsAndDraftsAreKept` (D-13) — `Completed`/`Failed`/`Cancelled`/`Draft` siguen con su estado y su `version`.
- `…AutomationInboxRowsOfThePurgedConversationsAreDeleted`
- `MessagingInboxMigrationTests.EventsAlreadyInTheOutboxAreMarkedProcessedWithoutEffect` (D-12) — siembra en `platform.outbox_messages`
  un `connection-deleted.v1` y una conversación de esa conexión, migra hasta `AddMessagingInbox`, corre el procesador y comprueba que la
  conversación sigue y que el inbox tiene la fila con `processed_at`. La pareja: un evento escrito **después** de migrar sí borra.
- `…AMalformedPayloadIsMarkedProcessedWithoutEffect`
- `MediaCopyProcessorTests.AStoredCopyWhoseRowWasDeletedIsRemovedFromTheBucket` (D-9).

**Bootstrapper — `tests/Bootstrapper/Bootstrapper.UnitTests` y la prueba entre módulos**

- `ConversationPurgeReferencesTests` — réplicas mínimas de `conversation_runs`, `run_units`, `triage_decisions` y `deals`, como
  `CustomerMergeReferencesTests`; incluye `ALiveRunIsDeleted` y `TheDealSurvivesWithoutItsOriginConversation`.
- `ConversationPurgeAcrossModulesTests.TheRealAdapterRunsAgainstTheMigratedSchema` — resuelve el adaptador del contenedor y lo corre contra
  el esquema migrado de todos los módulos, como `CustomerMergeAcrossModulesApiTests`.
- `ConnectionDeletedEventParityTests.MessagingListensToTheEventIntegrationsPublishes` — `MessagingConnectionEventNames.Deleted ==
  ConnectionEvents.Deleted`. Bootstrapper ve los dos módulos (precedente `ShopifyWebhookTopicsParityTests`).
- El payload literal vive en dos pruebas que se cambian juntas: la de §unitarias de Messaging y una nueva
  `IntegrationsEventPublisherTests.TheConnectionDeletedPayloadMatchesTheLiteral` en Integrations (precedente `geography.city_synonym.*`).

**Extremo a extremo — `tests/Modules/Integrations/Modules.Integrations.IntegrationTests`**

- `ConnectionLifecycleApiTests.DeletingAConnectionPurgesItsInbox` — `DELETE` con `If-Match`, una pasada del procesador, cero conversaciones.

**ArchitectureTests**

- `MessagingLayerTests` ya prohíbe Integrations, Customers y Storage en toda capa (`NoLayerReferencesIntegrationsCustomersOrStorage`) y
  limita Application a Tenancy y Audit. Se agrega `[InlineData("Modules.Automations")]` y `[InlineData("Modules.Pipeline")]` a la
  prohibición de toda capa: correrla **primero** y verificar que está verde hoy (si no, el `InlineData` es un hallazgo, no un cambio).
- El adaptador va en Bootstrapper, que ya puede ver todos los módulos.

**Gotcha para el `CLAUDE.md` al cerrar:** «`ConversationPurgeReferences` borra en Automations y Pipeline por nombre», con sus pruebas,
como la entrada de `CustomerMergeReferences`.

## 11. `DECISIÓN-PENDIENTE`

- **DP-1.** ¿Cola durable para R2? Con O4 tal cual, un objeto cuyo borrado falla tres veces queda huérfano para siempre (la fila ya no
  existe para reintentar). Alternativa: insertar las claves en una tabla `messaging.media_deletions` en la misma transacción del lote y
  dejar que un worker las borre con reintentos. Costo: una tabla y un worker más. Recomendación: empezar sin cola (O4) y medir los Warning.
- **DP-2. Resuelta (2026-10-29, D-12): no se procesan.** La migración los marca procesados sin efecto. Las consultas quedan para el
  handoff, para saber cuántas conversaciones huérfanas viejas siguen ahí. Antes de desplegar, contar en producción:
  `SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'integrations.connection-deleted.v1'` y las conversaciones huérfanas
  (`messaging.conversations` sin fila en `integrations.connections`).
- **DP-3. Resuelta (2026-10-29, D-13):** se conservan las terminadas, se cancelan `Scheduled`/`Sending` y los `Draft` quedan.
- **DP-4.** ¿Entran en este spec el espejo de plantillas, `template_mirror_syncs`, `connection_business_hours` y
  `connection_send_budgets`? Son de Messaging y se borrarían en el cierre por `(tenant_id, connection_id)`.
- **DP-5.** `quotations.tenant_whatsapp_channels` y `automations.chatbot_connections`: ¿cada módulo consume el evento, o se suman al
  adaptador de Bootstrapper?
- **DP-6.** Si el candado por conversación del motor (§7.2) no se puede compartir sin referenciar Automations, ¿se acepta el riesgo de una
  corrida huérfana confirmada justo después del lote, o se agrega al adaptador de Bootstrapper un método para tomar el candado?
- **DP-7.** Frontend (slice propio en `qep-frontend`): el diálogo de borrado debería decir que se borran las conversaciones y sus
  adjuntos. ¿Con la cantidad de conversaciones? Eso pide un dato nuevo en la respuesta de la conexión o un endpoint de conteo.
- **DP-8.** ¿Avisar por tiempo real a las bandejas abiertas, o basta con que desaparezcan al recargar?
