# Bloque «Agente IA» dentro de los chatbots (backend)

- **Fecha:** 2026-11-17.
- **Módulos:** `Automations` (bloque nuevo, motor, validador, procesador), `Ai` (sesión en modo bloque, herramienta final nueva,
  retorno), `Messaging` (delegación de la conversación entre bots, cola, señal de retorno). Frontend en `qep-frontend` (spec
  aparte, mismo nombre de archivo).
- **Estado:** borrador para aprobación del owner. **Sin código.**
- **Se apoya en:** [`2026-10-29-agentes-ia-design.md`](2026-10-29-agentes-ia-design.md) (en adelante «spec de agentes»), el plan
  [`2026-11-12-agentes-ia-claude-backend.md`](../plans/2026-11-12-agentes-ia-claude-backend.md) y el spec de producto del frontend
  `qep-frontend/docs/superpowers/specs/2026-10-28-agentes-ia-design.md` §6 («Evolución»), que es la idea que este documento aterriza.
  Las decisiones D-A1 a D-A30 del spec de agentes **no se reabren**: este documento las extiende.
- **Verificado contra el código el 2026-11-17** en la rama `feat/agentes-ia-claude` (worktree `qep-backend-worktrees/agentes-ia-claude`).
  Las referencias `archivo:línea` son de ese día. Lo que no tiene referencia es **propuesta**. Donde este documento y el código
  discrepen, gana el código.

## 1. Contexto y alcance

Hoy una conexión de Mensajería la atiende **un solo bot**: un chatbot (canvas) o un agente IA, nunca los dos
(`messaging.connection_bots`, spec de agentes §4.3). El tenant que quiere lógica fija antes o después de la IA —menú, horario,
política de datos, captura de un dato, pasar a un asesor— tiene que elegir entre el canvas y el agente. Este spec agrega un bloque
**«Agente IA»** al canvas: el flujo le entrega la conversación a un agente publicado en ese punto, el agente conversa varios turnos con
el cliente y, cuando termina, **devuelve el control al flujo por una salida** (terminó, pidió un asesor, no supo responder, inactivo,
error).

**Dentro (MVP):**

- Bloque `aiAgent` en el documento del flujo, con referencia a un **agente publicado**, un **objetivo** propio del paso, contexto con
  variables, tiempo de inactividad y cinco salidas.
- Delegación de la conversación del chatbot al agente y retorno, por Mensajería (los dos módulos siguen sin conocerse, D-A3).
- Sesión del agente en **modo bloque**: misma ejecución asíncrona (cola + worker, D-A13), herramientas del agente, herramienta final
  nueva `finish_objective`, y `handoff_to_human` que **sale por una salida** en vez de traspasar.
- Variables que el bloque escribe para los bloques siguientes (resumen, motivo, número de cotización).
- Validación al publicar el chatbot, simulador, consumo atribuido al chatbot y al agente, versiones.

**Fuera:** extracción estructurada de campos por el agente (respond.io «Collect Information»), varios bloques de IA vivos a la vez en
una conversación, un agente «en línea» configurado dentro del bloque sin agente publicado, el bloque en Instagram mientras el motor de
chatbots no corra en Instagram (§2), la palabra de reinicio del chatbot durante la delegación (§6.6).

## 2. Lo que existe y en qué se apoya (verificado)

| Pieza | Dónde | Qué significa para el bloque |
| --- | --- | --- |
| Bloques y salidas del flujo | `FlowNodeKinds.cs:8-23` (once tipos), `OutputsOf` `:30-43`, nombres de salida `FlowOutputs` `:76-94` | `aiAgent` es el tipo doce, con salidas propias. |
| Corrida viva | `RunStatus` `Running`/`Waiting` y finales (`RunState.cs:5-16`); `TraceOutcomes.Waiting`/`Pending` (`:23-37`) | La corrida queda `Waiting` en el bloque mientras el agente conversa, con un outcome de trace nuevo. |
| Entradas del motor | `EngineInput.Start`, `CustomerMessage`, `HttpResult`, `BusinessHours`, `DataConsent` (`EngineInput.cs:8-37`); `FlowEngine.Step` las despacha (`FlowEngine.cs:19-32`) | Se suma `EngineInput.AgentResult`. `OnHttpResult` (`FlowEngine.cs:124-138`) es la plantilla: valida que la corrida siga parada en ese bloque (`PendingAt`) y sale por una salida. |
| Bloque que espera un efecto | `HttpNode` pide `Effect.CallHttp` y para `Running` con `Pending` (`FlowEngine.cs:479-481`); el bucle de efectos lo resuelve en la misma unidad (`RunEffects.cs:101-117`) | El agente **no** cabe en la unidad (D-6 del spec de producto, D-A13): el bloque pide un efecto que **delega** y la corrida queda `Waiting`, como una pregunta. |
| Salida sin conectar | `Go` con destino `null` emite `EndRun(OutputUnconnected)` y termina `Completed` (`FlowEngine.cs:405-414`) | Para `human`, `noAnswer` y `error` sin conectar se cambia a traspaso (D-B12). |
| Traspaso desde un bloque | `HandOff(node, reason, memberId)` (`FlowEngine.cs:318-324`), `Effect.Handoff` (`Effect.cs:25`) | Lo usa el bloque cuando una salida de problema no está conectada. |
| Abandono de corridas | 24 h en `Waiting` sin moverse → `Abandoned` (`RunProcessor.cs:43`, `AbandonAsync` `:272-313`) | Una corrida delegada no se mueve mientras el agente conversa: el barrido tiene que saltarla (§6.5). |
| Toma humana | `BotAttendance.HumanTookOver` (`BotAttendance.cs:17-25`): asesor, `Resolved`, `Paused`/`None` o devuelta después del arranque | No mira el tipo de bot: con la conversación en `Active` bajo el agente, la corrida del chatbot **no** se da por interrumpida. Es lo que permite delegar sin tocar esa regla. |
| Tipo de bot de la conversación | `BotKind` `Chatbot = 1`, `Agent = 2` (`BotRef.cs:7-11`); `Conversation.StartAutomation(BotRef)` y `SetBot` (`Conversation.cs:382-420`) | La delegación cambia el bot de la conversación **sin** pasar por `None`. `StartAutomation` lanza `AutomationAlreadyActive` con la conversación en `Active` (`:391-392`), así que hace falta un método nuevo. |
| Ruteo de la cola | `InboundIngestion.BotKindForAsync` (`InboundIngestion.cs:249-260`): conversación `Active` → su `automation_bot_kind`; si no, el ocupante de la conexión | Con la conversación delegada, los entrantes van solos a la cola del agente (`bot_kind = 2`). No hay que tocar la ingesta. |
| Tipos de fila de la cola | `AutomationInboxKinds.Engine` y `ReleaseRun` (`IAutomationInbox.cs:7-11`); `ClaimAsync` por tipo de bot (`:42`) | Se suman dos tipos de fila: `Delegated` (al agente) y `AgentReturned` (al chatbot). |
| Señal de liberación | `ReleaseRunSignal` (`ReleaseRunSignal.cs:12`), `EnqueueReleaseRun(…, BotKind, …)` (`AutomationInbox.cs:138`) | Con una delegación viva, la toma humana avisa a los dos bots (§6.4). |
| Instagram | La ingesta de Instagram encola **sólo** para agentes: `onlyBotKind: BotKind.Agent` (`WebhookDeliveryProcessor.Instagram.cs:191-192`); el validador del flujo ya trata Instagram aparte (`FlowValidator.cs:34-35`) | El motor de chatbots no corre en Instagram: el bloque llega a Instagram el día que el chatbot llegue (fuera). |
| Sesión del agente | `AgentSession` (`AgentSession.cs:19-75`), estados `Live`…`Abandoned` (`:4-12`), versión fija por sesión (D-A5) | Se suma el origen `ChatbotBlock` y el estado `Returned`. |
| Apertura de sesión | `AiInboxProcessor.TryOpenAsync` (`AiInboxProcessor.cs:168-268`): ocupante de la conexión, disparador, credencial del proveedor del agente (C-19), saludo | El modo bloque abre por otra puerta: no mira ocupante ni disparador, y no manda saludo. |
| Herramientas finales | `send_reply`, `handoff_to_human` (`AgentTools.cs:15-18`); causas de traspaso `HandoffCauseKind` (`AgentHandoff.cs:10-31`) y códigos (`:47-71`) | `finish_objective` es la tercera final, sólo en modo bloque. |
| Topes y cierre | `AgentSession.MaxSupersededInRow`, `RecordReply`, `RecordFailure`, `Finish` (`AgentSession.cs:21-142`); barrido de inactividad `AiSessionSweeper` con `IdleAfter` (`AiSessionSweeper.cs:28`, `AiInboxProcessor.cs:10-11`) | El bloque fija su propio tiempo de inactividad por sesión. |
| Publicar un agente exige conexión | `connections_missing` (`AgentConfigValidator.cs:54`) | Un agente que sólo sirve bloques no tiene conexión propia: pasa a aviso (D-B6). |
| Capas | `AiLayerTests` prohíbe `Modules.Automations`, `Messaging`, `Integrations`… en Ai (`AiLayerTests.cs:45-64`) | Todo cruce va por puertos con adaptador en `src/Bootstrapper`. |

## 3. Qué hace la competencia

Fuentes consultadas el 2026-11-17. **P** = documentación del fabricante; **T** = terceros; **SV** = sin verificar (no se pudo leer la
página o el dato no aparece).

| Plataforma | Cómo se configura el paso | Salidas | Varios turnos e inactividad | Traspaso dentro del paso | Fuente |
| --- | --- | --- | --- | --- | --- |
| respond.io (paso heredado «AI Objective») | En línea en el paso: objetivo «Answer Questions» o «Collect Information». | `Success`, `Failure: Speak to Human`, `Failure: AI Unable to Answer`, `Failure: Others` (técnico), `Failure: Idle` (opcional, 60 min por defecto). | Sigue «until the objective is met or a failure condition is triggered». | Es una salida. | P: <https://respond.io/help/workflows/how-to-migrate-ai-objective-legacy-workflows-to-ai-agents> |
| respond.io (modelo actual) | El flujo **asigna** la conversación a un AI Agent configurado en su propio módulo; ya no hay objetivo en línea. | Sin salidas: el éxito, el traspaso y la inactividad se escriben como instrucciones con acciones. Un usuario pide en el roadmap la salida «pidió humano» que falta. | Esperas de hasta 24 h por instrucción. | Acción «asignar» del agente. | P: misma URL y <https://respond.io/help/ai-skills/ai-skill-assign-to-agent-or-team.md>; T: <https://roadmap.respond.io/feature-request/p/ai-agent-new-branch-when-contact-asks-to-speak-to-human> |
| Botpress (Autonomous Node) | En línea: instrucciones, herramientas, variables, modelo por nodo. | Transición a un nodo conectado o a un sub-flujo; condiciones de salida configurables. | «Loops until exit condition» (por defecto). Inactividad en otra tarjeta. | Herramienta `hitl.startHitl`. | P: <https://botpress.com/docs/studio/concepts/nodes/autonomous-node/> |
| Wati (Astra) | **Referencia** a un agente ya creado: «Select an agent». No puede ser el primer nodo. | Ninguna: «Both nodes end the chatbot flow and take over the conversation». | Lo maneja el agente. | Bandeja del equipo. | P: <https://support.wati.io/en/articles/15186551-how-to-connect-your-astra-ai-agent-to-a-chatbot-flow-in-wati> |
| ManyChat (AI Step) | Objetivo y contexto del negocio en el paso. | «Next Step»; en la comunidad reportan que no siempre dispara. | SV. | SV. | P (parcial, SV): <https://help.manychat.com/hc/en-us/articles/14281187288860-Manychat-AI-Step>; T: <https://community.manychat.com/general-q-a-43/manychat-ai-step-next-step-wont-trigger-4774> |
| Zendesk (procedimientos generativos) | El procedimiento cuelga de un caso de uso del agente. | «Link to» (pasa a otro flujo), «Run template» (vuelve), «Escalate to» (humano; el agente para). | Inactividad de mensajería 3-15 min (10 por defecto). | «Escalate to» termina el procedimiento. | P: <https://support.zendesk.com/hc/en-us/articles/10473649691418-About-generative-procedures-for-AI-agents>, <https://support.zendesk.com/hc/en-us/articles/7043034053658-Automatically-releasing-agent-capacity-for-inactive-messaging-conversations> |
| Intercom (Fin en Workflows) | Paso «Let Fin answer» con su configuración. | Camino de traspaso (pide humano, feedback negativo, respuestas repetidas sin resolver) y «the next path» al vencer la inactividad sin cierre. | Fin sigue hasta que un asesor responde o escala. Chat: 4 min fijos; email: 1 h a 30 días. | Camino de traspaso del flujo. | P: <https://www.intercom.com/help/en/articles/10032299-use-fin-ai-agent-in-workflows> |

**Lo que converge:** (1) el paso sostiene la conversación varios turnos hasta una condición de salida; (2) los diseños con pasos
exponen salidas con nombre para éxito, pide humano, no sabe, inactivo y falla técnica (respond.io heredado es el más claro, Intercom
tiene traspaso y siguiente); (3) el traspaso dentro del paso **no asigna por su cuenta**: es una salida del flujo (respond.io heredado,
Intercom) o una acción explícita; (4) la tendencia reciente es **referenciar un agente publicado** en vez de configurar la IA en cada
paso (respond.io migró del objetivo en línea a sus AI Agents; Wati selecciona un agente). **Nadie documenta** qué pasa con una
conversación en curso cuando se edita el agente, ni la precedencia entre un agente propio del canal y un flujo con paso de IA, salvo
Intercom («Simple deployment» gana y sólo responde Fin).

## 4. Decisiones

| ID | Decisión | Motivo (evidencia) |
| --- | --- | --- |
| D-B1 | **El bloque referencia un agente publicado** (`agentId`) **y agrega un objetivo propio del paso** (`objective`, obligatorio) más un `context` con variables. Persona, instrucciones, FAQ, respuestas rápidas, herramientas y modelo son los del agente. No hay configuración de IA «en línea» sin agente. | Converge la industria hacia el agente referenciado (respond.io migró, Wati selecciona agente) y el objetivo por paso es lo que dio las salidas claras del modelo heredado de respond.io y de Botpress. En QEP el agente ya trae borrador, publicación, playground, proveedor por agente (C-16) y su cuenta BYOK (C-19): un agente en línea en el bloque duplicaría todo eso dentro del canvas y su modelo no tendría playground. |
| D-B2 | **«Una conexión, un solo bot» no cambia.** El ocupante de la conexión es el chatbot (`bot_kind = 1` en `messaging.connection_bots`). El agente del bloque **entra por el bloque** y no ocupa la conexión. Un agente puede servir bloques de chatbots y, a la vez, sus propias conexiones; en una misma conexión nunca coinciden porque la ocupa uno solo. | Spec de producto §0 y §6 (con la regla de §3.2, «una conexión con ese chatbot no tiene agente propio: el agente entra por el bloque»). La precedencia ambigua que Intercom tiene que documentar («Simple deployment gana») no puede darse acá. |
| D-B3 | **La delegación vive en Mensajería**, en una tabla neutral `messaging.bot_delegations` (§5.3): la conversación pasa del chatbot al agente **sin pasar por `None`**, queda `Active` con `automation_bot_kind = 2`, y el retorno la devuelve al chatbot con una fila `AgentReturned` en la cola. Automations y Ai no se conocen (D-A3). | Es el mismo argumento de D-A2: la única pieza que ambos módulos ven es Mensajería. Con la conversación en `Active` bajo el agente, la ingesta ya rutea los entrantes al agente (`InboundIngestion.cs:249-255`), la autoría del mensaje ya es «agente» (`sent_by_kind = 3`), el chip de la bandeja ya dice «Agente IA», la toma humana ya interrumpe al agente (`AgentAttendance.HumanTookOver`, `AiInboxProcessor.cs:21-26`) y `BotAttendance.HumanTookOver` no da por interrumpida la corrida del chatbot (`BotAttendance.cs:21-24`, no mira el tipo de bot). Todo lo que ya funciona para el agente se reusa sin reescribirlo en Automations. |
| D-B4 | **La corrida espera en `Waiting`** en el bloque, con `TraceOutcomes.Delegated` (nuevo). El motor pide `Effect.DelegateToAgent`; el procesador lo ejecuta en la misma unidad (con el candado de la conversación) por el puerto de Mensajería. El retorno entra como `EngineInput.AgentResult(nodeId, delegationId, output, variables)` y el motor sale por esa salida, igual que `OnHttpResult` (`FlowEngine.cs:124-138`). | `Waiting` es el estado de una corrida viva que espera algo de afuera sin unidad abierta (D-M12). `Running` + `Pending` (HTTP) supone que el efecto vuelve dentro de la unidad (`RunEffects.cs:101-117`), y el agente tarda minutos u horas. |
| D-B5 | **Versiones.** La corrida sigue con su versión del chatbot (D-C1). La sesión del agente fija **la versión publicada del agente al entrar al bloque** y termina con ella (D-A5). Republicar el agente mientras hay corridas esperando afecta sólo a las delegaciones que empiecen después; republicar el chatbot no toca corridas vivas. | Mismo criterio que ya rige para las dos piezas por separado. Ningún competidor lo documenta (SV en todos): se elige la regla que QEP ya usa. |
| D-B6 | **Un agente sin conexiones se puede publicar** (`connections_missing` pasa de error a aviso, `AgentConfigValidator.cs:54`). En modo bloque se ignoran `trigger`, `greeting` y el destino de `handoff` del agente: los decide el flujo. Se respetan `maxRepliesWithoutHuman` y `maxConsecutiveFailures` (salen por una salida, D-B10). | Un agente pensado sólo para bloques no tiene conexión propia (D-B2). El saludo lo manda el flujo con un bloque Mensaje antes, si lo quiere. |
| D-B7 | **Primer turno.** `firstTurn: lastMessage` (por defecto): el agente responde el último entrante del cliente que llevó la corrida hasta el bloque (p. ej. la respuesta a «¿En qué te ayudo?»). `nextMessage`: el agente calla hasta el próximo entrante. Si la corrida llegó sin entrante (arranque manual), `lastMessage` se comporta como `nextMessage`. | Botpress y respond.io arrancan con el mensaje en curso; un agente que pide «¿en qué te ayudo?» después de que el flujo ya lo preguntó repite la pregunta. |
| D-B8 | **Herramienta final `finish_objective`** sólo en modo bloque: `summary` ≤ 500 (lo escribe la variable `{saveTo}.summary`). El objetivo del bloque entra al prompt como contexto volátil, **después** del punto de caché del `system` (C-12), con la instrucción de llamarla cuando el objetivo se cumpla. | Sin una final explícita el agente no tiene cómo decir «terminé» (el problema de ManyChat «Next Step won't trigger», T). Respetar C-12: el objetivo cambia por bloque y por eso no va en el `system`. |
| D-B9 | **`handoff_to_human` dentro del bloque no traspasa: sale.** Motivo `customer_asked` o `complaint` → salida `human`; `no_answer` u `out_of_scope` → salida `noAnswer`. El mensaje de traspaso del agente no se envía. La salida la conecta el admin (normalmente a «Pasar a un asesor»). | respond.io heredado separa «Speak to Human» de «AI Unable to Answer»; Intercom saca el traspaso por un camino del flujo. Así el flujo decide a quién y con qué mensaje, que es para lo que existe el canvas. |
| D-B10 | **Salidas fijas, en este orden:** `done` (terminó), `human` (pidió un asesor), `noAnswer` (no supo responder), `idle` (inactivo), `error` (falla). Correspondencia completa en §6.3. | Las cinco del spec de producto §6, validadas contra respond.io heredado (`Success`, `Speak to Human`, `AI Unable to Answer`, `Idle`, `Others`). |
| D-B11 | **Inactividad por bloque:** `idleMinutes` 5..1380 (default 60). Sin entrante del cliente en ese lapso desde la última respuesta del agente, la sesión termina `Returned` por `idle`. | respond.io: 60 min por defecto. El tope de 23 h deja una hora de ventana de 24 h (`Conversation.WindowLength`) para que el flujo mande algo por `idle`. |
| D-B12 | **Salidas de problema sin conectar no terminan en silencio:** `human`, `noAnswer` y `error` sin conectar hacen `HandOff` a la bandeja sin asignar (como `DataConsent` agotado, `FlowEngine.cs:299-304`). `done` e `idle` sin conectar terminan la corrida `Completed` como cualquier salida (`FlowEngine.cs:409-414`). El validador avisa (`node.output_unconnected_optional`) en las cinco. | Una conversación en la que el cliente pidió un asesor y el flujo no tenía a dónde ir no puede quedar sin nadie: es el criterio de D-A15 y de «nunca se queda callado sin aviso» (spec de producto D-4). |
| D-B13 | **Borrador de cotización en modo bloque.** Si el agente crea el borrador (`create_quotation_draft`), la sesión sale por `human` con el código `quotation_draft_created` y escribe `{saveTo}.quotationNumber`. Si `human` no está conectada, el traspaso va **a la asesora del borrador** (P-4b), no a la bandeja sin asignar. | P-4b (owner, 2026-10-29): después del borrador, la conversación pasa a la asesora para revisarlo y enviarlo. En el bloque se respeta dejando que el flujo lo encamine; sin camino, se cumple P-4b tal cual. |
| D-B14 | **Toma humana durante la delegación:** la sesión termina `Interrupted` (como hoy) **y la corrida del chatbot termina `Interrupted` con `human_took_over`**, sin salir por ninguna salida. | El humano manda (D-A15, D-M15). Ninguna salida del flujo debe correr después de que una persona tomó la conversación. |
| D-B15 | **Consumo:** cada llamada al modelo sigue en `ai.usage_records` con el agente y la sesión (D-A27); la sesión guarda `delegation_id`, `chatbot_id` y `run_id`, así que el consumo se suma **por agente** (lista de agentes) y **por chatbot** (lista de chatbots, «consumo IA del mes»). Lo paga la cuenta BYOK del proveedor del agente (C-19). | La industria cobra por respuesta, resolución o conversación (respond.io créditos por respuesta, Intercom por resolución, Botpress por conversación); QEP no cobra el modelo (BYOK, D-4) pero tiene que mostrar de dónde sale el gasto. |
| D-B16 | **Dependencias de módulo:** el bloque exige `automations` **y** `ai` encendidos. Sin `ai`, el editor no ofrece el bloque, publicar falla con `ai_agent.module_disabled`, y una corrida que entra al bloque sale por `error` con `module_disabled`. Apagar `ai` con una delegación viva la devuelve al flujo por `error` (no a la bandeja como D-7 sin bloque). Apagar `automations` con la delegación viva: la sesión sigue hasta terminar y el retorno encuentra el motor apagado, que devuelve la conversación a `None` (`RunProcessor.StandDownAsync`, `RunProcessor.cs:233`). | D-1b: dos claves independientes. Con el bloque, el que decide qué pasa después de un error es el flujo; sin `automations`, nadie puede retomarlo y vale lo que ya hace el motor apagado. |
| D-B17 | **Simulador:** el bloque no llama al modelo en el simulador del chatbot; el tester elige la salida y los valores de las variables. El agente se prueba en su playground, que gana un modo bloque (`POST …/tests` con `block`). | El simulador corre el borrador sin efectos reales salvo `liveHttp`; una llamada al modelo por cada paso del simulador gastaría la clave del tenant sin que lo pida. El playground ya existe y ya cuenta su consumo aparte. |
| D-B18 | **Borrar un agente usado por un chatbot publicado responde 409** `ai.agent.in_use_by_chatbots` con la lista de chatbots en las extensiones. Pausarlo no se bloquea: los bloques que lo alcanzan salen por `error` con `agent_unavailable`, y publicar el chatbot avisa (`ai_agent.agent_paused`). | Mismo criterio que `has_live_sessions` (409 al borrar) y que la pausa de D-A6. |

## 5. Modelo

### 5.1 Bloque `aiAgent` (Automations, documento del flujo)

```jsonc
{
  "id": "ai-agent-1",
  "kind": "aiAgent",
  "title": "Asesor IA",
  "outputs": { "done": "message-3", "human": "handoff-1", "noAnswer": "handoff-1", "idle": null, "error": "handoff-1" },
  "agentId": "6f0c…",                    // agente del tenant; null en un borrador recién creado
  "objective": "Resuelve dudas de productos y precios. Termina cuando el cliente diga que no tiene más preguntas.",
  "context": "El cliente se llama {nombre} y eligió {menu}.",   // TemplateText, se interpola al entrar
  "firstTurn": "lastMessage",            // lastMessage | nextMessage
  "idleMinutes": 60,                     // 5..1380
  "saveTo": "asesor_ia"                  // prefijo de variables; regla de nombre de variables del flujo
}
```

- `record AiAgentNode(string Id, string Title, IReadOnlyDictionary<string, string?> Outputs, Guid? AgentId, string Objective,
  string Context, string FirstTurn, int IdleMinutes, string SaveTo) : FlowNode(Id, FlowNodeKinds.AiAgent, Title, Outputs)`, junto a
  `HttpNode` (`FlowDefinition.cs:86-94`).
- `FlowNodeKinds.AiAgent = "aiAgent"` y en `All`; `OutputsOf(AiAgentNode) = [done, human, noAnswer, idle, error]`; `FlowOutputs` suma
  `Done`, `Human`, `NoAnswer`, `Idle` (`error` ya existe, `:91`).
- Variables que escribe (con el prefijo `saveTo`): `{saveTo}.summary` (texto ≤ 500, vacío si no hubo `finish_objective`),
  `{saveTo}.reason` (código cerrado de §6.3), `{saveTo}.quotationNumber` (vacío si no hubo borrador). Entran en el chequeo de
  `variable.never_set` como las del mapeo HTTP.
- Topes: `objective` 1..2000, `context` ≤ 2000 (después de interpolar se corta a 2000), `title` como los demás bloques.

### 5.2 Validador (`FlowValidator`, códigos `ai_agent.*`)

| Código | Severidad | Cuándo |
| --- | --- | --- |
| `ai_agent.agent_missing` | error | `agentId` nulo o de un agente que no existe en el tenant. |
| `ai_agent.agent_not_published` | error | El agente está en `Draft`. |
| `ai_agent.agent_paused` | aviso | El agente está `Paused`: el bloque saldrá por `error` mientras siga así. |
| `ai_agent.objective_missing` | error | `objective` vacío. |
| `ai_agent.idle_out_of_range` | error | `idleMinutes` fuera de 5..1380. |
| `ai_agent.module_disabled` | error | El tenant no tiene `ai` (D-B16). |
| `ai_agent.channel_not_supported` | error | Una conexión del chatbot es de un canal donde el motor no corre (hoy, Instagram; mismo patrón que `list.channel_not_supported`, `FlowIssue.cs:47`). |
| `ai_agent.not_first` | error | El bloque es el primero después del Inicio **y** `firstTurn = lastMessage` con disparador que no trae entrante (arranque manual). Igual que Wati («no puede ser el primer nodo»), pero sólo cuando no tendría qué responder. |

El validador recibe el estado de los agentes por un puerto nuevo de Automations, `IAutomationsAgentDirectory.ListAsync(tenantId) →
[(id, name, status)]`, con adaptador en Bootstrapper sobre `IAgentRepository` de Ai (mismo patrón que `IAutomationsChatbotNames`, que
ya lee Ai). El editor del chatbot recibe la misma lista en su DTO (§7).

### 5.3 Mensajería: delegación

```sql
CREATE TABLE messaging.bot_delegations (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL, conversation_id uuid NOT NULL, connection_id uuid NOT NULL,
  chatbot_id uuid NOT NULL, run_id uuid NOT NULL, node_id varchar(64) NOT NULL,   -- referencias blandas a Automations
  agent_id uuid NOT NULL,                                                          -- referencia blanda a Ai
  last_inbound_message_id uuid NULL,                                               -- D-B7
  brief jsonb NOT NULL,      -- {objective, context, firstTurn, idleMinutes}: lo arma Automations, lo lee Ai; Mensajería no lo interpreta
  status varchar(16) NOT NULL,                                                     -- Live | Returned | Interrupted
  result jsonb NULL,         -- {output, reason, summary?, quotationNumber?}: lo escribe Ai al devolver
  started_at timestamptz NOT NULL, ended_at timestamptz NULL,
  CONSTRAINT "CK_bot_delegations_status" CHECK (status IN ('Live','Returned','Interrupted')),
  CONSTRAINT "CK_bot_delegations_result" CHECK ((status = 'Returned') = (result IS NOT NULL))
);
CREATE UNIQUE INDEX "IX_bot_delegations_live" ON messaging.bot_delegations (conversation_id) WHERE status = 'Live';

ALTER TABLE messaging.conversations ADD COLUMN automation_delegation_id uuid NULL;
-- CK nuevo: automation_delegation_id IS NULL OR (automation_status = 'Active' AND automation_bot_kind = 2)
```

Dominio de `Conversation`:

- `DelegateToAgent(Guid delegationId, Guid agentId, DateTimeOffset now)`: exige `Active` con `automation_bot_kind = 1` y sin asesor;
  pasa a `bot_kind = 2` con `SetBot(BotRef.Agent(agentId))` (`Conversation.cs:409-419`), guarda `automation_delegation_id`, **no** toca
  `AutomationStartedAt` ni `AutomationResumedAt` (si los tocara, `BotAttendance.HumanTookOver` daría la corrida por interrumpida,
  mismo gotcha que `ContinueAutomation`). Evento del hilo `AutomationDelegated` (chatbot → agente) para la bandeja.
- `ReturnFromAgent(Guid delegationId, Guid chatbotId, DateTimeOffset now)`: exige la delegación viva de la conversación; vuelve a
  `SetBot(BotRef.Chatbot(chatbotId))` y limpia `automation_delegation_id`. Evento `AutomationReturned`.
- `AutomationInboxKinds` suma `Delegated` (fila `bot_kind = 2`, `message_id` = id de la delegación) y `AgentReturned` (fila
  `bot_kind = 1`, `message_id` = id de la delegación). Como `ReleaseRun`, son filas sin mensaje: todo lector usa `LEFT JOIN` (gotcha de
  `automation_inbox` del `CLAUDE.md`).

Puertos (Messaging.Application, adaptadores en Bootstrapper):

- Automations: `IAutomationConversations.DelegateToAgentAsync(tenantId, conversationId, DelegationRequest(runId, chatbotId, nodeId,
  agentId, lastInboundMessageId, briefJson), ct) → Guid delegationId` y `ReadDelegationResultAsync(tenantId, delegationId, ct) →
  DelegationResult?`. Al lado de `HandoffAsync` (`AutomationsPorts.cs:297-328`).
- Ai: `IAiConversations.ReadDelegationAsync(tenantId, delegationId, ct) → AiDelegation(…, briefJson)?` y
  `ReturnToChatbotAsync(tenantId, conversationId, delegationId, resultJson, ct)`, junto a los de `AgentSessionPorts.cs:65-91`.

### 5.4 Ai: sesión en modo bloque

```sql
ALTER TABLE ai.sessions
  ADD COLUMN origin varchar(16) NOT NULL DEFAULT 'Connection',   -- Connection | ChatbotBlock
  ADD COLUMN delegation_id uuid NULL,
  ADD COLUMN chatbot_id uuid NULL, ADD COLUMN run_id uuid NULL,  -- copia para sumar consumo por chatbot sin cruzar esquemas (D-B15)
  ADD COLUMN idle_minutes int NULL,                              -- D-B11; NULL = AiSessionTimings.IdleAfter de siempre
  ADD COLUMN objective_summary varchar(500) NULL;                -- lo que dejó finish_objective
-- CK: (origin = 'ChatbotBlock') = (delegation_id IS NOT NULL)
-- CK_sessions_status suma 'Returned'
CREATE UNIQUE INDEX "IX_sessions_delegation" ON ai.sessions (delegation_id) WHERE delegation_id IS NOT NULL;
```

- `AgentSessionStatus.Returned` (nuevo, `AgentSession.cs:4-12`): terminó devolviendo el control al flujo; `end_reason` = la salida
  (`done`, `human`, `noAnswer`, `idle`, `error`) y `failure_code` = el motivo.
- `AgentSession.OpenForBlock(id, tenantId, conversationId, connectionId, agentId, agentVersionId, delegationId, chatbotId, runId,
  idleMinutes, now)`. El id es determinista por la delegación (como `AiDeterministicIds.SessionFor`), así un reintento de la fila
  `Delegated` no abre dos.
- `AgentToolNames.FinishObjective = "finish_objective"`, final, ofrecida **sólo** cuando `origin = ChatbotBlock`
  (`AgentTurnContext` sabe el origen). Argumentos `{ summary: string ≤ 500 }`. Resultado del bucle: `AgentFinished(summary)` junto a
  `AgentReplied` y `AgentHandedOff` (`AgentTurnRunner.cs:35-51`).
- Prompt: el `system` de la versión queda igual (C-12). El objetivo y el contexto del bloque viajan en el `VolatileContext`, como la fecha
  y la ventana, con la instrucción fija de QEP: «Estás atendiendo un paso de un chatbot. Tu objetivo: … Cuando lo cumplas, llama
  `finish_objective`. Si el cliente pide una persona o no puedes ayudar, llama `handoff_to_human`.» El texto exacto es contrato de prompt
  y se fija en el plan.

## 6. Ejecución

### 6.1 Entrada al bloque (Automations)

1. El motor entra en `AiAgentNode`: interpola `context` con las variables de la corrida, pide
   `Effect.DelegateToAgent(nodeId, agentId, objective, context, firstTurn, idleMinutes)` y para con `Stop(node, RunStatus.Waiting,
   TraceOutcomes.Delegated)`. Si `agentId` es nulo (borrador corrupto), sale por `error` con `agent_missing` sin pedir nada.
2. `RunEffects` ejecuta el efecto **en la misma unidad**, con el candado de la conversación: `DelegateToAgentAsync` (§5.3). Mensajería
   inserta la delegación, cambia la conversación y encola la fila `Delegated` en la **misma transacción**. Si la conversación ya no es
   del chatbot (una persona la tomó entre el claim y el efecto), Mensajería lanza la excepción de dominio de siempre y el procesador
   cierra la corrida `Interrupted` como con cualquier otro efecto.
3. La unidad confirma la corrida en `Waiting`. Los mensajes que el flujo envió antes del bloque en esa unidad salen como siempre.

### 6.2 Apertura de la sesión (Ai)

`AiInboxProcessor` reclama la fila `Delegated` (cola `bot_kind = 2`) y, dentro de su unidad con el candado (§6.2 del spec de agentes):

1. Lee la delegación (`ReadDelegationAsync`). Si no está `Live` → `skipped`.
2. Carga el agente: si no está `Active` con versión legible (`AiInboxProcessor.cs:184-197` hace la misma pregunta) → **retorno
   inmediato** por `error` con `agent_unavailable`. Si el módulo `ai` está apagado → `error` con `module_disabled`. Si la cuenta del
   proveedor del agente no está (C-19, `:214-232`) → `error` con `provider_not_configured`.
3. Abre la sesión con `OpenForBlock` y la versión publicada actual del agente (D-B5). **No** manda saludo (D-B6).
4. `firstTurn = lastMessage` y hay `last_inbound_message_id` → turno `Customer` con ese mensaje y trabajo `Pending` (D-A14, con el
   debounce de siempre). Si no, la sesión espera el próximo entrante.

Los entrantes siguientes llegan solos a la cola del agente (D-B3) y siguen el camino de siempre (`AiInboxProcessor`, paso 7 de §6.2 del
spec de agentes). La regla de disparador (D-A9) no aplica: la sesión ya está abierta.

### 6.3 Fin de la sesión y salida del bloque

Toda terminación de una sesión `ChatbotBlock` que no sea una toma humana **devuelve** en vez de traspasar: la sesión termina
`Returned`, Ai llama `ReturnToChatbotAsync` en su commit corto (el mismo que hoy envía o traspasa,
`AgentSessionJobProcessor.cs:226-412`), y Mensajería, en esa transacción, marca la delegación `Returned` con su `result`, devuelve la
conversación al chatbot y encola `AgentReturned`.

| Lo que pasó | Salida | `reason` | Hoy, sin bloque |
| --- | --- | --- | --- |
| El modelo llamó `finish_objective` | `done` | `objective_completed` | — (herramienta nueva) |
| `handoff_to_human` con `customer_asked` o `complaint` | `human` | el motivo | traspaso `AgentRequested` |
| `handoff_to_human` con `no_answer` u `out_of_scope` | `noAnswer` | el motivo | traspaso `AgentRequested` |
| Borrador de cotización creado (P-4b) | `human` | `quotation_draft_created` | traspaso a la asesora del borrador |
| Cliente sin datos para cotizar (P-4) | `noAnswer` | `quotation_customer_not_ready` | traspaso |
| `maxRepliesWithoutHuman` | `human` | `max_replies_without_human` | traspaso `AgentLimitReached` |
| Sin entrante en `idleMinutes` | `idle` | `idle` | `Abandoned` a las 24 h |
| Fallos seguidos, topes de D-A20, respuesta inválida | `error` | el código de `AgentHandoffCodes` (`AgentHandoff.cs:47-64`) | traspaso `AgentFailed` |
| Error del proveedor agotado o de cuenta (C-19) | `error` | el código de `AiModelErrorCodes` | traspaso `ProviderFailed` |
| Envío rechazado por Mensajería (`send_failed`) | `error` | `send_failed` | traspaso |
| Agente pausado, borrado o sin versión legible | `error` | `agent_unavailable` | traspaso |
| Módulo `ai` apagado | `error` | `module_disabled` | traspaso `ModuleDisabled` |
| Asesor toma, resuelve, pausa o transfiere | — (la corrida termina `Interrupted`) | `human_took_over` | sesión `Interrupted` |
| Contacto bloqueado | — (la corrida termina `Interrupted`) | `blocked` | sesión `Interrupted` |
| Ventana del canal cerrada (D-A18) | — (la corrida termina `WindowClosed`) | `window_closed` | traspaso `WindowClosed` |

Los códigos de `reason` cumplen la regla de códigos del hilo (`^[a-z0-9_.]{1,64}$`, `AgentHandoff.cs:40-46`).

`RunProcessor` reclama la fila `AgentReturned` (cola `bot_kind = 1`), lee el resultado por `ReadDelegationResultAsync` y, si la corrida
viva sigue `Waiting` en ese `nodeId` con `TraceOutcomes.Delegated` (si no, la fila es vieja: `skipped`), da el paso con
`EngineInput.AgentResult(nodeId, delegationId, output, variables)`. El motor mezcla las variables `{saveTo}.*` y hace
`Leave(node, output)`; si la salida es `human`/`noAnswer`/`error` y está desconectada, `HandOff` (D-B12; con `quotation_draft_created`
y sin camino, al miembro de la asesora que devuelve el resultado, D-B13).

### 6.4 Toma humana, ventana y bloqueo

- Una persona toma, resuelve, pausa o transfiere: `ReleaseRunSignal` hoy encola la señal **para el bot que tenía la conversación**
  (`ReleaseRunSignal.cs:12`, gotcha «La señal `ReleaseRun` lleva el `bot_kind`…»). Con `automation_delegation_id` no nulo, encola
  **dos** señales: una `bot_kind = 2` (cierra la sesión, como hoy) y una `bot_kind = 1` (cierra la corrida). La delegación pasa a
  `Interrupted` en esa transacción.
- `BotAttendance.HumanTookOver` (`BotAttendance.cs:17-25`) ya da la corrida por interrumpida cuando la conversación está `Paused`, con
  asesor o `Resolved`: la señal `bot_kind = 1` sólo adelanta lo que el próximo barrido haría.
- Ventana cerrada o contacto bloqueado: la sesión termina como hoy (`WindowClosed`, `Interrupted`) y Ai devuelve con `result.output =
  null` y el motivo; el motor termina la corrida `WindowClosed` o `Interrupted` sin salir por ninguna salida (no hay a quién mandarle
  nada).

### 6.5 Barridos

- **Inactividad del bloque (Ai):** `AiSessionSweeper` (`AiSessionSweeper.cs:28`) usa `idle_minutes` de la sesión cuando no es nulo, y en
  modo bloque termina `Returned` por `idle` en vez de `Abandoned`. La inactividad se mide desde el último turno (`UpdatedAt`), que ya es
  lo que mueve cada turno (`AgentSession.cs:60-61`).
- **Abandono de corridas (Automations):** `ClaimAbandonedAsync` (`AutomationsPorts.cs:439`) **excluye** las corridas cuyo último trace es
  `Delegated`: mientras el agente conversa, la corrida no se mueve y a las 24 h el barrido la daría por abandonada con la conversación
  todavía en manos del agente. La inactividad la decide el bloque (D-B11).
- **Delegación huérfana (Mensajería):** una delegación `Live` cuya conversación ya no tiene `automation_delegation_id` (borrado de
  cliente, rollback manual) se marca `Interrupted` en el barrido de Ai cada 10 min.

### 6.6 Lo que puede pasar en el medio

| Caso | Qué pasa |
| --- | --- |
| El cliente escribe la palabra de reinicio del chatbot durante la delegación | Va al agente, que no la conoce. Decisión de MVP: el admin la cubre en el `objective` («si el cliente pide el menú, termina») o con `finish_objective`. Reconocerla en la ingesta mezclaría reglas del chatbot en Mensajería. |
| Se republica el agente | Las sesiones vivas terminan con su versión (D-B5). |
| Se republica el chatbot | La corrida sigue con su versión; el bloque nuevo aplica a corridas nuevas. |
| Se pausa el agente | Las sesiones vivas siguen (D-A6); las delegaciones nuevas salen por `error` (`agent_unavailable`). |
| Se pausa el chatbot | Las corridas vivas siguen (D-C5) y vuelven del agente normalmente. |
| El agente responde justo cuando vence la inactividad | El commit corto toma el candado: si la sesión ya terminó `Returned`, el trabajo se descarta como hoy un trabajo de una sesión terminada. |
| `AgentReturned` llega dos veces (reintento de la fila) | La segunda no encuentra la corrida en ese bloque con `Delegated` y sale `skipped`. |
| El chatbot sale por `done` hacia otro bloque `aiAgent` | Delegación nueva, sesión nueva (la anterior ya terminó). Nunca hay dos delegaciones vivas (`IX_bot_delegations_live`). |
| Bucle `aiAgent` → `idle` → `aiAgent` | Cada entrada es un bloque más para `EngineReentries.Max` (25) del motor (gotcha «Esperas con tope»), que corta con `automations.run.loop_detected`. |

## 7. Endpoints

No hay rutas nuevas de escritura: el bloque viaja dentro del documento del flujo (`PUT …/automations/chatbots/{id}/draft`). Cambian
DTOs existentes:

| Ruta (existente) | Cambio |
| --- | --- |
| `GET /api/v1/tenants/{tenantId}/automations/chatbots/{id}` (editor) | Suma `agents: [{ id, name, status, provider, model }]` (todos los agentes del tenant, para el selector y para pintar «agente pausado») y `aiEnabled: bool` (módulo `ai`). Sin `ai`, `agents` llega `[]`. Va en el DTO del editor y no en una ruta de Ai porque quien edita un chatbot tiene `automations.chatbot.manage` y no necesariamente `ai.agent.read` (BFF: la pantalla necesita la lista para dibujar el bloque). |
| `POST /api/v1/tenants/{tenantId}/automations/chatbots/{id}/publish` (`ChatbotEndpoints.cs:55`) y `…/validate` (`:49`) | Errores nuevos de §5.2 en el 422 `automations.chatbot.invalid` existente (`AutomationsErrorCodes.cs:27`). |
| Simulador (`POST …/automations/simulations/{simulationId}/messages`, `SimulationEndpoints.cs:25`) | Con la simulación parada en `aiAgent`, el cuerpo acepta `{ agentResult: { output, summary?, reason?, quotationNumber? } }` en lugar de un mensaje del cliente (D-B17). |
| `GET /api/v1/tenants/{tenantId}/automations/chatbots` | Cada fila suma `aiUsageThisMonth: { inputTokens, outputTokens } | null` (suma de `usage_records` de las sesiones con ese `chatbot_id`, reloj del tenant). |
| `GET /api/v1/tenants/{tenantId}/ai/agents` | Cada fila suma `usedByChatbots: [{ id, name }]`. |
| `GET /api/v1/tenants/{tenantId}/ai/sessions` | Cada sesión suma `origin` y, en modo bloque, `chatbot: { id, name }` y `output`. |
| `POST /api/v1/tenants/{tenantId}/ai/agents/{id}/tests` | Cuerpo opcional `block: { objective, context? }`: la prueba ofrece `finish_objective` y la transcripción muestra `returned: { output, reason, summary }` en vez de `handedOff`. |
| `DELETE /api/v1/tenants/{tenantId}/ai/agents/{id}` | 409 `ai.agent.in_use_by_chatbots` con `chatbots: [{ id, name }]` en las extensiones (D-B18). |
| `GET /api/v1/tenants/{tenantId}/messaging/conversations/{id}` | `automationBot` suma `delegatedBy: { chatbotId, name } | null` para que el chip diga «Agente IA · desde el chatbot X». |

Códigos nuevos: `ai.agent.in_use_by_chatbots` (409), `ai_agent.*` del validador (dentro del 422 del chatbot),
`messaging.automation.delegation_not_live` (409, interno: no llega a un endpoint).

## 8. Datos personales y seguridad

- La delegación no copia mensajes (D-A28 sigue): `brief` guarda objetivo y contexto **ya interpolado**, que puede traer datos del
  cliente (`{nombre}`). Retención: se borra con la conversación (se agrega `messaging.bot_delegations` a
  `CustomerDeletionReferences`, gotcha «Borrar clientes escribe en muchos esquemas»).
- El `context` se interpola con variables de la corrida que el cliente escribió: es texto del cliente y viaja **en el contexto volátil
  marcado como datos**, nunca como instrucciones (D-A30 y la guía de Anthropic de poner el contenido de terceros separado de las
  instrucciones: <https://platform.claude.com/docs/en/test-and-evaluate/strengthen-guardrails/mitigate-jailbreaks>).
- `finish_objective.summary` lo escribe el modelo y termina en una variable que el flujo puede mandar al cliente o a una API (bloque
  HTTP). Se trata como texto del cliente: el bloque HTTP ya lo escapa al interpolarlo (P2-R11, P2-R31).

## 9. Migraciones (orden)

1. Messaging `AddBotDelegations` (tabla, columna `automation_delegation_id`, CHECK nuevo).
2. Ai `AddSessionOrigin` (columnas de §5.4, CHECK de estado con `Returned`).
3. Automations: sin migración de esquema (el bloque vive en el jsonb del flujo).

Despliegue: backend completo antes que el frontend (un editor nuevo contra un backend viejo guardaría un `aiAgent` que el validador viejo
rechaza con `flow.unknown_kind`, `FlowJson.cs:16`).

## 10. Pruebas

- **Unitarias (Automations, motor):** entrar al bloque pide `DelegateToAgent` y para `Waiting/Delegated`; `AgentResult` por cada salida
  mezcla `{saveTo}.*`; resultado de otro nodo o de otra delegación no cambia nada; salidas de problema sin conectar traspasan
  (D-B12) y `quotation_draft_created` traspasa a la asesora (D-B13); interpolación y corte de `context`; validador, un caso por código.
- **Unitarias (Ai):** apertura por `Delegated` (agente pausado, sin cuenta, módulo apagado → retorno `error`); `firstTurn`;
  `finish_objective` sólo en modo bloque; cada fila de la tabla de §6.3 produce su salida y nunca un traspaso; inactividad por bloque.
- **Unitarias (Messaging):** `DelegateToAgent`/`ReturnFromAgent` no tocan `AutomationStartedAt` ni `AutomationResumedAt`; la señal
  doble con delegación viva; CHECK de `automation_delegation_id`.
- **Integración (Testcontainers, filtradas por nombre mientras se itera):** recorrido completo entrante → flujo → bloque → agente con
  servidor HTTP falso del proveedor → `finish_objective` → retorno → bloque siguiente envía; toma humana durante la delegación cierra
  sesión **y** corrida; el barrido de abandono no toca corridas delegadas; `IX_bot_delegations_live` frena dos delegaciones.
- **Arquitectura:** `AiLayerTests` y `AutomationsLayerTests` sin cambios de reglas (todo cruce por Bootstrapper); paridad de códigos de
  `reason` con la regla del hilo.
- **TDD:** RED antes que GREEN, con evidencia literal.

## 11. Orden de implementación

| Slice | Contenido |
| --- | --- |
| AIB-1 | Messaging: delegación (tabla, dominio, tipos de fila, señal doble) + puertos. |
| AIB-2 | Ai: sesión en modo bloque, `finish_objective`, retorno, inactividad por bloque, `connections_missing` a aviso, playground en modo bloque. |
| AIB-3 | Automations: bloque, validador, efecto, `AgentResult`, barrido de abandono, simulador, DTO del editor y consumo por chatbot. |

## 12. Decisiones pendientes del owner

| ID | Pregunta | Por qué no se puede asumir |
| --- | --- | --- |
| OB-1 | ¿Un flujo puede **seguir sin asesor** después de que el agente crea un borrador de cotización dentro del bloque? Este spec respeta P-4b: sale por `human` y, sin camino, va a la asesora del borrador (D-B13). | P-4b es una decisión de negocio del owner tomada para el agente sin canvas; el bloque abre la posibilidad de encadenar otra cosa. |
| OB-2 | ¿Prioridad de llevar el motor de chatbots a Instagram? Hasta entonces el bloque sólo existe en WhatsApp (§2: Instagram encola sólo para agentes). | Es alcance y prioridad, no diseño. |
| OB-3 | ¿QEP cobra algo por las conversaciones atendidas por IA dentro de un chatbot (además del consumo del modelo, que paga la cuenta BYOK del tenant)? | Precio y empaquetado; la competencia cobra por respuesta, resolución o conversación (§3). |
