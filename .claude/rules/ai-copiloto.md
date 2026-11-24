---
paths:
  - "src/Modules/Ai/**"
  - "tests/Modules/Ai/**"
  - "src/Bootstrapper/Copilot/**"
  - "src/Bootstrapper/**/Ai*.cs"
---

# Agentes de IA y copiloto

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **Un agente de IA nunca cae al otro proveedor** (plan 2026-11-12, C-20). El resolvedor toma el proveedor **de la versión de la
  sesión**, no del borrador ni del tenant, y un error agotado del proveedor termina en traspaso, nunca en una llamada al otro. No
  agregues un «respaldo» aunque el tenant tenga las dos conexiones: el historial del cliente iría a una cuenta que el tenant no eligió
  para ese agente y le gastaría saldo sin aviso. El único respaldo es el de Anthropic **dentro** de Anthropic (C-9,
  `server-side-fallback`). Lo amarra `AgentTurnRunnerTests.TheRunnerAsksTheResolverForTheProviderOfTheVersion`.
- **El `issue` bloqueante de IA es por proveedor, no por tenant** (C-19). Clave rechazada, sin permiso o sin saldo pasan a
  `NeedsAttention` **la conexión de ese proveedor** (`IAiProviderCredentials.MarkNeedsAttentionAsync`), y sus agentes dejan de tomar
  conversaciones; los del otro proveedor siguen. El procesador de entrantes revisa la cuenta del proveedor **del agente** antes de abrir
  sesión. Lo amarran `AgentTurnRunnerTests.AnAccountErrorLeavesTheIssueOnlyOnThatProvidersConnection` y
  `AgentSessionProcessorTests.WithoutTheAgentsProviderAccountNoSessionOpensEvenIfTheOtherProviderIsConnected`. La saca una prueba
  exitosa de la clave en Integraciones, no un reintento.
- **Cuatro reglas de `ClaudeModelClient` que parecen simplificables y no lo son** (plan 2026-11-12). (1) **C-8:** herramientas con
  `strict: true` y sin `tool_choice` forzado: en Opus 5.5 y Sonnet 5.5, `any` o `tool` responde 400. Por eso un `end_turn` con texto y
  sin herramienta final se acepta como `send_reply` sin fuentes (`ToolsAreStrictSortedByNameAndNeverForced`,
  `AnEndTurnWithTextComesBackAsASendReplyWithoutSources`). (2) **C-11:** la petición nunca termina en `assistant`, que es _prefill_ y
  responde 400; el saludo ya enviado va como mensaje `system` después del primer turno del cliente
  (`TheGreetingIsASystemMessageAfterTheFirstCustomerTurnAndTheRequestNeverEndsInAssistant`). (3) **C-12:** el `system` es idéntico byte a
  byte en la misma versión y lleva el único punto de caché; la fecha, la hora y la ventana van en `VolatileContext`, **después**
  (`TheSystemPromptCarriesTheOnlyCachePointAndTheVolatileContextGoesAfterIt`). Un `DateTime.Now` en `AgentPromptBuilder` invalida el
  caché en cada llamada sin ningún error; sólo lo delata `cache_read_input_tokens` en 0, que es lo que mide el eval. (4) **C-13:**
  `MaxRetries = 0`: con los reintentos del SDK el tiempo real es el timeout por tres y el trabajo pasa su tope y su lease sin saberlo.
  Reintenta el worker (`ModelJob.MaxAttempts`), igual para los dos proveedores.
- **OpenAI va con `HttpClient` directo, sin su SDK, y con `store: false` explícito** (spec 2026-10-29 §9.1, O-2 y O-3). El SDK oficial
  reintenta solo 408, 429 y 5xx hasta tres veces, que es justo lo que C-13 prohíbe; no lo «modernices» agregando el paquete. Y `store`
  vale `true` si se omite: la conversación del cliente quedaría guardada en la cuenta del tenant (`ItKeepsNoStateOnTheProvider`). Al
  comparar consumo: en OpenAI `input_tokens` **incluye** los leídos de caché y en Anthropic no; `ai.usage_records` los guarda como los
  reporta cada proveedor, que es lo que se contrasta con su factura (O-7). `cache_creation_input_tokens` de OpenAI es `NULL`, no `0` (C-4).
- **Una clave de proveedor de IA jamás se imprime**, ni al verificar que una conexión existe. Son BYOK: viven cifradas en `integrations`
  (no en `user-secrets`, el ConfigMap ni el Secret), y en código viajan en `AiCredential`, cuyo `ToString` no la escribe. Los clientes
  nombrados `ai.anthropic` y `ai.openai` van con `RemoveAllLoggers()`: el log de `HttpClient` escribiría el `Authorization`. Lo amarran
  `AnthropicNoLeakTests` y `OpenAiNoLeakTests`, con el host en `Trace`. El eval (`Modules.Ai.Eval`) lee la clave sólo de
  `QEP_EVAL_ANTHROPIC_API_KEY` / `QEP_EVAL_OPENAI_API_KEY`, de una cuenta de evaluación, y nunca la acepta por argumento. **El eval gasta
  dinero real: no se corre sin el presupuesto aprobado por el owner** (README, «Agentes IA», «Eval»).
- **La cola del bot se rutea por `bot_kind`, y una conexión sin ocupante no deja fila** (spec 2026-10-29 §6.1). El webhook decide en
  `InboundIngestion.BotKindForAsync`: una conversación con el asistente `Active` sigue con el bot con que empezó
  (`automation_bot_kind`); si no, la toma quien ocupa la conexión en `messaging.connection_bots`; sin ocupante **no se encola** (antes se
  encolaba y el motor la marcaba `skipped`). El síntoma en una prueba que siembra la conversación sin sembrar el ocupante es un bot mudo
  sin error (`AutomationInboxTests.WithoutAnOccupantTheInboundIsNotEnqueued`). Los agentes reclaman sólo `bot_kind = 2` y el motor de
  chatbots sólo `1` (`AnAgentOccupantRoutesTheRowToTheAgentAndTheChatbotEngineDoesNotClaimIt`). Los valores de `BotKind` se guardan en
  tres columnas: no se reordenan.
- **`sent_by_kind = 3` es un agente de IA: lleva `sent_by_agent_id` y nunca miembro** (`CK_messages_sent_by_agent`,
  `CK_messages_sent_by_kind IN (1,2,3,4)`). Un join al miembro lo pierde en silencio, igual que al `4`. Para «primera respuesta» cuenta
  como un chatbot y nunca como asesor (`MessagingReportSource`); su nombre sale del mismo diccionario de los chatbots
  (`IAutomationsChatbotNames`, que ahora lee también Ai). Un lector nuevo de autores decide qué hace con el 3 y con el 4.
- **El `Down` de `AddAgentAutomation` (Messaging) sólo corre sobre una base sin agentes.** Con un mensaje `sent_by_kind = 3` o una
  conversación de un agente, los CHECK viejos fallan y el `Down` se deshace entero. El rollback de los agentes es revertir el código, no
  correr ese `Down`.
- **El `Down` de `AddOpenAiProvider` borra las conexiones de `openai`, y con ellas sus secretos** (`CASCADE`), igual que el de
  `AddAnthropicProvider` con las de `anthropic`: Postgres valida las filas al volver a poner el CHECK. Correrlo en producción le borra la
  clave a cada tenant que la había conectado; tienen que volver a ponerla.
- **El borrador de cotización de un agente no se duplica por el índice, no por la búsqueda previa** (spec 2026-10-29 D-A16). La clave es
  `ai:{jobId}` en `quotations.quotations.external_key`, **del trabajo y no de la llamada**: el id de la llamada lo inventa el modelo y
  cambia si el trabajo se retoma, y con él en la clave cada reintento creaba otro borrador (revisión final, hallazgo 1). Por la misma
  razón el bucle corre a lo sumo una herramienta que escribe por respuesta (`AgentToolNames.Writes`); la segunda vuelve con
  `one_write_per_response` (`CreateQuotationDraftToolTests.TwoDraftCallsInOneResponseCreateOneDraft`); `IX_quotations_tenant_external_key` (parcial) cierra la carrera y
  `QuotationsUnitOfWork` lo traduce **por nombre de índice** a `quotation.quotation.external_key_taken`, que `ExternalQuotationService`
  resuelve devolviendo la cotización que ganó (`AlreadyExisted`). Mismo patrón que `IX_orders_tenant_external_key`: no lo cambies por una
  consulta en el servicio (`ExternalQuotationServiceTests.TheSameExternalKeyReturnsTheExistingQuotation`). Y un traspaso con borrador
  escrito no lo descarta un turno nuevo del cliente: el trabajo siguiente crearía otro.
- **La respuesta del modelo se guarda en el trabajo antes de enviar nada, y nunca se vuelve a pedir** (revisión final, hallazgo 2).
  `AgentSessionJobProcessor` llama `ModelJob.RememberResult` apenas vuelve el bucle; un commit corto que falla, un proceso que muere o el
  candado ocupado retoman con `result` y no pagan el modelo otra vez. El traspaso de Mensajería lanza `RequestConcurrencyException`
  cuando un entrante sube `conversations.version` a la vez: el commit se deshace y el trabajo se reprograma por `IAiModelJobs.RetryHeldAsync`,
  SQL directo para no guardar lo que quedó a medio cambiar en el contexto. Cada reclamo suma un intento, también los que no llaman al
  modelo, así que el tope de reclamos es `ModelJob.MaxClaims` (10) y no `MaxAttempts`; pasado, traspaso `AgentFailed` con `job_timed_out`.
  No lo bajes a `MaxAttempts`: unos pocos candados ocupados terminarían el turno. Y el worker mira que la conversación siga siendo del
  agente **antes** de llamar al modelo (`AgentSessionProcessorTests.AJobIsDiscardedBeforeCallingTheModelWhenAPersonTookTheConversation`).
- **El trabajo pendiente de una sesión se lee con `FOR UPDATE`** (`AgentSessionRepository.FindPendingJobAsync`, revisión final,
  hallazgo 3). El reclamo del worker no toma el candado de la conversación; sin el candado de fila, el worker reclamaba el trabajo con el
  `up_to_seq` viejo mientras el procesador de entrantes lo subía, y el turno nuevo del cliente quedaba sin respuesta. No lo cambies por un
  `SingleOrDefaultAsync` (`AgentSessionJobRaceTests`).
- **Una conversación `Active` de un agente sin sesión viva es del agente, y si no la puede retomar se traspasa** (revisión final,
  hallazgo 4). Pasa cuando una persona se la devuelve: el siguiente entrante abre otra sesión sólo si el agente está `Active`, tiene versión
  legible y la cuenta de su proveedor; si no (pausado, borrado, sin cuenta, módulo apagado) `AiInboxProcessor` la traspasa. Devolver
  `null` sin traspaso la dejaba asignada a un agente que nunca contesta. Un trabajo cuyo agente se borró con la sesión viva (la carrera
  con `DeleteAgentHandler`, que mira las sesiones sin el candado de la conversación) también termina en traspaso. Pausar un agente
  corta sus sesiones vivas (owner, 2026-11-18; revierte D-A6): `AgentSessionJobProcessor` no llama al modelo y traspasa con
  `agent_paused`. Lo amarra `AJobOfAPausedAgentIsNotAnsweredAndTheConversationIsHandedOff`.
- **La señal `ReleaseRun` lleva el `bot_kind` del bot que tenía la conversación antes del cambio** (revisión final, hallazgo 5).
  `ReleaseRunSignal.EnqueueIfAssisted` recibe el `AutomationBotKind` leído **antes** (resolver lo borra) y el autoasignado lo devuelve en su
  `RETURNING`. Con el default `1`, la señal de un agente la reclamaba el motor de chatbots y la sesión del agente seguía viva con su
  trabajo pendiente. Un camino nuevo que encole la señal pasa el bot (`ConversationAutomationApiTests.TheSignalOfAnAgentConversationGoesToTheAgentQueue`).
- **La causa de un traspaso vive en tres enums que se cambian juntos**: `HandoffCauseKind` del motor de chatbots (Automations), el de los
  agentes (`Modules.Ai.Application.HandoffCauseKind`) y `AutomationHandoffKind` (Messaging), traducidos por nombre en Bootstrapper. Un
  código de traspaso de agente nuevo tiene que pasar `AutomationHandoff.IsValidCode` o se guarda `null` sin error, y un proveedor nuevo
  tiene que sobrevivir la limpieza del hilo. Los amarra `HandoffCauseKindParityTests` (Bootstrapper.UnitTests).
- **Toda transición que saca una conversación de `Active` (o le cambia el bot) limpia `automation_delegation_id`** (spec 2026-11-17 agente
  IA en chatbots, plan fase 2 B1.1). `CK_conversations_delegation` sólo admite la delegación con `automation_status = 'Active'` y
  `automation_bot_kind = 2`: tomar, resolver, pausar, transferir o terminar sin pasar por la limpieza central de `Conversation` falla en la
  base, no en el dominio. Un método nuevo que mueva el estado de automatización pasa por ella.
- **La toma humana de una conversación delegada señala a los dos bots** (B1.3). `ReleaseRunSignal.EnqueueIfAssisted` recibe la delegación
  leída **antes** del cambio, como el bot: con una viva encola `bot_kind = 2` (la sesión del agente termina) **y** `bot_kind = 1` (la corrida
  que espera en el bloque se interrumpe), y marca la delegación `Interrupted` en el mismo guardado. Con una sola señal, el otro bot queda
  esperando una vuelta que nunca llega. Las filas `Delegated` y `AgentReturned` de `automation_inbox` no tienen mensaje: todo lector nuevo
  de la cola va con `LEFT JOIN` al mensaje.
- **Los `Down` de la fase 2 de agentes no se corren en producción.** `AddBotDelegations` (Messaging) sólo sobre una base sin delegaciones
  vivas: con una, la conversación queda en `bot_kind = 2` sin agente que la devuelva. `AddChatbotBlockSessions` (Ai) falla con sesiones
  `Returned` (el CHECK viejo no las admite) y se deshace entero. `AddCopilot` (Ai) falla con trabajos o consumo del copiloto (`agent_id`
  nulo); borrarlos antes pierde el consumo pagado. `AddCopilotModuleKey` (Tenancy) falla si algún tenant ya tiene `copilot`. El rollback es
  revertir el código con los módulos `ai` y `copilot` apagados.
- **Sólo `CopilotToolExecutor` crea un `DelegatedExecutionContext`** (spec 2026-11-17 copiloto D-C3, plan C2.4). `IExecutionContext` sale
  de una fábrica scoped: el delegado si el scope lo trae, el de HTTP si no. Abrir un scope delegado en otro lugar haría correr código como
  un miembro que no pidió nada; lo amarra `CompositionRootTests` leyendo el IL. Ninguna herramienta del copiloto corre con
  `AiServicePrincipal`.
- **Las herramientas del copiloto sólo despachan consultas, y la misma del endpoint** (D-C4). `CopilotToolsTests` (ArchitectureTests) lee
  el IL de `Bootstrapper.Copilot`: ningún `IRequestDispatcher.SendAsync`, ningún `ICommand<>`, nada de Integrations ni consultas
  `Operator*`, y cada herramienta despacha algún `IQuery<>`. Una herramienta nueva usa la constante de permiso del módulo (no un texto
  copiado), el módulo de su endpoint, esquema con forma estricta, tope de 20 filas con `total`/`truncated`, y un `kind` de cita que el frontend
  sepa enrutar (`citationRoute`; `charge` no tiene pantalla y sale como cita sin enlace). El tenant sale del hilo, nunca de un argumento.
- **Las herramientas del copiloto van sin `strict` y no se recortan** (2026-11-18). Anthropic acepta a lo sumo **20 herramientas con
  `strict: true` por request** (y 24 parámetros opcionales y 16 uniones, `["string","null"]` incluida, sumando todas); el tenant dueño de la
  plataforma, con todos los módulos, ya pasaba de 20 y la llamada respondía 400. `CopilotReportTools.Contract` las declara
  `Strict: false` y `CopilotArguments` valida cada argumento (`invalid_arguments`). Estrictas quedan sólo las finales y las que escriben
  (`AiToolDefinition.Strict`, por defecto `true`). No vuelvas a un `.Take(20)`: dejaba sin herramientas, en silencio, a los módulos
  que caen al final del orden alfabético. Lo amarran `NoCopilotToolIsStrict` y `MoreThanTwentyToolsAreAllOffered`.
- **La garantía de permisos del copiloto es la de un request, no instantánea** (D-C3). Cada llamada a una herramienta resuelve los
  permisos del miembro, pero con `Valkey:Areas:Permissions` prendido pasa por `CachedAuthorizationService`: lo que sube la versión de
  acceso del tenant se ve en la llamada siguiente; lo que no, en ≤ 60 s.
- **El texto del copiloto vive sólo en el hilo** (D-C13). Preguntas y respuestas están en `ai.copilot_messages` (y la respuesta ya pagada
  en `model_jobs.result` hasta su commit); nunca en logs, auditoría ni `tool_calls`. La auditoría (`ai.copilot.settings_updated`,
  `ai.copilot.thread_deleted`) es de metadatos: `IAiAuditRecorder.RecordCopilot` no tiene parámetro de texto libre. Borrar un hilo
  guarda su auditoría en la misma transacción del borrado (`CopilotRepository.DeleteThreadAsync`). Lo amarra `CopilotNoLeakTests`.

- **Agente que agenda (spec 2026-11-23, plan `2026-11-23-agente-ia-agenda-backend`): `IChannelScheduling` es el único camino sin sesión a la Agenda.**
  Ai declara `IAiScheduling` y `Bootstrapper/AiScheduling.cs` lo traduce a `IChannelScheduling`; Ai no referencia Scheduling, Messaging ni Customers
  (`AiLayerTests`, `SchedulingLayerTests`). El tenant y el cliente salen del contexto del trabajo (`ConversationCustomerResolver` relee el cliente en
  cada llamada), nunca de un argumento del modelo. Una cita de otro cliente es `appointment_not_found`, igual que una inexistente.
- **La llave de reserva es `ai:{jobId:N}:book:{n}` y el ordinal `n` no cuenta fallas** (D-14). `AgentTurnRunner.AgentProfile` suma sólo las
  reservas que devolvieron cita: si el modelo reintenta tras un `slot_taken` la llave sigue siendo la misma y un trabajo retomado (perfil nuevo,
  cuenta desde 1) encuentra su cita (`alreadyBooked`) aunque pida otra hora. Contar fallas haría que el reintento reservara dos veces. Una
  segunda escritura en la misma respuesta del modelo vuelve `one_write_per_response` y no consume ordinal.
- **Una escritura de la Agenda que tuvo éxito no se descarta por un turno nuevo del cliente.** `AgentReplied.Wrote` y `AgentHandedOff.Wrote`
  (guardados en `StoredOutcome`) lo dicen, y `AgentSessionJobProcessor.CommitAsync` los trata como el borrador de cotización: sin eso la
  respuesta se pierde, la cita queda hecha y el siguiente trabajo (otro `JobId`, otra llave) reserva de nuevo.
- **Las herramientas simuladas del playground no escriben nunca.** Sólo leen por IAiScheduling (SimulatedBook/Cancel/RescheduleAppointmentTool): el resultado es inventado con la misma forma y sus ids llevan el prefijo de SimulatedSchedulingIds; una cita ajena que pidan es `appointment_not_found` sin tocar nada.
- **`appointment_events.by_channel` es sólo del agente** (D-17): `SchedulingActor.ByChannel` lo pone el actor del agente, ninguna escritura
  por HTTP lo marca. Los traspasos `appointment_change_blocked` y `appointment_customer_unavailable` van al destino configurado del agente
  (persona o sin asignar); en modo bloque `AgentBlockReturn.For` los trata como `NoAnswer` con su código de motivo, no como `Human`.
- **El núcleo de Scheduling relee la cita después del candado (`RefreshAsync`) y vuelve a decidir.** Plazo del cliente, candado del recurso y
  filtro de la misma sede se calculan con la lectura de antes del candado; si tras `RefreshAsync` cambió `ResourceId`, `StartsAt` o
  `LocationId`, `AppointmentCancellation` y `AppointmentRescheduling` lanzan `RequestConcurrencyException` y quien llama relee
  (`ChannelScheduling` lo traduce a «ya hecho» sólo si lo que otro escribió es justo lo pedido). Excepción: un cambio de hora que ya está en
  el inicio pedido devuelve `false` (nada que escribir). Un camino nuevo que decida antes del candado repite esta comprobación.
- **`ai.tool_calls.arguments` y `result` son `jsonb`: normalizan los espacios.** Para contar o filtrar por lo que devolvió una herramienta
  (`AgentActivityReader`, reservas del día) se pregunta por contención (`@>`), nunca por el texto del resultado: un `LIKE` o `Contains` sobre
  el JSON depende de espacios y orden que Postgres reescribe.