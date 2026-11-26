# Automatizaciones: cierre por inactividad del cliente

**Fecha:** 2026-11-03
**Módulos:** Automations (chatbot, barrido del worker, `RunProcessor`), Messaging (dominio de la conversación y evento `Resolved`),
Bootstrapper (adaptadores). **Frontend:** `qep-frontend`: editor del chatbot y texto del evento en el hilo (spec aparte o tarea del builder).
**Estado:** listo para construir. Las decisiones «del owner» las tomó el owner el 2026-11-03; las «del orquestador» las tomó el orquestador
ese mismo día; las «del redactor» son criterio del autor. No queda ninguna `DECISIÓN-PENDIENTE`.
**Depende de** `2026-11-03-bot-suelta-conversacion-design.md` sólo en una cosa: una acción humana cierra la corrida enseguida, así que el
barrido de este spec casi nunca encuentra una conversación tomada.

## 1. Contexto

**Pedido del owner.** Cuando el bot espera la respuesta del cliente y el cliente no contesta en X horas, el bot le avisa que la conversación se
va a cerrar (y que puede volver a escribir) y la resuelve.

**Lo que dice el código hoy:**

- **Lo único con reloj es el barrido de abandonadas.** Cada `AbandonSweepMinutes` (10, `AutomationsWorkerOptions.cs:16`) el worker
  (`AutomationInboxWorker.SweepAsync`, `:109-126`) llama `RunProcessor.FindAbandonedAsync` (`RunProcessor.cs:226-231`), que elige con
  `ClaimAbandonedAsync` las `Waiting` con `updated_at < now - 24 h`, con `FOR UPDATE SKIP LOCKED` (`AutomationsRepositories.cs:189-204`), y
  después `AbandonAsync` (`RunProcessor.cs:240-284`) cada una en su unidad: candado de la conversación, relectura, `HumanTookOver` →
  `Interrupted`; si no, `EndAsync` y `Abandoned`, con auditoría `automations.run.abandoned`. No le escribe al cliente.
- **`run.UpdatedAt` es «desde cuándo espera».** Lo escribe cada unidad del motor (`ConversationRun.Apply`, `ConversationRun.cs:92-105`) y
  «Continuar» (`KeepWaiting`, `:153-161`). El índice parcial `IX_conversation_runs_waiting_updated` cubre esa lectura (`AutomationsDbContext.cs:236`).
- **Los ajustes del chatbot no están versionados.** `restart_keyword`, `max_attempts`, `exhausted_action` y `exhausted_member_id` son columnas de
  `automations.chatbots` (`AutomationsDbContext.cs:133-140`), se editan con `PUT /chatbots/{id}` (`UpdateChatbotHandler`, `Chatbots.cs:530-542`)
  y una corrida viva lee los valores **actuales** (`FindEngineSettingsAsync`, `AutomationsRepositories.cs:40`). El flujo sí se versiona.
- **Cómo envía el bot.** `IAutomationMessenger.SendTextAsync(tenant, conversación, chatbot, clientId, texto)` devuelve `BotSendOutcome`
  (`AutomationsPorts.cs:337-359`): `Sent`, `NotAutomated`, `WindowClosed`, `Blocked`, `Rejected(code)`. El adaptador (`BotOutbound.SendAsync`,
  `BotOutbound.cs:53-101`) deduplica por `clientId`, revisa bloqueo, que la conversación sea del bot (abierta, sin asesor, asistente `Active`) y
  la ventana de 24 h desde `LastInboundAt`.
- **Cómo resuelve el bot, y su hueco.** `IAutomationConversations.ResolveAsync` (`Bootstrapper/AutomationConversations.cs:142-162`) carga el
  agregado, llama `Conversation.Resolve` y escribe el evento `Resolved` firmado por el chatbot (`ChatbotId`, sin `Actor`). `Resolve` sólo comprueba
  que no esté ya resuelta (`EnsureCanResolve`, `Conversation.cs:267-273`): **no mira asesor ni estado del asistente**. Si un asesor toma la
  conversación entre la lectura y la resolución, el bot se la resuelve igual.
- **El `details` de un evento es JSON** escrito y leído sólo por `ConversationEventJson` (`ConversationEventJson.cs:11-77`), con lectura
  tolerante (un valor desconocido se lee `null`, `:63-66`). El hilo lo expone en `MessageEventDto` (`MessagingDtos.cs:148-151`), armado en
  `MessagingSupport.cs:232-246`. Un `Resolved` del bot ya lleva `chatbot` (`MessagingDtos.cs:145-147`).
- **`clientId` determinista:** `DeterministicClientId.For(runId, baseStepNumber, effectIndex)` (`DeterministicClientId.cs:18-19`). La siguiente
  unidad de una corrida usa `baseStep = run.StepNumber` y empieza en el índice 0.
- **Instagram:** el bot nunca atiende una conversación de Instagram. `BotOutbound` responde `NotAutomated` a todo envío por ese canal
  (`BotOutbound.cs:79-82`), así que una corrida ahí termina `Interrupted` en su primer envío y nunca llega a `Waiting` con una pregunta hecha.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Un chatbot puede cerrar por inactividad: tras X horas (1 a 23, default 8) sin respuesta del cliente mientras el bot espera, manda un aviso y
  resuelve la conversación.
- El hilo dice «Resuelta por inactividad», y la vista de corridas distingue el cierre.
- Si el aviso no sale, la conversación se resuelve igual, la corrida se cierra y queda el motivo.

**No objetivos**

- Variables (`{nombre}`) en el aviso: es texto fijo. Se puede agregar después con `TemplateText`.
- Un motivo de resolución del tenant para este cierre (owner, D-13).
- Recordatorios antes de cerrar, o varios avisos.
- Cerrar por inactividad del asesor (lo descartó el owner en el spec 2026-10-26, §2).
- Cambiar el barrido de abandonadas de 24 h.
- Cerrar el mismo hueco en el bloque «Resolver» del flujo (§7, seguimiento).

**Anti-objetivos**

- Resolver una conversación que tiene un asesor, aunque lo haya tomado un instante antes (D-9).
- Tocar una corrida con el asistente pausado por `Manual`: rompería «Continuar donde iba» (D-7).
- Que el aviso se mande dos veces, o que su `clientId` choque con el de un mensaje del flujo.
- Que activar el cierre en un chatbot con corridas viejas resuelva de golpe conversaciones que esperaban desde antes (D-6).
- Cambiar el comportamiento de los chatbots existentes al desplegar.

## 3. Decisiones

**D-1 (del owner).** Sólo con la corrida `Waiting` y el bot atendiendo (`BotAttendance.IsAttending`, `BotAttendance.cs:28-29`). Con un asesor, o
con el asistente pausado o en `None`, no hay cierre por inactividad: ni aviso ni resolución. Lo que el barrido hace con esas corridas (cerrarlas
`Interrupted` o dejarlas) lo dice D-7.

**D-2 (del owner).** Pasadas X horas sin respuesta: el bot manda el aviso y resuelve. Si el cliente escribe después, la conversación se reabre y
el bot empieza desde cero, como tras cualquier resolución del bot (la ingesta reabre y encola; la corrida ya está cerrada).

**D-3 (del owner).** Por chatbot: horas y texto. Default 8 h. Rango 1 a 23, para que el aviso, que es texto libre, quepa en la ventana de 24 h.
Apagado para todo chatbot existente; se prende por chatbot.

**D-4 (del owner).** Si el aviso no sale (ventana cerrada, contacto bloqueado, Meta rechaza), se resuelve igual, la corrida se cierra y queda el
motivo.

**D-5 (del redactor). Dónde vive la configuración: en `automations.chatbots`, sin versionar**, como la regla de agotamiento (spec 2026-10-25 D-2).
Es un ajuste de atención, no del flujo: publicar no lo cambia, y una corrida viva usa el valor vigente.

| Columna | Tipo | Regla |
|---|---|---|
| `inactivity_close_enabled_at` | `timestamptz NULL` | `NULL` = apagado. Al prender se escribe `now`; volver a guardar prendido no lo mueve |
| `inactivity_close_hours` | `smallint NOT NULL DEFAULT 8` | `CK_chatbots_inactivity_hours`: `BETWEEN 1 AND 23` |
| `inactivity_close_message` | `varchar(1000) NULL` | Prendido exige texto (`CK_chatbots_inactivity_message`: `inactivity_close_enabled_at IS NULL OR inactivity_close_message IS NOT NULL`) |

1000 es el tope de Instagram (`SendMessageValidator.InstagramTextMaxLength`, `SendMessage.cs:21`) y queda debajo de los 4096 de WhatsApp
(`:17`). Apagar conserva horas y texto, para no perderlos al volver a prender.

**D-5a (del owner). Texto por defecto del aviso**, exacto:

> Como no hemos recibido tu respuesta, vamos a cerrar esta conversación. Si necesitas algo más, escríbenos de nuevo y con gusto te ayudamos.

Vive como constante del dominio (`Chatbot.DefaultInactivityCloseMessage`). No se guarda en la base hasta que el usuario guarda el chatbot
prendido: D-11 lo manda al editor para que lo dibuje.

**D-6 (del redactor). Desde cuándo se cuenta.** Cutoff = `now - inactivity_close_hours`, y la corrida califica si
`GREATEST(run.updated_at, chatbot.inactivity_close_enabled_at) < cutoff`. Prender el cierre no resuelve de golpe las conversaciones que ya
esperaban: su reloj arranca al prender. Cambiar las horas sí aplica enseguida a las que esperan. «Continuar» (`KeepWaiting`) renueva el reloj,
como el del abandono.

**D-7 (decisión del orquestador, 2026-11-03). Qué hace el barrido con una corrida que el bot no atiende.** Hay que separar la pausa `Manual`
del resto de `HumanTookOver` (`BotAttendance.cs:21-24`):

| Estado de la conversación bajo el candado | Qué hace el barrido de inactividad |
|---|---|
| Asistente `Paused` por `Manual`, sin asesor | **Nada.** La corrida sigue `Waiting` para «Continuar» (spec 2026-11-01). Además, la elección la excluye (paso 1) |
| Con asesor, `Resolved`, asistente `None`, otra pausa (`AdvisorTook`, `AdvisorReplied`, `AdvisorTransferred`, `Handoff`), `AutomationResumedAt > run.StartedAt`, o la conversación ya no existe | `Finish(Interrupted)` y `WriteClosed(…, HumanTookOver)`, sin auditoría ni aviso: lo mismo que `AbandonAsync` (`RunProcessor.cs:261-266`) |
| El bot atiende (`IsAttending`) | D-8 |

Con el spec de la señal (`ReleaseRun`) la segunda fila casi no ocurre: la acción humana ya cerró la corrida. Queda como red, igual que el abandono.

**D-7a (del redactor, con la exclusión del orquestador). El barrido.** Un paso nuevo en `AutomationInboxWorker.SweepAsync`, **antes** del de
abandonadas, con su propio `StepAsync("inactivity", …)`, en el mismo intervalo de 10 min. Copia la forma de D-M13:

1. **Elegir.** `RunProcessor.FindInactiveAsync(now)` abre la transacción de Automations y pide hasta 100 candidatas (`AbandonBatchSize`) a un
   puerto nuevo, `IInactiveRunCandidates.ClaimAsync(now, limit)`. Las candidatas: `conversation_runs` `Waiting`, de un chatbot con el cierre
   prendido, que cumplen D-6, **y cuya conversación no está `Paused` por `Manual`**. Ese último filtro va en la elección (orquestador) para que
   las pausas manuales, que pueden esperar hasta 24 h, no ocupen los lotes en cada pasada.
   - La conversación es de Mensajería, así que el adaptador va en **Bootstrapper** (`Bootstrapper/InactiveRunCandidates.cs`): SQL crudo sobre
     `automations.conversation_runs`, `automations.chatbots` y `messaging.conversations` (por `conversation_id` y `tenant_id`), con
     `ORDER BY r.updated_at LIMIT {limit} FOR UPDATE OF r SKIP LOCKED`, sobre la conexión y la transacción de `AutomationsDbContext`, como
     `CatalogVariantUsageChecker` corre en la del `PUT` de producto. Automations no referencia Messaging y su Infrastructure no lee el esquema de
     otro módulo.
   - Sólo elige y suelta, como `FindAbandonedAsync` (el deadlock de P2-R20).
2. **Cerrar.** `RunProcessor.CloseInactiveAsync(candidate, now)`, cada una en su scope y su unidad:
   - `TryLockConversationAsync`; ocupado → nada (el siguiente barrido la vuelve a ver).
   - `FindLiveForUpdateAsync`; si ya no es la misma, no está `Waiting`, o con los ajustes **releídos** ya no califica (se apagó, cambió la hora,
     llegó un entrante que movió la corrida) → nada.
   - Lee la conversación y aplica D-7.
   - Si `conversation.LastInboundAt` es posterior al cutoff → nada: el cliente escribió algo que no movió la corrida (un sticker, una reacción, un
     entrante fuera de orden; `InboundIngestion.cs:228-235`). Se vuelve a elegir en cada barrido hasta que su último entrante también venza; con
     lotes de 100 se acepta.
   - Si el módulo `automations` está apagado → nada; el barrido de 24 h la cierra como hoy.
   - Si no: D-8.
3. El barrido de abandonadas sigue igual. Con horas de 1 a 23 el cierre por inactividad llega antes; si una corrida falla en él cada vez, a las
   24 h la toma el abandono, como hoy.

**D-8 (del redactor). El cierre, en una unidad.**

1. **Aviso:** `messenger.SendTextAsync(tenant, conversación, run.ChatbotId, clientId, mensaje)`, con
   `clientId = DeterministicClientId.ForInactivityNotice(run.Id)`, un hash de `"inactivity:{runId}"`. **No** `For(run.Id, run.StepNumber, 0)`: si
   la unidad se deshiciera después del envío y el cliente contestara, su respuesta correría con esa misma base y el primer mensaje del bot se
   tomaría por ya enviado (`BotOutbound.cs:65-68`) y nunca saldría.
2. **Resolución:** `IAutomationConversations.ResolveForInactivityAsync(tenant, conversación, run.ChatbotId)`, con la guarda de D-9.
3. **Corrida:** `run.Finish(RunStatus.Resolved, failureCode, now)`, `WriteClosed(run, null, RunCloseReasons.Inactivity, now)` y auditoría de
   sistema `automations.run.inactivity_closed` sobre `conversation_run`, en un solo `SaveChanges`.

| Aviso (`BotSendOutcome`) | Resolución (D-9) | Corrida | `failure_code` | Fila `Closed` |
|---|---|---|---|---|
| `Sent` | Resuelta | `Resolved` | `null` | `inactivity` |
| `WindowClosed` | Resuelta (D-4) | `Resolved` | `automations.run.inactivity_window_closed` | `inactivity` |
| `Blocked` | Resuelta (D-4) | `Resolved` | `automations.run.inactivity_contact_blocked` | `inactivity` |
| `Rejected(code)` | Resuelta (D-4); el código de Mensajería va al log | `Resolved` | `automations.run.inactivity_notice_rejected` | `inactivity` |
| cualquiera | `false`: ya no es del bot (D-9) | `Interrupted` | `null` | `human_took_over` |
| `NotAutomated` | No se intenta: una persona la tomó antes del envío | `Interrupted` | `null` | `human_took_over` |
| Excepción, o `concurrency.conflict` al resolver | La unidad se deshace; el siguiente barrido reevalúa y el `clientId` evita el doble envío | — | — | — |

**D-8a (decisión del orquestador, 2026-11-03). Cómo se distingue en la vista de corridas.** Todo cierre por inactividad lleva la fila `Closed`
con `closed_reason = inactivity`, haya salido el aviso o no. `failure_code` sólo cuando el aviso falló de verdad (las tres filas de D-4). El
estado es `Resolved` y no uno nuevo: para las métricas es una resolución del bot (`TodayAsync` la cuenta en `Handled`, no en `HandedOff`,
`ConversationRunQueries.cs:10-36`). El `status` no cambia de largo (`varchar(16)`) y los códigos caben en `failure_code` (`varchar(64)`,
`AutomationsDbContext.cs:226`).

Límite que hay que saber: la fila de `run_units` dura 3 días (`RunDetailRetentionDays`, spec 2026-10-25 D-E7). Pasado eso, la corrida sigue
diciendo `Resolved` y la marca queda en la auditoría (`automations.run.inactivity_closed`) y en el evento del hilo (D-10), que no se purgan.

**D-9 (decisión del orquestador, 2026-11-03). Guarda contra el asesor que toma en el último momento.** La resolución por inactividad sólo
ocurre si la conversación **sigue** abierta, sin asesor y con el asistente `Active`, y la comprobación es atómica del lado de Mensajería:

- **Dominio de Messaging:** `Conversation.ResolveForInactivity(now)`. Comprueba, en este orden: abierta (`AlreadyResolved`, como
  `EnsureCanResolve`), sin asesor y asistente `Active` (`MessagingDomainException` con `AutomationNotAvailable`, el código que ya usa
  `StartAutomation` para «con asesor», `Conversation.cs:373-376`). Después hace lo mismo que `Resolve(now, reasonId: null)`.
- **Adaptador (Bootstrapper):** `AutomationConversations.ResolveForInactivityAsync` carga el agregado, llama ese método y guarda con el
  `version` de la conversación como token de concurrencia (`MessagingDbContext.cs:244`). Tomar (`Assign`, que sube la versión, `Conversation.cs:598`),
  la autoasignación (`version + 1` por SQL, `ConversationRepository.cs:170`) y un entrante (la ingesta también la sube) entre la carga y el
  guardado chocan con `concurrency.conflict`: nunca se resuelve encima de un cambio que no se vio. Devuelve `false` si la regla no se cumple
  (el `MessagingDomainException`), y deja subir el `concurrency.conflict`.
- **Puerto:** `IAutomationConversations.ResolveForInactivityAsync(tenantId, conversationId, chatbotId, cancellationToken) → Task<bool>`.
  `ResolveAsync` no cambia.

Si el asesor tomó la conversación justo después del aviso, el cliente queda con un aviso de cierre y la conversación abierta con su asesor.
Se acepta: es la carrera de un instante, y lo contrario (cerrarle la conversación al asesor) es lo que la guarda evita.

**D-10 (del owner; la forma, del redactor). El hilo dice «Resuelta por inactividad».** Campo aditivo en el evento, sin migración:

- `ConversationEvent` gana `ConversationResolutionCause? ResolutionCause = null`, con el enum nuevo `ConversationResolutionCause { Inactivity }` en el
  dominio de Messaging. Sólo lo usa un `Resolved` hecho por `ResolveForInactivity`, que además lleva `ChatbotId`, como el del bloque Resolver.
- `ConversationEventJson` lo escribe como `"resolutionCause": "Inactivity"` y lo lee tolerante, como `pauseReason`: un valor desconocido se lee
  `null` (`ConversationEventJson.cs:63-66`).
- `MessageEventDto` gana `string? ResolutionCause = null`, llenado en `MessagingSupport.cs:234-246`.
- **Contrato del frontend:** en un evento `type = "Resolved"` con `resolutionCause = "Inactivity"`, el hilo dice **«Resuelta por inactividad»**.
  Sin el campo (eventos viejos, resoluciones de personas o del bloque Resolver), el texto de hoy. Un frontend viejo ignora el campo y pinta la
  resolución del asistente como hoy.

**D-11 (del redactor). Contrato del editor, al estilo BFF.**

- `ChatbotEditorDto` (`AutomationsDtos.cs:42-55`) gana `InactivityClose: ChatbotInactivityCloseDto(bool Enabled, int Hours, string Message)`,
  **siempre presente**. Sin texto guardado, `Message` trae el de D-5a y `Hours` el 8: el formulario lo dibuja sin conocer los defaults. El motivo va
  en el `<summary>` del DTO.
- `UpdateChatbotRequest` (`ChatbotEndpoints.cs:294-297`) y `UpdateChatbotCommand` (`Chatbots.cs:34-38`) ganan `InactivityClose` opcional,
  `{ enabled, hours, message }`. **Ausente = no cambia.** Distinto de `ExhaustedAction` (ausente = `Restart`) a propósito: un frontend viejo que
  guarde el chatbot no puede apagar un cierre que configuró uno nuevo.
- `ChatbotExportDto` (`AutomationsDtos.cs:62-78`) y la importación lo llevan opcional; duplicar lo copia (`Chatbots.cs:763-765`). Importado o
  duplicado prendido, `enabled_at` es el `now` de esa operación.

**D-12 (del redactor). Validación.**

- `UpdateChatbotValidator` (`Chatbots.cs:198-215`) y el de importar: con `inactivityClose` presente, `hours` entre 1 y 23
  (`inactivityClose.hours`); con `enabled = true`, `message` no vacío tras `Trim`, hasta 1000 y sin los caracteres de control que rechaza
  `SendMessageValidator.HasForbiddenControl` (copia de la regla: Automations no referencia Messaging) (`inactivityClose.message`). 422
  `validation.failed` con el mapa `errors` (`ApiExceptionHandler.cs:51`). Mensajes al usuario en tuteo.
- `Chatbot.SetInactivityClose(enabled, hours, message, now)`: guarda de programación (`ArgumentOutOfRangeException`), como `UpdateSettings` con
  `maxAttempts` (`Chatbot.cs:69-86`); los `CHECK` de D-5 son la última red. Sin código de dominio nuevo en Automations.

**D-13 (del owner).** Sin motivo de resolución del tenant: `resolution_reason_id` queda `null`. La marca de inactividad vive en el evento (D-10).

**D-14 (del redactor). Canal.** El owner pidió WhatsApp e Instagram. El cierre no mira el canal y hoy sólo puede ocurrir en WhatsApp, porque el
bot no atiende Instagram (§1). Si el bot llega a atender Instagram, aplica sin cambios; el tope de 1000 del aviso ya es el de Instagram.

**D-15 (del redactor). Estado del chatbot.** Un chatbot en pausa conserva sus corridas vivas (D-C5) y el cierre las sigue cerrando. Un chatbot
borrado no aparece en el `JOIN`: sus corridas las cierra el abandono, como hoy.

## 4. Decisiones pendientes

Ninguna. Las cuatro del borrador las cerró el owner o el orquestador el 2026-11-03: texto (D-5a), rastro en la vista (D-8a), motivo (D-13) y
texto del hilo (D-10).

## 5. Qué cambia en el código

Rutas relativas a `qep-backend/src/`.

**Automations**

- `Domain/Chatbot.cs`: tres propiedades, `SetInactivityClose`, constantes (`InactivityCloseDefaultHours = 8`, `…MinHours = 1`, `…MaxHours = 23`,
  `InactivityCloseMessageMaxLength = 1000`, `DefaultInactivityCloseMessage`).
- `Infrastructure/Persistence/AutomationsDbContext.cs`: columnas y los dos `CHECK`. Migración `AddChatbotInactivityClose` (con el factory de
  diseño, `--context AutomationsDbContext`). Columnas nulas o con default: ningún chatbot cambia.
- `Application/AutomationsPorts.cs`: `IInactiveRunCandidates`; `ResolveForInactivityAsync` en `IAutomationConversations`; la lectura de los
  ajustes vigentes para la relectura bajo el candado; `RunCloseReasons.Inactivity = "inactivity"`;
  `AutomationsAuditActions.RunInactivityClosed = "automations.run.inactivity_closed"`.
- `Application/Engine/AutomationsRunFailures.cs`: los tres códigos de D-8.
- `Application/Engine/DeterministicClientId.cs`: `ForInactivityNotice(runId)`.
- `Application/Engine/RunProcessor.cs`: `FindInactiveAsync`, `CloseInactiveAsync` y el record del candidato (como `AbandonedRun`). Necesita
  `IAutomationMessenger`, que hoy recibe `RunEffects`: el builder decide si se inyecta al procesador o si el aviso pasa por un método de
  `RunEffects`.
- `Infrastructure/Engine/AutomationInboxWorker.cs`: el paso `inactivity` y su conteo en `LogSwept`.
- `Application/Chatbots.cs`, `AutomationsDtos.cs`, `ChatbotEditorBuilder.cs`, `Api/ChatbotEndpoints.cs`: D-11 y D-12.

**Messaging**

- `Domain/Conversation.cs`: `ResolveForInactivity(now)` (D-9).
- `Domain/ConversationEvent.cs`: `ResolutionCause` y el enum `ConversationResolutionCause` (D-10). El `<summary>` dice quién lo usa.
- `Application/ConversationEventJson.cs`: escribir y leer `resolutionCause`.
- `Application/MessagingDtos.cs` y `MessagingSupport.cs`: `MessageEventDto.ResolutionCause`; el `<summary>` del DTO dice qué texto pinta el hilo.
- Sin migración: `details` ya es JSON.

**Bootstrapper**

- `AutomationConversations.cs`: `ResolveForInactivityAsync` (D-9), con el evento `Resolved` con `ChatbotId` y `ResolutionCause.Inactivity` y la
  auditoría de sistema `MessagingAuditActions.Resolved`, como `ResolveAsync`.
- `InactiveRunCandidates.cs` (nuevo): el SQL de D-7a.

**`CLAUDE.md`**, gotcha nuevo:

> **`InactiveRunCandidates` lee `automations.conversation_runs`, `automations.chatbots` y `messaging.conversations` por nombre** (spec 2026-11-03,
> cierre por inactividad). Es SQL crudo en Bootstrapper sobre la conexión de `AutomationsDbContext`, y excluye las conversaciones `Paused` por
> `Manual` para no romper «Continuar». Renombrar `automation_status`, `automation_pause_reason` o una columna de esas tablas compila y deja el
> paso `inactivity` del barrido fallando en cada pasada (se loguea, los demás pasos siguen); lo amarra `InactiveRunCandidatesTests`.

**Frontend** (spec aparte o tarea del builder): en el editor, junto a la regla de agotamiento, un interruptor «Cerrar por inactividad», las horas
(1 a 23) y el texto, con los valores que trae `inactivityClose`. En el hilo, el texto de D-10.

## 6. Pruebas (TDD estricto: RED antes que GREEN, con evidencia literal de los dos)

**Unitarias, `tests/Modules/Automations/Modules.Automations.UnitTests/`**

- `ChatbotTests`: `SetInactivityClose` prende con `enabled_at = now`; guardar prendido otra vez no lo mueve; apagar conserva horas y texto;
  horas fuera de 1..23 lanzan.
- `RunProcessorTests` (parcial nuevo, `RunProcessorTests.Inactivity.cs`):
  - `AWaitingRunPastTheHoursSendsTheNoticeAndResolves`: un envío con `ForInactivityNotice`, `ResolveForInactivityAsync`, corrida `Resolved` sin
    `failure_code`, fila `Closed` con `inactivity`, auditoría `automations.run.inactivity_closed`.
  - Una prueba por cada fila de la tabla de D-8: `WindowClosed`, `Blocked` y `Rejected` resuelven con su código y fila `inactivity`;
    `NotAutomated` → `Interrupted` sin resolver; `ResolveForInactivityAsync` devuelve `false` → `Interrupted` con `human_took_over`, sin
    auditoría de inactividad; excepción y `concurrency.conflict` → nada guardado.
  - D-7: `AManualPauseIsLeftWaiting` (sin envío, sin cambio, la corrida sigue `Waiting`); `ATakenConversationIsInterruptedLikeTheAbandonSweep`,
    y lo mismo con `Resolved`, `None`, pausa `AdvisorTook` y `AutomationResumedAt` posterior al arranque.
  - `ACustomerInboundAfterTheCutoffSkips`; `AChangedRunUnderTheLockSkips`; `TheSettingsAreRereadUnderTheLock` (apagado entre la elección y la
    unidad); `ADisabledModuleSkips`; `ALockedConversationSkips`.
  - `TheNoticeClientIdNeverMatchesAFlowSend`: `ForInactivityNotice(run)` distinto de `For(run, step, i)` para los primeros pasos e índices.
- Worker: el paso `inactivity` corre antes que `abandon` y una falla suya no frena a los demás.
- Validador: `inactivityClose.hours` 0 y 24 → 422 con esa clave; prendido sin texto, con más de 1000 o con un `\0` → 422 en
  `inactivityClose.message`; ausente → nada cambia.

**Unitarias, `tests/Modules/Messaging/Modules.Messaging.UnitTests/`**

- `ConversationTests`: `ResolveForInactivity` resuelve una abierta, sin asesor y `Active`; rechaza con asesor, con el asistente `Paused` o
  `None` (`AutomationNotAvailable`) y ya resuelta (`AlreadyResolved`), sin tocar estado ni versión.
- Pruebas de `ConversationEventJson`: `resolutionCause` va y vuelve; un valor desconocido se lee `null`; un `Resolved` sin el campo sigue igual.
- `ConversationAutomationMappingTests` (o el de `MessagingSupport`): `MessageEventDto.ResolutionCause = "Inactivity"` sólo en ese evento.

**Bootstrapper, `tests/Bootstrapper/Bootstrapper.UnitTests/AutomationAdaptersTests.cs`:** `ResolveForInactivityAsync` devuelve `false` con
asesor y no escribe evento; con una toma concurrente entre la carga y el guardado sube `concurrency.conflict` y la conversación queda con su
asesor, sin resolver.

**Integración (una vez al final, con `$env:Seed__Enabled = "false"`)**

- `InactiveRunCandidatesTests` (Bootstrapper o `Modules.Automations.IntegrationTests`, donde haya esquema migrado de los dos módulos): elige sólo
  `Waiting` de chatbots prendidos, respeta `GREATEST(updated_at, enabled_at)`, excluye `Paused` por `Manual`, salta las tomadas por otra
  transacción (`SKIP LOCKED`) y no elige chatbots borrados.
- Migración: los `CHECK` rechazan 0, 24 y prendido sin texto; un chatbot existente queda apagado con 8 h.
- `Modules.Automations.IntegrationTests/EngineEndToEndTests.Lifecycle.cs`: el bot pregunta, el reloj pasa las horas, corre el barrido → mensaje del
  bot con el aviso, conversación `Resolved` con el evento firmado por el chatbot, `resolutionCause = "Inactivity"` en el hilo y sin motivo, corrida
  `Resolved`. El cliente escribe → la conversación se reabre y arranca una corrida nueva desde el primer bloque. Con la ventana vencida → sin
  mensaje, resuelta, con `automations.run.inactivity_window_closed`. Con el asistente pausado por `Manual` → nada cambia y «Continuar» sigue
  ofrecido.
- `Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`: el `GET` trae `inactivityClose` con los defaults de D-5a; el `PUT` lo guarda; un `PUT`
  sin el campo no lo apaga; exportar, importar y duplicar lo llevan.

## 7. Despliegue, rollback y seguimiento

- **Migración de Automations** aditiva: todo chatbot queda apagado. **El backend va primero**; el frontend viejo no ve el campo y, por D-11, no lo
  apaga al guardar. Mientras sólo esté el backend nadie puede prender el cierre (el campo sólo lo manda el editor nuevo), así que el texto del
  hilo y el editor pueden salir juntos en el frontend.
- **Verificación:** prender el cierre en un chatbot de prueba con 1 h; a la hora y hasta 10 min después, la corrida `Resolved` con su fila
  `inactivity`, el aviso en el hilo y el evento con `resolutionCause`.
- **Rollback:** revertir el código. Las columnas quedan y el código viejo no las lee: ninguna conversación se vuelve a cerrar por inactividad. Un
  `resolutionCause` guardado lo ignora el `Parse` viejo, que lee sólo lo que conoce (`ConversationEventJson.cs:68-71`). Un `failure_code` que el
  código viejo no conoce se lee como texto.
- **Seguimiento (fuera de alcance):** el bloque «Resolver» del flujo tiene la misma carrera. `RunEffects` (`RunEffects.cs:156-163`) llama
  `ResolveAsync`, que no mira asesor (`Conversation.cs:267-287`); el `HandsOverAsync` que lo envuelve sólo atrapa el `DomainException` de una ya
  resuelta. La ventana es más chica (la unidad corre segundos después del entrante), pero existe. Se cierra con la misma guarda de D-9 en otro
  cambio.

## 8. Para el builder

- Cómo llega `IAutomationMessenger` al cierre (D-8: procesador o `RunEffects`).
- El nombre del record candidato y de la lectura de ajustes.
- Dónde viven las pruebas de `InactiveRunCandidates` (el proyecto que ya tenga el esquema migrado de Automations y Messaging).
- El texto de los mensajes de validación, en tuteo.
