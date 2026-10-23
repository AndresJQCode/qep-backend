# Automatizaciones: constructor de chatbots (motor, editor, bloque HTTP)

- **Fecha:** 2026-10-21.
- **Módulos:** `Automations` (nuevo), `Messaging` (salida interactiva, autoría del bot, cola de entrantes),
  `Integrations` (proveedor «API externa»), `Tenancy` y `Authorization` (módulo y permisos). Frontend en
  `qep-frontend`. Backend de Clasic (`calentadores-classic-backend`) como consumidor externo.
- **Estado:** borrador para aprobación del owner. Es el **spec 4** del roadmap de
  [`2026-10-21-asistentes-investigacion.md`](2026-10-21-asistentes-investigacion.md) §7, y absorbe la parte del
  spec 2 (interactivos salientes) y del spec 3 (conexión con clave de API) que el motor necesita. Lo que queda de
  esos dos (compositor de interactivos para humanos, envío proactivo) sigue siendo trabajo aparte.
- **Es un delta sobre:** [`2026-10-21-mensajeria-asistente-y-horarios-design.md`](2026-10-21-mensajeria-asistente-y-horarios-design.md)
  (spec 1): estado de automatización en la conversación, `Conversation.StartAutomation`, motivo `Handoff`, puerto
  `IBusinessHours`. Nada de eso se rediseña.
- **Concepto y diseño visual:** investigación §3 y §5; editor rediseñado y aprobado el 2026-10-21
  (canvas https://claude.ai/artifact/VZK9bR8vxufo3obdsB8d7N, cuatro artboards); diagrama del camino actual y
  del camino con QEP (https://claude.ai/artifact/H3tPtcFCoLzYhU9sEBHiZg).
- **Verificado contra el código el 2026-10-21:** `qep-backend` en `develop`, `qep-frontend` en `develop`
  (maqueta del módulo en `e2003638`), `calentadores-classic-backend` en `main` (`f8d58fe`),
  `lulo-chat-backend` en `main`. Donde este documento y el código discrepen, gana el código.

## 1. Contexto y alcance

Hoy el bot de Clasic vive en su propio backend: lulo-chat recibe el webhook de Meta, reenvía el body a
`api-clasic.qcode.co/whatsapp-bot/webhook` si el mensaje va al número de Clasic, tiene chatbot y no tiene
asesor, y el bot responde por `POST /clasic` de lulo-chat con una clave quemada. Clasic va a pasar a QEP, y el
owner decidió (investigación §6) que el bot se construye **en QEP, dinámico por tenant**, con el flujo armado en
un editor React Flow y los datos del negocio consultados por HTTP a la API de Clasic.

**Meta de la primera entrega (decisión del owner, 2026-10-21):** un chatbot armado en el editor **contesta de
verdad en WhatsApp**, y los bloques que entran son **los que necesita el flujo de Clasic** (§Anexo B). Todo lo
demás del concepto queda para una entrega siguiente.

**Dentro:**

- Módulo `Automations`: agregado Chatbot con borrador y versiones publicadas, conexiones que atiende, disparador,
  plantillas públicas y del tenant, validación, publicación, pausa.
- Motor: cola de entrantes por conversación, ejecución del flujo publicado, variables, intentos, reinicio,
  traspaso a asesor (`Handoff`) y resolución; «paso actual» visible en la bandeja.
- Bloques: Inicio, Mensaje (con botón de URL opcional), Pregunta abierta, Botones, Lista (fijas y dinámicas),
  Pedir adjunto, Condición, Horario de atención, Llamada HTTP, Pasar a un asesor, Resolver.
- Messaging: envío interactivo de Meta (`button`, `list`, `cta_url`), autoría «chatbot» en los mensajes, salida
  del bot sin asignación, fila de cola en la ingesta.
- Integrations: proveedor «API externa» (`baseUrl`, header y clave cifrada), probar conexión, validación de URL.
- Simulador contra el borrador, con el paso actual marcado en el lienzo.
- Frontend: el módulo deja de ser maqueta, el editor se alinea al canvas aprobado, la bandeja muestra la autoría
  del bot y el paso.
- Clasic: guard dual (JWT o clave de API), filtros `phone` y `document` en `GET /customers`, sin usuario bot (D-K11;
  corregido en el plan 4, 2026-10-23).

**Fuera** (§13): Instagram en el motor, los bloques Plantilla, Imagen o archivo, Esperar, Etiquetar cliente,
Completar ficha, Negocio e Ir a otro flujo; compositor de interactivos para asesores; envío proactivo y aviso de
visita; IA; bots creados por el operador; migración de lulo-chat.

## 2. Decisiones

### 2.1 Chatbot y editor

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-C1 | **Un chatbot tiene un borrador y N versiones publicadas.** El borrador es un documento (`flow` + `layout`) en el agregado; publicar lo copia a `chatbot_versions` con número correlativo y la conversación en curso **termina con la versión con que empezó** (la corrida guarda `chatbot_version_id`). | Patrón 4 de la investigación. No hay precedente de doble versión en el repo; el precedente de «documento entero en jsonb» es `BusinessHoursSchedule`. |
| D-C2 | **El backend es dueño del modelo del flujo; el `layout` (posiciones) viaja aparte y no se valida.** El `flow` se valida en el servidor en cada guardado y en publicar; el cliente dibuja lo que el servidor le devuelve. | Decisión del owner (investigación §6). Un `flow` roto nunca se publica; un `layout` roto sólo desordena el lienzo. |
| D-C3 | **Guardar ≠ publicar.** `PUT /chatbots/{id}/draft` guarda siempre (aunque tenga problemas) y responde la lista de problemas; `POST …/publish` responde 422 `automations.chatbot.invalid` con la misma lista si hay **errores**. Los **avisos** no bloquean. | Mockup aprobado (panel de validación): «No se puede publicar con problemas; los avisos sí dejan publicar». Autoguardar no puede fallar por un flujo a medias. |
| D-C4 | **Una conexión tiene un solo chatbot activo** (índice parcial único en `chatbot_connections` por `(tenant_id, connection_id) WHERE active`). Activar un chatbot en una conexión ocupada responde 409 `automations.chatbot.connection_taken` con el nombre del que la ocupa. | Patrón 7 (Kommo): dos bots en la misma conversación se pisan. El formulario ya deshabilita la fila con «la atiende X». |
| D-C5 | **Estados del chatbot:** `Draft` (nunca publicado), `Active` (publicado y atendiendo), `Paused` (publicado, no entra a conversaciones nuevas; las corridas en curso terminan). Pausar no toca las corridas. | Lo que la lista ya pinta. Cortar una corrida a mitad deja al cliente hablando solo. |
| D-C6 | **Disparador** por chatbot: `NewConversation` (toda conversación nueva o reabierta sin asesor), `Keyword` (palabra exacta, sin distinguir mayúsculas ni tildes, lista ≤ 10), `OutsideHours` (sólo si `IBusinessHours` dice cerrado). Se evalúa al llegar un entrante a una conversación sin corrida y en `None`. | Concepto §3 «Nuevo asistente». D-A5 del spec 1: una reabierta queda en `None` y el motor decide. |
| D-C7 | **Palabra de reinicio y tope de intentos son del chatbot**, no de cada bloque: `restartKeyword` (default «reiniciar») y `maxAttempts` (default 3, 1..5). Un bloque de pregunta puede sobrescribir sus intentos. | Es lo que hace el bot de Clasic hoy (`whatsapp-bot.service.ts`: «reiniciar» o 3 intentos → conversación nueva). |
| D-C8 | **Plantillas del sistema** viajan como JSON embebido en `Modules.Automations.Infrastructure` (`Seed/Templates/*.json`), cargado al arrancar como los seeds de Geografía y Catálogo; **plantillas del tenant** en `automations.chatbot_templates`. Instalar una crea un borrador copia con los parámetros aplicados; nada queda enlazado. | Investigación §5. La nota de investigación decía «como los catálogos de proveedores», pero ese catálogo es C# estático; el precedente real de archivos en el repo es `GeographySeeder`/`CatalogSeeder`. |
| D-C9 | **Importar y exportar el flujo** como JSON (`GET …/export`, `POST /chatbots/import`), con permiso `manage`. | Mi decisión: es la forma de cargar el flujo de Clasic (Anexo B) en el tenant sin armarlo a mano, de llevarlo entre ambientes y de fijarlo en las pruebas. |
| D-C10 | **Permiso propio del módulo:** `automations.chatbot.read` y `automations.chatbot.manage`, **core** (`RequiredModules: []`) con `TenantModuleGuard` en cada handler, como Pipeline y Messaging. Rol `admin` tiene los dos; `advisor`, `billing` y `cashier` ninguno. | Decisión del owner (2026-10-21): el permiso es del módulo, no de Mensajería. Core por la máscara de entitlements (`PipelinePermissions.cs:3-8`): con `[module]` el 403 diría `authorization.denied` en vez de `tenancy.module_not_enabled`. Que el asesor no vea el módulo es mi decisión: un bot habla por la empresa. |
| D-C11 | **Módulo por tenant `automations`**, último de `All`, depende de `messaging`, fuera de `DefaultForNewTenants`. | Como `pipeline` y `messaging`: se prende por tenant. |
| D-C12 | **Nombre único por tenant** (≤ 80, `lower(name)`), 409 `automations.chatbot.name_taken`; duplicar numera «(copia)», «(copia 2)». | D-H14 del spec 1. |
| D-C13 | **La conexión se ocupa al publicar y se conserva en `Paused`.** En `Draft` elegir una conexión no bloquea a nadie; `POST …/publish` pone `active = true` en las filas del chatbot y responde 409 `connection_taken` si otra publicada (activa o pausada) la tiene. Pausar no libera; borrar sí. | Mi decisión (chequeo del constructor). Un borrador no atiende a nadie; una pausa es temporal y el admin espera volver sin perder su número. |
| D-C14 | **Parámetros de plantilla:** se referencian como `{param.<key>}` **sólo en campos de texto** del flujo (textos, etiquetas, URL del botón, palabras clave) y se sustituyen al instalar. Una plantilla nunca referencia conexiones: sus bloques HTTP vienen con `connectionId: null` y Validar los marca `http.connection_missing` hasta que el admin elija la suya. | Mi decisión: una sola sintaxis, resuelta una vez, sin dejar rastros en el flujo instalado. |
| D-C15 | **Importar admite `connectionMap`** (`{ "<id origen>": "<id destino>" }`) para reapuntar los bloques HTTP; los ids sin mapa quedan tal cual y Validar los marca. `GET …/export` incluye `externalApis: [{ id, name }]` para que quien importa sepa qué mapear. | Mi decisión: el flujo de Clasic se arma en un ambiente y se carga en otro; los ids de conexión no viajan. |

### 2.2 Motor

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-M1 | **El motor no corre dentro del procesador del webhook.** La ingesta deja una fila en `messaging.automation_inbox` **en su misma transacción**, sólo si la conversación quedó sin asesor y en `None` o `Active`; un worker del módulo Automations la reclama y la procesa **con bloqueo por conversación y en orden**. | `WebhookDeliveryWorker` no serializa por conversación, su lease es de 10 s y, tras un reintento, `IngestAsync` devuelve `null` para el mismo wamid: engancharse ahí es «a lo sumo una vez» y sin orden. Precedente: outbox/inbox de Pipeline (`ClaimedOutboxConsumer`). |
| D-M2 | **Un paso del motor es una función pura:** `FlowEngine.Step(flow, state, input) → (state', effects[])`. Los efectos (`SendText`, `SendInteractive`, `CallHttp`, `WaitForCustomer`, `Handoff`, `Resolve`, `End`) los ejecuta el worker. Un efecto `CallHttp` vuelve a entrar al motor con su resultado como `input`. | Es lo que hace testeable el flujo sin Meta ni base, y lo que comparten el motor real y el simulador. |
| D-M3 | **El motor sólo responde si la conversación está `Open`, sin asesor y en `Active` (o `None` al arrancar).** Se verifica al reclamar la fila y otra vez al enviar. En cualquier otro estado la fila se marca procesada sin efecto. | Regla única del spec 1 (I-1). Entre encolar y procesar un asesor pudo tomar la conversación. |
| D-M4 | **Arranque:** sin corrida y `automation_status = None` → evaluar disparador del chatbot activo de la conexión → `Conversation.StartAutomation` + crear `conversation_runs` + ejecutar desde el primer nodo **en la misma unidad de trabajo**. Si el aggregate falla por `concurrency.conflict` (la ingesta sube `version` por SQL), se reintenta la fila. | `StartAutomation` existe desde el spec 1 y no tiene llamadores: es el punto de entrada. |
| D-M5 | **Traspaso (`Handoff`):** `PauseAutomation(actor: null, Handoff)` con auditoría de sistema, evento `AutomationPaused` en el hilo, la corrida termina `HandedOff`, `notify` a todo el tenant como un entrante sin asignar. La conversación queda `Paused`, sin asesor, en la bandeja. **Resolver:** `Resolve(now, reasonId?)` → `None` (D-A5). | Spec 1 D-A9 y §4.6 reservaron `Handoff` y el `RecordBySystem` para este spec. |
| D-M6 | **Instagram queda fuera del motor en esta entrega.** Un chatbot sólo se activa en conexiones WhatsApp; con una de Instagram, `POST /chatbots` y `PUT /chatbots/{id}` responden 422 `automations.chatbot.channel_not_supported`. La paleta del editor sigue preparada (atenúa Plantilla y Lista) para cuando entre. | El flujo de Clasic es WhatsApp; Instagram no tiene listas ni botón URL y su parser no extrae `quick_reply`. Entra con lo que falta del spec 2. |
| D-M7 | **Un mensaje entrante que no es lo que el bloque espera** (audio en una pregunta de texto, texto en una lista) cuenta como intento inválido del bloque que espera. Al agotar `maxAttempts`: si el bloque tiene salida «no válido» conectada, va por ahí; si no, la corrida **reinicia** desde el primer nodo (como Clasic hoy) y manda el primer mensaje. | Mantiene al cliente dentro del flujo sin quedar colgado. |
| D-M8 | **Variables:** las capturadas por el flujo, las de respuesta HTTP (objetos y listas, acceso por puntos `{cliente.firstName}`), las de adjunto (`{factura.url}` firmada 15 min, `{factura.fileName}`, `{factura.mimeType}`) y las fijas `{nombre}` (perfil de WhatsApp), `{telefono}`, `{empresa}`, `{conexion}`. Se guardan en `conversation_runs.variables` (jsonb) y se interpolan en todo texto, ruta, query y cuerpo. Una variable que no existe se interpola como vacío y genera **aviso** en Validar si no la produce ningún bloque anterior. | Patrón 3 de la investigación. |
| D-M9 | **La ventana de 24 h se respeta**: fuera de ventana el bot no envía (la corrida termina `WindowClosed`, sin traspaso). El bot sólo responde a entrantes, así que en la práctica nunca pasa, salvo un HTTP lento. | `OutboundGuards` ya lo exige para humanos; un bot no lo puede saltar. |
| D-M10 | **El bot no consume `ConnectionSendBudget`** ni espera 3 s entre mensajes. Envía los mensajes de un paso en orden, uno tras otro, y espera el `wamid` de cada uno antes del siguiente. | El cupo es de campañas (envío masivo). Enviar secuencialmente y esperar la confirmación garantiza el orden que lulo-chat lograba con la pausa de 3 s. |
| D-M11 | **«Paso actual» en la bandeja:** la conversación gana `automation_chatbot_id` y `automation_step` (título del nodo en espera); el DTO expone `chatbotName` y `stepTitle`. **No hay «de m»**: un grafo no tiene largo. | Spec 1 §4.1 dejó ambas columnas para este spec. |
| D-M12 | **Una corrida por conversación como máximo activa.** Terminada (`Completed`, `HandedOff`, `Resolved`, `WindowClosed`, `Failed`, `Abandoned`), la siguiente arranca sólo si la conversación vuelve a `None` (resolver, o reabrir después de resolver). | Evita que el bot «vuelva a saludar» en medio de una conversación pausada. |
| D-M13 | **Abandono:** una corrida en espera más de 24 h sin entrante termina `Abandoned`. Un barrido del worker cada 10 min. | Sin esto una corrida queda esperando para siempre y la siguiente conversación del mismo cliente no arranca. |
| D-M14 | **Toda corrida que termina sin `Handoff` ni `Resolve`** (`Completed`, `WindowClosed`, `Abandoned`) **devuelve la conversación a `None`** con un método de dominio nuevo, `Conversation.EndAutomation(now)` (`Active → None`, limpia las columnas, sin evento en el hilo). `Failed` pasa a `Paused` con `Handoff` (que un humano la vea). | Mi decisión (chequeo del constructor): un bot que terminó no puede seguir «atendiendo» en la bandeja. Un chip de asistente activo sobre un bot mudo es una mentira. |
| D-M15 | **Una acción humana termina la corrida viva** con estado `Interrupted`: tomar, responder, transferir, pausar manualmente o resolver. Messaging no conoce Automations, así que **lo detecta el worker al reclamar**: si la conversación tiene asesor, está `Paused` o `Resolved`, o está en `None` con una corrida viva, la corrida se cierra `Interrupted` antes de decidir qué hacer con el entrante. «Devolver al asistente» (`POST …/automation/resume`, spec 1) deja la conversación `Active` **sin corrida**; el siguiente entrante arranca una corrida nueva desde el primer nodo **sin evaluar el disparador** (el humano ya decidió que el bot vuelve). | Mi decisión: una corrida no sobrevive a un humano; continuar «en el mismo paso» después de que un asesor conversó confundiría al cliente. Sin puerto nuevo en Messaging. |
| D-M16 | **`NewConversation` y `OutsideHours` disparan sólo con el entrante que abre o reabre la conversación** (`IngestedInbound.OldStatus` es nulo o `Resolved`); la fila del inbox lleva `opened boolean`. **`Keyword` dispara con cualquier entrante** en `None` sin corrida. | Mi decisión: «toda conversación nueva o reabierta» (D-C6) leído literalmente. Si no, el bot saludaría a mitad de un hilo que un asesor liberó. |
| D-M17 | **El entrante que dispara no se procesa como respuesta** del primer bloque en espera: el motor entra con `Start`, envía hasta el primer bloque que espera y se queda esperando el siguiente mensaje. Vale para los tres disparadores. | Mi decisión: el «Hola» del cliente no puede contar como intento inválido de «Número de documento». Es lo que hace el bot de Clasic hoy. |

### 2.3 Salida del bot e interactivos (lo que se toma del spec 2)

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-I1 | `IWhatsAppCloudClient` gana **`SendInteractiveAsync(sender, target, InteractiveSend, callbackData, contextWamid, ct)`** con `InteractiveSend` = `Buttons(body, buttons ≤ 3, header?, footer?)` \| `List(body, buttonLabel, sections, rows ≤ 10)` \| `CtaUrl(body, displayText, url)`. Payload `{"type":"interactive","interactive":{…}}` por el mismo `PostMessageAsync`. Límites de Meta en el validador: botón ≤ 20 caracteres, fila título ≤ 24 y descripción ≤ 72, cuerpo ≤ 1024. | El patrón `[type] = content` de `MessagePayload` calza tal cual (`WhatsAppCloudClient.cs:297-357`). |
| D-I2 | El mensaje interactivo saliente se guarda con `kind = Interactive` y `details` = el objeto `interactive` enviado; `text` = el cuerpo. El hilo lo pinta como burbuja con sus botones o el botón de la lista. | Simétrico con el entrante, que ya guarda el `interactive` crudo en `details`. |
| D-I3 | **Autoría:** `messages.sent_by_kind smallint NOT NULL DEFAULT 1` (`1` Member, `2` Chatbot) y `sent_by_chatbot_id uuid NULL`; `CHECK ((sent_by_kind = 2) = (sent_by_chatbot_id IS NOT NULL))`. `OutboundDraft.SentByMemberId` pasa a `Guid?` y gana `SentByChatbotId`. `MessageDto` conserva `SentBy` (miembro) y suma `SentByChatbot: ChatbotRefDto(Id, Name)?`. | Spec 1 §8 lo dejó para acá. Dejar `sent_by_member_id` nulo sin más mostraría «Miembro eliminado». |
| D-I4 | **Salida del bot propia:** `Messaging.Application` expone `IBotOutbound` (`SendTextAsync`, `SendInteractiveAsync`) que reutiliza `ClaimAsync` y `SettleAsync` pero **no** autoriza, **no** exige membresía y **no** llama a `TryAutoAssignAsync`. Verifica `Open`, ventana y que `automation_status = Active` sin asesor. | `OutboundGuards.LoadAsync` exige `SubjectId` y permiso, y `AdmitAsync` auto-asigna y pausaría al asistente con `AdvisorReplied` (`OutboundGuards.cs:39-113`). |
| D-I5 | **Los `id` de `button_reply` y `list_reply` se leen del `details` del entrante**; no se agregan columnas. El motor compara por `id`; si no hay `id` (texto escrito), compara el texto con los títulos sin distinguir mayúsculas ni tildes. | Hoy sólo el título llega a `text` (`WebhookPayloadParser.cs:193-198`); el `id` ya está en el JSON. |
| D-I6 | **El `callbackData` del bot es `"qep:" + messageId`** como el de los humanos, así `StatusIngestion` actualiza entregado y leído igual. | Sin cambios en el camino de estados. |

### 2.4 Bloque HTTP y conexión «API externa» (lo que se toma del spec 3)

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-H1 | **Proveedor `external-api` en el catálogo de Integrations**, categoría nueva `Automation`, consumido por el módulo `automations`, onboarding `Form`, `maxConnections: 10`. Campos: `baseUrl` (`FieldKind.Url`, https obligatorio), `authHeader` (texto, default `x-api-key`), `apiKey` (`Secret`), `testPath` (texto opcional, default `/`). **Sin habilitación por tenant** (`requiresTenantEnablement: false`). | Trae gratis la pantalla de conexiones, «Probar», pausar, cifrado AES-GCM y la auditoría (`IntegrationConnection`, `ISecretProtector`). Zenvia es el precedente de proveedor por formulario. |
| D-H2 | **Validación anti-SSRF de `baseUrl`:** esquema `https`, host con nombre (no IP literal), que no resuelva a rangos privados, loopback, link-local ni metadata (`169.254.0.0/16`) en el momento de probar **y** en cada llamada (`SocketsHttpHandler.ConnectCallback` resuelve y rechaza). Sin redirecciones (`AllowAutoRedirect = false`). | `FieldKind.Url` no valida nada hoy. Una URL interna permitiría usar al backend como proxy. |
| D-H3 | **`POST …/test`** hace `GET {baseUrl}{testPath}` con el header de clave y acepta 2xx o 3xx; 401/403 → `credentials_rejected`; otro → `unreachable`. | Mismo contrato de `IProviderConnectionTester`. |
| D-H4 | **El bloque HTTP referencia la conexión por id**, método `GET`/`POST`/`PUT`/`PATCH`, ruta relativa con variables, query (lista clave/valor con variables), cuerpo `json` (texto con variables, validado como JSON al guardar) o `multipart` (campos de texto y **un** campo de archivo ligado a una variable de adjunto), `timeoutSeconds` 1..10 (default 8), **mapeo** de respuesta `[{ from: "data.items", to: "equipos" }]` con rutas por puntos e índices (`[0]`), y `okWhen` = status 2xx (default) o lista de status. Salidas `ok` y `error`. | Investigación §3 «Bloque HTTP». Multipart entra porque `POST /customers/new-customer` de Clasic recibe la factura así y el owner pidió reutilizar sus APIs. |
| D-H5 | **La clave nunca sale del servidor:** se resuelve con `IIntegrationConnections.ResolveAsync` al momento de llamar y se pone en el header; nunca se interpola en un texto ni se guarda en la corrida. Cliente HTTP nombrado `automations.external-api`, `RemoveAllLoggers()`, User-Agent `qep-automations`. | Patrón de `ShopifyAdminGateway` / `IntegrationsInfrastructureExtensions.cs:61-114`. |
| D-H6 | **Registro de cada llamada** en `automations.http_calls`: tenant, corrida, nodo, conexión, método, ruta ya interpolada **sin query**, status, duración, `outcome` (`ok`, `error_status`, `timeout`, `network`, `mapping_failed`), `error_code`. **Nunca** cuerpo, headers ni clave. Retención 30 días (barrido del worker). | Investigación §3: «registro de cada llamada sin cuerpo ni clave». |
| D-H7 | **El cuerpo de la respuesta se guarda sólo a través del mapeo** (las variables `to`). Sin mapeo, no queda nada. Tope de respuesta 1 MB; más grande → `error`. | La corrida no puede crecer sin límite por una API verbosa. |
| D-H8 | **Un adjunto se entrega a la API externa cuando ya está en R2** (`message_media.stored_at IS NOT NULL`). Si el bloque Pedir adjunto recibe el archivo antes de la copia, el worker espera hasta 60 s reintentando cada 5 s; pasado eso, `error` del bloque siguiente que lo use. | `MediaCopyWorker` copia asíncrono; al llegar el hook sólo existe `meta_media_id`. |

### 2.5 Simulador

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-S1 | **El simulador corre el mismo `FlowEngine` sobre el borrador**, con un canal falso que acumula la transcripción, y persiste su estado en `automations.simulations` (jsonb, TTL 2 h) para sobrevivir réplicas. | Patrón 9. Un estado en memoria por pod no sirve con dos réplicas. |
| D-S2 | **Las llamadas HTTP no salen por defecto:** cada bloque HTTP guarda una «respuesta de ejemplo» (JSON) que el simulador usa; la sesión de simulación tiene un interruptor «llamar a la API de verdad» (apagado por defecto) que las ejecuta contra la conexión real y las registra en `http_calls` con `simulation: true`. | Mi decisión: probar un flujo no puede crear órdenes de servicio reales sin que alguien lo pida. |
| D-S3 | «Horario de atención» en el simulador pregunta el estado al puerto real; «Pasar a un asesor» y «Resolver» sólo escriben en la transcripción. | Lo primero es lectura; lo segundo tocaría conversaciones reales. |

### 2.6 Clasic

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-K1 | **`JwtAuthGuard` acepta JWT o clave de API** (header `x-api-key` igual a `BOT_API_KEY`): con clave, pone `req.user = { id: 0, role: 'Administrador' }` (D-K11; decía `BOT_USER_ID`). Sin `BOT_API_KEY` configurada, o con el token del pipeline sin resolver (`#{…}#`), la rama de clave no existe. | Pedido del owner: reutilizar los endpoints que ya existen con un solo guard. Los 11 controladores que usan `JwtAuthGuard` quedan cubiertos sin tocarlos. |
| D-K2 | ~~**Usuario «bot»** real en la tabla `users` de Clasic~~. **Anulada por D-K11** (decidido en el plan 4, 2026-10-23): no hay usuario bot. | `POST /service-orders` sólo tomaba del usuario el `createdBy`, y sin usuario escribe `'Whatsapp'`, que es lo que ya escribía el bot. |
| D-K3 | **`GET /customers` gana los filtros `phone` y `document`.** `phone`: últimos 10 dígitos, como `findCustomersByPhone`; el bot manda el `wa_id` completo (`57…`) y Clasic lo recorta; vacío responde 400 a propósito. `document` (decidido en la revisión final del plan 4, 2026-10-23): igualdad exacta sobre `identificationNumber`, 0 o 1 elemento. La respuesta es `{ items, totalCount }`. No son los únicos cambios en Clasic: ver D-K5, D-K6 y D-K9. | Política de datos: hoy el bot la consulta por teléfono antes de tener la cédula. `?identificationNumber=` es la búsqueda del admin y casa por subcadena (con «1» el bot leería a otra persona); el bot viejo buscaba exacto. QEP manda `telefono = ''` cuando la conversación no tiene `wa_id`: si Clasic ignorara el filtro vacío, el bot leería un cliente cualquiera y aceptaría la política en su nombre. |
| D-K4 | **`POST /customers/new-customer` sigue público** y se usa tal cual: crea cliente, factura ficticia y orden en borrador. Su envío de la plantilla `solicitud_os_creada` por lulo-chat queda **pendiente de mover al envío proactivo** (spec 3) y, mientras lulo exista, sigue saliendo por ahí. | Fuera del alcance de este spec; se deja escrito. |

D-K5 a D-K12, **decididas en el plan 4 (2026-10-22/23)**
([`2026-10-22-constructor-de-chatbots-clasic-4.md`](../plans/2026-10-22-constructor-de-chatbots-clasic-4.md)), donde está el
detalle de cada una:

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-K5 | **`new-customer` guarda el teléfono como sus últimos 10 dígitos**, con el mismo helper del filtro `phone`. | El motor manda `{telefono}` como `573004445566`; sin esto, un cliente creado por el bot nunca se encuentra por teléfono. |
| D-K6 | **`GET /services` gana el filtro `name` y `GET /failures` el filtro `cause`**, de igualdad exacta y sensibles a mayúsculas (`STM`, `MANTENIMIENTO`); los dos responden el arreglo en la raíz. | El flujo no puede filtrar una lista por campo; ids fijos en el flujo cambiarían por ambiente. |
| D-K7 | **La ciudad del cliente nuevo va como texto en `addressDescription` («Ciudad: …»)**, y `departmentId`/`cityId` van fijos en el bloque HTTP (`5` / `5001000`, Antioquia / Medellín), editables en el editor. No hay lista de departamentos ni de ciudades. | Una lista de WhatsApp tiene 10 filas; Colombia tiene 33 departamentos. La orden nace en borrador y un agente la confirma, como hoy. |
| D-K8 | **«Otra dirección» de un cliente existente termina en un asesor**, después de pedir la dirección como texto (queda en «Datos capturados»). | `new-customer` rechaza un documento ya registrado. |
| D-K9 | **`PUT /customers/{id}` acepta cuerpo parcial** y, cuando `acceptPolicy` **pasa** a `true` sin fecha, pone `acceptPolicyDate` él mismo (un `PUT` con `"true"` sobre un cliente que ya la había aceptado no mueve la fecha). **`POST /service-orders` sin `serviceDate` usa la fecha actual.** | El motor no tiene variable «ahora»; los dos endpoints rompían con campos ausentes. |
| D-K10 | **La ruta de prueba de la conexión «API externa» es `/failures`.** | El probador sólo mira el status: la ruta tiene que estar protegida. `/services`, `/departments/*` y `/health/*` son públicas; `testPath` no admite query. |
| D-K11 | **No hay usuario bot (anula D-K2).** El guard pone `{ id: 0, role: 'Administrador' }` y `POST /service-orders` escribe `createdBy = 'Whatsapp'` cuando no hay usuario. | Menor impacto en Clasic; es la misma marca que deja hoy el bot. |
| D-K12 | **La clave va al ConfigMap de Clasic, sin Secret**: `BOT_API_KEY` en el pod, desde la variable secreta `CLASIC_BOT_API_KEY` del grupo `Backend-prod`. | Decisión del owner: en Clasic no se trabaja con Secrets en los YAML. |

## 3. Trazabilidad: historias → componentes

| # | Historia | Componentes |
| --- | --- | --- |
| 1 | «Como admin, creo un chatbot en blanco o desde una plantilla y lo armo en el editor» | `POST /automations/chatbots`, `PUT …/draft`, `ChatbotEditorPage` alineada al canvas |
| 2 | «Como admin, valido y publico, y sé qué me falta» | `FlowValidator`, `POST …/validate`, `POST …/publish`, panel de validación |
| 3 | «Como admin, asigno el chatbot a mi número y nadie más lo ocupa» | `chatbot_connections` + índice parcial, `GET /automations/connections`, formulario |
| 4 | «Como cliente, escribo al número y el bot me responde en orden» | `messaging.automation_inbox`, `AutomationInboxWorker`, `FlowEngine`, `IBotOutbound` |
| 5 | «Como cliente, elijo con botones o en una lista y adjunto mi factura» | `SendInteractiveAsync`, bloques Botones, Lista, Pedir adjunto |
| 6 | «Como negocio, el bot consulta y crea datos en mi API» | proveedor `external-api`, bloque HTTP, `http_calls` |
| 7 | «Como asesor, veo en el hilo que escribió el bot y en qué paso va» | `sent_by_kind`, `SentByChatbot`, `automation_step`, chip de la bandeja |
| 8 | «Como asesor, cuando el bot me la pasa, la veo en la bandeja y el bot se calla» | efecto `Handoff` → `PauseAutomation(null, Handoff)`, `notify` |
| 9 | «Como admin, pruebo el borrador sin gastar mensajes» | `automations.simulations`, pestaña Simular |
| 10 | «Como admin, guardo mi flujo como plantilla y lo reutilizo» | `chatbot_templates`, `POST …/save-as-template` |
| 11 | «Como Clasic, QEP consume mi API con una clave» | `JwtAuthGuard` dual, filtros `phone` y `document` (sin usuario bot: D-K11; plan 4, 2026-10-23) |

## 4. Backend: módulo `Automations`

### 4.1 Proyectos y cableado

- `src/Modules/Automations/Modules.Automations.{Domain,Application,Infrastructure,Api}` con la misma forma de
  Pipeline (`Backend.slnx`, `ArchitectureTests.csproj`, `AutomationsLayerTests.cs` nuevo: Application sólo
  referencia `Modules.Automations*` y `Modules.Tenancy*`; prohibido Messaging, Integrations, Customers e
  Identity en todas las capas).
- Esquema `automations`, `AutomationsDbContext` con `__ef_migrations_history` propia, migración inicial
  `AddAutomations`, aplicada en `Program.cs` **después** de Messaging e Integrations.
- Registro en `QepServiceCollectionExtensions`: handlers uno por uno, validadores por assembly,
  `AddAutomationsInfrastructure`, adaptadores en `src/Bootstrapper/Automations*.cs`, permisos y políticas
  (`CompositionRootTests.PermissionDiscoveryFindsTheFiftyEightConstants` pasa a 60).
- Tenancy: `TenantModuleKeys.Automations = Define("automations")`, último de `All`,
  `Dependencies[Automations] = [Messaging]`, fuera de `DefaultForNewTenants`; migración
  `AddAutomationsModuleKey` que recrea `CK_tenant_modules_module_key` (plantilla
  `20261018105954_AddShopifyModuleKey.cs`); el mensaje del validador de `ChangeTenantModules.cs:27-28` se
  corrige al contar real; pruebas de Tenancy listadas en §12.

### 4.2 Modelo

```sql
CREATE TABLE automations.chatbots (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  name varchar(80) NOT NULL, description varchar(300) NOT NULL DEFAULT '',
  status varchar(16) NOT NULL DEFAULT 'Draft',           -- Draft | Active | Paused
  trigger varchar(24) NOT NULL DEFAULT 'NewConversation',-- NewConversation | Keyword | OutsideHours
  keywords jsonb NOT NULL DEFAULT '[]',
  restart_keyword varchar(40) NOT NULL DEFAULT 'reiniciar',
  max_attempts smallint NOT NULL DEFAULT 3,
  draft_flow jsonb NOT NULL, draft_layout jsonb NOT NULL DEFAULT '{}',
  draft_updated_at timestamptz NOT NULL, draft_updated_by uuid NOT NULL,
  published_version_id uuid NULL,
  created_at timestamptz NOT NULL, created_by uuid NOT NULL, updated_at timestamptz NOT NULL,
  version bigint NOT NULL DEFAULT 1,
  CONSTRAINT "CK_chatbots_status" CHECK (status IN ('Draft','Active','Paused')),
  CONSTRAINT "CK_chatbots_trigger" CHECK (trigger IN ('NewConversation','Keyword','OutsideHours')),
  CONSTRAINT "CK_chatbots_published" CHECK ((status = 'Draft') = (published_version_id IS NULL)),
  CONSTRAINT "CK_chatbots_max_attempts" CHECK (max_attempts BETWEEN 1 AND 5)
);
CREATE UNIQUE INDEX "IX_chatbots_tenant_name" ON automations.chatbots (tenant_id, lower(name));

CREATE TABLE automations.chatbot_versions (
  id uuid PRIMARY KEY, chatbot_id uuid NOT NULL REFERENCES automations.chatbots(id) ON DELETE CASCADE,
  tenant_id uuid NOT NULL, number int NOT NULL,
  flow jsonb NOT NULL, layout jsonb NOT NULL,
  published_at timestamptz NOT NULL, published_by uuid NOT NULL,
  UNIQUE (chatbot_id, number)
);

CREATE TABLE automations.chatbot_connections (
  chatbot_id uuid NOT NULL REFERENCES automations.chatbots(id) ON DELETE CASCADE,
  tenant_id uuid NOT NULL, connection_id uuid NOT NULL,   -- referencia blanda a integrations.connections
  active boolean NOT NULL DEFAULT false,
  PRIMARY KEY (chatbot_id, connection_id)
);
-- D-C4: una conexión, un chatbot activo.
CREATE UNIQUE INDEX "IX_chatbot_connections_active" ON automations.chatbot_connections (tenant_id, connection_id) WHERE active;

CREATE TABLE automations.chatbot_templates (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL,
  name varchar(80) NOT NULL, category varchar(24) NOT NULL, description varchar(300) NOT NULL DEFAULT '',
  flow jsonb NOT NULL, layout jsonb NOT NULL, parameters jsonb NOT NULL DEFAULT '[]',
  created_at timestamptz NOT NULL, created_by uuid NOT NULL
);
CREATE UNIQUE INDEX "IX_chatbot_templates_tenant_name" ON automations.chatbot_templates (tenant_id, lower(name));

CREATE TABLE automations.conversation_runs (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, conversation_id uuid NOT NULL, connection_id uuid NOT NULL,
  chatbot_id uuid NOT NULL, chatbot_version_id uuid NOT NULL,
  status varchar(16) NOT NULL,                 -- Running | Waiting | Completed | HandedOff | Resolved | WindowClosed | Failed | Abandoned | Interrupted
  current_node_id varchar(64) NULL, step_number int NOT NULL DEFAULT 0,
  attempts smallint NOT NULL DEFAULT 0,
  variables jsonb NOT NULL DEFAULT '{}',
  trace jsonb NOT NULL DEFAULT '[]',           -- [{at, nodeId, kind, outcome}] tope 200 entradas
  started_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, ended_at timestamptz NULL,
  failure_code varchar(64) NULL
);
-- D-M12: una corrida viva por conversación.
CREATE UNIQUE INDEX "IX_conversation_runs_live" ON automations.conversation_runs (conversation_id) WHERE status IN ('Running','Waiting');
CREATE INDEX "IX_conversation_runs_tenant_chatbot_started" ON automations.conversation_runs (tenant_id, chatbot_id, started_at DESC);

CREATE TABLE automations.http_calls (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, tenant_id uuid NOT NULL,
  run_id uuid NULL, simulation_id uuid NULL, node_id varchar(64) NOT NULL, connection_id uuid NOT NULL,
  method varchar(8) NOT NULL, path varchar(512) NOT NULL,      -- interpolada, sin query
  status_code int NULL, duration_ms int NOT NULL,
  outcome varchar(16) NOT NULL, error_code varchar(64) NULL, called_at timestamptz NOT NULL
);
CREATE INDEX "IX_http_calls_tenant_called" ON automations.http_calls (tenant_id, called_at DESC);

CREATE TABLE automations.simulations (
  id uuid PRIMARY KEY, tenant_id uuid NOT NULL, chatbot_id uuid NOT NULL, member_id uuid NOT NULL,
  live_http boolean NOT NULL DEFAULT false,
  state jsonb NOT NULL, transcript jsonb NOT NULL DEFAULT '[]',
  created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, expires_at timestamptz NOT NULL
);
```

En **Messaging** (migración `AddAutomationEngine`, después de `AddBusinessHours`):

```sql
ALTER TABLE messaging.conversations
  ADD COLUMN automation_chatbot_id uuid NULL,
  ADD COLUMN automation_step varchar(120) NULL,
  ADD CONSTRAINT "CK_conversations_automation_chatbot" CHECK (automation_status = 'None' OR automation_chatbot_id IS NOT NULL);

ALTER TABLE messaging.messages
  ADD COLUMN sent_by_kind smallint NOT NULL DEFAULT 1,
  ADD COLUMN sent_by_chatbot_id uuid NULL,
  ADD CONSTRAINT "CK_messages_sent_by_kind" CHECK (sent_by_kind IN (1,2)),
  ADD CONSTRAINT "CK_messages_sent_by_chatbot" CHECK ((sent_by_kind = 2) = (sent_by_chatbot_id IS NOT NULL)),
  ADD CONSTRAINT "CK_messages_sent_by_one" CHECK (sent_by_kind <> 2 OR sent_by_member_id IS NULL);

CREATE TABLE messaging.automation_inbox (
  message_id uuid PRIMARY KEY, tenant_id uuid NOT NULL, connection_id uuid NOT NULL, conversation_id uuid NOT NULL,
  opened boolean NOT NULL,                     -- D-M16: este entrante abrió o reabrió la conversación
  created_at timestamptz NOT NULL, attempts smallint NOT NULL DEFAULT 0,
  next_attempt_at timestamptz NOT NULL, processed_at timestamptz NULL, outcome varchar(24) NULL
);
CREATE INDEX "IX_automation_inbox_pending" ON messaging.automation_inbox (next_attempt_at) WHERE processed_at IS NULL;
CREATE INDEX "IX_automation_inbox_conversation" ON messaging.automation_inbox (conversation_id, created_at) WHERE processed_at IS NULL;
```

`chatbot_connections.connection_id` y `conversation_runs.conversation_id` son referencias blandas entre módulos,
como `assigned_member_id` hoy: sin FK cruzada entre esquemas.

### 4.3 Dominio

`Chatbot` (agregado): `Create(tenantId, name, description, trigger, keywords, flow, layout, by, now)`,
`Rename`, `UpdateSettings(trigger, keywords, restartKeyword, maxAttempts)`, `SaveDraft(flow, layout, by, now)`,
`Publish(validation, by, now) → ChatbotVersion` (exige cero errores; sube `number`; pasa a `Active` si estaba
`Draft`), `Pause()`/`Resume()` (exigen publicado), `AssignConnections(ids)` (la activación de `active` la hace el
handler junto con el estado). Concurrencia optimista por `version` con `If-Match` al estilo Messaging
(`RequireVersion`, 428/412).

`Flow` (objeto de valor, deserializado del jsonb con `System.Text.Json`, anexo A): `Start`, `Nodes` tipados por
`kind`, `Variables` declaradas. `FlowValidator.Validate(flow, context) → IReadOnlyList<FlowIssue(Severity,
Code, NodeId?, Message)>`, con `context` = canal de las conexiones, conexiones «API externa» del tenant y si
las conexiones tienen horario. Errores (bloquean): `start.no_first_node`, `node.output_unconnected`,
`node.unknown_target`, `node.unreachable` (aviso, no error), `buttons.too_many`, `buttons.label_too_long`,
`list.too_many_rows`, `list.row_title_too_long`, `list.row_description_too_long`, `text.too_long`,
`text.empty`, `http.connection_missing`, `http.connection_paused`, `http.body_not_json`, `http.path_invalid`,
`condition.no_rules`, `variable.name_invalid`. Avisos: `variable.never_set`, `business_hours.not_assigned`,
`http.no_sample_response`, `node.unreachable`.

`FlowEngine` (puro, Domain): `Step(FlowDefinition flow, RunState state, EngineInput input) → StepResult(RunState
state, IReadOnlyList<Effect> effects)`. `EngineInput` = `Start` \| `CustomerMessage(kind, text, replyId,
attachment?)` \| `HttpResult(nodeId, ok, status, mappedVariables)` \| `BusinessHours(nodeId, isOpen)`. El motor
avanza de nodo en nodo hasta un efecto que necesite al mundo (`WaitForCustomer`, `CallHttp`,
`CheckBusinessHours`) o un terminal (`Handoff`, `Resolve`, `End`). Interpolación y comparación de textos viven
en `Domain/Text/TemplateText.cs` y `TextMatch.cs` (sin tildes, sin mayúsculas, espacios colapsados).

Códigos (`AutomationsErrorCodes`): `automations.chatbot.not_found`, `.name_taken`, `.invalid`,
`.connection_taken`, `.channel_not_supported`, `.not_published`, `.already_paused`, `.already_active`,
`.has_live_runs` (al borrar), `automations.template.not_found`, `automations.simulation.not_found`,
`automations.simulation.expired`, `automations.external_api.not_found`.

### 4.4 Endpoints

Grupo `/api/v1/tenants/{tenantId:guid}/automations`, tag `Automations`. Lectura con `automations.chatbot.read`,
escritura con `automations.chatbot.manage`; todos revalidan tenant, permiso y `TenantModuleGuard(automations)`.

| Ruta | Permiso | Respuesta | Notas |
| --- | --- | --- | --- |
| `GET /chatbots?search=` | read | `ChatbotListResponse(items: ChatbotSummaryDto[])` | `today: { handled, handedOff }` cuenta corridas iniciadas hoy (reloj del tenant) y terminadas `HandedOff`; `publishedAt` es instante, el frontend lo formatea |
| `POST /chatbots` | manage | 201 `ChatbotEditorDto` | `CreateChatbotRequest(name, description, trigger, keywords, connectionIds, source: { kind: blank\|template\|duplicate, templateId?, chatbotId?, parameters? })`. 422 `channel_not_supported` si una conexión es Instagram |
| `GET /chatbots/{id}` | read | `ChatbotEditorDto` | chatbot + `draft { flow, layout, updatedAt, updatedBy }` + `published { number, at }?` + `issues` (validación del borrador) + `connections` elegibles + `externalApis` (conexiones `external-api` del tenant, id y nombre) + `variables` conocidas |
| `PUT /chatbots/{id}` | manage, If-Match | `ChatbotEditorDto` | nombre, descripción, disparador, palabras, reinicio, intentos, `connectionIds` |
| `PUT /chatbots/{id}/draft` | manage, If-Match | `DraftSavedDto(version, updatedAt, issues)` | D-C3: siempre guarda. Cuerpo ≤ 512 KB |
| `POST /chatbots/{id}/validate` | read | `issues` | sin efectos |
| `POST /chatbots/{id}/publish` | manage, If-Match | `ChatbotEditorDto` | 422 `automations.chatbot.invalid` con `issues` en `errors` |
| `POST /chatbots/{id}/pause` · `/resume` | manage, If-Match | `ChatbotEditorDto` | D-C5 |
| `POST /chatbots/{id}/duplicate` | manage | 201 `ChatbotEditorDto` | copia el borrador, sin conexiones, `Draft` |
| `DELETE /chatbots/{id}` | manage, If-Match | 204 | 409 `has_live_runs` si hay corridas vivas |
| `POST /chatbots/{id}/save-as-template` | manage | 201 `TemplateDto` | `(name, category, description)` |
| `GET /chatbots/{id}/export` · `POST /chatbots/import` | manage | JSON del flujo con metadatos / 201 `ChatbotEditorDto` | D-C9 |
| `GET /templates` | read | `TemplateCatalogDto(system: TemplateDto[], mine: TemplateDto[])` | `TemplateDto(id, name, category, description, blockCount, channels, requirement, parameters, preview)` |
| `DELETE /templates/{id}` | manage | 204 | sólo las del tenant |
| `GET /connections` | read | `SelectableConnectionDto[]` | `(id, name, channel, handle, status, servedBy: { chatbotId, name }?, businessHoursName?)` por `IAutomationsConnections` (Messaging `ListAllAsync` + `IBusinessHours`) |
| `POST /chatbots/{id}/simulations` | manage | 201 `SimulationDto` | `(liveHttp: bool)`; arranca y responde la transcripción inicial |
| `POST /simulations/{id}/messages` | manage | `SimulationDto` | `(text?, replyId?, attachment?: { fileName, mimeType })`; responde transcripción completa, `currentNodeId`, `variables`, `ended` |
| `DELETE /simulations/{id}` | manage | 204 | |
| `GET /runs?chatbotId=&from=&to=` | read | `RunDto[]` | historial de corridas del chatbot (estado, paso final, duración) para la lista; **lo que ve el asesor en el hilo no está acá** (§5.5) |

Las conexiones Instagram viajan en `GET /connections` con `status` y `channel` para que el formulario las
muestre deshabilitadas con «Próximamente» (D-M6).

### 4.5 Plantillas del sistema (D-C8)

`Modules.Automations.Infrastructure/Seed/Templates/*.json` (`EmbeddedResource`), un archivo por plantilla:
`{ id, name, category, description, channels, requirement, parameters: [{ key, label, kind, options?,
defaultValue }], preview: [títulos], flow, layout }`. `SystemTemplateCatalog` los carga una vez al arrancar y
una prueba unitaria valida cada uno con `FlowValidator`. Primera entrega: «Chatbot en blanco» (no es archivo:
lo arma el handler), «Menú de bienvenida con traspaso», «Fuera de horario». Las demás de la investigación §5
quedan para cuando existan sus bloques. **El flujo de Clasic no es plantilla del sistema** (investigación §5):
se carga en su tenant con `POST /chatbots/import` (Anexo B).

### 4.6 Auditoría

`IAutomationsAuditRecorder.Record(tenantId, actorId, bySystem, action, resourceType, resourceId, at)` como el
de Pipeline. Acciones: `automations.chatbot.{created,updated,draft_saved,published,paused,resumed,deleted,
imported}`, `automations.template.{created,deleted}`, `automations.run.{started,handed_off,resolved,
completed,failed}` (sistema). `draft_saved` se registra **una vez por minuto por chatbot** como máximo: el
autoguardado dispara cada pocos segundos.

## 5. Backend: motor

### 5.1 Cola (D-M1)

`InboundIngestion.IngestAsync` (`Modules.Messaging.Infrastructure/Webhook/InboundIngestion.cs`) agrega
`automation_status`, `automation_chatbot_id` y `assigned_member_id` al `RETURNING` del `UPDATE`, y en la misma
transacción inserta en `messaging.automation_inbox` **si** `assigned_member_id IS NULL AND automation_status IN
('None','Active')` y el `kind` es `Text`, `Interactive`, `Image`, `Document`, `Video`, `Audio` o `Unsupported`.
Los ecos de Instagram y los eventos nunca encolan. `IngestedInbound` gana esos campos para que el procesador
no vuelva a leer.

`Messaging.Application` expone el puerto **`IAutomationInbox`**: `ClaimAsync(now, batch, lease) →
IReadOnlyList<InboxItem>` (SQL: `UPDATE … SET attempts = attempts + 1, next_attempt_at = now + lease FROM (SELECT
… WHERE processed_at IS NULL AND next_attempt_at <= now AND NOT EXISTS (otra fila pendiente más vieja de la
misma conversación) ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT batch) RETURNING …`), `CompleteAsync(messageId,
outcome)`, `RetryLaterAsync(messageId, delay)`, `ReadInboundAsync(messageId) → InboundForAutomation(kind, text,
replyId, media?, occurredAt)` (lee `text`, `details->interactive->*_reply->id`, `message_media`). Leases
escalonados 30 s, 2 min, 10 min; a los 5 intentos, `outcome = 'failed'` y la conversación queda como estaba.

### 5.2 Worker y unidad de trabajo

`AutomationInboxWorker` (`Modules.Automations.Infrastructure/Engine/`, `BackgroundService` con
`PeriodicTimer` de 1 s, `DrainAsync` interno para pruebas, opciones en `Automations:Workers`) reclama hasta 20
filas y procesa cada una en su propio scope con `RunProcessor.ProcessAsync(item)`:

1. `SELECT … FOR UPDATE` sobre la corrida viva de la conversación (o `pg_advisory_xact_lock(hash(conversation_id))`
   si no hay corrida) en la transacción de `AutomationsDbContext`.
2. Lee el estado de la conversación por el puerto `IAutomationConversations.GetAsync(tenantId, conversationId)
   → (status, assignedMemberId, automationStatus, automationChatbotId, lastInboundAt, customerName, waId)`.
   **D-M15:** si hay corrida viva y la conversación tiene asesor, está `Paused`, `Resolved` o en `None`, la
   corrida se cierra `Interrupted`. Después, si no está `Open` o tiene asesor: `CompleteAsync(outcome:
   'skipped')`.
3. Sin corrida y `None`: resuelve el chatbot activo de la conexión y evalúa el disparador (D-C6, D-M16:
   `NewConversation` y `OutsideHours` sólo con `opened`; `OutsideHours` consulta `IAutomationBusinessHours`);
   si no aplica, `skipped`. Si aplica: `IAutomationConversations.StartAsync(tenantId, conversationId, chatbotId,
   firstStepTitle)` (adaptador en Bootstrapper que carga el agregado, llama `StartAutomation`, escribe
   `automation_chatbot_id`/`automation_step`, guarda y notifica; `concurrency.conflict` → `RetryLaterAsync(2 s)`),
   crea la corrida con la versión publicada y entra al motor **sólo** con `EngineInput.Start` (D-M17).
   **Sin corrida y `Active`** (un humano devolvió la conversación al asistente, D-M15): lo mismo, sin evaluar el
   disparador y sin `StartAsync` (ya está `Active`); si el chatbot de la conexión ya no está publicado, la
   conversación vuelve a `None` con `EndAsync` y la fila queda `skipped`.
4. Con corrida `Waiting`: `ReadInboundAsync` y entra al motor con `CustomerMessage`.
5. Ejecuta los efectos en orden, volviendo a entrar al motor con los resultados de `CallHttp` y
   `CheckBusinessHours`. Un envío que falle con `WindowClosed` termina la corrida `WindowClosed`; cualquier otro
   fallo de envío → `Failed` con `failure_code` y la conversación pasa a `Paused` con `Handoff` (que un humano la
   vea).
6. Persiste corrida, `automation_step`, trace, y `CompleteAsync`. Toda la unidad ocurre dentro del lease; un
   HTTP de 10 s más tres mensajes cabe en 30 s.

El tope es **un entrante por unidad**: si el cliente mandó tres mensajes seguidos, se procesan en orden, uno por
pasada, y los que no son lo que el bloque espera cuentan como intento (D-M7).

### 5.3 Puertos del módulo (declarados en `Modules.Automations.Application`, adaptados en Bootstrapper)

- `IAutomationInbox` (arriba; Messaging lo implementa en su Infrastructure y Bootstrapper lo reexpone).
- `IAutomationConversations`: `GetAsync`, `StartAsync`, `SetStepAsync(tenantId, conversationId, stepTitle)`,
  `HandoffAsync(tenantId, conversationId)` (`PauseAutomation(null, Handoff)` + evento + auditoría de sistema
  `messaging.conversation.automation_paused` con `bySystem` + `notify` a todo el tenant),
  `ResolveAsync(tenantId, conversationId, reasonId?)` (`Resolve` + evento `Resolved` con actor nulo + auditoría
  de sistema), `EndAsync(tenantId, conversationId)` (`Conversation.EndAutomation`, D-M14, sin evento). Messaging
  gana en `IMessagingAuditRecorder` un `RecordBySystem(tenantId, action, conversationId, at)` (spec 1 §4.6).
- `IConversationAutomationDetails` (**declarado en Messaging**, adaptado en Bootstrapper sobre Automations):
  `FindAsync(tenantId, conversationId) → (chatbotName, versionNumber, status, stepTitle, variables, trace)?` de
  la corrida más reciente. Es lo que el asesor ve como «Datos capturados» (§5.5).
- `IAutomationMessenger`: `SendTextAsync(tenantId, conversationId, chatbotId, text, clientId)`,
  `SendInteractiveAsync(…, InteractiveSend, clientId)` → `BotSendResult(Sent(messageId) | WindowClosed |
  Rejected(code))`. Adapta `IBotOutbound` de Messaging (D-I4). `clientId` determinístico = hash de `(runId,
  nodeId, índice del mensaje, attempts)` para que un reintento de la fila no duplique.
  **Errata (plan 2, ruling P2-R5):** la implementación usa `(runId, baseStepNumber, effectIndex)` y no `attempts`, que cambia en cada reintento y haría que el reintento duplicara.
- `IAutomationBusinessHours`: `IsOpenAsync(tenantId, connectionId)` sobre `IBusinessHours`.
- `IAutomationConnections`: `ListAsync(tenantId) → (id, name, channel, handle, status)` sobre
  `IMessagingConnectionDirectory.ListAllAsync` + `IBusinessHours.GetStatusesAsync`.
- `IAutomationExternalApis`: `ListAsync(tenantId)` y `ResolveAsync(tenantId, connectionId) → (baseUrl,
  authHeader, apiKey)` sobre `IIntegrationConnections` filtrando `external-api`.
- `IAutomationMedia`: `TryGetStoredAsync(tenantId, messageId) → (key, fileName, mimeType, size)?`,
  `CreateDownloadUrlAsync(key, 15 min)`, `OpenReadAsync(key)` sobre `IMessagingMediaStore`/`IMediaReads`.
- `IAutomationTenantNames`: nombre de la empresa para `{empresa}`.

### 5.4 Semántica de los bloques (Anexo A tiene la forma JSON)

| Bloque | Al entrar | Espera | Salidas |
| --- | --- | --- | --- |
| Inicio | nada | no | `next` |
| Mensaje | `SendText` o, con `urlButton`, `SendInteractive(CtaUrl)` | no | `next` |
| Pregunta abierta | envía el texto | sí | `answered` (valida: `none`, `digits`, `number`, `regex`, `keywords`); `invalid` |
| Botones | `SendInteractive(Buttons)` | sí | una por botón (`id`), `other` (respuesta no esperada; con `fallback = repeat` reenvía hasta `maxAttempts` y luego D-M7) |
| Lista | `SendInteractive(List)`; con `dynamic`, arma filas desde la variable colección (tope 10; más → `tooMany` sin enviar); `extraOption` agrega una fila fija al final | sí | `chosen` (guarda `{saveTo} = { id, title }`), `extra`, `tooMany`, `other` |
| Pedir adjunto | envía el texto | sí | `received` (guarda `{saveTo} = { messageId, fileName, mimeType, url }`; `accept` = `image` \| `document` \| `any`), `invalid` |
| Condición | evalúa reglas (`isEmpty`, `isNotEmpty`, `equals`, `notEquals`, `contains`, `greaterThan`, `lessThan`, `countEquals`, `countGreaterThan`) con `all` / `any` | no | `yes`, `no` |
| Horario de atención | `CheckBusinessHours` | no | `open`, `closed` |
| Llamada HTTP | `CallHttp` | no | `ok`, `error` |
| Pasar a un asesor | mensaje opcional y `Handoff` | terminal | — |
| Resolver | mensaje opcional y `Resolve(reasonId?)` | terminal | — |

Una salida sin conectar en tiempo de ejecución (sólo posible si se publicó con avisos, nunca con errores) termina
la corrida `Completed`.

### 5.5 Lectura en Messaging

`ConversationAutomationDto` suma `ChatbotId`, `ChatbotName` (por `IAutomationsChatbotNames`, puerto nuevo de
Messaging adaptado en Bootstrapper) y `StepTitle`. `MessageDto` suma `SentByChatbot` (D-I3). `MessageMapping.
MemberIdsOf` ignora los de kind 2. `ListConversationsQuery.Automation` no cambia.

**«Datos capturados» para el asesor:** `GET /api/v1/tenants/{tenantId}/messaging/conversations/{id}/automation`
con `messaging.conversation.read`, sobre `IConversationAutomationDetails` (§5.3). Vive en Messaging y con su
permiso porque es la pantalla del asesor, que no tiene `automations.chatbot.read` (D-C10). Responde 404
`messaging.automation.not_available` si la conversación nunca tuvo corrida.

## 6. Backend: salida interactiva y autoría (Messaging)

- `IWhatsAppCloudClient.SendInteractiveAsync` (D-I1) y su builder en `WhatsAppCloudClient.cs`; `InteractiveSend`
  en `Modules.Messaging.Application/InteractiveSend.cs` con validación de límites de Meta.
- `IBotOutbound` (D-I4) en Application, público: `Task<BotSendResult> SendAsync(BotSend send)` con `BotSend
  (TenantId, ConversationId, ChatbotId, ClientId, Text, InteractiveSend?)`. Internamente: carga mínima de la
  conversación, `Open` + ventana + `Active` sin asesor, `ClaimAsync(OutboundDraft { SentByMemberId = null,
  SentByChatbotId })`, `ResolveSenderAsync`, `SendTextAsync`/`SendInteractiveAsync`, `SettleAsync` (que ya
  hace `MarkRepliedAsync` y `ConversationChangedAsync`). Un `clientId` repetido devuelve el existente.
- `OutboundMessages.ClaimAsync`/`CommitSentAsync` escriben `sent_by_kind` y `sent_by_chatbot_id`;
  `MessageRow`/`ExistingOutbound` los leen.
- El compositor de la bandeja **no** manda interactivos en esta entrega (sigue siendo spec 2); sólo los pinta.

## 7. Backend: bloque HTTP y proveedor «API externa» (Integrations)

- `IntegrationProviders.ExternalApi` (D-H1), `IntegrationCategory.Automation`, `FieldDefinition`s con los
  patrones: `baseUrl` `^https://[^\s/?#]+(/[^\s?#]*)?$` ≤ 300, `authHeader` `^[A-Za-z0-9-]{1,64}$`, `apiKey`
  `^[\x21-\x7E]{1,512}$`, `testPath` `^/[^\s?#]*$` ≤ 200. Migración de Integrations que recrea el `CHECK` de
  `provider_key`. `ExternalApiConnectionTester` (D-H3) con cliente nombrado `integrations.external-api`.
- `ExternalApiUrlPolicy` (D-H2) en `Modules.Integrations.Infrastructure/ExternalApi/`: valida y resuelve DNS;
  compartida con el cliente del bloque HTTP vía `IExternalApiUrlPolicy` (puerto de Automations, adaptado).
- `HttpBlockExecutor` (`Modules.Automations.Infrastructure/Http/`): arma la petición desde el nodo y las
  variables (D-H4), timeout del nodo, `AllowAutoRedirect = false`, lee hasta 1 MB, aplica el mapeo
  (`JsonPath` por puntos e índices, implementado a mano sobre `JsonElement`, sin librería), escribe `http_calls`
  (D-H6) y devuelve `HttpResult(ok, status, mappedVariables, errorCode)`. Para `multipart`, abre el adjunto por
  `IAutomationMedia.OpenReadAsync` (D-H8).
- Frontend de Integraciones: el proveedor aparece en el catálogo como «API externa» con su formulario genérico
  (ya renderiza por `FieldDefinition`); sólo hay que asegurar el `FieldKind.Url` en el formulario.

## 8. Simulador (D-S1..S3)

`SimulationRunner` (Application) usa el mismo `FlowEngine` con un `SimulatedChannel`: `SendText`/
`SendInteractive` agregan a la transcripción `{ from: bot, nodeId, nodeTitle, text, buttons?, list? }`;
`WaitForCustomer` guarda el estado y responde; `CallHttp` usa la `sampleResponse` del nodo (o la API real con
`liveHttp`); `CheckBusinessHours` consulta el puerto; `Handoff`/`Resolve` agregan una línea de sistema y
terminan. El mensaje del tester entra como `CustomerMessage` (texto, `replyId` de un botón o fila, o un adjunto
ficticio `{ fileName, mimeType }`). `SimulationDto` trae `currentNodeId` para que el lienzo marque el paso.

## 9. Frontend (`qep-frontend`)

Depende del plan del spec 1 (`docs/superpowers/plans/2026-10-21-mensajeria-asistente-y-horarios-frontend.md`,
AH-01..AH-09) para el chip y los botones del asistente en la bandeja; lo de este spec se construye encima.

- **Módulo y permisos.** `automations` en `TENANT_MODULE_KEYS` y `MODULE_LABELS`; `routes/_authenticated/
  automations.tsx` monta `ModuleGate(['automations'])` y el gate de acceso por `automations.chatbot.read`; el
  ítem del sidebar deja de ser `provisional`.
- **API.** `features/automations/services/automations.api.ts` con los endpoints de §4.4 y parsers defensivos;
  se borran `fixtures/chatbots.fixtures.ts`, `use-demo-read.ts` y `demo-search.ts`. Los tipos de
  `types/automations.ts` se reconcilian con los DTOs (`publishedAt` instante → «hace 3 días» con
  `formatRelative` del tenant; `FlowBlockData` deriva de `Flow` + `layout`).
- **Lista y creación.** Lista con buscador, cards de plantillas (`GET /templates`, «Chatbot en blanco» primero),
  formulario con conexiones (`GET /connections`; Instagram deshabilitada «Próximamente»).
- **Editor, alineado al canvas aprobado.** Cabecera con chips de estado y autoguardado («Guardado hace…»),
  «Guardar como plantilla» y «Exportar/Importar» en el menú «⋯»; paleta con buscador e íconos **sólo con los
  bloques de esta entrega**; lienzo con una salida por handle, tono por salida, salida sin conectar en ámbar con
  «+» que abre «¿Qué sigue?» y conecta; minimapa, zoom, «Ordenar» (`dagre` no entra: ordenar es un layout simple
  por niveles, propio); panel derecho con Propiedades por tipo de bloque (formularios por `kind`, variables
  insertables) y Simular (transcripción, variables capturadas, compositor, «Reiniciar», «Llamar a la API de
  verdad»); panel inferior de validación con «Ir al bloque». Autoguardado del borrador con debounce de 2 s y
  `If-Match`; un 412 recarga y avisa «Alguien más editó este chatbot».
- **Bandeja.** Burbuja del bot con «Chatbot · {nombre}» (`SentByChatbot`), interactivos salientes pintados
  (botones, lista) y el chip del asistente con «{chatbot} · {paso}» (`ConversationAutomationDto`). El detalle de
  la conversación suma «Datos capturados» desde `GET /messaging/conversations/{id}/automation` cuando
  `automation` no es `null` o el hilo tiene mensajes del bot.
- **Integraciones.** Nada nuevo salvo verificar que el formulario genérico pinte `FieldKind.Url`.

## 10. Clasic (`calentadores-classic-backend`)

Corregido en el plan 4 (2026-10-22/23): sin usuario bot (D-K11) y sin Secret (D-K12).

- `src/auth/guards/jwt-auth.guard.ts`: `canActivate` mira `x-api-key`; si coincide con `BOT_API_KEY`
  (comparación de tiempo constante), pone `request.user = { id: 0, role: 'Administrador' }` y devuelve `true`; si no,
  delega en `super.canActivate`. Variable en `configurations.ts`, `.env.example` y `k8s/prod-configMap.yaml`, donde
  el valor es el token `#{CLASIC_BOT_API_KEY}#` que el pipeline sustituye desde el grupo `Backend-prod` (D-K12); el
  token sin resolver apaga la rama de clave.
- `CustomersQueryDto` gana `phone?: string` (últimos 10 dígitos; vacío es 400) y `document?: string` (igualdad exacta)
  (D-K3); `new-customer` guarda el teléfono en 10 dígitos (D-K5).
- `GET /services?name=` y `GET /failures?cause=` (D-K6); `PUT /customers/{id}` parcial y `POST /service-orders` sin
  `serviceDate` ni usuario (D-K9, D-K11).
- No hay usuario «Chatbot QEP» ni migración de datos (D-K11).
- Contrato que QEP consume, en el Anexo C.

## 11. Despliegue y orden de construcción

Backend QEP primero, en este orden, cada fase mergeable sola a `develop`:

1. **Messaging:** interactivos salientes, autoría, `IBotOutbound`, `automation_inbox` + `IAutomationInbox`,
   `RecordBySystem`, columnas de la conversación. Nada visible cambia (nadie encola todavía: el `INSERT` en la
   ingesta entra con un flag `Messaging:Automation:Enqueue` apagado hasta la fase 4).
2. **Tenancy y Authorization:** módulo `automations`, permisos, políticas, roles.
3. **Automations:** proyectos, modelo, agregado, validador, endpoints, plantillas, import/export.
4. **Motor:** `FlowEngine`, worker, puertos y adaptadores; se prende el flag.
5. **Integrations + bloque HTTP:** proveedor `external-api`, política de URL, ejecutor, `http_calls`.
6. **Simulador.**
7. **Frontend** (sobre AH-01..09).
8. **Clasic:** guard, filtros y ajustes de la API (sin usuario bot: D-K11; corregido en el plan 4, 2026-10-23); conexión «API externa» creada en el tenant
   Clasic; flujo importado. El paso a paso está en el README de `qep-backend`, § «Clasic: conexión, importación y corte».
9. **Corte:** el número de Clasic pasa de lulo-chat a QEP (webhook de Meta apuntando a QEP) con el chatbot
   activo; lulo-chat deja de reenviar.

Migraciones: una de Messaging, una de Tenancy, una de Integrations, una inicial de Automations; sin datos que
migrar. `docs/estado.md` se actualiza en `develop` al mergear cada fase.

## 12. Pruebas (TDD, RED antes que GREEN)

- **Unitarias (Automations):** `FlowEngineTests` (cada bloque, intentos, reinicio, variables, interpolación,
  comparación sin tildes, salidas sin conectar), `FlowValidatorTests` (cada código), `ChatbotTests`
  (estados, publicar, versiones), `SystemTemplatesTests` (cada JSON embebido valida), `JsonPathMappingTests`,
  `HttpBlockExecutorTests` (stub handler: timeout, 1 MB, multipart, sin redirección, nada del cuerpo ni la
  clave en logs), `ExternalApiUrlPolicyTests`.
- **Integración (Automations, harness propio con Testcontainers):** `ChatbotsApiTests` (CRUD, If-Match 428/412,
  409 nombre y conexión, 422 publicar inválido, import/export, plantillas), `SimulationsApiTests`,
  `EngineEndToEndTests` (tenant con módulo y conexión, chatbot importado del Anexo B contra un `FakeMetaGraphHandler`
  y un `FakeExternalApiHandler`: entrante → fila en inbox → `DrainAsync` → mensajes salientes con autoría bot
  → botones → lista dinámica → adjunto → HTTP con multipart → resolver; y los casos asesor toma a mitad, ventana
  cerrada, palabra de reinicio, tres inválidos, dos entrantes seguidos en orden), `RunsPersistenceTests` (índice
  de corrida viva, `CHECK`s).
- **Messaging:** `SendInteractiveTests` (payloads de los tres tipos, límites), `BotOutboundTests` (no asigna,
  no pausa, ventana, autoría), `AutomationInboxTests` (la ingesta encola sólo sin asesor y en `None`/`Active`;
  reenvío de Meta no duplica; orden por conversación con `SKIP LOCKED`), `MessagesPersistenceTests` (CHECKs de
  autoría), `ConversationAutomationTests` (`RecordBySystem`, `StepTitle`, `ChatbotName` en el DTO).
- **Integrations:** `ExternalApiConnectionTesterTests`, `ConnectionSecretLeakTests` extendido al nuevo
  proveedor, `ConsumerPortsApiTests` con `external-api`.
- **Tenancy/Authorization:** las de §4.1 (`TenantModuleKeysTests`, `CompositionRootTests`, roles).
- **ArchitectureTests:** `AutomationsLayerTests`, `CompositionRootTests` (handlers registrados).
- **Frontend:** por ruta con `render-route.tsx`: carga, vacío, error y éxito de lista, creación, editor
  (autoguardado 412, validación, publicar 422), simulador; bandeja con autoría bot y chip de paso.
- **Clasic:** sin pruebas automáticas (decisión del developer, 2026-10-23: no se instala Jest ni se escriben pruebas
  unitarias en Clasic por ahora). Cada cambio se verifica a mano con `curl.exe` contra la app local; lo que el flujo espera
  de la API lo fijan `ClasicFlowTests` y `ClasicFlowEndToEndTests` en QEP, contra el contrato del Anexo C.

## 13. Fuera de alcance

- Instagram en el motor (D-M6); bloques Plantilla, Imagen o archivo, Esperar, Etiquetar cliente, Completar
  ficha, Resolver / abrir negocio, Ir a otro flujo; bloque «Asistente con IA».
- Compositor de interactivos para asesores (resto del spec 2); envío proactivo, claves de API de QEP para
  terceros y aviso de visita (resto del spec 3); el envío de `solicitud_os_creada` desde Clasic (D-K4).
- Bots creados por el operador para un tenant (investigación §5, diferido); métricas más allá de `today`.
- Migración del historial de lulo-chat (spec 5).
- Varios chatbots por conexión con prioridad; disparadores por etiqueta o por campaña.

## Anexo A. Modelo del flujo (`flow`, versión 1)

```json
{
  "schema": 1,
  "start": { "firstNodeId": "n1" },
  "variables": [ { "name": "cedula", "type": "text" }, { "name": "equipos", "type": "list" } ],
  "nodes": [
    { "id": "n1", "kind": "message", "title": "Bienvenida", "text": "Hola {nombre}…", "urlButton": { "label": "Registrarme", "url": "https://…" }, "outputs": { "next": "n2" } },
    { "id": "n2", "kind": "openQuestion", "title": "Número de documento", "text": "…", "saveTo": "cedula", "validation": { "kind": "digits" }, "maxAttempts": 3, "outputs": { "answered": "n3", "invalid": null } },
    { "id": "n3", "kind": "http", "title": "Buscar cliente", "connectionId": "…", "method": "GET", "path": "/customers", "query": [ { "key": "identificationNumber", "value": "{cedula}" } ], "body": null, "timeoutSeconds": 8, "okWhen": [200], "mapping": [ { "from": "data[0]", "to": "cliente" } ], "sampleResponse": { "data": [] }, "outputs": { "ok": "n4", "error": "n9" } },
    { "id": "n4", "kind": "condition", "title": "¿Cliente encontrado?", "match": "all", "rules": [ { "variable": "cliente", "operator": "isNotEmpty" } ], "outputs": { "yes": "n5", "no": "n8" } },
    { "id": "n5", "kind": "list", "title": "¿Cuál equipo?", "text": "…", "buttonLabel": "Ver equipos", "options": { "kind": "dynamic", "collection": "equipos", "idField": "id", "titleField": "referenceCode", "descriptionField": "address", "extraOption": { "id": "other", "title": "Otra dirección" } }, "saveTo": "equipo", "fallback": "repeat", "outputs": { "chosen": "n6", "extra": "n10", "tooMany": "n9", "other": null } },
    { "id": "n6", "kind": "buttons", "title": "¿Qué necesitas?", "text": "…", "buttons": [ { "id": "tech", "label": "Servicio técnico" }, { "id": "maint", "label": "Mantenimiento" } ], "saveTo": "motivo", "fallback": "repeat", "outputs": { "tech": "n7", "maint": "n11", "other": null } },
    { "id": "n7", "kind": "attachment", "title": "Foto de la factura", "text": "…", "accept": "any", "saveTo": "factura", "outputs": { "received": "n12", "invalid": null } },
    { "id": "n9", "kind": "handoff", "title": "Pasar a un asesor", "text": "Te paso con un asesor." },
    { "id": "n12", "kind": "http", "title": "Crear orden", "connectionId": "…", "method": "POST", "path": "/service-orders", "body": { "kind": "json", "template": "{ \"customerId\": {cliente.id}, … }" }, "outputs": { "ok": "n13", "error": "n9" } },
    { "id": "n13", "kind": "resolve", "title": "Listo", "text": "Tu orden es la {orden.id}." }
  ]
}
```

`layout`: `{ "positions": { "n1": { "x": 20, "y": 100 }, … } }`. Los `id` de nodo son `^[a-z0-9-]{1,64}$`; los
nombres de variable `^[a-z][a-zA-Z0-9]{0,39}$`; los `id` de botón y fila `^[a-zA-Z0-9_-]{1,40}$`.

El bloque `n3` es ilustrativo (`identificationNumber`, `data[0]`): el contrato real de Clasic está en el Anexo C
(`?document=`, `items[0]`; plan 4, 2026-10-23).

## Anexo B. El flujo de Clasic en bloques

Derivado de `calentadores-classic-backend/src/whatsapp-bot/step-services` (2026-10-21). Se carga por
`POST /chatbots/import` en el tenant Clasic y se ajusta desde el editor. **Corregido en el plan 4 (2026-10-22/23)**: el flujo
importable es `tests/Modules/Automations/Modules.Automations.IntegrationTests/Fixtures/clasic.json` (43 bloques desde la revisión final, 2026-10-23; los ids entre
paréntesis son los suyos), y donde este anexo y el fixture discrepen, gana el fixture.

1. **Horario**? No: el bot actual atiende 24/7. Inicio `NewConversation`, reinicio «reiniciar», 3 intentos.
2. **HTTP** `GET /customers?phone={telefono}` → `{clientesPorTelefono}` (`items`; `policy-lookup`). **Condición** «¿Aceptó
   la política?» (`policy-check`, `match: any`): la lista vacía o `clientesPorTelefono[0].acceptPolicy` igual a `true` → paso 4;
   si no, paso 3. Sólo se mira al **primer** cliente con ese teléfono (el bot viejo miraba a todos).
3. **Pregunta abierta** «Política de datos» (texto de `accept-policy.message.ts`, tuteando), validación `regex`
   `(?i)^(?![\s\S]*\bno\b)[\s\S]*acepto[\s\S]*$`: cualquier texto con «acepto» que no diga «no», como el
   `includes('acepto')` del bot viejo (revisión final, 2026-10-23: las palabras clave rechazaban «Sí, acepto.» o «Acepto!»),
   `invalid` → repite. **HTTP**
   `PUT /customers/{clientesPorTelefono[0].id}` con `{ "acceptPolicy": "true", "sourceAcceptPolicy": "Whatsapp" }`: Clasic
   pone `acceptPolicyDate` (D-K9). Un teléfono que Clasic no conoce no tiene a quién guardarle la aceptación y salta al paso 4.
4. **Mensaje** bienvenida (`welcome`, con la palabra REINICIAR; no repite el saludo de la política). **Pregunta abierta**
   «Número de documento» («sólo números, sin puntos ni espacios»), `regex` `^[0-9]{4,15}$`, `saveTo: cedula`; agotar los
   intentos pasa a un asesor (`document-handoff`) en vez de reiniciar el flujo desde la política (revisión final, 2026-10-23).
5. **HTTP** `GET /customers?document={cedula}` (filtro exacto, D-K3) → `{cliente}` (`items[0]`). **Condición**
   «¿Cliente encontrado?» → no: paso 6; sí: paso 7.
6. **Mensaje** «Usuario no encontrado» con botón URL «Registrarme» (app.clasic.com.co/registro-clientes) y **Botones**
   «Dejar mis datos» / «Otro documento» (el tope de un botón es 20 caracteres) → otro documento: vuelve a la pregunta del
   documento; dejar datos: **Pregunta abierta** nombres, apellidos, dirección, barrio y **ciudad como texto** (D-K7: sin
   lista de departamentos ni de ciudades), **HTTP** `GET /failures` y **Lista** dinámica de fallas, **Pedir adjunto**
   factura, **HTTP** `POST /customers/new-customer` (multipart, con `addressDescription` = «Ciudad: {ciudad}» y
   `departmentId`/`cityId` fijos `5` / `5001000`) → **Resolver** «Recibimos tus datos» con el `serviceOrderNumber`.
7. **HTTP** `GET /invoices/customer/{cliente.id}/all-invoice-items` → `{equipos}`. **Lista** «¿Cuál equipo?»
   dinámica (`referenceCode` / `address`), extra «Otra dirección» → **Pregunta abierta** con la dirección, el barrio y la
   ciudad → **Pasar a un asesor** (D-K8; la dirección queda en «Datos capturados»).
8. **Condición** «¿Tiene orden abierta?» (`equipo.item.haveOrderOpen`) → sí: **Botones** «Ese equipo ya tiene una orden de
   servicio abierta.» con «Elegir otro equipo» (vuelve a la Lista) y «Hablar con un asesor» (`handoff`); no: paso 9
   (revisión final, 2026-10-23: era un mensaje que sólo volvía a la Lista). La regla es **por equipo**, porque
   `haveOrderOpen` es lo que la API expone por elemento; el bot viejo la aplicaba por dirección. **Pendiente del owner:** si
   la quiere por dirección, como el bot viejo, la API tiene que exponer la orden abierta por dirección; hoy la expone por equipo.
9. **Botones** «¿Qué tipo de servicio?» Servicio técnico / Mantenimiento → técnico: **HTTP** `GET /failures` y **Lista**
   dinámica de fallas; mantenimiento: **HTTP** `GET /services?name=STM` → `{servicio}` y `GET /failures?cause=MANTENIMIENTO`
   → `{fallaMantenimiento}` (los dos `$[0]`, D-K6). Sobre `[]` el mapeo deja la variable en `null` y el bloque sale por `ok`,
   así que la **Condición** `maintenance-check` (los dos no vacíos) va antes del paso 10 y, si falta uno, pasa a un asesor
   (revisión final, 2026-10-23).
10. **Pregunta abierta** nombre de contacto y **Pregunta abierta** teléfono de contacto («Sólo números, sin espacios.»,
    `digits`; agotar los intentos pasa a un asesor: revisión final, 2026-10-23).
11. **Dos bloques de crear orden**, según el motivo. Técnico: **HTTP** `GET /service-orders/auto-select-service-type?invoiceItemId={equipo.id}`
    → `{servicio}` y **HTTP** `POST /service-orders` (`create-order`) con `customerId`, `failureId` (la falla elegida),
    `serviceId`, `invoiceItems: [{ id, contact, optionalPhone, update: true }]` (`id` es el número del equipo,
    `{equipo.item.id}` sin comillas: con un id de texto Clasic responde 500, revisión final, 2026-10-23) → **Resolver** con el resumen
    (`summary.message.ts`). Mantenimiento: **HTTP** `POST /service-orders` (`maintenance-order`) con
    `failureId = {fallaMantenimiento.id}` → **Resolver** con el resumen sin «Tipo de daño» y con los precios del
    mantenimiento. Ninguno manda `serviceDate` (D-K9). `error` de cualquiera de los dos (409 orden abierta, 400 bloqueado):
    **Mensaje** y **Pasar a un asesor**. Todo otro bloque HTTP que sale por `error` pasa a un asesor.

Lo que el bot de hoy hace y acá cambia de forma: el correo de cliente nuevo se reemplaza por `new-customer`
(crea la orden en borrador y es lo que ya usa el formulario web), y el correo de «otra dirección» por un asesor (D-K8). La
ciudad **sigue** siendo texto libre (D-K7; este anexo decía lo contrario).

## Anexo C. Contrato de la API de Clasic que consume QEP

**Corregido contra el código de Clasic en el plan 4 (2026-10-22/23).** Rutas relativas a la `baseUrl` de la conexión
(`https://api-clasic.qcode.co`). Las protegidas por `JwtAuthGuard` reciben el header `x-api-key: <BOT_API_KEY>` (D-K1) y actúan
como `{ id: 0, role: 'Administrador' }` (D-K11); el bloque HTTP manda la clave en todas, también en las públicas.

| Uso | Método y ruta | Acceso | Entrada | Salida que usa el flujo |
| --- | --- | --- | --- | --- |
| Cliente por teléfono | `GET /customers?phone={telefono}` | protegida | `wa_id` completo; Clasic compara los últimos 10 dígitos; vacío es 400 (D-K3) | `items[0].id`, `.acceptPolicy`; `totalCount` |
| Cliente por cédula | `GET /customers?document={cedula}` | protegida | igualdad exacta, 0 o 1 elemento (D-K3) | `items[0].id` (vacío: «Usuario no encontrado») |
| Aceptar política | `PUT /customers/{id}` | protegida (Administrador) | cuerpo parcial `{ "acceptPolicy": "true", "sourceAcceptPolicy": "Whatsapp" }`; Clasic pone `acceptPolicyDate` (D-K9) | 200 |
| Direcciones | `GET /invoices/customer/{id}/all-invoice-items` | protegida | — | `[].id`, `.referenceCode`, `.address`, `.haveOrderOpen` |
| Fallas | `GET /failures`, `GET /failures?cause=MANTENIMIENTO` | protegida (es la ruta de prueba, D-K10) | `cause` exacto y sensible a mayúsculas (D-K6) | arreglo en la raíz: `[].id`, `.description` |
| Servicio de mantenimiento | `GET /services?name=STM` | pública | `name` exacto y sensible a mayúsculas (D-K6) | arreglo en la raíz: `[0].id`, `.description` |
| Servicio técnico | `GET /service-orders/auto-select-service-type?invoiceItemId=` | protegida | — | objeto: `id`, `.description` |
| Crear orden | `POST /service-orders` | protegida | `customerId`, `failureId`, `serviceId`, `invoiceItems: [{ id, contact, optionalPhone, update: true }]` (todos los ids son números; `"id": "1"` da 500, revisión final, 2026-10-23); sin `serviceDate` usa ahora (D-K9) | `id`; 409 orden abierta; 400 cliente o dirección bloqueados. `createdBy = 'Whatsapp'` (D-K11) |
| Cliente nuevo | `POST /customers/new-customer` | pública, multipart | `identificationNumber`, `firstName`, `lastName`, `address`, `neighborhood`, `addressDescription`, `phone`, `departmentId`, `cityId`, `failureId` + `file`; el teléfono se guarda en 10 dígitos (D-K5) | `serviceOrderNumber` (un número) |

Fuera del contrato: `GET /departments/{id}` (pública) devuelve el departamento con sus `cities`, pero el flujo no la usa
(D-K7), y **no existe** `GET /cities`. `GET /customers` sin filtro trae todos los clientes, y `?identificationNumber=` es la
búsqueda del admin, por subcadena: el bot no la usa.
