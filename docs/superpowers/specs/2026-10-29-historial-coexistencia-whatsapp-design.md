# Historial de WhatsApp al conectar por coexistencia

**Fecha:** 2026-10-29
**Módulos:** Integrations (pedido de sincronización tras la conexión), Messaging (recepción de `history`, `smb_message_echoes` y
`smb_app_state_sync`, estado de la sincronización), Customers (cliente incompleto por teléfono), Bootstrapper (adaptadores).
**Repos:** `qep-backend`. Lo que ve el usuario en `qep-frontend` es un slice aparte (§10).
**Addendum 2026-10-30 (§12):** se repone como temporal el override por WABA y cambia el orden de la puesta en marcha.
**Estado:** borrador para revisión. Las reglas de §2 son del owner (conversación del 2026-10-29). Las que toma el spec están en
§9 (D-1…), a ratificar. Lo que no se pudo decidir con el código ni con la documentación está en §11 como `DECISIÓN-PENDIENTE`.

## 1. Contexto

Un negocio que usa la app WhatsApp Business puede conectar el mismo número a la Cloud API sin dejar la app (coexistencia). QEP ya
soporta esa conexión: `CompleteWhatsAppSignupHandler` acepta el evento `FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING`, no registra el
número y elige el único libre de la WABA (`CompleteWhatsAppSignup.cs:27,176,249-278`). Lo que no hace es traer lo que el negocio
ya habló en la app: el asesor abre la bandeja y la encuentra vacía.

Meta permite traer ese historial, con estas reglas (documentación de Meta, «Onboarding WhatsApp Business app users», revisada el
2026-10-29):

| Regla de Meta | Consecuencia |
|---|---|
| `POST /{phone_number_id}/smb_app_data` con `sync_type` = `history` o `smb_app_state_sync` (contactos). Devuelve un `request_id` que sólo dice que se aceptó el pedido | El pedido no confirma que el negocio haya compartido nada |
| Cada tipo se pide **una sola vez** y dentro de las **24 h** siguientes a la conexión | Si falla o se vence, la única salida es desconectar y repetir el Embedded Signup |
| Hasta 180 días, en fases: 0 (día 0–1), 1 (día 1–90), 2 (día 90–180). Cada parte trae `phase`, `chunk_order` y `progress` (0–100) | Las partes llegan desordenadas; una fase no está completa por haber llegado una parte suya |
| Cada hilo (`threads[].id`) es el **teléfono** del cliente, no el BSUID | El emparejamiento con la bandeja es por teléfono |
| Los archivos traen id de media sólo los de los últimos 14 días; llegan como `media_placeholder` y el contenido en un webhook aparte. Los grupos no se sincronizan | |
| Si el negocio no acepta compartir el historial, llega un `history` con el error `2593109` | |
| Los mensajes que el negocio manda desde la app después de conectar llegan por `smb_message_echoes` | Sin ellos, el hilo en QEP queda incompleto |
| Los mensajes del historial y los que manda la app **no abren ni extienden** la ventana de 24 h de la Cloud API (Telnyx, EngageLab, TimelinesAI) | Una conversación importada se contesta con plantilla hasta que el cliente vuelva a escribir |

**La app de Meta es la de lulochat y pasa a QEP.** El owner crea en QEP las conexiones de Clasic y Transportes CI con los datos de
Mongo (sin Embedded Signup) y, con esas conexiones ya creadas, cambia la URL del webhook de la app a QEP. Desde ahí todo lo que
manda Meta llega a QEP por la URL de la app: no hay redirección por WABA ni reenvío desde lulochat.

Estado del código, verificado el 2026-10-29:

| Hecho | Dónde |
|---|---|
| Los campos que entiende el parser son `messages`, `message_template_status_update`, `account_update` y `user_id_update`. Cualquier otro termina en `UnknownChange`, se registra en el log y la entrega queda **procesada**: hoy el historial se perdería sin error | `WebhookPayloadParser.cs:54-61`, `WebhookDeliveryProcessor.cs:96-98` |
| Un mensaje sin `from_user_id` se descarta | `WebhookPayloadParser.cs:106-109,142` |
| La conversación se identifica por `(connection_id, user_id)`. Existe una fila «legada» por `(connection_id, wa_id)` con `user_id NULL`, y el primer mensaje en vivo con BSUID y el mismo `wa_id` la **adopta** | `MessagingDbContext.cs:249-252`, `InboundIngestion.ResolveConversationAsync` (`InboundIngestion.cs:249-297`) |
| `ConversationOpener.OpenByPhoneAsync` abre o encuentra una conversación por teléfono (hoy sólo la usa la cotización por plantilla) | `ConversationOpener.cs:87-143` |
| Un entrante suma `unread_count`, reabre la conversación, mueve `last_inbound_at`, emite `Reopened` y otros eventos, avisa en tiempo real, puede marcar la baja de marketing y puede encolar al bot | `InboundIngestion.cs:131-229`, `WebhookDeliveryProcessor.cs:158-162` |
| El eco de Instagram ya inserta un saliente sin esos efectos (sin no leídos, sin ventana, sin bot) | `InboundIngestion.IngestEchoAsync` (`InboundIngestion.cs:47-95`) |
| Los mensajes se deduplican por `IX_messages_connection_wamid` | `MessagingDbContext.cs:335-336` |
| Con la conexión `Paused` o el módulo apagado, lo que entra se descarta | `WebhookDeliveryProcessor.cs:117-129` |
| La ventana es `LastInboundAt + WindowLength` | `Conversation.cs:152-154` |
| `IMessagingCustomerDirectory.EnsureAsync` exige BSUID (`MessagingContact.UserId` no es nulable). `CustomerWhatsAppDirectory` busca por BSUID, luego por teléfono, y si no encuentra crea un incompleto **con** BSUID | `IMessagingCustomerDirectory.cs:8-9`, `CustomerWhatsAppDirectory.cs:22-69` |
| La conexión no tiene dónde guardar el estado de una sincronización (`ConnectionStatus` es `Active`, `Paused` o `NeedsAttention`) | `IntegrationConnection.cs:3-8` |

## 2. Reglas del owner

1. **Es una función del producto:** cualquier tenant que conecte por coexistencia trae su historial. El primer uso es el número de
   QCode en el tenant del dueño de la plataforma.
2. **Se trae todo lo que Meta entrega**, hasta 180 días, sin recorte.
3. **El historial llena los hilos sin generar trabajo:** no dispara el bot, no crea negocios en el embudo y no asigna asesor.
4. **Las conversaciones importadas entran cerradas**, salvo las que cumplen las dos condiciones: el último mensaje es **del
   contacto** y llegó en las últimas 24 h antes de la conexión. Esas entran **abiertas**.
5. **Una conversación abierta del historial se contesta con plantilla.** El historial no abre la ventana (§1). Cuando el cliente
   vuelva a escribir, se abre la ventana normal.
6. **Los contactos de `smb_app_state_sync` crean clientes incompletos en QEP**, tengan o no conversación en el historial.
7. **Sin redirección por WABA.** La URL del webhook de la app pasa a QEP una vez creadas en QEP las conexiones de Clasic y
   Transportes CI. El número de QCode se conecta por coexistencia **después** de ese cambio y después de desplegar este spec. El
   owner renombra la app a QEP; el `AppId` y el `AppSecret` no cambian, así que `Meta:App:*` no se toca.
8. **Hoy no hay conexiones reales de tenants en QEP** (owner, 2026-10-29), y producción ya usa `Meta:App:*` de esa app.

## 3. Conexión: pedido de sincronización

Al completar una conexión **en coexistencia**, la suscripción y la creación de la conexión y su ruta siguen como hoy. Lo nuevo va
**después del commit**: pedir `smb_app_state_sync` y `history` a `smb_app_data`, y guardar cada `request_id`. Va después del commit
porque una entrega que llega sin ruta se descarta como `unknown-route` y se marca procesada: si el historial llegara antes de que
exista la ruta, se perdería.

El pedido no puede depender de que el proceso siga vivo después del commit: si el pod cae entre el commit y el pedido, el pedido
tiene que salir igual (D-3). Si Graph rechaza el pedido, la sincronización queda en `Failed` con el código, y el tenant puede
reintentarla mientras no se venza el plazo de 24 h (§5, D-9).

## 4. Recepción

El parser reconoce tres campos nuevos. Ninguno pasa por el camino de un entrante normal (§1): no suma no leídos, no reabre, no
mueve `last_inbound_at`, no emite `Reopened`, no avisa en tiempo real, no marca bajas de marketing, no hereda asesor y no encola al
bot.

### 4.1 `history`

- **Conversación.** Se busca o se crea por teléfono (`threads[].id`), como `OpenByPhoneAsync`: primero la conversación con BSUID
  de ese `wa_id`, luego la legada; si no hay, una legada nueva (`user_id NULL`). Cuando el cliente escriba en vivo, la adopción que
  ya existe la une a su BSUID sin trabajo extra.
- **Mensaje.** Se inserta con `wamid` = `id`, `occurred_at` = `timestamp` y dirección según quién lo mandó (el campo `to` sólo
  viene en los del negocio). Una repetición se descarta por `IX_messages_connection_wamid`, igual que un entrante.
- **Resumen de la conversación.** `last_message_*` y `last_activity_at` avanzan por `occurred_at`, igual que hoy (se queda el más
  reciente), para que la bandeja la ordene donde corresponde. `last_inbound_at` **no** se toca (regla 5).
- **Abierta o cerrada.** Una conversación **creada por la importación** queda abierta si su último mensaje, contando todo lo
  importado, es del contacto y su `occurred_at` es igual o posterior a la hora de la conexión menos 24 h. Si no, queda cerrada. Como
  las partes llegan desordenadas, la regla se reevalúa con cada parte que toca la conversación. Una conversación que **ya existía**,
  o que recibió un mensaje en vivo durante la importación, no la cambia la importación: manda el flujo en vivo.
- **No leídos de una abierta.** Los mensajes del contacto que siguen al último mensaje del negocio (D-6).
- **Progreso.** Cada parte actualiza el estado de la sincronización: fase, `chunk_order` y `progress`. `progress = 100` la da por
  terminada.
- **Rechazo.** Un `history` con el error `2593109` deja la sincronización en `Declined`.
- **Archivos.** Un `media_placeholder` crea el mensaje con su tipo y sin contenido. Si después llega el archivo con un id de media,
  entra a la cola de `message_media` y lo baja el worker de siempre. Si nunca llega (más de 14 días), el mensaje queda como «archivo
  no disponible». La forma exacta del webhook que trae el archivo está en V-2.
- **Cliente.** Se asegura un cliente incompleto por teléfono (§4.3) y se liga a la conversación.

### 4.2 `smb_message_echoes`

Un mensaje que el negocio mandó desde la app se inserta como saliente, con estado `Sent`, en la conversación de ese teléfono (misma
búsqueda que §4.1), igual que `IngestEchoAsync` de Instagram. Actualiza el resumen de la conversación y no toca la ventana. Esto vale
para toda conexión en coexistencia, mientras exista, no sólo durante la importación.

### 4.3 `smb_app_state_sync`

Cada contacto crea o actualiza un cliente incompleto por teléfono, sin BSUID: se busca un cliente con ese teléfono y, si no existe,
se crea con el nombre del contacto. Hace falta un camino nuevo en Customers, porque el actual exige BSUID (§1). Cuando el cliente
escriba en vivo, el camino actual lo encuentra por teléfono y le adjunta el BSUID. Un contacto que el negocio borra en la app no
borra ni toca al cliente en QEP (D-7).

### 4.4 Conexión pausada o módulo apagado

`history`, `smb_message_echoes` y `smb_app_state_sync` se procesan aunque la conexión esté `Paused` o Messaging esté apagado para el
tenant. El historial no se puede volver a pedir, y estos tres campos no envían nada ni disparan nada (D-5). Lo de `messages` sigue
la regla de hoy.

## 5. Estado de la sincronización

Una fila por conexión en coexistencia, en Messaging (D-4), con:

- `connection_id`, `tenant_id`, la hora de la conexión (de ahí salen el plazo de 24 h y la regla de abiertas);
- por tipo (`history`, `smb_app_state_sync`): `request_id`, pedido en, último código de error;
- el progreso del historial: fase y `progress` más altos vistos;
- un estado: `Pending` (falta pedir), `Requested`, `InProgress`, `Completed`, `Declined`, `Failed` y `Expired` (pasó el plazo sin
  completar el pedido).

El tenant lo ve en la pantalla de la conexión y, mientras no se venza el plazo, puede reintentar un pedido que quedó en `Failed` o
`Pending`. Vencido el plazo, la pantalla dice que hay que desconectar y volver a conectar. Leerlo exige el mismo permiso que ver la
conexión; reintentar, el de editarla.

## 6. Configuración y despliegue

- **Sin configuración nueva en QEP.** `Meta:App:*` ya es el de la app de lulochat (regla 7). *Superado por §12: hay una clave nueva, temporal.*
- **Panel de la app de Meta:** suscribir `history`, `smb_message_echoes` y `smb_app_state_sync`.
- **Orden de la puesta en marcha:** *reemplazado por §12: el número de QCode se conecta con la clave desplegada, sin mover la URL de
  la app.* Queda como el plan original: (1) conexiones de Clasic y Transportes CI creadas en QEP; (2) URL de la app → QEP; (3) este
  spec desplegado; (4) recién entonces, el número de QCode por coexistencia.
- **Límite de tamaño:** revisar `Messaging:Webhook:MaxBodyBytes` contra el tamaño real de una parte de historial (V-3).
- **Orden:** este backend se despliega **antes** de que cualquier tenant conecte por coexistencia. Una conexión hecha antes pide el
  historial, QEP lo descarta como `UnknownChange` y no hay segunda oportunidad.

## 7. Qué no cambia

- El flujo de `messages` en vivo, la adopción de conversaciones legadas, la ventana y el bot.
- La suscripción de la conexión (`SubscribeAppAsync` sin cuerpo): no hay redirección por WABA. *Superado por §12.*

## 8. Pruebas

TDD, RED antes que GREEN. Como mínimo:

- El parser reconoce los tres campos nuevos con cuerpos tomados de la documentación de Meta (y de un webhook real cuando exista,
  V-1).
- Importar un hilo crea una conversación legada cerrada, con sus mensajes en orden, sin no leídos, sin `last_inbound_at`, sin fila
  en `automation_inbox` y sin eventos `Reopened`.
- La regla de abiertas: último mensaje del contacto dentro de las 24 h → abierta con sus no leídos; del negocio → cerrada; del
  contacto pero de hace más de 24 h → cerrada; partes que llegan desordenadas dan el mismo resultado.
- Un mensaje en vivo con BSUID adopta la conversación importada, y la importación ya no la cambia.
- Una parte repetida no duplica mensajes.
- `2593109` deja la sincronización en `Declined`.
- Un eco de la app se inserta como saliente sin tocar la ventana.
- Un contacto crea un cliente incompleto sin BSUID; uno que ya existe por teléfono no se duplica; el BSUID se le adjunta cuando
  escribe en vivo.
- El pedido a `smb_app_data` sale después del commit y sobrevive a una caída entre los dos.
- Con la conexión `Paused`, el historial se procesa igual.
- Prueba de capas: nada de lo nuevo rompe `MessagingLayerTests`, `IntegrationsLayerTests` ni `CustomersLayerTests`.

## 9. Decisiones del spec (a ratificar)

- **D-1, D-2.** *Retiradas el 2026-10-29* (redirección por WABA y su URL de configuración): decisión del owner, QEP recibe por el
  URL de la app, que pasa a QEP (regla 7). *Repuestas como temporales el 2026-10-30: §12.*
- **D-3.** El pedido a `smb_app_data` tiene que sobrevivir a una caída después del commit. El mecanismo (un evento de outbox que
  consume Messaging, o una fila `Pending` que un worker reclama) lo elige el constructor.
- **D-4.** El estado vive en Messaging, que es quien recibe los webhooks. Para pedir, Messaging obtiene el token por su puerto hacia
  la conexión, como ya lo hace `MediaTransfer`.
- **D-5.** Los tres campos nuevos se procesan con la conexión pausada o el módulo apagado.
- **D-6.** Los no leídos de una conversación abierta del historial son los mensajes del contacto posteriores al último del negocio.
- **D-7.** Un contacto borrado en la app no borra ni toca al cliente en QEP.
- **D-8.** *Retirada el 2026-10-29*: sin redirección no hay qué conservar al suscribir.
- **D-9.** El reintento de §5 sólo aplica a un pedido que Graph **rechazó** (no devolvió `request_id`). Se asume que un pedido
  rechazado no gasta la única oportunidad (V-6). Uno aceptado no se reintenta nunca.
- **D-10.** El cliente de una conversación importada se resuelve con la misma regla que hoy usa el camino por teléfono (el cliente
  más antiguo con ese teléfono, `FindOldestByPhoneE164Async`). La importación no compara BSUIDs. Si después el BSUID en vivo
  pertenece a otro cliente, manda el flujo en vivo, como pasa hoy con una conversación legada.
- **D-11, D-12.** *Retiradas el 2026-10-29*: sin redirección por WABA no hay tráfico que devolverle a lulochat ni WABA que cortarle.

## 10. Frontend (slice aparte)

- Estado y progreso de la sincronización en la pantalla de la conexión, con el reintento y el aviso de «vencido: reconecta».
- Una conversación abierta del historial muestra la ventana cerrada y ofrece plantilla. Debería salir sola del cálculo de la
  ventana que ya existe, porque `last_inbound_at` queda vacío; hay que verificarlo.
- «Archivo no disponible» para un mensaje con archivo que nunca llegó.

## 11. Pendientes

- **V-1.** Forma real de los tres webhooks contra Meta, en especial si `history` trae nombre o `username` del contacto.
- **V-2.** Forma del webhook que trae el archivo de un `media_placeholder`.
- **V-3.** Tamaño de una parte de historial frente a `Messaging:Webhook:MaxBodyBytes`.
- **V-4.** Que la ventana de 24 h no la abren los mensajes del historial. Hoy está respaldado por documentación de terceros, no de
  Meta.
- **V-5.** Que `TriageRules.IsPending` con `last_inbound_at` vacío no deje fuera del triage a una conversación abierta del
  historial que sí debería verse.
- **V-6.** Que un pedido a `smb_app_data` rechazado por Graph no gaste la única oportunidad. Si la gasta, `Failed` es final, como
  `Declined`, y el reintento se quita.
- **V-7.** *Retirada el 2026-10-29*: no hay reenvío desde lulochat.
- **Fuera de alcance:** volver a sincronizar después del plazo, los grupos, Instagram, y la migración de Clasic y Transportes CI
  desde lulochat: no pasa por el Embedded Signup sino por la migración de datos desde Mongo (token y datos de la conexión;
  `qep/lulo-migrator`), así que esas conexiones no tocan nada de este spec.

## 12. Addendum 2026-10-30: override temporal por WABA

**Decisión del owner (2026-10-30).** Se repone la redirección por WABA que §2.7 y D-1/D-2 habían retirado, **como medida temporal**.
QEP y lulo chat comparten la app de Meta y la URL de la app sigue en lulo. El owner quiere conectar el número de QCode por coexistencia
con Embedded Signup en producción y recibir sus webhooks en QEP, mientras los demás números de la app siguen yendo a lulo, hasta que
quede satisfecho y mueva la URL de la app.

- **Mecanismo.** `Meta:App:WebhookOverrideCallbackUrl` (variable `META_WEBHOOK_OVERRIDE_CALLBACK_URL`, ConfigMap). Con valor, el
  `POST /{waba_id}/subscribed_apps` de `SubscribeAppAsync` lleva `override_callback_uri` y `verify_token` en un cuerpo JSON. Vacía, o con
  el token del pipeline sin resolver en `Production`, la llamada sale sin cuerpo, como antes. Es por WABA, no por número. Reemplaza lo
  que dice §7 sobre la suscripción.
- **Rechazo.** Si Meta rechaza la suscripción, el signup falla con `integrations.whatsapp.registration_failed`, sin conexión y sin
  evento. No hay respaldo sin override: el historial se pide una vez, y le llegaría a lulo.
- **Orden garantizado por el código.** `subscribed_apps` corre antes del commit y un fallo lanza. El evento que crea la fila de
  `history_syncs` se publica en la transacción de después, así que `smb_app_data` no puede salir sin la suscripción ya aceptada.
- **Orden de la puesta en marcha (reemplaza el de §6 para el número de QCode).** (1) Este spec desplegado, con la clave y la variable
  en `Backend-prod`; (2) campos `history`, `smb_message_echoes` y `smb_app_state_sync` suscritos en el panel de la app; (3) el número de
  QCode por coexistencia. **No hace falta mover la URL de la app.** Las conexiones de Clasic y Transportes CI dejan de ser
  prerrequisito de este número, y siguen el camino de §1 cuando el owner mueva la URL.
- **Límite de Meta (documentación revisada el 2026-10-30).** El override cubre `messages`, `history`, `smb_message_echoes` y
  `smb_app_state_sync`, pero los webhooks de plantillas y de cuenta (`message_template_status_update`, `account_update`…) van siempre a
  la URL de la app: mientras dure, le llegan a lulo.
- **Retiro.** Se mueve la URL de la app, se repite la suscripción sin cuerpo en la WABA (Meta lo documenta como la forma de borrar el
  override) y se quita la clave. Quitar la clave no limpia la WABA. Una conexión creada por `lulo-migrator` no se suscribe: un número
  migrado así necesita la llamada manual del README, § Historial de coexistencia.
