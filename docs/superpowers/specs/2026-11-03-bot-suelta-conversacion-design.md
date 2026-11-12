# Automatizaciones: el bot suelta la conversación cuando una persona la toma

**Fecha:** 2026-11-03
**Módulos:** Messaging (handlers de asignación, resolución y automatización; la cola `automation_inbox`), Automations (`RunProcessor`,
borrado de chatbot), Bootstrapper (adaptador de la cola). **Frontend:** sin cambios.
**Estado:** listo para construir. Las decisiones «del owner» las tomó el owner el 2026-11-03 (incluida la de pausar, D-6a, que cerró la
antigua DP-1); las «del orquestador» las tomó el orquestador ese mismo día; las «del redactor» son criterio del autor. No queda ninguna
`DECISIÓN-PENDIENTE`.
**Acota** D-M15 del spec `2026-10-21-constructor-de-chatbots-design.md`: la corrida ya no se interrumpe sólo de forma perezosa.

## 1. Contexto

**Lo que pasó (producción).** El bot mandó un mensaje y quedó esperando. Un asesor resolvió la conversación. La corrida siguió `Waiting`.
Después el usuario pausó y desconectó el chatbot y quiso borrarlo: 409 `automations.chatbot.has_live_runs`, por cuatro corridas `Waiting` que
nadie atendía.

**Lo que dice el código hoy:**

- **Borrar bloquea con cualquier corrida viva.** `DeleteChatbotHandler` (`Chatbots.cs:814-818`) llama `IConversationRunQueries.HasLiveRunsAsync`,
  que cuenta `Running` o `Waiting` del chatbot sin mirar la conversación (`ConversationRunQueries.cs:38-41`).
- **Ninguna acción de una persona cierra la corrida.** Resolver (`ConversationLifecycle.cs:150-191`), tomar y transferir
  (`ConversationAssignment.cs:121`, `:142`), pausar y devolver (`ConversationAutomation.cs:70-94`) y la autoasignación al responder
  (`ConversationRepository.cs:155-198`) sólo tocan Mensajería. Mensajería no ve Automations.
- **La corrida se interrumpe de forma perezosa**, en dos lugares:
  - Con el siguiente entrante: `RunProcessor.ProcessUnitAsync` (`RunProcessor.cs:178-187`) ve `BotAttendance.HumanTookOver` y hace
    `run.Finish(RunStatus.Interrupted, null, now)`, `run.MarkInbound(item.MessageId)` y
    `recorder.WriteClosed(run, item.MessageId, RunCloseReasons.HumanTookOver, now)`. Sin auditoría. La fila del inbox cierra `interrupted`.
  - Con el barrido de abandonadas, a las 24 h (`RunProcessor.AbandonAfter`, `:43`): `AbandonAsync` (`:257-267`) hace lo mismo con
    `inboundMessageId = null`.
  - Una conversación resuelta no encola entrantes hasta que la ingesta la reabre, y una pausada por `Manual` o `Handoff` no encola nunca
    (`InboundIngestion.cs:228-235`). En el incidente el único camino era el barrido.
- **`HumanTookOver` ya es la regla** (`BotAttendance.cs:17-25`): asesor asignado, conversación `Resolved`, asistente `Paused` o `None`, o
  `AutomationResumedAt > run.StartedAt`. `IsAttending` es «corrida viva y no `HumanTookOver`» (`:28-29`).
- **El canal Mensajería → Automations que existe es `messaging.automation_inbox`**: una tabla de Mensajería que la ingesta llena, que el
  worker de Automations reclama por el puerto `IAutomationInboundQueue` (adaptador `Bootstrapper/AutomationInboundQueue.cs`) y que procesa
  `RunProcessor` con el candado de la conversación (`RunProcessor.cs:146-193`). El arranque manual ya escribe ahí desde un handler de Mensajería,
  en el mismo `SaveChanges` del cambio (`IAutomationInbox.EnqueueManualStart`, `AutomationInbox.cs:120-131`; `ConversationAutomation.cs:34`).
- **Mensajería no escribe en `platform.outbox_messages`.** `MessagingOutboxRecord` es una proyección de sólo lectura, excluida de las
  migraciones (`MessagingDbContext.cs:695-704`), que usa el consumidor del historial de coexistencia. Automations no tiene inbox de eventos de
  plataforma.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Cuando una persona resuelve, toma, transfiere o devuelve la conversación al asistente, la corrida viva del bot termina en ese momento (con
  un retraso de segundos), no cuando el cliente vuelve a escribir ni a las 24 h.
- Borrar un chatbot sólo se bloquea si el bot de verdad está atendiendo alguna conversación.

**No objetivos**

- Migrar o cerrar a mano las corridas ya trabadas: las cierra el barrido de 24 h y el borrado las ignora (owner).
- Mandarle algo al cliente cuando el asesor resuelve (owner).
- Cambiar qué hace el bot cuando el cliente vuelve a escribir: sigue empezando desde cero, como hoy.
- Cerrar corridas al bloquear el contacto: el envío del bot ya responde `Blocked` y la corrida termina `Interrupted` (`RunEffects.cs:272-277`).
- Cerrar la corrida al pausar el asistente (`Manual`): D-6a.

**Anti-objetivos**

- Que la señal arranque o haga avanzar una corrida, o mande un mensaje.
- Que la señal cierre una corrida que arrancó después de la acción humana.
- Romper «Continuar donde iba» (spec 2026-11-01): D-6a.
- Que el resolver, traspasar o terminar **del propio bot** encole una señal.

## 3. Decisiones

**D-1 (del owner).** La corrida termina `Interrupted`, el mismo desenlace del camino perezoso, con `RunCloseReasons.HumanTookOver`
(`human_took_over`) en su fila `Closed` de `run_units` (`RunUnitWriter.WriteClosed`, `RunUnitWriter.cs:41-55`; el `outcome` sale
`interrupted`). Sin auditoría, como hoy (`RunProcessor.cs:178-187`, `:257-267`). Las métricas (`TodayAsync`, `ConversationRunQueries.cs:10-36`)
y la vista de corridas no cambian de sentido.

**D-2 (del owner).** Si el cliente vuelve a escribir, el bot empieza desde cero: es lo que ya pasa con una corrida cerrada.

**D-3 (decisión del orquestador, 2026-11-03). El aviso viaja por `messaging.automation_inbox`, como una fila `ReleaseRun`, no por el outbox
de plataforma.** «Outbox» fue la palabra del asistente en el planteo, no un requisito del owner. Lo que el owner pidió es que Mensajería avise
sin referenciar Automations, con retraso aceptable, y que se use el patrón que ya existe. El que existe entre estos dos módulos es la cola del
motor: se escribe en la misma transacción que el cambio de Mensajería, es asíncrona y Mensajería sigue sin referenciar Automations.

| | A: fila en `automation_inbox` (elegida) | B: evento en `platform.outbox_messages` (descartada) |
|---|---|---|
| Escritura | `IAutomationInbox`, como `EnqueueManualStart` | Mensajería no escribe outbox hoy: proyección nueva con todas las columnas |
| Consumo | El worker y `RunProcessor` que ya existen | Consumidor nuevo en Automations, copia de `ClaimedOutboxConsumer`, y tabla de inbox nueva |
| Orden con los entrantes | FIFO por conversación (`AutomationInbox.cs:36-41`): la señal va antes que el entrante que la sigue | Ninguno: compite con la cola del motor |
| Candado | El de la conversación, mismo orden que una unidad (P2-R20) | Hay que repetirlo |
| Migración | Una columna en `messaging.automation_inbox` | Tabla de inbox en `automations` |

**D-4 (del redactor). Tipo de fila explícito.** Hoy «sin mensaje en `messages`» significa «arranque manual» en tres lugares:
`HasPendingManualStartAsync` (`AutomationInbox.cs:134-143`), `ContinueAsync` (`RunProcessor.cs:415-421`) y el traspaso tras una excepción
(`RunProcessor.cs:77-88`). Una señal también es una fila sin mensaje, y por esas tres reglas mostraría «arrancando…», o traspasaría la
conversación si fallara. Por eso la fila lleva `kind`: `Engine` (default, todo lo de hoy: entrantes y arranques manuales) o `ReleaseRun`
(la señal). La regla «sin mensaje es manual» sigue igual dentro de `Engine`.

**D-5 (del redactor). Qué hace `RunProcessor` con una `ReleaseRun`.** Una rama al principio de `ProcessUnitAsync`, después del candado
(P2-R20) y antes de mirar el módulo, que nunca llega a `DecideAsync`:

1. `TryLockConversationAsync`; ocupado → `Locked`, como hoy.
2. `FindLiveForUpdateAsync`. Sin corrida → `skipped`. P2-R24 igual que hoy: si `FindLatestAsync` ya tiene `LastInboundMessageId` igual a la
   fila → `duplicate`.
3. Cierra **sólo si** se cumplen las dos:
   - `run.StartedAt < item.CreatedAt` (la corrida es anterior a la acción; anti-objetivo 2);
   - `BotAttendance.HumanTookOver(conversation, run)` con la conversación leída bajo el candado (o la conversación ya no existe).
4. Si cierra: `Finish(Interrupted)`, `MarkInbound(item.MessageId)`, `WriteClosed(…, HumanTookOver, now)`, un solo `SaveChanges`, outcome
   `interrupted`. Si no: `skipped`, sin tocar nada.

Releer `HumanTookOver` en vez de confiar en la señal protege el caso en que el estado cambió otra vez antes de que el worker llegue: la
señal sólo cierra lo que, bajo el candado, ya no es del bot. **El módulo apagado no frena la señal:** cerrar una corrida no es que el bot conteste,
y `StandDownAsync` (`RunProcessor.cs:201-218`) la cerraría igual con otro motivo. Una excepción en esta rama sube y la fila se reintenta por lease,
**nunca** pasa por `FailManualStartAsync`.

**D-6 (del owner: resolver, tomar y transferir; el resto de la lista, del orquestador y del redactor según la fila). Qué acciones encolan la
señal.** Se encola en el **handler** de Mensajería, no en el agregado:
`Conversation.Resolve` también lo llama el bot (`Bootstrapper/AutomationConversations.cs:153`), y esa resolución no debe avisar. Condición
común: el asistente **no** estaba en `None` antes del cambio. Encolar de más no hace daño (D-5 responde `skipped`).

| Acción | Dónde | ¿Encola? | Por qué |
|---|---|---|---|
| Resolver | `ResolveConversationHandler` (`ConversationLifecycle.cs:162`) | Sí | Owner. `Resolve` deja `None` (`Conversation.cs:285`): leer el estado **antes** |
| Tomar | `TakeConversationHandler` (`ConversationAssignment.cs:121`) | Sí, si hubo cambio | Owner. Pausa `AdvisorTook` si estaba `Active` (`Conversation.cs:313`) |
| Transferir | `TransferConversationHandler` (`ConversationAssignment.cs:142`) | Sí, si hubo cambio | Owner. `AdvisorTransferred` (`:331-333`) |
| Responder sin asignar (autoasignación) | `ConversationRepository.TryAutoAssignAsync` (`:155-198`) | Sí (del redactor) | Toma igual que «Tomar» (pausa `AdvisorReplied`). Es SQL: el `INSERT` va en esa transacción, cuando `automation_status <> 'None'` en el `RETURNING` (el `UPDATE` sólo cambia `Active` por `Paused`) |
| Pausar (`Manual`) | `PauseAutomationHandler` (`ConversationAutomation.cs:78`) | **No** (owner, D-6a) | La corrida sigue `Waiting` para «Continuar» |
| Devolver al asistente | `ResumeAutomationHandler` (`:92`) | Sí (orquestador) | Sella `AutomationResumedAt`: por P2-R25 el camino perezoso ya la interrumpe hoy con el siguiente entrante; cerrarla ya deja la vista al día. D-5 nunca arranca la corrida nueva: la arranca el próximo entrante, como hoy |
| Liberar | `ReleaseConversationHandler` (`:155`) | No | No cambia la automatización; la toma ya avisó |
| Iniciar asistente | `StartAutomationHandler` | No | `ManualRunStarter.StartAsync` ya cierra la corrida viva bajo el candado (`ManualRunStarter.cs:164-169`) |
| Continuar | `ContinueAutomationHandler` | **No** | Owner. Su razón de ser es que la corrida siga `Waiting` (spec 2026-11-01 D-8) |
| Reabrir, marcar leída/no leída, cambiar cliente, bloquear | varios | No | No tocan la automatización |
| Traspaso, resolución, fin y arranque del bot | `Bootstrapper/AutomationConversations.cs` | No | Los hace el motor dentro de su unidad, que ya cierra la corrida |
| Eco de coexistencia | `CoexistenceIngestion` | No | No asigna ni pausa (spec 2026-11-01 OD-5) |

**D-6a (del owner, 2026-11-03). Pausar el asistente no encola la señal ni cierra la corrida.** «Continuar donde iba» (spec 2026-11-01, ya en
`develop`) exige la corrida `Waiting` durante una pausa `Manual` (`BotAttendance.CanContinue`, `BotAttendance.cs:36-42`; spec 2026-11-01 D-4.3).
El borrado del incidente igual se destraba: con el asistente `Paused`, `HumanTookOver` es verdadero (`BotAttendance.cs:23`), así que
`IsAttending` es falso y D-7 no bloquea. Si después de pausar alguien toma, transfiere, resuelve o devuelve, esa acción sí encola (la
conversación no está en `None`), y coincide con la D-5 del spec 2026-11-01: si un asesor la tuvo, no se continúa. La corrida pausada que nadie
toca la cierra el barrido de 24 h, como hoy.

**D-7 (del owner). El borrado bloquea sólo si el bot de verdad atiende**, con la misma regla: `BotAttendance.IsAttending`. El handler lista las
corridas vivas del chatbot y, por cada una, lee la conversación con `IAutomationConversations.GetAsync` (ya inyectable en Application; adaptador
en `Bootstrapper/AutomationConversations.cs:42-49`). Basta una que atienda para el 409 `has_live_runs`; se corta en la primera. Una conversación
que ya no existe no atiende (`HumanTookOver` con `conversation is null`, como `RunProcessor.cs:180`). Una `Running` sin persona atiende: es una
unidad en curso.

- `IConversationRunQueries.HasLiveRunsAsync` se reemplaza por `ListLiveAsync(tenantId, chatbotId)` (sin seguimiento), que devuelve las corridas
  vivas. Del redactor: lecturas una por una, con corte temprano. Si un tenant junta cientos de corridas vivas en un chatbot, el builder puede
  agregar una lectura en lote al puerto; no se hace antes de medirlo.
- La carrera entre el chequeo y el borrado (una corrida que arranca en medio) es la misma de hoy, que tampoco toma candado.
- Las corridas vivas que no atienden quedan como están: no hay FK de `conversation_runs` a `chatbots` (`AutomationsDbContext.cs:204-237`).
  El siguiente entrante o el barrido las cierra `Interrupted`, y si se continuaran, el chatbot ya no sirve la conexión y el resumen pasa a
  `FlowStart` (spec 2026-11-01 D-4.5).
- El mensaje del 409 sigue siendo «El chatbot tiene conversaciones en curso…» (`Chatbots.cs:817`): ahora es verdad.

**D-8 (del owner). Corridas ya trabadas:** sin migración. Las cierra el barrido de 24 h (`Interrupted` vía `HumanTookOver`,
`RunProcessor.cs:261-266`) y D-7 las ignora desde el despliegue.

**D-9 (del redactor). Sin cambio de contrato HTTP.** Ni rutas, ni DTO, ni códigos. El frontend no cambia.

## 4. Decisiones pendientes

Ninguna. La antigua DP-1 (pausar) la cerró el owner el 2026-11-03 con la opción A: D-6a.

## 5. Qué cambia en el código

Rutas relativas a `qep-backend/src/`.

**Messaging**

- `Modules.Messaging.Application/IAutomationInbox.cs`: `EnqueueReleaseRun(tenantId, connectionId, conversationId, now)`, hermano de
  `EnqueueManualStart` (sólo agrega al contexto); `AutomationInboxItem` gana el tipo.
- `Modules.Messaging.Infrastructure/Persistence/AutomationInbox.cs`: `kind` en el `RETURNING` de `ClaimAsync`; `HasPendingManualStartAsync`
  filtra `kind = 'Engine'`; el `INSERT` de la señal.
- `Persistence/MessagingDbContext.cs` (`:537-551`) y migración `AddAutomationInboxKind`: `kind varchar(16) NOT NULL DEFAULT 'Engine'`, con
  `CHECK (kind IN ('Engine','ReleaseRun'))`. Las filas existentes quedan `Engine`.
- `Persistence/ConversationRepository.cs`: `TryAutoAssignAsync` inserta la señal en su transacción (D-6). `IConversationRepository` no cambia.
- Handlers de D-6: `ConversationLifecycle.cs` (resolver), `ConversationAssignment.cs` (tomar, transferir), `ConversationAutomation.cs`
  (devolver). `PauseAutomationHandler` **no cambia** (D-6a). Encolan antes de `steps.CommitAsync` / `SaveChangesAsync`, y sólo si hubo cambio
  (`changes.Count > 0`).

**Automations**

- `AutomationsPorts.cs`: `InboundQueueItem` gana el tipo (p. ej. `bool ReleaseRun`); `IConversationRunQueries.ListLiveAsync` reemplaza a
  `HasLiveRunsAsync`.
- `Engine/RunProcessor.cs`: la rama de D-5, antes de `modules.IsEnabledAsync`; y el `catch` de `ProcessAsync` (`:75-88`) no trata una
  `ReleaseRun` como arranque manual.
- `Infrastructure/Persistence/ConversationRunQueries.cs`: `ListLiveAsync`.
- `Chatbots.cs`: `DeleteChatbotHandler` gana `IAutomationConversations` y aplica D-7. Su `<summary>` dice la regla nueva.

**Bootstrapper:** `AutomationInboundQueue.cs` mapea el tipo.

**`CLAUDE.md`:** reemplazar el gotcha «Una fila de `automation_inbox` sin mensaje es un arranque manual» por:

> **Una fila de `automation_inbox` tiene tipo** (spec 2026-11-03). `ReleaseRun` es la señal de que una persona resolvió, tomó, transfirió o
> devolvió la conversación: `RunProcessor` sólo cierra la corrida viva (`Interrupted`, `human_took_over`) si arrancó antes de la fila y
> `HumanTookOver` sigue siendo verdadero, y nunca arranca ni avanza nada. Dentro de `Engine`, una fila sin mensaje sigue siendo un arranque
> manual: todo lector nuevo usa `LEFT JOIN` y filtra por `kind`. Una acción humana nueva sobre una conversación con asistente decide si encola
> la señal, en el handler y no en el agregado (el bot también resuelve).

## 6. Pruebas (TDD estricto: RED antes que GREEN, con evidencia literal de los dos)

**Unitarias, `tests/Modules/Automations/Modules.Automations.UnitTests/`**

- `RunProcessorTests` (archivo parcial nuevo, `RunProcessorTests.ReleaseRun.cs`):
  - `AReleaseRowClosesTheLiveRunWhenAPersonTookOver`: corrida `Waiting`, conversación resuelta → `Interrupted`, fila `Closed` con
    `human_took_over`, outcome `interrupted`, ningún envío, sin auditoría.
  - Una fila por cada estado de D-6 (asesor asignado; `Paused` `AdvisorTook`/`AdvisorTransferred`/`AdvisorReplied`; `ResumedAt` posterior).
  - `AReleaseRowDoesNotCloseARunThatStartedAfterIt` (`run.StartedAt >= item.CreatedAt`) → `skipped`.
  - `AReleaseRowDoesNotCloseWhenThePersonUndidIt`: conversación `Active`, sin asesor y sin sello → `skipped`.
  - `AReleaseRowNeverStartsARun`: conversación `Active` sin corrida (tras «Devolver») → sin corrida nueva y sin envío.
  - `AReleaseRowIgnoresTheDisabledModule`; `AReleaseRowWithoutLiveRunIsSkipped`; `AReleaseRowAlreadyAppliedIsDuplicate` (P2-R24).
  - `AReleaseRowThatThrowsIsRetriedNotHandedOff`: la excepción sube; `HandoffAsync` no se llama.
  - `ALockedConversationReschedulesTheReleaseRow`.
- `RunProcessorTests.ManualStart.cs`: una fila manual sigue arrancando (regresión de D-4).
- `DeleteChatbotHandlerTests` (nuevo, o en el archivo de handlers de chatbot que ya exista): corrida `Waiting` con la conversación resuelta /
  tomada / pausada / inexistente → borra; una que atiende → 409 `automations.chatbot.has_live_runs`; `Running` sin persona → 409.

**Unitarias, `tests/Modules/Messaging/Modules.Messaging.UnitTests/`:** por cada handler de D-6, que encola en el mismo guardado sólo si el
asistente no estaba en `None` y hubo cambio; que tomar lo que ya era tuyo (D-A2) no encola; que «Pausar», «Continuar» e «Iniciar» no encolan
(`PausingDoesNotQueueTheReleaseSignal`, D-6a); que tomar una conversación ya pausada por `Manual` sí encola.

**Regresión de «Continuar» (D-6a), en `RunProcessorTests.Continue.cs` y `EngineEndToEndTests.Continue.cs`:** pausar por la API, correr el
worker → la corrida sigue `Waiting`; «Continuar» y el siguiente entrante avanzan desde el paso pendiente, como hoy.

**Integración (una vez al final, con `$env:Seed__Enabled = "false"`)**

- `Modules.Messaging.IntegrationTests/AutomationInboxTests.cs`: `ClaimAsync` devuelve el tipo; `HasPendingManualStartAsync` no cuenta una
  `ReleaseRun`; una `ReleaseRun` frena al entrante posterior de la misma conversación (FIFO).
- `Modules.Messaging.IntegrationTests/ConversationAutomationApiTests.cs` (o el archivo de asignación): `TryAutoAssignAsync` deja la fila en su
  transacción.
- `Modules.Automations.IntegrationTests/EngineEndToEndTests.Lifecycle.cs`: el incidente de punta a punta. El bot pregunta, el asesor resuelve
  por la API, el worker corre → corrida `Interrupted` sin que el cliente escriba; el chatbot se pausa y se borra → 204.
- `Modules.Automations.IntegrationTests/ChatbotsApiTests.cs`: borrar con una corrida `Waiting` cuya conversación se tomó → 204; con una
  pausada por `Manual` (corrida todavía `Waiting`, D-6a) → 204; con una que atiende → 409.
- `Bootstrapper.UnitTests/AutomationAdaptersTests.cs`: el mapeo del tipo.

## 7. Despliegue y rollback

- **Migración de Mensajería** (`AddAutomationInboxKind`), aditiva, con default: el código viejo sigue insertando y leyendo sin la columna.
- **El backend va solo.** Sin frontend.
- **Verificación:** en `messaging.automation_inbox`, filas `ReleaseRun` cerradas `interrupted` o `skipped`; en `automations.run_units`, filas
  `Closed` con `human_took_over` y `inbound_message_id` igual al id de la señal; el borrado del tenant del incidente responde 204.
- **Rollback con cuidado.** El código viejo lee una `ReleaseRun` pendiente como un arranque manual: con la conversación `Active` (tras
  «Devolver») arrancaría el flujo y mandaría el primer mensaje, y el resumen mostraría «arrancando…». Antes de revertir:

  ```sql
  UPDATE messaging.automation_inbox SET processed_at = now(), outcome = 'skipped'
   WHERE kind = 'ReleaseRun' AND processed_at IS NULL;
  ```

  La columna puede quedar: el código viejo no la lee.

## 8. Para el builder

- El nombre del tipo en el puerto (`bool` o string) y de los métodos nuevos.
- Si `ListLiveAsync` devuelve la entidad o una proyección.
- El orden exacto de la rama de D-5 respecto de P2-R24, siempre después del candado.
