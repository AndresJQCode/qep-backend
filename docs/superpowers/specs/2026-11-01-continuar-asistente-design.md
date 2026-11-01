# Automatizaciones: continuar el asistente donde iba

**Fecha:** 2026-11-01
**Módulos:** Automations (motor), Messaging (conversación, resumen del hilo, eventos), Bootstrapper (adaptador). **Frontend:**
`qep-frontend`, `features/messaging/` (encabezado del hilo).
**Estado:** aprobado por el owner el 2026-11-01, con todas las recomendaciones de este documento (D-4 a D-13 y OD-2 a OD-7).
**Depende de** `2026-10-31-iniciar-bot-desde-el-hilo-design.md` (ya en `develop`). **Acota** D-M15 y P2-R25 del spec
`2026-10-21-constructor-de-chatbots-design.md`: una pausa manual sin asesor deja de ser, por sí sola, el fin de la corrida.

## 1. Contexto

Con el asistente pausado, el asesor tiene dos salidas y las dos reinician el flujo. «Iniciar asistente» manda el primer mensaje.
«Devolver al asistente» no manda nada, pero sella `AutomationResumedAt` (`Conversation.cs:506`); con el siguiente entrante
`HumanTookOver` da verdadero (`BotAttendance.cs:20`), `RunProcessor` cierra la corrida `Interrupted` (`RunProcessor.cs:180-187`) y
`ResumeAsync` arranca una nueva desde el primer bloque (`RunProcessor.cs:304`, `:368-385`).

**Pedido del owner:** un modo que retome el paso pendiente, sin repetir el saludo.

Lo que dice el código hoy:

- **Una pausa `Manual` no cierra la corrida.** Pausar sólo toca Messaging (`ConversationAutomation.cs:50`). Si la corrida esperaba, sigue
  `Waiting`, con su bloque, variables e intentos. Si había una unidad en curso, puede cerrarse `Interrupted` al ir a enviar
  (`RunEffects.cs:267-270`).
- **Vive hasta 24 h desde su último paso, no desde la pausa.** El barrido corre cada 10 min (`AutomationsWorkerOptions.cs:16`) y elige
  `Waiting` con `updated_at` vencido (`AutomationsRepositories.cs:197-203`). Pausada, `HumanTookOver` es verdadero, así que la cierra
  `Interrupted` y la conversación sigue `Paused` (`RunProcessor.cs:261-266`). Una corrida que llevaba 23 h esperando deja 1 h para continuar.
- **Un entrante durante una pausa `Manual` o `Handoff` no se encola** (`InboundIngestion.cs:231-232`): queda en el hilo y nunca llega a la
  cola del motor. Tampoco mueve `run.UpdatedAt`.
- **Con `Handoff` no hay corrida viva:** la corrida termina `HandedOff` (`FlowEngine.cs:325`, `:485`).
- **No hay temporizadores ni recordatorios en un paso que espera.** Sólo esperan Pregunta abierta, Botones, Lista, Pedir adjunto y Política
  de datos (`FlowEngine.cs:113`), y la corrida sólo se mueve con un entrante (`RunProcessor.cs:417-421`). Lo único con reloj es el
  barrido. La pausa no tiene nada que congelar.
- **Continuar ya funciona solo si la conversación queda `Active` sin sello:** `HumanTookOver` da falso y el entrante entra por
  `ContinueAsync`, en la versión de la corrida (`RunProcessor.cs:428`) y con los intentos guardados.

**La trampa.** `AutomationPauseReason = Manual` **no** prueba que ningún asesor haya intervenido. Tomar, transferir o responder sobre una
pausa existente no cambia el motivo (`Conversation.cs:555-560`, `ConversationRepository.cs:171-174`), y responder sin asignar autoasigna
(`OutboundGuards.cs:112-116`). Después de liberar queda `Paused`, `Manual` y sin asesor, igual que si nadie hubiera hablado.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Un asesor devuelve una conversación en pausa manual y, cuando el cliente escribe, el bot retoma el paso pendiente sin repetir nada.
- El modal dice la verdad: si no se puede continuar, dice que el flujo empieza cuando el cliente escriba.

**No objetivos**

- Continuar después de un traspaso, o con un asesor asignado.
- Reenviar la pregunta pendiente al continuar: esta opción no manda nada.
- Cambiar «Devolver al asistente», «Iniciar asistente» (`automation/start`) ni la conversación `Sin asistente`.
- Sacar la corrida de `Waiting` para que el motor corra sin un entrante.

**Anti-objetivos** (lo que haría fallar el cambio aunque funcione)

- Que el modal prometa continuar y el bot reinicie, o lo contrario.
- Que la corrida siga como si nada después de que un asesor conversó con el cliente.
- Que «Devolver al asistente» deje de reiniciar: P2-R25 sigue vigente.
- Que el motor automático reactive una pausa manual.

## 3. Decisiones

**D-1 (del owner).** En una conversación pausada, «Iniciar asistente» abre un modal con dos opciones: «Continuar donde iba» (no manda nada;
cuando el cliente escriba, el asistente sigue en el paso pendiente, sin repetir el saludo) y «Enviar primer mensaje» (el
`automation/start` que ya existe). Maqueta aprobada.

**D-2 (del owner).** Un solo botón en ese caso. «Devolver al asistente» se queda sólo cuando «Iniciar» está deshabilitado u oculto, como hoy.

**D-3 (del owner).** `Sin asistente` sigue con la confirmación de una sola opción.

**D-4 (del redactor, por aprobar). Se puede continuar sólo si se cumple todo esto**, con una sola regla en `BotAttendance`, que usan el
resumen y el POST:

1. `automationStart` es `Available`, la misma decisión del botón (`ManualRunStarter.DecideAsync`). Eso ya exige abierta, sin asesor, sin
   bloqueo, WhatsApp, módulo activo, chatbot `Active` y ventana abierta.
2. `Paused` por `Manual`.
3. Corrida `Waiting`. Una `Running` con el candado libre es una unidad muerta (D-7 del spec 2026-10-31) y no se retoma.
4. Nadie la devolvió después de que arrancó: `AutomationResumedAt` no es posterior a `run.StartedAt`. Sin esto, «Devolver» y luego «Pausar»
   resucitaría una corrida que P2-R25 ya dio por terminada.
5. El chatbot que atiende la conexión es el de la corrida (`chatbot.ChatbotId == run.ChatbotId`, como en `RunProcessor.cs:489`).
6. D-5.

**D-5 (del redactor, por aprobar). Si un asesor tomó la conversación, se reinicia, como hoy.** Después de hablar con una persona, la
pregunta vieja se lee mal. Como el motivo de pausa no lo dice (§1), se mide con la historia: ningún evento `Taken`, `AutoTaken` o
`Transferred` del hilo después de `AutomationPausedAt`. Mismo mecanismo que `WasResolvedSinceAsync` (`ConversationQueries.cs:130-142`), sin
migración. Tomar y liberar sin escribir también cuenta: la regla es «un asesor la tuvo». Un puerto de una sola consulta, que corre sólo si
el resto de D-4 ya dio verdadero.

**D-6 (del redactor, por aprobar). El backend decide y lo expone en el resumen** (regla de BFF), al lado de `automationStart`: la pantalla
no conoce la corrida. Va como `automationContinue` (§5). Es `null` si no hay modal de dos opciones, y sólo se llena donde ya se llena
`AutomationStart`: en las respuestas de una conversación (`MessagingSupport.cs:57-60`), no en la bandeja.

**D-7 (del redactor, por aprobar). Sin corrida que continuar, el modal no promete.** Con `FlowStart` (un `Handoff`, un motivo automático, la
corrida cerrada por el barrido, D-5), la primera opción cambia de texto y llama a `automation/resume`, que hace exactamente eso. Propuesta:

> **Esperar a que escriba.** No se envía nada. Cuando el cliente escriba, el asistente empezará el flujo desde el principio.

Hoy esas conversaciones sólo ven «Iniciar» (D-10 del spec 2026-10-31), así que la opción no agrega un camino: recupera el de
«Devolver al asistente» sin un segundo botón.

**D-8 (del redactor, por aprobar). Endpoint nuevo `POST …/automation/continue`, sin marcador nuevo.** El marcador es la ausencia de sello:
`Conversation.ContinueAutomation` deja la conversación `Active`, limpia la pausa y **no escribe** `AutomationResumedAt`. `HumanTookOver` no
cambia y da falso por sí solo; `resume` sigue sellando. No hace falta columna.

| | A: endpoint nuevo (recomendada) | B: parámetro en `resume` |
|---|---|---|
| Efecto | Una ruta, un significado | `resume` hace dos cosas según el cuerpo |
| Handler | Aparte, como `StartAutomationHandler`, con candado y Automations | `ResumeAutomationHandler` gana el puerto y el candado sólo para un modo; `resume` también libera asesores, que aquí no aplica |
| Cliente viejo | No lo ve | Sin cuerpo, igual que hoy |
| Costo | Una ruta más | Una rama y un 422 que sólo existe con un valor del cuerpo |

**Concurrencia.** Mismo orden que `ManualRunStarter.StartAsync`: agregado e `If-Match` antes del candado; luego
`TryLockConversationAsync`, `FindLiveForUpdateAsync` y D-4 repetida bajo el candado.

- **Dos clics, dos asesores, o un entrante en medio:** la versión de la conversación sube, así que sale 412 (`If-Match` o el guardado).
  Con el candado tomado, `Busy` y 412, como iniciar.
- **La corrida se cerró con el modal abierto:** el barrido no sube la versión de la conversación, así que `If-Match` no lo ve; lo ve la
  relectura bajo el candado. Responde 422 `messaging.automation.cannot_continue` y **no** cae a `resume` en silencio: el frontend relee y el
  modal repinta con `FlowStart`.
- **Barrido y continuar:** usan el mismo candado. Si el barrido eligió la corrida antes y llega después, ve el `UpdatedAt` nuevo de D-9
  (`RunProcessor.cs:252`) y no la toca.
- **Falla parcial:** son dos `DbContext` y Messaging confirma primero. Si el guardado de Automations falla, la corrida sigue viva y el
  continuar funciona; sólo su reloj del barrido queda sin renovar. Se acepta.

**D-9 (del redactor, por aprobar). El límite de 24 h se queda, y continuar lo renueva.** No cambia qué cierra el barrido: un día después, la
pregunta ya se lee mal y D-M13 da la corrida por abandonada. Pero `ContinueAsync` del arrancador toca `run.UpdatedAt` bajo el candado
(`ConversationRun.KeepWaiting(now)`). Sin eso, una corrida de 23 h continuada a la hora 23 terminaría `Abandoned` a la hora 24: ya sin
pausa, `HumanTookOver` da falso y el barrido llama a `EndAsync`, que deja la conversación en `None` (`RunProcessor.cs:272-279`). El asesor
vería desaparecer el asistente que acaba de devolver. `UpdatedAt` sólo lo leen ese filtro y `:252`.

**D-10 (del owner, 2026-11-01). Lo que el cliente escribió durante la pausa no se reprocesa.** La corrida responde al
**siguiente** entrante. Esos mensajes no se pierden: están en el hilo y en no leídos. Pero nunca entraron a la cola, y si el cliente ya
contestó la pregunta, tras «Continuar» nadie le responde hasta que escriba de nuevo. Reprocesar el último mandaría mensajes del bot al
presionar, contra D-1, y obliga a elegir cuál de varios era la respuesta. Por eso no se reprocesa y se **avisa en el modal**, con
`customerWroteDuringPause` (`LastInboundAt > AutomationPausedAt`).

**D-11 (del redactor, por aprobar). Riesgo aceptado: el siguiente entrante es la respuesta.** En una Pregunta abierta sin validación, un
«¿sigues ahí?» se guarda como respuesta (`FlowEngine.cs:70-78`); en Botones o Lista, cuenta como intento y aplica su `fallback`. Un toque en
un botón viejo vale, salvo en Política de datos, donde se ata a la pregunta citada (`FlowEngine.cs:103-107`). No hay forma de distinguirlo sin
mandar nada.

**D-12 (del redactor, por aprobar). Cambios en el chatbot.**

- **Versión nueva publicada durante la pausa:** la corrida sigue en la suya (D-C1, `RunProcessor.cs:428`), como cualquier corrida viva. La
  palabra de reinicio la pasa a la nueva (`restarted_new_version`, `RunProcessor.cs:485-501`). Sin aviso en el modal.
- **Chatbot pausado o sin chatbot en la conexión:** `NoChatbot`, sin modal, y queda «Devolver al asistente» (D-2).
- **Otro chatbot en la conexión:** `FlowStart` (D-4.5). P2-R26 sólo aplica al arrancar.
- **Versión ilegible:** el POST la lee bajo el candado, como `ManualRunStarter.cs:74`, y responde `cannot_continue`. El resumen no la lee, para
  no cargar el flujo en cada detalle.
- **Se pausa el chatbot después de continuar:** la corrida sigue, porque `ContinueAsync` no mira el estado del chatbot (`RunProcessor.cs:428-429`;
  `Chatbot.Pause` sólo cambia el estado). Es lo que hoy le pasa a toda corrida viva; no lo cambia este spec.

**D-13 (del redactor, por aprobar). Rastro.** Evento nuevo `AutomationContinued`, con actor, y auditoría
`messaging.conversation.automation_continued`, en la misma transacción que el cambio, como `AutomationResumed` (`IMessagingAuditRecorder.cs:76`).
Tipo aparte para que el hilo diga qué pasó y la auditoría distinga un regreso que reinicia de uno que continúa. No hay
`automations.run.started` ni fila de `run_units`: ninguna corrida arranca ni avanza. Texto: «{actor} devolvió la conversación al asistente
para continuar.»

**D-14 (del redactor).** Mismo permiso, membresía y capas que pausar, devolver e iniciar: `messaging.conversation.manage`, con `If-Match`.

## 4. Qué cambia en el código

Rutas relativas a `qep-backend/src/`.

**Dominio de Messaging**

- `Modules.Messaging.Domain/Conversation.cs`: `ContinueAutomation(actorMemberId, now)`. Abierta, `Paused` por `Manual`, sin asesor. Limpia
  los tres campos de pausa, conserva `AutomationStep`, `AutomationStartedAt` y `AutomationChatbotId`, y no escribe `AutomationResumedAt`. El
  comentario de la propiedad (`:107-113`) dice por qué.
- `ConversationEvent.cs`: `AutomationContinued`, al final (se guarda por nombre). `MessagingErrorCodes.cs`: `AutomationCannotContinue`.
- `Modules.Messaging.Application/IMessagingAuditRecorder.cs`: la acción y su caso en `ForEvent`.

**Aplicación de Messaging y API**

- `ConversationAutomation.cs`: `ContinueAutomationCommand` y su handler, con la forma de `StartAutomationHandler`.
- `Modules.Messaging.Api/MessagingEndpoints.cs`: la ruta, junto a `start`, con las mismas respuestas. Registro en
  `QepServiceCollectionExtensions.cs`.
- `IAutomationManualStart.cs`: `ContinueAsync`, y `FindStateAsync` pasa a devolver los dos valores en una lectura: la corrida ya se lee
  ahí. Su falla sigue siendo `null` en los dos, la dirección segura.
- `MessagingDtos.cs`: `AutomationContinue` y su DTO. `MessagingSupport.cs:57-60`: llenarlo.

**Automations y Bootstrapper**

- `Engine/BotAttendance.cs`: `CanContinue(conversation, run)`, con D-4.2 a D-4.4. `HumanTookOver` e `IsAttending` no cambian.
- `Engine/ManualRunStarter.cs`: la evaluación del resumen, `ContinueAsync` (§3, D-8) y el veredicto `CannotContinue`.
- `ConversationRun.cs`: `KeepWaiting(now)`, sólo sobre una corrida `Waiting`.
- `AutomationsPorts.cs`: `AutomationPausedAt` en `AutomationConversationState` y `WasTakenSinceAsync` en `IAutomationConversations`.
- `Bootstrapper/AutomationConversations.cs` y `Bootstrapper/AutomationManualStart.cs`: los adaptadores y el error de `CannotContinue`.
- `Modules.Messaging.Infrastructure/Persistence/ConversationQueries.cs`: la consulta de D-5.

El barrido, `RunProcessor`, `InboundIngestion.ShouldEnqueue` y `resume` **no cambian**.

**Frontend (`qep-frontend`, spec aparte o tarea del builder)**

`utils/automation-start.ts` y `components/thread-header.tsx` (el modal de D-1, sólo si llega `automationContinue`; si no, el diálogo de
hoy), `services/messaging.api.ts` y `types/messaging.ts` (parser, `CONVERSATION_EVENT_TYPES`), `utils/conversation-event.ts`, y una mutación
junto a la de `automation/resume`. Ante `cannot_continue`, relee y deja el modal abierto con el texto nuevo.

## 5. Contrato

`POST /api/v1/tenants/{tenantId}/conversations/{conversationId}/automation/continue`, con `If-Match` y sin cuerpo. Responde `ConversationSummary`, 403,
404, 412, 422 y 428. Los 422 son los de iniciar y `messaging.automation.cannot_continue`. No hace falta mapearlos aparte: `MessagingDomainException` es
un `DomainException` (`ApiExceptionHandler.cs:184`).

En `ConversationSummary`:

```jsonc
"automationContinue": { "mode": "PendingStep", "customerWroteDuringPause": false } // o null
```

| Estado | `automationContinue` | Primera opción del modal | POST |
|---|---|---|---|
| `Paused`, `automationStart: Available`, D-4 cumplida | `mode: PendingStep` | «Continuar donde iba» | `automation/continue` |
| `Paused`, `automationStart: Available`, D-4 no cumplida | `mode: FlowStart` | Texto de D-7 | `automation/resume` |
| Cualquier otro caso, o falló la evaluación | `null` | Sin modal de dos opciones | — |

## 6. Datos, compatibilidad y despliegue

**Sin migración:** ninguna columna nueva, y `AutomationContinued` va como texto en `details`. **El backend va primero.**

- **Frontend viejo, backend nuevo:** ignora el campo y sigue con el diálogo de hoy. Sin «Continuar» nadie llama al endpoint, así que tampoco
  ve el evento nuevo. Si otro cliente ya actualizado lo escribe, pinta «La conversación tuvo un cambio.» (`conversation-event.ts:95`).
- **Frontend nuevo, backend viejo:** el campo no llega, y se comporta como hoy.
- **Rollback del backend:** un evento que no conoce se lee como `null`, sin error (`ConversationEventJson.cs:57-60`). Una conversación
  continuada queda `Active` y la corrida sigue por las reglas de siempre: nada depende del código nuevo para terminar.

## 7. Pruebas que tienen que existir

- **Continuar:** `Manual`, corrida `Waiting`, sin asesor → `Active`, sin sello, sin mensaje saliente. El siguiente entrante avanza desde el
  paso pendiente, misma corrida, no repite el saludo y conserva variables e intentos.
- **P2-R25 sigue:** el mismo caso por `resume` → `Interrupted` y corrida nueva desde el primer bloque.
- **Regla (una fila por cada una, resumen y POST coinciden):** corrida `Running`; sin corrida; `Handoff`; motivos automáticos; `resume` y
  luego pausa; otro chatbot en la conexión; `Taken`, `AutoTaken` y `Transferred` después de la pausa, con la conversación ya liberada.
- Un asesor que tomó **antes** de una pausa posterior no cuenta: sólo los eventos desde `AutomationPausedAt`.
- Una corrida de arranque manual (`ResumedAt` igual al piso de `StartedAt`) sí se continúa.
- **Barrido:** pausada a las 24 h → `Interrupted`, sigue `Paused`, y el resumen pasa a `FlowStart`. Continuada a la hora 23 → sigue viva a la
  hora 24 y a las 24 h del clic termina `Abandoned`. Una candidata elegida antes del continuar no se toca.
- **Carreras:** corrida cerrada con el modal abierto → 422 `cannot_continue`, sin cambios. Candado tomado → 412. Dos llamadas → una sola
  transición. Entrante en medio → 412.
- **Entrantes:** uno durante la pausa no avanza nada al continuar y enciende `customerWroteDuringPause`; el siguiente sí avanza.
- **Chatbot:** versión nueva publicada → sigue en la vieja; la palabra de reinicio pasa a la nueva. Chatbot pausado → sin modal y 422.
  Versión ilegible → `cannot_continue`, sin tocar la conversación.
- **Efecto:** evento con actor, auditoría `automation_continued`, y ni `automations.run.started` ni fila de `run_units`.
- `Conversation.ContinueAutomation` rechaza `Active`, `None`, otros motivos, con asesor y resuelta. `StartAutomation` sigue rechazando `Manual`.
- Otro tenant → 403, nunca 404. Sin permiso → 403. Sin `If-Match` → 428. Versión vieja → 412.
- **Frontend:** modal en `PendingStep` y `FlowStart`, aviso de `customerWroteDuringPause`, diálogo de hoy con `null`, `Sin asistente` sin
  cambios, y el repintado tras `cannot_continue`.

## 8. Decisiones abiertas para el owner

1. **OD-1: mensajes durante la pausa (D-10).** Cerrada el 2026-11-01: el owner eligió no reprocesarlos.
2. **OD-2: texto del aviso de D-10.** Propuesta: «El cliente escribió mientras estaba en pausa. El asistente no lee esos
   mensajes: responde cuando vuelva a escribir.»
3. **OD-3: reloj de 24 h (D-9).** Recomiendo dejarlo y renovar al continuar. La alternativa, contar desde la pausa, cambia el barrido de
   toda corrida pausada.
4. **OD-4: tomar y liberar sin escribir (D-5).** Recomiendo que cuente como asesor: es la regla simple. Si no, hay que mirar los mensajes
   salientes.
5. **OD-5: respuesta desde la app de WhatsApp Business.** Un eco de coexistencia no asigna ni deja evento (`CoexistenceIngestion.cs` no escribe
   ninguno), así que D-5 no lo ve y se continuaría. Recomiendo aceptarlo en esta versión. Para cerrarlo hay que mirar también los salientes.
6. **OD-6: el texto de `FlowStart` (D-7)**, que no sale de la maqueta aprobada.
7. **OD-7: acotar D-M15.** Su razón («una corrida no sobrevive a un humano») sigue en pie con asesor. Este spec la exceptúa sólo para la pausa
   manual sin asesor; al aprobar, hay que anotarlo en el spec del 2026-10-21.

Lo que queda para el builder: el nombre exacto del método del puerto y la forma de `FindStateAsync`.
