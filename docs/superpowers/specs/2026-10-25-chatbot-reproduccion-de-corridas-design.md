# Automatizaciones: reproducir una corrida del chatbot en el editor («Ejecuciones»)

- **Fecha:** 2026-10-25.
- **Módulos:** `Automations` (registro por bloque, lecturas, simulador), `Messaging` (sólo lectura, por un adaptador en
  Bootstrapper; sólo suma `RunId` a «Datos capturados», §6.2), `Authorization` sin cambios (P-E1: permiso existente). Frontend en `qep-frontend`,
  `features/automations/` y un enlace desde `features/messaging/`.
- **Estado:** borrador para revisión del owner. Ninguna decisión está aprobada: todas las de §4 tienen origen «propuesta» y las
  bifurcaciones de fondo están en §16 como DECISIÓN-PENDIENTE. **No se escribe código hasta que el owner lo apruebe.**
- **Es un delta sobre:** [`2026-10-21-constructor-de-chatbots-design.md`](2026-10-21-constructor-de-chatbots-design.md) (§4.2 modelo,
  §5.2 unidad, D-M2, D-M8, D-H5..D-H8, D-S1..D-S3) y [`2026-10-24-causa-del-traspaso-design.md`](2026-10-24-causa-del-traspaso-design.md)
  (D-T3, D-T6). Planes que lo implementaron: [`plan 2`](../plans/2026-10-22-constructor-de-chatbots-backend-2.md) (tareas 8, 11, 13) y,
  en `qep-frontend`, `docs/superpowers/plans/2026-10-22-constructor-de-chatbots-frontend.md` (P-14).
- **Verificado contra el código el 2026-10-25:** `qep-backend` en `develop` (`96c43acf`), `qep-frontend` en `develop` (`f1cff1c3`).
  Donde este documento y el código discrepen, gana el código.
- **Revisión del owner, 2026-10-26** (§0): la respuesta HTTP de las corridas reales **sí** se guarda, la retención del detalle baja a
  **3 días** y los cuerpos van a R2, no a Postgres. Eso resuelve P-E1..P-E5 y P-E8, reemplaza D-E3, D-E7 y D-E13 y agrega D-E15..D-E19.
  Quedan abiertas sólo P-E6 y P-E7, ambas fuera de la primera entrega.
- **Estado vigente: APROBADO por el owner el 2026-10-26, documento completo.** Las filas D-E que dicen «propuesta» quedan aprobadas
  tal como están escritas; lo de «borrador» de la línea **Estado** de arriba es histórico.

## 0. Cambios del 2026-10-26 (decididos por el owner)

| Tema | Borrador del 2026-10-25 | Decisión del 2026-10-26 | Origen |
| --- | --- | --- | --- |
| Respuesta HTTP en corridas reales | No se guarda (opción B de §3.1); sólo en el simulador, si P-E3 lo aprobaba | **Se guarda siempre**, en éxito y en error, sin modo opt-in. Se levanta la restricción de D-H6/D-H7 **sólo para el cuerpo de la respuesta**: la petición, los headers y la clave siguen fuera | owner |
| Quién ve la respuesta | — | El tenant, dentro de la app; nunca sólo soporte de QCode | owner |
| Retención del detalle | 30 días | **3 días** para `run_units` y para los cuerpos | owner |
| Dónde viven los cuerpos | — | **R2**, con regla de ciclo de vida por prefijo. La base de datos es compartida con otras aplicaciones: los cuerpos no van a Postgres | owner |
| Pérdida aceptable | — | **Se puede perder** el cuerpo de una llamada (reinicio del proceso, cola llena, R2 caído). Nunca puede frenar ni romper el bot | owner |
| Vista | §8 | Confirmada: lienzo de la versión que corrió con el camino marcado y panel por bloque (mockup B del 2026-10-26) | owner |

Motivo del owner: el bloque HTTP llama a plataformas externas y hoy no se sabe qué respondieron; sin eso no se puede depurar ni
saber si una integración está fallando. La razón escrita de D-H7 era el **tamaño** de la corrida («no puede crecer sin límite por una
API verbosa»), no la seguridad; con los cuerpos fuera de la corrida y fuera de Postgres, esa razón deja de aplicar. La clave sigue
protegida por D-H5 y por D-E17.

## 1. Por qué

Hoy, cuando un chatbot hace algo raro en una conversación real, la única forma de saber qué pasó es leer `conversation_runs` y
`http_calls` a mano en la base, como se hizo el 2026-10-24 con Clasic (`2026-10-24-causa-del-traspaso-design.md` §1). El editor no
tiene ninguna vista de corridas: `GET /automations/runs` existe en el backend (`RunEndpoints.cs:17-21`) pero **el frontend no lo
consume** (búsqueda de `/runs` en `qep-frontend/src`: cero resultados), y la traza que el asesor recibe en «Datos capturados» no se
dibuja a propósito (`captured-data-panel.tsx:10-15`, P-14 del plan del frontend).

**Pedido:** en el editor del chatbot, una vista como la de «Executions» de n8n: eliges una corrida y ves, bloque por bloque, qué
entró, qué produjo (variables, mensajes enviados, salida tomada) y, en los bloques HTTP, la petición hecha y la respuesta recibida.

**Dentro:** registro por bloque de las corridas reales y de las simulaciones; lista de corridas; vista de una corrida con el lienzo
de **su** versión, el camino tomado y un panel por bloque; panel HTTP; retención y permisos.

**Fuera** (§13): volver a ejecutar efectos (mensajes, llamadas HTTP) de una corrida real; métricas agregadas. (La captura de la
respuesta cruda de corridas reales **entró** el 2026-10-26, §0.)

## 2. Qué existe hoy y sobre qué se puede construir

### 2.1 Lo que se guarda

| Dato | Dónde | Qué tiene | Límite |
| --- | --- | --- | --- |
| La corrida | `automations.conversation_runs` (`AutomationsDbContext.cs:179-210`) | tenant, conversación, conexión, chatbot, **versión** (`chatbot_version_id`), estado por nombre, `current_node_id`, `step_number`, `attempts`, `variables` (jsonb), `trace` (jsonb), `started_at`, `updated_at`, `ended_at`, `failure_code`, `last_inbound_message_id` | **No se purga nunca**: el barrido sólo abandona, purga el inbox y `http_calls` (`AutomationInboxWorker.cs:109-121`) |
| La traza | `conversation_runs.trace`, `TraceEntry(At, NodeId, Kind, Outcome)` (`RunState.cs:20`) | una entrada por bloque en que se entra o del que se sale: la salida tomada (`ok`, `yes`, el id del botón…) o `waiting` / `pending` / `handedOff` / `resolved` / `restart` (`RunState.cs:23-37`) | **200 entradas**, se pierden las más viejas (`RunState.cs:54`, `FlowEngine.cs:368-377`) |
| Las variables | `conversation_runs.variables` (`ConversationRun.cs:42`, `:91`) | **sólo la foto final**: cada unidad la reescribe entera | el reinicio deja sólo las fijas (`FlowEngine.cs:233-240`, `RunVariables.cs:27-39`) |
| La versión del flujo | `automations.chatbot_versions` (`ChatbotVersion.cs:5-42`) | `flow` **y `layout`** de cada publicación, inmutables | se borran en cascada con el chatbot (`AutomationConversationDetails.cs:25`) |
| Cada llamada HTTP | `automations.http_calls` (`AutomationsDbContext.cs:212-233`, `HttpCallLog.cs:20-40`) | `run_id` **o** `simulation_id`, `node_id`, conexión, método, ruta interpolada **sin query**, status, `duration_ms`, `outcome`, `error_code`, `called_at`. Nunca cuerpo, headers ni clave (D-H6) | 30 días (`AutomationsWorkerOptions.cs:23`); sin fila si no hubo conexión usable (`HttpBlockExecutor.cs:91-107`) |
| Los mensajes del bot | `messaging.messages`, con `client_id` indexado por conversación (`MessagingDbContext.cs:293`, `:316`) y autoría `sent_by_chatbot_id` | texto, `details` del interactivo, estado (`Sent`, `Delivered`, `Read`, `Failed`, `MessageEnums.cs:59-65`) | el `client_id` es `DeterministicClientId.For(runId, baseStepNumber, effectIndex)` (`DeterministicClientId.cs:18-19`): un hash, **no dice de qué bloque salió** |
| Lo que escribió el cliente | `messaging.messages` (entrantes) | texto, `interactive.*_reply.id`, adjunto | la corrida sólo guarda el **último** entrante (`ConversationRun.cs:55-58`) |
| La simulación | `automations.simulations` (`Simulation.cs:14-109`) | estado del motor **con su traza** (`Simulation.cs:100-104`) y la transcripción (≤ 500 líneas, `:21`) | TTL 2 h (`:17`); se borra al cerrar el panel (`Simulations.cs:422-436`); `SimulationDto` **no expone la traza** (`Simulations.cs:183-192`) |
| «Datos capturados» | `GET /messaging/conversations/{id}/automation` (`GetConversationAutomation.cs:12-46`) | chatbot, versión, estado, paso, **variables completas** y traza de la corrida más reciente | con `messaging.conversation.read` (`:37`): el asesor ya ve las variables, PII incluida |
| La lista de corridas | `GET /automations/runs?chatbotId=&from=&to=` (`Runs.cs:9-54`) | id, conversación, estado, nodo actual, step, inicio, fin, duración, `failure_code` | 7 días por defecto, tope 500 (`AutomationsRepositories.cs:129`, `:163-175`); **carga las filas enteras con `trace` y `variables`** para devolver nueve campos |

Lo que el lienzo ya sabe hacer: modo de sólo lectura (`flow-canvas.tsx:206-209`), marcar **un** bloque como actual
(`CanvasMarks.currentNodeId`, `flow-canvas.ts:22-26`, `:133`) y dibujar las aristas desde el documento (`toCanvasEdges`, `:208`). No
marca aristas recorridas ni varios bloques a la vez. El panel derecho tiene dos pestañas, «Propiedades» y «Simular»
(`editor-side-panel.tsx:60-63`).

### 2.2 Lo que hoy NO queda registrado por bloque

1. **Qué entró al bloque.** `EngineInput.CustomerMessage` no lleva el id del mensaje (`EngineInput.cs:18`); la corrida sólo recuerda
   el último entrante. No hay forma de saber qué respuesta del cliente sacó a un bloque por `invalid` o por el botón `b2`.
2. **Qué escribió el bloque.** `Save` (respuesta del cliente, `FlowEngine.cs:181-187`) y `Merge` (mapeo HTTP, `:164-170`) escriben en
   las variables sin anotar nada en la traza. Lo único que sobrevive es la foto final, que pisa valores repetidos y pierde todo lo
   anterior a un reinicio.
3. **Qué mensajes mandó.** Los efectos `SendText`/`SendInteractive` llevan `NodeId` (`Effect.cs:11-13`) pero no se guardan; el
   `clientId` se calcula en `RunEffects.cs:64` y no se anota en ningún lado. El mensaje existe en Mensajería, pero no se puede
   atribuir a un bloque.
4. **Qué llamada HTTP corresponde a qué paso.** `http_calls` no tiene `step` ni enlace con la traza. Un bloque HTTP visitado dos veces
   (un reinicio, o hasta `EngineReentries.Max = 25` reentradas, `EngineReentries.cs:12`) deja filas que sólo se distinguen por
   `called_at`.
5. **La respuesta HTTP.** Se lee hasta 1 MB y se descarta salvo lo que dice el mapeo (`HttpBlockExecutor.cs:216-234`, D-H7). No queda
   ni su tamaño.
6. **La petición HTTP.** Sólo la ruta sin query. La query, el cuerpo y los campos del multipart no se guardan (D-H6).
7. **El tiempo de cada bloque.** Todas las entradas de una unidad llevan el **mismo** `At`: el `now` de la unidad
   (`FlowEngine.cs:141`, `:370`; `RunEffects.cs:56`). Sólo el HTTP tiene duración real (`duration_ms`).
8. **La evaluación de una Condición.** Se guarda la salida (`yes`/`no`), no qué regla cumplió ni con qué valor.

### 2.3 Lo que hace esto más difícil de lo que parece

- **Las unidades deshechas dejan rastro a medias.** `http_calls` se confirma en su propio scope, fuera de la transacción de la unidad
  (`HttpCallLog.cs:8-11`, `RunEffects.cs:270-274`); la traza, no. Si la unidad se deshace después de llamar, el reintento llama otra
  vez (P2-R37) y quedan **dos filas de `http_calls` para una sola visita** de la traza. La vista tiene que mostrarlo, no esconderlo.
- **El tope de 200 de la traza y la retención de 30 días de `http_calls` no coinciden con la vida de la corrida**, que es infinita.
  Una corrida vieja tiene traza recortada y ninguna llamada.
- **Una corrida de un chatbot borrado no tiene lienzo**: las versiones se van en cascada y la corrida queda.
- **El simulador corre el borrador vigente en cada mensaje**, no el del arranque (`Simulations.cs:356-358`, `:405-418`): si el admin
  edita a mitad, los pasos ya registrados apuntan a bloques que pueden no existir. Y la sesión vive 2 h o hasta cerrar el panel.
- **El orden lo da `StepNumber`, no `At`.** `StepNumber` sube en cada bloque en que se entra, nunca vuelve a cero (P2-R5,
  `RunState.cs:46-47`, `FlowEngine.cs:307`) y es estable entre reintentos de la misma unidad porque cada unidad arranca de su base. Es
  la llave natural de una visita: el bloque que espera y su salida, o el HTTP `pending` y su `ok`/`error`, comparten el mismo step.
- **Un mapeo puede guardar la respuesta entera** (`from: "$"`), hasta 1 MB, en una sola variable. Registrar «lo que escribió el bloque»
  sin tope multiplicaría ese tamaño por cada visita.
- **Las URL de adjuntos son firmadas por 15 min** (D-M8) y se refrescan antes de cada paso (`RunEffects.cs:50-52`): registrarlas no
  sirve y una URL firmada no debe quedar en un registro.

## 3. Las dos tensiones

### 3.1 D-H5/D-H6: lo que responde una API externa no se guarda

La regla vigente (CLAUDE.md, gotcha «Nada de lo que responde una API externa…»; D-H6; D-H7) es que ni el cuerpo, ni los headers, ni la
clave llegan a logs, auditoría ni `http_calls`, y que la respuesta se guarda **sólo a través del mapeo**. Lo que n8n muestra (la
respuesta cruda) choca de frente con eso.

| Opción | Qué se ve en una corrida real | A favor | En contra |
| --- | --- | --- | --- |
| A. Detalle completo sólo en simulaciones | Real: nada nuevo de HTTP. Simulación: petición y respuesta crudas | Cero datos nuevos de terceros en corridas reales | La vista de corridas reales no sirve para depurar un HTTP, que es justo lo que pasó el 2026-10-24 |
| **B. Real: estructural + lo que el flujo ya mapea** | Método, ruta, status, duración, `outcome`, `error_code`, **tamaño** de la respuesta, conexión, y por cada `from` del mapeo si encontró algo y el valor que dejó en su variable; la petición **reconstruida** desde la versión y las variables de ese paso | No guarda nada que la corrida no guarde ya (`conversation_runs.variables` tiene esos mismos valores y el asesor los ve); respeta D-H7 al pie de la letra | No muestra la parte de la respuesta que el mapeo no tomó: un `from` mal escrito se ve como «no encontró nada», no qué había |
| C. Captura opt-in por chatbot, con TTL y redacción | Respuesta cruda recortada (p. ej. 64 KB) durante una ventana que el admin abre (p. ej. 24 h) | Es lo que más ayuda a arreglar un mapeo contra la API de verdad | Contradice D-H5/D-H6 y el gotcha; guarda datos de un tercero; la redacción por nombre de campo es frágil; exige auditoría del encendido |
| D. Sólo nombres de variables mapeadas, sin valores | Igual que B, sin valores | Mínimo absoluto | Pierde lo más útil (qué valor tomó el flujo) y no ahorra nada: los valores ya están en la corrida |

~~Recomendación: B para corridas reales, y A además para el simulador.~~ **Decisión del owner (2026-10-26): C sin opt-in**, siempre
encendida, con retención de 3 días, cuerpos en R2 y sin redacción por nombre de campo. Lo que sí se redacta es la clave de la
conexión si la API la devuelve (D-E17). Lo estructural de B (status, duración, tamaño, mapeo con ✓/✗, petición reconstruida) se
mantiene: la respuesta cruda se suma, no reemplaza. Ver D-E15..D-E19.

### 3.2 La PII de los clientes

Las variables tienen nombres, documentos, teléfonos y direcciones (Anexo B del spec del constructor), y los textos de los mensajes del
bot ya vienen interpolados con ellos. Lo que cambia con esta vista **no es el tipo de dato** —el asesor ya ve las variables completas
en «Datos capturados»— sino **la cantidad y la forma**: el historial de todas las conversaciones de un chatbot, en una sola lista, con
el valor que tuvo cada variable en cada paso.

Cómo se trata (D-E6..D-E8):

- **No se copia lo que ya vive en Mensajería.** Los textos de los mensajes (los del bot y los del cliente) se leen de
  `messaging.messages` por `client_id` / `message_id` al abrir la corrida; el registro sólo guarda ids. Si Mensajería no tiene el
  mensaje, la vista dice «mensaje no disponible».
- **Los valores escritos por paso se guardan con retención propia** (**3 días**, §0), en una tabla aparte que se purga. Pasado eso,
  la corrida se sigue viendo con la traza de hoy (bloque, salida) y sin valores.
- **Las respuestas HTTP crudas pueden traer PII de clientes** (saldos, documentos, direcciones) o tokens que emite la API externa. El
  owner lo aceptó el 2026-10-26: quien tenga el permiso de P-E1 las ve durante 3 días. Nunca llegan a logs, auditoría ni a Postgres.
- **Permiso:** `automations.chatbot.read`, el existente (P-E1, owner 2026-10-26), revalidado en el handler con tenant y módulo (403,
  nunca 404). Quien ve el chatbot ve sus corridas y sus respuestas.
- **Sin URL firmadas en el registro**: de un adjunto se guardan `fileName`, `mimeType` y `messageId`, nunca `url`.
- **Topes por valor y por fila** (§9) para que un mapeo `$` no copie una respuesta de 1 MB por visita.

## 4. Alcance: qué significa «reproducir»

| Opción | Qué es | Riesgo |
| --- | --- | --- |
| **Visualizar una traza registrada** | La vista lee lo que pasó y lo dibuja. Navegar paso a paso es mover un cursor sobre pasos ya guardados | Ninguno: no hay efectos |
| Re-ejecutar la corrida real | Volver a correr el motor con la versión y las entradas grabadas | Manda mensajes reales al cliente y vuelve a llamar APIs que **no son idempotentes** (CLAUDE.md, «Una llamada HTTP del bloque no se puede deshacer», P2-R37; Clasic no tiene llave de idempotencia): un `POST /service-orders` crea otra orden. **Descartado** |
| Re-ejecutar en el simulador con las entradas grabadas | Abrir una simulación del **borrador** y alimentarla con los mensajes del cliente de la corrida, con `liveHttp` apagado; los bloques HTTP responden con lo que la corrida mapeó o con su respuesta de ejemplo | Seguro: el simulador no toca conversaciones, inbox ni corridas (`Simulations.cs:14-15`), y con `liveHttp` apagado no llama a nadie (D-S2). Sólo «Horario de atención» consulta el puerto real, que es lectura (D-S3) |

**Recomendación:** la primera entrega **sólo visualiza** trazas registradas, de corridas reales y de simulaciones (D-E1, D-E2). La
re-ejecución en el simulador es el único camino seguro que muestra el código y queda como slice posterior (P-E6).

## 5. Decisiones

| ID | Decisión | Motivo | Origen |
| --- | --- | --- | --- |
| D-E1 | **Reproducir es visualizar.** Ninguna acción de la vista ejecuta un efecto: no manda mensajes, no llama APIs, no toca la conversación ni la corrida. | §4: los envíos llegan a clientes reales y el HTTP no es idempotente (P2-R37). | propuesta |
| D-E2 | **Mismo modelo de pasos para corridas reales y simulaciones.** Una sola forma (`RunStepDto`, §7) que la pantalla pinta igual; cambia de dónde sale. | Un solo panel por bloque en el frontend; el simulador ya comparte motor con el procesador (D-M2). | propuesta |
| D-E3 | **Corridas reales: HTTP estructural + valores mapeados + respuesta cruda.** Se agrega a `http_calls` el `step_number` de la visita, el `response_bytes` y `response_object` (la llave del cuerpo en R2, D-E15); nunca headers, query ni clave en ninguna parte. Lo que el mapeo dejó en cada variable se ve como escritura del paso. | Reemplazada el 2026-10-26 (§0): antes era la opción B. | owner |
| D-E4 | **La petición de una corrida real se reconstruye al leer, no se guarda.** El backend vuelve a armar `HttpBlockRequest.Build(node, variables)` (`HttpBlockRequest.cs:25-39`, puro) con el nodo de la versión y las variables de ese paso (fijas + escrituras anteriores), y la marca `reconstructed: true`; `exact: false` si una variable usada se recortó (§9). La clave nunca: el header de autenticación se muestra por nombre (`x-api-key: ••••`), sin leer el secreto. | D-H6 dice «nunca cuerpo»; reconstruir evita una copia nueva de la PII del cuerpo. El armado es determinista. | propuesta |
| D-E5 | **El registro por bloque va en una tabla nueva, `automations.run_units`, una fila por unidad del procesador**, escrita en la misma transacción que la corrida; `conversation_runs.trace` se queda como está (lo lee «Datos capturados»). | Una unidad es la frontera real (un entrante, un recorrido, un guardado): si se deshace, su fila se va con ella y la traza no miente. Separada, se purga sin tocar la corrida. | propuesta |
| D-E6 | **Los textos de mensajes no se copian:** la fila guarda `clientId` de cada envío y el `messageId` del entrante; la lectura los resuelve en Mensajería por un puerto de Automations adaptado en Bootstrapper sobre `MessagingDbContext`. | Mensajería es la dueña de los mensajes y de su retención. Precedente: `PipelineConversationDirectory` (Bootstrapper) lee `MessagingDbContext` para Pipeline. | propuesta |
| D-E7 | **Retención del detalle: 3 días** (`Automations:Workers:RunDetailRetentionDays`, default 3), purgado en el barrido existente por lotes de 1000, como `http_calls` (`AutomationInboxWorker.cs:116-119`, `:195-208`). Vencido, la vista cae a la traza de `conversation_runs` con el aviso «El detalle de esta corrida ya venció». `http_calls` sigue en 30 días: una corrida de entre 3 y 30 días muestra sus llamadas (status, duración) sin pasos ni cuerpos, nunca al revés. | Owner, 2026-10-26: basta con mirar 3 días atrás, y menos días es menos carga en la base compartida. | owner |
| D-E8 | **Topes:** un valor escrito se guarda si su JSON compacto mide ≤ 8 KB; si no, `{ "truncated": true, "bytes": N }`. Una fila de unidad ≤ 256 KB (si se pasa, se descartan valores desde el más grande). URL de adjuntos nunca. | §2.3: un mapeo `$` copiaría hasta 1 MB por visita. | propuesta |
| D-E9 | **El orden de los pasos es `(step, seq)`**, nunca `at`. `seq` distingue la entrada (`waiting`/`pending`) de la salida del mismo bloque en el mismo step. | Las entradas de una unidad comparten `At` (§2.2-7). | propuesta |
| D-E10 | **El lienzo de una corrida es el de su versión** (`chatbot_versions.flow` + `layout`), en sólo lectura, nunca el borrador. Sin versión (chatbot borrado), la vista muestra la lista de pasos sin lienzo. | D-C1: la corrida termina con la versión con que empezó; el borrador puede no tener esos bloques. | propuesta |
| D-E11 | **El backend arma el camino para la pantalla:** pasos ordenados con título del bloque, aristas recorridas con su cuenta (`from`, `output`, `to`, `count`) y llamadas HTTP ya unidas a su paso. El frontend no cruza tablas ni conoce `StepNumber`. Los enums viajan por nombre (`RunStatus`, salidas, `HttpCallOutcomes`, `MessageStatus`). | BFF (CLAUDE.md, «Las APIs se construyen como backend for frontend»). | propuesta |
| D-E12 | **`GET /runs` pasa a una proyección con paginación por cursor** y trae lo que la lista dibuja: contacto, número de versión, último bloque con título, cantidad de HTTP fallidos y si hay detalle. Se puede cambiar la forma: no tiene consumidores en el frontend. | Hoy carga 500 filas con `trace` y `variables` (`AutomationsRepositories.cs:163-175`) para nueve campos, y la lista sin contacto no sirve para encontrar «la de Ana». | propuesta |
| D-E13 | **Simulador:** la simulación guarda sus pasos con la misma forma (jsonb en `automations.simulations`, tope 500 como la transcripción) y el número de versión del borrador con que corrió cada paso; `SimulationDto` gana `steps` y `edges`. Sin `liveHttp`, el panel HTTP muestra la respuesta de ejemplo (es del admin, no de un tercero). Con `liveHttp`, la respuesta cruda se captura **igual que en una corrida real** (D-E15): mismo prefijo de R2, misma cola, misma redacción. | D-E2; el borrador cambia entre mensajes (§2.3). Un solo camino de captura para los dos (owner, 2026-10-26, resuelve P-E3). | propuesta |
| D-E15 | **Los cuerpos de respuesta viven en R2, nunca en Postgres.** Llave `automations/http-responses/{tenantId}/{responseId}.json.gz`, con `responseId` un `Guid` que genera el ejecutor al terminar la llamada y que se escribe en `http_calls.response_object` **en el mismo insert** de la fila (sin un `UPDATE` posterior). Comprimido con gzip. Se guarda lo que se leyó, hasta el tope existente de 1 MB (`MaxResponseBytes`); una respuesta que pasa el tope sigue siendo `response_too_large` y no se guarda nada. | Owner, 2026-10-26: la base es compartida con otras aplicaciones; R2 ya está disponible en el módulo Storage y su ciclo de vida purga sin `DELETE` en la base. | owner |
| D-E16 | **La subida a R2 es asíncrona y se puede perder.** El ejecutor deja el cuerpo en una cola en memoria acotada (`Channel<T>` con capacidad fija y `BoundedChannelFullMode.DropWrite`) y un `BackgroundService` lo sube. Si la cola está llena, el proceso se reinicia o R2 falla, el cuerpo se pierde: la fila de `http_calls` ya tiene `response_object` y la lectura responde «La respuesta de esta llamada no se guardó». Un fallo de subida se registra en el log **sólo con ids** (tenant, `responseId`, tipo de la excepción). | Owner, 2026-10-26: perder algún cuerpo es aceptable; frenar o romper al bot no. La llamada HTTP no espera a R2. | owner |
| D-E17 | **La clave nunca se guarda, tampoco si la API la devuelve.** Antes de encolar, el cuerpo se recorre y cada aparición literal del valor de `apiKey` se reemplaza por `••••` (comparación exacta de bytes, sobre el cuerpo ya leído y antes de comprimir). Los headers de la respuesta no se guardan. | D-H5 sigue vigente. Una API de eco (tipo httpbin) devuelve los headers de la petición en el cuerpo; sin esto la clave terminaría en R2. | propuesta |
| D-E18 | **Se lee el cuerpo también cuando el status no cumple.** Hoy `SendAsync` vuelve sin leerlo (`HttpBlockExecutor.cs:182-186`); pasa a leerlo con el mismo tope, sólo para capturarlo. El resultado del bloque no cambia (`error_status` con su `http_{status}`) y el mapeo sigue sin correr sobre un error. Timeout, red y URL rechazada no tienen cuerpo. | Owner, 2026-10-26: la respuesta de un error es justo la que más hace falta para depurar. | owner |
| D-E19 | **El cuerpo se pide aparte, al abrir la llamada.** `RunReplayDto` no lo trae; el panel lo pide con `GET /runs/{runId}/http-calls/{callId}/response`, que el backend lee de R2 y devuelve (nunca una URL firmada). Fuera de la ventana de 3 días contada desde `called_at`, responde 404 `automations.http_response.expired` aunque el objeto siga en R2: el ciclo de vida de R2 borra con hasta 24 h de retraso. | La corrida puede tener decenas de llamadas de hasta 1 MB; el detalle sólo necesita la que el usuario abre. La ventana la impone el backend, no el bucket. | propuesta |
| D-E14 | **Las corridas que cierra alguien que no es el motor también dejan unidad:** `Interrupted` (asesor o módulo apagado, `RunProcessor.cs:128-136`, `:150-166`), `Abandoned` (barrido) y `Completed` por reinicio con versión nueva, con su motivo y sin pasos. | La vista tiene que explicar por qué una corrida terminó en un bloque que espera. | propuesta |

## 6. Qué se construye

### 6.1 Backend: módulo `Automations`

**Modelo** (migración `AddRunUnits` en `AutomationsDbContext`):

```sql
CREATE TABLE automations.run_units (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tenant_id uuid NOT NULL, run_id uuid NOT NULL,
  base_step int NOT NULL,                    -- StepNumber al empezar la unidad (la misma base de los clientIds, P2-R5)
  trigger varchar(16) NOT NULL,              -- Start | CustomerMessage | Resume | Closed
  inbound_message_id uuid NULL,              -- el entrante del inbox que procesó la unidad
  outcome varchar(24) NOT NULL,              -- el RunOutcomes de la fila (started, advanced, ended, interrupted, …)
  started_at timestamptz NOT NULL, ended_at timestamptz NOT NULL,   -- reloj real, no el now del motor
  steps jsonb NOT NULL DEFAULT '[]',         -- [{step, seq, nodeId, kind, outcome, writes?, sends?, input?, reset?}]
  closed_status varchar(16) NULL, closed_reason varchar(64) NULL    -- D-E14
);
-- Un entrante, una unidad confirmada (P2-R24); los cierres del barrido no tienen entrante.
CREATE UNIQUE INDEX "IX_run_units_run_inbound" ON automations.run_units (run_id, inbound_message_id) WHERE inbound_message_id IS NOT NULL;
CREATE INDEX "IX_run_units_tenant_run" ON automations.run_units (tenant_id, run_id, base_step);

ALTER TABLE automations.http_calls
  ADD COLUMN step_number int NULL,           -- D-E3: la visita del bloque; NULL en filas viejas
  ADD COLUMN response_bytes int NULL,        -- leído y bajo el tope; NULL si no se leyó (timeout, red, demasiado grande)
  ADD COLUMN response_object varchar(200) NULL;  -- D-E15: llave en R2; NULL si no hubo cuerpo que guardar

ALTER TABLE automations.simulations
  ADD COLUMN steps jsonb NOT NULL DEFAULT '[]';   -- D-E13
```

El procesador ya corta el reintento de una unidad confirmada por `LastInboundMessageId` (P2-R24, `RunProcessor.cs:110-116`); el
índice único es la segunda capa, como en el resto del repo. Ojo: el entrante que **arranca** una corrida y el que la cierra
`Interrupted` antes de arrancar otra (`RunProcessor.cs:130-136`) son el mismo mensaje en dos corridas distintas, por eso la llave
incluye `run_id`.

**Motor (Domain, sigue puro).** `FlowEngine.Step` devuelve, además de estado y efectos, los **registros de paso** del recorrido:
`StepRecord(Step, Seq, NodeId, Kind, Outcome, Writes, Reset)`. `Writes` son las variables que escribió `Save` o `Merge` antes del
`Leave` (nombre y valor), `Reset` marca un reinicio. Los efectos de envío ganan el `Step` del bloque que los emitió, para que el
procesador una cada `clientId` con su paso. `TraceEntry` y `conversation_runs.trace` no cambian.

**Procesador.** `RunEffects.RunAsync` (`RunEffects.cs:35-136`) acumula los `StepRecord` de cada paso, les agrega los `clientId` que
calcula (`:64`), las URL de adjunto quitadas, los topes de D-E8, y al final de la unidad `RunProcessor` agrega la fila de
`run_units` **antes** del único `SaveChanges` (`RunProcessor.cs:138-141`). `EngineInput.CustomerMessage` gana `MessageId` para el
`input` del paso que lo consumió. Los cierres de D-E14 escriben su fila sin pasos. Un fallo al armar la fila de registro **no puede**
deshacer la unidad (repetiría envíos y HTTP): se registra en el log sin datos (sólo ids) y la unidad sigue sin fila.

**Bloque HTTP.** `HttpCallContext` gana `StepNumber` (`IHttpBlockExecutor.cs:8`); `HttpCallEntry` gana `StepNumber` y `ResponseBytes`
(`:27-29`); `HttpBlockExecutor` cuenta los bytes leídos en `ReadCappedAsync` (`HttpBlockExecutor.cs:239-261`); el cuerpo se captura como dice el
párrafo siguiente.
`HttpCallLog.RecordAsync` escribe las columnas nuevas (`HttpCallLog.cs:24-38`).

**Captura de la respuesta (D-E15..D-E18).** `SendAsync` lee el cuerpo también en un status que no cumple (D-E18). Con el cuerpo leído,
`FinishAsync` (`HttpBlockExecutor.cs:424-453`) genera el `responseId`, redacta la clave (D-E17), escribe `response_object` en la
fila y encola `(tenantId, responseId, body)` en `IHttpResponseCapture`, un puerto de `Application` que nunca lanza ni espera: devuelve
`false` si la cola está llena. La implementación vive en `Infrastructure`: un `Channel<T>` acotado (capacidad en
`Automations:HttpResponses:QueueCapacity`, default 256) y un `HttpResponseUploadWorker : BackgroundService` que comprime y sube.
Para hablar con R2 usa el cliente que expone el módulo Storage (`StorageInfrastructureExtensions.cs:61,81`) detrás de un puerto propio
de Automations; si `AutomationsLayerTests` no permite la referencia, el adaptador va en Bootstrapper como los otros puertos. El
precedente de subida a R2 con tope es `MediaTransfer.RunInstagramAsync` (`Messaging.Infrastructure/Media/MediaTransfer.cs:194-258`).
Ningún log lleva el cuerpo; el `HttpClient` sigue con `RemoveAllLoggers()`.

**Ciclo de vida de R2 (paso manual, fuera del código).** Una regla en el bucket que borra a los 3 días **sólo** el prefijo
`automations/http-responses/`. El bucket guarda también los adjuntos de Mensajería: una regla sin prefijo los borraría. La regla se
crea en Cloudflare antes de desplegar RC-B1 y el plan deja el paso escrito para el owner.

**Barrido.** Un paso más en `SweepAsync` (`AutomationInboxWorker.cs:109-121`), `run_units_purge`, con `PurgeAsync` por lotes y
`RunDetailRetentionDays` (default 3, D-E7) en `AutomationsWorkerOptions`.

**Simulador.** `SimulationRunner.RunAsync` (`Simulations.cs:35-92`) recoge los `StepRecord` igual que el procesador y
`Simulation.Apply` (`Simulation.cs:70-79`) los agrega a `steps` con el `version` del chatbot leído en ese mensaje. Con `liveHttp` y
la respuesta se captura por el mismo camino que una corrida real (D-E13, D-E15): la fila de `http_calls` con `simulation_id` lleva su
`response_object`, y el paso de la simulación apunta a esa llamada. Nunca en un log.

**Puertos nuevos** (declarados en `Modules.Automations.Application`, adaptados en Bootstrapper; Automations sigue sin referenciar
Messaging, `AutomationsLayerTests`):

- `IAutomationRunMessages.FindAsync(tenantId, conversationId, clientIds, inboundMessageIds)` → por cada id: `messageId`, `kind`,
  `text`, `replyId`/`replyTitle` del interactivo, adjunto (`fileName`, `mimeType`) y `status` por nombre. Lee `messaging.messages`
  con `IX_messages_conversation_client`.
- `IAutomationRunContacts.FindAsync(tenantId, conversationIds)` → nombre de perfil y teléfono (`wa_id`, `null` en Instagram) para la
  lista. Una consulta por página.

**Endpoints** (grupo `/api/v1/tenants/{tenantId:guid}/automations`, `TenantModuleGuard(automations)`; las rutas `/runs/…` con el
permiso de P-E1 y las `/simulations/…` con `automations.chatbot.manage`, como hoy el simulador, §10):

| Ruta | Respuesta | Notas |
| --- | --- | --- |
| `GET /runs?chatbotId=&from=&to=&status=&cursor=` | `RunListResponse` | D-E12. `status` repetible por nombre. Rango por defecto 7 días como hoy (`Runs.cs:30`). Página de 50 |
| `GET /runs/{runId}` | `RunReplayDto` | §7. Otro tenant es 403 antes de llegar al repositorio; 404 `automations.run.not_found` sólo si la corrida no existe en el tenant de la ruta |
| `GET /simulations/{id}` | `SimulationDto` con `steps` | no existe hoy; hace falta para reabrir el panel sin mandar un mensaje |
| `GET /runs/{runId}/http-calls/{callId}/response` | `HttpResponseBodyDto` | D-E19. Lee de R2. 404 `automations.http_response.expired` fuera de 3 días; 404 `automations.http_response.not_stored` si la fila no tiene `response_object` o el objeto no está (D-E16) |
| `GET /simulations/{id}/http-calls/{callId}/response` | `HttpResponseBodyDto` | Igual, para una simulación con `liveHttp` |

Códigos nuevos en `AutomationsErrorCodes`: `automations.run.not_found`, `automations.run.cursor_invalid`,
`automations.http_response.expired`, `automations.http_response.not_stored`.

### 6.2 Backend: Messaging, Authorization, Bootstrapper

- **Messaging:** un solo cambio aditivo (decisión del diseñador, 2026-10-26): `ConversationAutomationDetails`
  (`IConversationAutomationDetails.cs:10-20`) gana `RunId`, y su adaptador en Bootstrapper y la respuesta de
  `GET /messaging/conversations/{id}/automation` lo llevan, para que «Ver recorrido» abra la corrida exacta. Fuera de eso, sin cambios. Los dos adaptadores viven en `src/Bootstrapper/AutomationRun*.cs` sobre
  `MessagingDbContext`, como `PipelineConversationDirectory.cs:14`.
- **Authorization:** sin cambios: las rutas `/runs/…` usan `automations.chatbot.read` (P-E1, owner 2026-10-26). No hay
  permiso, política ni rol nuevos.

## 7. Contratos que necesita la pantalla

```text
RunListResponse(items: RunListItemDto[], nextCursor: string?)

RunListItemDto(
  id, conversationId,
  contact: { name: string?, phone: string? },          // de Mensajería; null si la conversación ya no existe
  versionNumber: int?,                                   // null si el chatbot se borró
  status: RunStatus por nombre,                          // Waiting, HandedOff, Failed…
  startedAt, endedAt?, durationSeconds?,
  lastNode: { id, title? }?,                             // el título de la versión: la lista no conoce el flujo
  failureCode: string?,                                  // el frontend lo traduce (handoff-cause.ts ya tiene el diccionario)
  httpFailures: int,                                     // filas de http_calls con outcome ≠ ok
  detail: "Full" | "TraceOnly")                          // TraceOnly: vencido o de antes de este spec
```

`detail` y `httpFailures` existen para que la lista no tenga que abrir cada corrida para pintar el ícono de «falló un HTTP» ni para
saber si al abrirla va a haber valores.

```text
RunReplayDto(
  run: RunListItemDto,
  version: { id, number, flow, layout }?,                // D-E10; null sin versión
  detail: "Full" | "TraceOnly", detailExpiresAt?,
  steps: RunStepDto[],                                   // ordenados por (step, seq), D-E9
  edges: { fromNodeId, output, toNodeId?, count }[],     // D-E11: lo que el lienzo resalta
  units: { index, trigger, startedAt, endedAt, outcome, closedStatus?, closedReason? }[],
  traceTruncated: bool)                                  // la traza de conversation_runs pasó las 200

RunStepDto(
  step, seq, nodeId, nodeTitle?, kind, outcome,          // outcome = la salida (ok, yes, b2…) o waiting/pending/restart/…
  at, unitIndex,
  input: { kind: "CustomerMessage", messageId, text?, replyId?, replyTitle?, attachment?: { fileName, mimeType } }?,
  writes: { name, value: JSON?, truncated: bool }[],     // vacío en TraceOnly
  sends: { clientId, messageId?, kind: "Text" | "Interactive", text?, status?: MessageStatus por nombre }[],
  http: RunHttpDto?,
  reset: bool)                                           // reinicio: las variables vuelven a las fijas

RunHttpDto(
  connectionName?,
  calls: { id, method, path, statusCode?, durationMs, outcome, errorCode?, responseBytes?, calledAt,
           superseded: bool,                             // superseded: de un intento de la unidad que se deshizo (§2.3)
           response: "Available" | "Expired" | "NotStored" | "None" }[],   // D-E19; None: timeout, red, URL rechazada
  request: { method, path, query: {key, value}[], body: string?, authHeaderName, reconstructed: true, exact: bool }?,
  mapping: { from, to, found: bool }[],
  sample: JSON?)                                         // la respuesta de ejemplo del bloque, sólo en simulaciones sin liveHttp

HttpResponseBodyDto(
  status: int, bytes: int, contentType: string?,         // contentType: el que dijo la API, sin el resto de los headers
  body: string,                                          // el texto tal cual (redactado, D-E17); el frontend intenta JSON y si no muestra texto
  calledAt)
```

`response` en cada llamada existe para que el panel sepa, sin pedir el cuerpo, si mostrar «Ver respuesta», «La respuesta ya venció»
o «La respuesta de esta llamada no se guardó». `Available` sólo dice que la fila tiene `response_object` y está dentro de la ventana;
si el objeto se perdió (D-E16), el `GET` del cuerpo responde `not_stored` y el panel muestra lo mismo.

`SimulationDto` (`Simulations.cs:183-192`) suma `steps: RunStepDto[]` y `edges`. Los `sends` del simulador no tienen `clientId` ni
`status`: apuntan a la línea de la transcripción por índice.

Por qué cada campo que el backend podría ahorrarse:

- `nodeTitle` en cada paso: la pantalla de una corrida sin versión (chatbot borrado) no tiene de dónde sacarlo; con versión, el
  backend ya la tiene leída.
- `edges` armadas en el servidor: deducirlas exige saber que un `waiting` y su salida comparten step y que el siguiente bloque es el
  primer `seq` del step siguiente; eso es del motor, no de la pantalla.
- `superseded`: sin el campo, la pantalla vería dos llamadas para un paso y no podría explicarlas.

## 8. Frontend (`qep-frontend`)

**Dónde vive.** Un selector en la cabecera del editor, **«Editor | Ejecuciones»**, como n8n. La vista es estado de la ruta por search
params (`?view=runs&run=<id>&step=<n>`) en `routes/_authenticated/automations/chatbots/$chatbotId.tsx`, para que un enlace abra una
corrida y un paso exactos sin rehacer el árbol de rutas. Todo lo demás en `features/automations/` (Screaming Architecture, SDD-ADR-07).

**En «Ejecuciones»:**

1. **Lista a la izquierda** (en el lugar de la paleta): filtros por estado y rango, buscador por contacto (sobre la página cargada),
   cada fila con contacto, hora relativa con el formato del tenant, duración, estado, último bloque y un ícono si `httpFailures > 0`.
   Paginación con «Cargar más» por `nextCursor`. Vacío: «Este chatbot todavía no atendió conversaciones en este rango».
2. **Lienzo en sólo lectura con la versión de la corrida** (`FlowCanvas` con `readOnly`, `flow-canvas.tsx:206-209`), no el borrador.
   `CanvasMarks` pasa de un `currentNodeId` a un conjunto: bloques visitados, bloque del paso elegido, bloque final con el tono de su
   estado; `toCanvasEdges` (`flow-canvas.ts:208`) marca las aristas de `edges` y muestra la cuenta cuando es mayor que 1. Sin versión,
   el lienzo se reemplaza por la lista de pasos.
3. **Línea de pasos** bajo el lienzo o en el panel: anterior / siguiente, y clic en un bloque visitado salta a su primera visita (con
   selector si tiene varias).
4. **Panel derecho «Paso»** con secciones: *Entrada* (el mensaje del cliente con su opción tocada, o «Inicio», o «Respuesta del
   bloque HTTP»), *Salida tomada*, *Variables escritas* (aplanadas como en «Datos capturados», `flattenVariables`), *Mensajes
   enviados* (como burbujas, con su estado), *HTTP* (llamadas con status, duración, tamaño y resultado; petición reconstruida con la
   etiqueta «Reconstruida a partir de las variables de este paso» y la clave enmascarada; mapeo con ✓/✗ por `from`; **la respuesta**, pedida al desplegar la llamada con `GET …/response`, como JSON con
   formato si se puede parsear y como texto si no, con status y tamaño, y con carga, error, «La respuesta ya venció» y «La respuesta de
   esta llamada no se guardó»). En `TraceOnly`,
   el panel dice «El detalle de esta corrida ya venció» y deja sólo bloque y salida.
5. **Enlace desde la bandeja:** en «Datos capturados» (`captured-data-panel.tsx`), «Ver recorrido» para quien tenga el permiso de
   P-E1, que abre el editor en esa corrida. El asesor sin permiso no ve el enlace; la pestaña no cambia.

**Simulador.** La pestaña «Simular» suma «Ver pasos»: el mismo panel «Paso» y el mismo resaltado del lienzo sobre `simulation.steps`,
contra el borrador. Un paso cuyo bloque ya no existe en el borrador se muestra con «Este bloque ya no está en el borrador».

**Sin `automations.chatbot.manage`** el editor ya es de sólo lectura (P-13 del plan del frontend); «Ejecuciones» siempre lo es.

## 9. Almacenamiento, retención y volumen

- **Restricción del owner:** el servidor de Postgres es compartido con otras aplicaciones. Nada de esta vista puede sumar escrituras
  grandes ni barridos pesados ahí. Por eso los cuerpos van a R2 (D-E15) y el detalle en Postgres vive 3 días (D-E7).
- **Volumen estimado en Postgres** (a medir con la consulta de abajo): una corrida del flujo de Clasic visita del orden de 20 a 40 bloques en 5 a 10
  unidades. Con 300 conversaciones al día por tenant: ~3.000 filas de `run_units` al día, **~9.000 en 3 días**. Entre 2 y 20 KB por
  fila: **del orden de 20 a 200 MB por tenant activo** en el peor caso. Los topes de D-E8 acotan el extremo.
- **Volumen en R2:** una respuesta típica de unos KB, comprimida; el tope es 1 MB antes de comprimir. Con R2 no hay costo por egreso y
  el ciclo de vida purga sin tocar la base.
- **Escrituras extra por llamada HTTP en Postgres:** ninguna. `response_object` va en el mismo insert que `http_calls` ya hace.
- Para medir en producción, en PowerShell, con el contexto correcto (`--context`, memoria «Contexto kubectl contabo-prod») y sin
  imprimir secretos, contar corridas y largo medio de traza por tenant:
  `SELECT tenant_id, count(*), avg(jsonb_array_length(trace)) FROM automations.conversation_runs WHERE started_at > now() - interval '30 days' GROUP BY 1;`
- **Purga:** el mismo patrón de `http_calls` (`HttpCallLog.cs:45-57`): `DELETE … WHERE id IN (SELECT id … WHERE started_at < cutoff
  LIMIT 1000)` por lotes, cada uno en su scope, en el barrido de 10 min. Sin índice por fecha mientras la tabla sólo guarde la
  ventana (mismo razonamiento que `HttpCallLog.cs:42-44`); el plan lo revisa con el `EXPLAIN` real.
- **`simulations.steps`** vive y muere con la simulación (2 h o al cerrar el panel): no necesita purga propia.
- **`conversation_runs` sigue sin retención** (con sus variables y su traza). No lo introduce este spec, pero queda más a la vista: P-E7.

## 10. Permisos: quién ve qué

| Qué | Quién | Por qué |
| --- | --- | --- |
| Lista y detalle de corridas | `automations.chatbot.read` (P-E1) | Owner, 2026-10-26: sin permiso nuevo |
| «Datos capturados» | sin cambios: `messaging.conversation.read` | Es la pantalla del asesor (D-C10) |
| Pasos de una simulación | `automations.chatbot.manage`, como hoy la simulación (`Simulations.cs:294`, `:341`) | Es parte del simulador |
| Respuesta cruda de un HTTP real | el mismo permiso que la lista y el detalle (P-E1) | Owner, 2026-10-26: la ve el tenant |
| Respuesta cruda en una simulación | `automations.chatbot.manage`, como el simulador | Es parte del simulador |

Aislamiento de tenant en doble capa: la ruta lleva `{tenantId:guid}` y la política; el handler revalida tenant, permiso y módulo
antes de tocar el repositorio, y todo método de repositorio recibe `tenantId` (CLAUDE.md, «Aislamiento de tenant, doble capa»).

## 11. Restricciones

- **Ningún efecto desde la vista** (D-E1). Ninguna ruta nueva escribe en `messaging.*` ni cambia una corrida.
- **D-H5 sigue vigente; D-H6 y D-H7 se levantan sólo para el cuerpo de la respuesta** (§0). Headers, query, cuerpo de la petición y
  clave siguen fuera de todo registro. `TheKeyNeverAppearsInLogsAuditOrHttpCalls` (`ClasicFlowEndToEndTests.cs:549`) se extiende a
  `run_units`, a `simulations` y **a los objetos subidos a R2**, con una API de prueba que devuelve la clave en el cuerpo: el
  centinela no aparece.
- **Hay que corregir la documentación que dice lo contrario**, en el mismo slice que cambia el código: `CLAUDE.md:519` (gotcha «Nada
  de lo que responde una API externa…»), `README.md:3202`, D-H6/D-H7 en `2026-10-21-constructor-de-chatbots-design.md:118-119` (con
  una nota que apunte a este spec) y los comentarios de `HttpBlockExecutor.cs:20-22`, `:182`, `:215` e `IHttpBlockExecutor.cs:26`.
- **El cuerpo nunca entra a un log**, tampoco al fallar la subida (D-E16).
- **El registro no puede romper el bot.** Un fallo al armar o guardar el registro no deshace la unidad ni cambia el resultado de la
  fila del inbox: sólo se pierde ese registro y queda un log con ids.
- **Ni el motor ni el procesador cambian de comportamiento.** Los `clientId` siguen siendo `(runId, baseStep, effectIndex)`
  (P2-R5); las pruebas existentes de `FlowEngineTests`, `RunProcessorTests` y `ClasicFlowEndToEndTests` pasan sin tocarlas.
- **El motor sigue puro** (D-M2): los `StepRecord` salen del `Walk`, sin reloj ni puertos.
- **Los enums viajan por nombre** y el frontend es dueño del diccionario código → texto.
- **Textos de producto en español colombiano, tuteando** («Ver recorrido», «El detalle de esta corrida ya venció»).

## 12. Slices

Dos repos, dos planes. Cada slice cierra mergeable a `develop` en su repo.

**Backend (`qep-backend`):**

| Slice | Contenido | Depende de |
| --- | --- | --- |
| RC-B1 Registro | `run_units` + migración, `StepRecord` del motor, `Step` en efectos de envío, `MessageId` en `CustomerMessage`, `RunEffects`/`RunProcessor` escriben la unidad, cierres de D-E14, `http_calls.step_number`/`response_bytes`, purga a 3 días, opción de retención | — |
| RC-B1b Captura de respuestas | `http_calls.response_object` (en su propia migración; `step_number` y `response_bytes` son de RC-B1), lectura del cuerpo en error (D-E18), redacción de la clave (D-E17), cola y `HttpResponseUploadWorker` a R2 (D-E15, D-E16), corrección de `CLAUDE.md`/README/spec del constructor/comentarios (§11), paso manual de la regla de ciclo de vida | RC-B1 (sólo por `step_number`; puede ir en paralelo si la columna sale en RC-B1) |
| RC-B2 Lecturas | `GET /runs` nuevo (proyección, cursor, contacto), `GET /runs/{id}` con reconstrucción de la petición y unión de `http_calls`, `GET …/http-calls/{callId}/response` (D-E19), puertos `IAutomationRunMessages`/`IAutomationRunContacts` y adaptadores, `automations.chatbot.read` (P-E1), `RunId` en «Datos capturados» (§6.2) | RC-B1, RC-B1b |
| RC-B3 Simulador | `simulations.steps`, pasos en `SimulationRunner`, `SimulationDto.steps/edges`, `GET /simulations/{id}`, `GET /simulations/{id}/http-calls/{callId}/response` | RC-B1, RC-B1b |
| RC-B4 (posterior) | «Abrir en el simulador con estas entradas» (P-E6) | RC-B2, RC-B3 |

**Frontend (`qep-frontend`):**

| Slice | Contenido | Depende de |
| --- | --- | --- |
| RC-F1 Lista y lienzo | Selector «Editor \| Ejecuciones», search params, parsers de `RunListResponse`/`RunReplayDto`, lista con filtros y cursor, lienzo de la versión con camino y aristas | RC-B2 desplegado |
| RC-F2 Panel del paso | Panel «Paso» (entrada, salida, variables, mensajes, HTTP), navegación anterior/siguiente, estados `TraceOnly` y sin versión | RC-F1 |
| RC-F3 Simulador y bandeja | «Ver pasos» en el simulador; «Ver recorrido» en «Datos capturados» | RC-B3, RC-F2 |

Despliegue: backend primero (RC-B1 y RC-B1b pueden salir solos: empiezan a registrar sin que nadie lo lea, y así la vista arranca con
3 días de historia el día que se prende). La regla de ciclo de vida de R2 se crea **antes** de desplegar RC-B1b.

## 13. Fuera de alcance

- Re-ejecutar una corrida real, total o parcialmente (D-E1).
- Guardar la petición cruda (query, cuerpo, headers): se sigue reconstruyendo al leer (D-E4).
- Redactar campos de la respuesta por nombre (`password`, `token`…): frágil, y el owner aceptó que el tenant vea la respuesta. Sólo se
  redacta la clave de la conexión (D-E17).
- Garantía de entrega de los cuerpos: se pueden perder (D-E16).
- Métricas (tasa de traspaso por bloque, embudos, tiempos): la tabla nueva las haría posibles, pero son otra pantalla.
- Marcar variables como «sensibles» para enmascararlas en la vista (`FlowVariable(Name, Type)`, `FlowDefinition.cs:20`, no tiene dónde
  decirlo); queda como seguimiento si P-E1 no alcanza.
- Auditar la lectura de una corrida: no hay precedente de auditoría de lecturas en el repo.
- Retención de `conversation_runs` (P-E7 decide si se abre como trabajo aparte).
- Instagram: el motor no lo atiende (D-M6), así que no hay corridas que mostrar.

## 14. Pruebas (TDD, RED antes que GREEN)

- **Unitarias (Automations):** `FlowEngineTests` gana casos de `StepRecord`: el `waiting` y su salida comparten step con `seq`
  distinto; `Save` y `Merge` aparecen como escrituras del paso que sale; el reinicio marca `reset`; los envíos llevan el step del
  bloque. `RunEffectsTests`/`RunProcessorTests`: la unidad deja una fila con los `clientId` reales (los mismos de
  `DeterministicClientId.For`); una unidad deshecha no deja fila; un fallo del registro no deshace la unidad. Topes de D-E8 (valor de
  9 KB → `truncated`; URL de adjunto ausente). Reconstrucción de la petición: igual a `HttpBlockRequest.Build` con las variables del
  paso; `exact: false` con una variable recortada; la clave nunca aparece. Captura: un 500 con cuerpo deja `response_object` y el
  bloque sigue saliendo por `error`; la clave en el cuerpo sale como `••••`; con la cola llena la llamada termina igual y la fila
  queda con `response_object`; un fallo de R2 no cambia el resultado del bloque; timeout y red no dejan `response_object`; una
  respuesta de más de 1 MB sigue siendo `response_too_large` sin cuerpo.
- **Integración (captura):** con un R2 de prueba (el doble que ya usen las pruebas de Storage/Mensajería), el objeto llega comprimido
  bajo `automations/http-responses/{tenantId}/`; `GET …/response` lo devuelve, responde `expired` pasados 3 días aunque el objeto
  exista, `not_stored` sin objeto, y 403 desde otro tenant.
- **Integración (Automations):** `RunsApiTests` (lista con contacto, cursor, filtros por estado, 403 de otro tenant, 403 sin permiso,
  404 de una corrida inexistente), `RunReplayApiTests` (el flujo de Clasic de `ClasicFlowEndToEndTests`: pasos en orden, aristas con
  cuenta, HTTP unido por step, mensajes resueltos en Mensajería con su estado, una unidad con HTTP y rollback → dos llamadas con una
  `superseded`), purga (`SweepAsync` borra `run_units` vencidas y deja la corrida en `TraceOnly`), `SimulationsApiTests` (pasos con la
  versión del borrador; un bloque borrado a mitad). `TheKeyNeverAppearsInLogsAuditOrHttpCalls` extendida a `run_units`.
- **ArchitectureTests:** `AutomationsLayerTests` sigue sin Messaging en Automations; `CompositionRootTests` con el permiso nuevo.
- **Migración:** `AddRunUnits` sobre una base con corridas y `http_calls` existentes (columnas nuevas `NULL`), volviendo a la última
  migración antes de llamar a la API (CLAUDE.md, gotcha de pruebas de migración).
- **Frontend:** por ruta con `render-route.tsx`: lista (carga, vacío, error, cursor), corrida con versión (camino y aristas
  resaltados), sin versión, `TraceOnly`, panel HTTP con petición reconstruida, permiso faltante (sin selector ni enlace), simulador
  con pasos.

## 15. Archivos (referencias del código actual)

Backend:

- `src/Modules/Automations/Modules.Automations.Domain/Engine/RunState.cs:20` (`TraceEntry`), `:23-37` (`TraceOutcomes`), `:51-61` (`RunState`, `TraceMax`).
- `src/Modules/Automations/Modules.Automations.Domain/Engine/FlowEngine.cs:134-162` (`Walk`, un solo `now`), `:164-187` (`Merge`, `Save`), `:233-240` (reinicio), `:305-347` (`Enter`, `stepNumber++`), `:368-377` (`Record`, tope).
- `src/Modules/Automations/Modules.Automations.Domain/Engine/Effect.cs:9-33`; `Engine/EngineInput.cs:18`, `:23-25`.
- `src/Modules/Automations/Modules.Automations.Domain/ConversationRun.cs:42-58`, `:84-95`, `:123-138`, `:152`.
- `src/Modules/Automations/Modules.Automations.Domain/Simulation.cs:17-21`, `:70-79`, `:100-108`.
- `src/Modules/Automations/Modules.Automations.Application/Engine/RunEffects.cs:35-136`, `:64`, `:78`, `:270-280`.
- `src/Modules/Automations/Modules.Automations.Application/Engine/RunProcessor.cs:98-142`, `:150-166`.
- `src/Modules/Automations/Modules.Automations.Application/Engine/DeterministicClientId.cs:18-22`; `Engine/EngineReentries.cs:12`.
- `src/Modules/Automations/Modules.Automations.Application/Http/IHttpBlockExecutor.cs:8`, `:17`, `:27-29`, `:45-57`; `Http/HttpBlockRequest.cs:25-39`; `Http/HttpResponseMapping.cs:17-27`.
- `src/Modules/Automations/Modules.Automations.Application/Runs.cs:9-54`; `Simulations.cs:28-128`, `:183-192`, `:356-358`, `:422-436`.
- `src/Modules/Automations/Modules.Automations.Application/AutomationsPermissions.cs:9-16`.
- `src/Modules/Automations/Modules.Automations.Infrastructure/Http/HttpBlockExecutor.cs:91-107`, `:216-261`, `:424-453`; `Http/HttpCallLog.cs:20-57`.
- `src/Modules/Automations/Modules.Automations.Infrastructure/Engine/AutomationInboxWorker.cs:109-121`, `:124-129`, `:195-208`.
- `src/Modules/Automations/Modules.Automations.Infrastructure/Options/AutomationsWorkerOptions.cs:20-26`.
- `src/Modules/Automations/Modules.Automations.Infrastructure/Persistence/AutomationsDbContext.cs:179-250`; `AutomationsRepositories.cs:127-193`.
- `src/Modules/Automations/Modules.Automations.Api/RunEndpoints.cs:14-26`, `SimulationEndpoints.cs`.
- `src/Bootstrapper/AutomationConversationDetails.cs:17-49`; `src/Bootstrapper/PipelineConversationDirectory.cs:14` (precedente de adaptador sobre `MessagingDbContext`).
- `src/Modules/Messaging/Modules.Messaging.Application/GetConversationAutomation.cs:12-46`; `IConversationAutomationDetails.cs:10-28`.
- `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/MessagingDbContext.cs:293`, `:316`; `Modules.Messaging.Domain/MessageEnums.cs:59-65`.
- `tests/Modules/Automations/Modules.Automations.IntegrationTests/ClasicFlowEndToEndTests.cs:549`; `SimulationsApiTests.cs:227`; `Modules.Automations.UnitTests/FlowEngineTests.cs:292`.

Frontend:

- `src/routes/_authenticated/automations/chatbots/$chatbotId.tsx:182`, `:372-375` (simulador y `currentNodeId`).
- `src/features/automations/pages/chatbot-editor-page.tsx:94-139`; `components/editor-side-panel.tsx:60-63`; `components/flow-canvas.tsx:206-209`; `components/flow-block-node.tsx:38`, `:63`.
- `src/features/automations/utils/flow-canvas.ts:22-26`, `:119-134`, `:208`.
- `src/features/automations/services/automations.api.ts:303-317` (`parseSimulation`); `types/automations.ts:218-229`.
- `src/features/messaging/components/captured-data-panel.tsx:10-15`; `components/contact-sheet.tsx:216`.
- `docs/superpowers/plans/2026-10-22-constructor-de-chatbots-frontend.md:153` (P-14).

## 16. Decisiones pendientes

| ID | DECISIÓN-PENDIENTE | Opciones | Recomendación |
| --- | --- | --- | --- |
| ~~P-E1~~ | **Resuelta 2026-10-26 por el owner:** (a), `automations.chatbot.read` existente. Sin constante, política ni rol nuevos | — | — |
| ~~P-E2~~ | **Resuelta 2026-10-26 (decisión del diseñador):** (a), valores con tope (D-E8) y 3 días (D-E7). Los valores ya están en `conversation_runs.variables` y el asesor los ve; sin ellos la vista no explica por qué una Condición dio «no», y son menos sensibles que la respuesta cruda que el owner ya aprobó mostrar | — | — |
| ~~P-E3~~ | **Resuelta 2026-10-26:** el simulador con `liveHttp` captura igual que una corrida real (D-E13) | — | — |
| ~~P-E4~~ | **Resuelta 2026-10-26:** se captura siempre en corridas reales, sin opt-in (§0, D-E15..D-E18) | — | — |
| ~~P-E5~~ | **Resuelta 2026-10-26:** 3 días (D-E7) | — | — |
| P-E6 | **¿Entra «Abrir en el simulador con estas entradas»** (re-ejecución segura sobre el borrador con `liveHttp` apagado y lo mapeado como respuesta)? | (a) slice posterior RC-B4; (b) en la primera entrega; (c) no | **(a).** Es el único camino de re-ejecución sin efectos que muestra el código, pero exige decidir qué hacer cuando el borrador ya no tiene los bloques de la corrida |
| P-E7 | **¿Se abre trabajo aparte para la retención de `conversation_runs`** (variables y traza sin vencimiento hoy)? | (a) sí, spec aparte; (b) no | **(a)**, fuera de este spec: no lo causa esta vista, pero la vista la deja a la vista |
| ~~P-E8~~ | **Resuelta 2026-10-26 por el owner:** (a), selector «Editor \| Ejecuciones» en la cabecera, estado en search params, misma ruta (sin recargar el editor ni perder el borrador en memoria). Cada vista en su propio componente de `features/automations/`; la lista se pide sólo al entrar a «Ejecuciones». Fila original: **¿Dónde vive la vista en el editor?** | (a) selector «Editor \| Ejecuciones» en la cabecera, como n8n; (b) tercera pestaña del panel derecho; (c) página aparte | **(a).** La vista necesita el lienzo entero y un panel propio; como pestaña del panel derecho competiría con «Propiedades» y «Simular» por 320 px |
