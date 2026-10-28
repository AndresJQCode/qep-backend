# Automatizaciones: cuándo toma el bot la conversación

**Fecha:** 2026-10-26
**Módulos:** Automations (disparador, procesador), Messaging (ingesta, agregado `Conversation`, constraint de la base, pausa al
responder), Bootstrapper (adaptadores). **Frontend:** `qep-frontend`, `features/messaging/` (bandeja e hilo) y
`features/automations/` (texto del disparador).
**Estado:** borrador para el owner. Las decisiones «del owner» vienen de la conversación del 2026-10-26; las «del redactor» las tomé
yo y se pueden discutir; las DECISIÓN-PENDIENTE (§8) las decide el owner. **No se escribe código hasta que el developer lo diga.**
**Es un delta sobre** `2026-10-21-constructor-de-chatbots-design.md` (D-C6, D-M3, D-M15, D-M16) y
`2026-10-21-mensajeria-asistente-y-horarios-design.md` (pausa del asistente al tomar o responder). Lo que este documento no toca
sigue como está ahí.
**Fuera de este documento:** los mensajes seguidos del cliente (el owner eligió la opción A: lo escrito antes de una pregunta no la
responde). Va en un diseño propio.

## 1. Contexto: el caso verificado

Conversación `01a13a1a-b49b-7c1e-8389-a5b46247bb29` (base local, 2026-10-25/26). El chatbot de la conexión usa el disparador
`NewConversation`.

| Hora (Bogotá) | Qué pasó | `automation_inbox` |
| --- | --- | --- |
| 25-10 23:54:36 | El bloque Resolver resuelve: corrida `Resolved`, evento `Resolved` y auditoría `messaging.conversation.resolved` | — |
| 26-10 09:44:21 | «hola» | `opened = false`, sin evento `Reopened` → `skipped` |
| 09:49:52 | «hola» | `opened = false` → `skipped` |
| 18:44:31 | «reiniciar» | `opened = false` → `skipped` |
| 18:46:26 | Un asesor resuelve | — |
| 18:46:50 | «reiniciar» | `opened = true` + evento `Reopened` → `started` |

`TriggerEvaluator.Applies` sólo arranca `NewConversation` con el entrante que abrió o reabrió la conversación
(`TriggerEvaluator.cs:21`, D-M16). A las 09:44 la conversación ya estaba `Open` en la base, sin que ningún evento ni auditoría lo
explique (§8, DP-3), y el cliente quedó sin bot y sin asesor durante nueve horas.

El defecto de fondo no es esa anomalía: es que el disparador depende de un **dato de estado** (si la conversación estaba resuelta)
como sustituto de «la charla anterior terminó». Cualquier cosa que deje la conversación abierta sin dueño —reabrirla a mano, una
corrida que terminó sin resolver, un estado inconsistente— deja al cliente sin respuesta.

**Pedido del owner:** «independientemente si la conversación está abierta o cerrada, el chatbot la puede tomar siempre y cuando no
esté asignada a un asesor», y «fuera del horario, el chatbot puede contestar así la conversación esté asignada a un asesor», sin
que el asesor pierda la conversación: «al otro día el asesor no vería que ese usuario lo tiene asignado».

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- En horario, el bot toma toda conversación sin asesor cuando el cliente escribe, esté abierta o resuelta.
- Fuera de horario, el bot atiende también la conversación asignada, y la asignación no cambia.
- La pausa explícita («Pausar asistente») sigue mandando siempre.

**No objetivos**

- Liberar una conversación asignada por inactividad del asesor. El owner lo consideró y lo descartó.
- Agrupar mensajes seguidos del cliente (diseño aparte).
- Cambiar el disparador `Keyword`.

**Anti-objetivos** (lo que haría fallar el cambio aunque funcione)

- Que el asesor pierda la conversación de su bandeja porque el bot la atendió de noche.
- Que el bot le escriba a un cliente cuya conversación alguien pausó a propósito.
- Que el bot y un asesor le hablen al cliente a la vez sin que la corrida se corte.

## 3. Decisiones

### En horario

**D-1 (del owner).** `NewConversation` arranca con **cualquier** entrante, no sólo con el que abre o reabre la conversación. Las demás
condiciones de `RunProcessor.DecideAsync` (`RunProcessor.cs:244-260`) no cambian: conversación sin asesor, sin corrida viva, asistente
en `None` (o `Active` sin corrida, camino de `ResumeAsync`), chatbot de la conexión `Active` y módulo `automations` encendido.

**D-1b (del owner, DP-1).** `OutsideHours` también deja de depender de `opened`: arranca con **cualquier** entrante que llegue fuera de
horario (`TriggerEvaluator.cs:24` pasa a `!businessHoursOpen`). Las mismas condiciones de D-1, más D-12.

**D-2 (del owner).** Da igual si la conversación estaba `Open` o `Resolved`. Una resuelta ya la reabre la ingesta
(`InboundIngestion.cs:136-139`); con D-1 deja de importar si la ingesta la vio resuelta.

**D-3 (del owner, riesgo aceptado).** Se va la protección de D-M16 contra «saludar a mitad de un hilo que un asesor liberó». Si un
asesor desasigna sin resolver y el cliente escribe «gracias», el bot arranca el flujo desde Inicio.

**D-3b (del redactor, derivada del pedido).** En horario, una conversación **sin** asesor con una pausa automática (`AdvisorTook`,
`AdvisorReplied`, `AdvisorTransferred`) también la toma el bot. Pasa cuando un asesor la toma y después la libera: `Release` quita el
asesor pero no la pausa (`Conversation.cs:309-320`), y hoy queda abierta, sin dueño y sin bot, el mismo hueco de §1. El owner pidió
que el bot no la tome sólo si está «pausado en esa conversación», que es la pausa `Manual` (D-6). `Handoff` también la frena (D-13b).

**D-12 (del owner, DP-4).** Un flujo que **terminó por su cuenta** (corrida `Completed`: llegó al final sin resolver ni traspasar) no
vuelve a empezar hasta que la conversación **se resuelva**. Sin esto, un «gracias» después del final arrancaría el saludo otra vez.

**D-12b (del redactor, alcance de D-12).** El límite se mide con la **historia**, no con el estado actual: «hubo una resolución
después de que terminó la última corrida» (el evento `Resolved` del hilo, o la corrida misma terminada `Resolved`). Leer
`status = Resolved` repetiría el defecto de §1. Sólo aplica a la última corrida `Completed`. Después de `Resolved`, `HandedOff`,
`Interrupted`, `Abandoned` o `Failed` el bot puede volver a empezar sin esperar una resolución: en esos casos una persona intervino, el
cliente desapareció 24 h o el bot ya resolvió, que es lo que D-3 y D-3b piden. Sin corrida previa, arranca.

**D-13 (del owner, DP-5).** «Hay asesores disponibles» = **la conexión está en horario de atención** (D-9). No hay presencia por
miembro y este diseño no la agrega. Validar antes de traspasar se hace en el flujo, con el bloque «Horario de atención»
(`FlowNodeKinds.BusinessHours`) antes de «Pasar a un asesor»: en horario traspasa, fuera de horario el flujo dice lo que el tenant
quiera («te respondemos mañana a las 8»). No requiere código del motor.

**D-13b (del redactor, DP-5).** Un traspaso que igual ocurre fuera de horario (el bloque sin «Horario de atención» delante, la regla de
agotamiento con `Handoff`, o un flujo que falla, D-M14) deja al asistente `Paused Handoff`, y esa pausa **frena la cobertura** hasta
que actúe una persona o se resuelva la conversación, como `Manual`. Después de «Te paso con un asesor», volver a arrancar el flujo
contradiría al bot; es también el criterio de Zenvia (fila de espera «para que el cliente no vuelva directo al chatbot»). D-6 queda
con `AdvisorTook`, `AdvisorReplied` y `AdvisorTransferred`.

**D-4 (del owner).** En horario, una conversación con asesor sigue sin bot, aunque el asesor no esté escribiendo. No hay liberación por
inactividad (ver no objetivos).

### Fuera de horario: el bot cubre, el asesor sigue siendo el dueño

**D-5 (del owner).** Fuera del horario de atención, el bot atiende **también la conversación asignada**, si el chatbot está
configurado para eso (D-5b). `assigned_member_id` no se
toca: al otro día la conversación sigue en la bandeja «mías» del asesor, con lo que el bot conversó durante la noche.

**D-5b (del owner, DP-2; corregida 2026-10-27).** La cobertura de una conversación asignada **sólo existe si el chatbot de la conexión
tiene el disparador `OutsideHours`** («Cuándo entra el chatbot» → «Sólo fuera del horario de atención de la conexión», en los ajustes del
chatbot). Con `NewConversation` o `Keyword`, una conversación asignada no tiene bot nunca, ni de día ni de noche. El disparador es una
sola opción (`chatbot-settings.schema.ts:10`), así que un tenant no puede tener a la vez «el bot atiende lo que no tiene asesor de día» y
«el bot cubre lo asignado de noche»: si eso se pide, es otro diseño. Una corrida viva de cobertura sigue con cualquier entrante, como
toda corrida viva. D-16 sólo aplica a estos chatbots.

**D-6 (del owner).** Las pausas que pone el trabajo del asesor no frenan la cobertura: `AdvisorTook`, `AdvisorReplied` y
`AdvisorTransferred` (`CK_conversations_automation_reason`). `Handoff` sí la frena (D-13b). La pausa `Manual` («Pausar asistente») se respeta siempre,
en horario y fuera de él: es la regla del owner de «no pausado en esa conversación».

**D-7 (del owner).** Si el horario de atención empieza mientras el bot está a mitad del flujo, **el bot termina el flujo**. Lo que corta
la cobertura es una persona, no el reloj.

**D-8 (del owner).** Cuando un asesor responde en una conversación que el bot está cubriendo, el asistente pasa a `Paused` con
`AdvisorReplied` y la corrida termina `Interrupted`, como hoy cuando interviene una persona (D-M15).

**D-16 (del owner, 2026-10-27).** Un asesor que atiende fuera de horario **frena la cobertura** de esa conversación hasta el próximo
cierre del horario. Sin esto, D-6 y D-8 arman un loop: el asesor responde (corrida `Interrupted`, `Paused AdvisorReplied`), el cliente
contesta, `AdvisorReplied` no frena la cobertura y el bot arranca el flujo desde Inicio en medio de la charla. Cuenta una pausa
`AdvisorTook`, `AdvisorReplied` o `AdvisorTransferred` cuyo `AutomationPausedAt` cae **dentro de la ventana fuera de horario actual**
(desde el último cierre de la conexión), o en el margen de D-16b antes de ese cierre. La ventana siguiente vuelve a cubrir: D-6 sigue
valiendo para el asesor que respondió de día y no está de noche. En horario no cambia nada (D-4).

**D-16b (del owner, 2026-10-27).** El margen hacia atrás cubre la charla que cruza el cierre: el asesor responde a las 17:50, cierra a las
18:00 y el cliente escribe a las 18:01, antes de que el asesor vuelva a responder. Sin margen esa respuesta queda en horario y el bot
entra. **Default 30 minutos, parametrizable por el tenant.** Con `0` no hay margen. El rango permitido es DP-8.

**D-16c (del owner, 2026-10-27).** «Pausar asistente» se ofrece también con el asistente `Paused` por un motivo automático (`AdvisorTook`,
`AdvisorReplied`, `AdvisorTransferred`), y lo convierte en `Manual`. Es la salida explícita del asesor que va a seguir en la conversación
sin importar la hora; con D-6, `Manual` frena siempre. Hoy el botón sólo aparece con el asistente `Active`
(`qep-frontend/src/features/messaging/components/thread-header.tsx:231`), y apenas el asesor responde deja de estar.

**D-9 (del redactor).** «Fuera de horario» es lo que ya responde `IBusinessHours.IsOpenAsync(tenantId, connectionId)`
(`RunProcessor.cs:274-275`), por conexión. No se agrega un horario nuevo.

**D-10 (del redactor).** En una conversación asignada, el bloque «Pasar a un asesor» la devuelve **a su asesor**: no la reasigna aunque
el bloque tenga un destino (spec 2026-10-25 D-1), y el asistente queda `Paused` con `Handoff`. El asesor dueño es el único que tiene
contexto del cliente; reasignar de noche le quitaría la conversación, que es justo el anti-objetivo.

**D-14 (del owner, DP-6).** Durante la cobertura, el bloque Resolver **resuelve** la conversación asignada, como en cualquier otra.
`Conversation.Resolve` no toca `assigned_member_id` (`Conversation.cs:249-260`): la conversación queda resuelta **a nombre del
asesor**, y un mensaje nuevo del cliente la reabre con él. El anti-objetivo de §2 aplica a la bandeja abierta mientras la charla sigue,
no a una que el flujo dio por terminada.

**D-11 (del redactor).** Mientras el bot cubre una conversación asignada, la bandeja y el hilo lo muestran, con algo como «Asignada a Ana
· el asistente atiende fuera de horario». La forma exacta es del frontend.

## 4. Qué cambia en el código

Rutas relativas a `qep-backend/src/`, salvo la sección del frontend.

### 4.1 Disparador (Automations)

- `TriggerEvaluator.Applies` (`Modules/Automations/.../Engine/TriggerEvaluator.cs:16-27`): `NewConversation` deja de depender de
  `opened` (D-1). `OutsideHours` también (D-1b).
- El parámetro `opened` y la columna `automation_inbox.opened` pueden quedar sin consumidor; si es así, se retiran en un slice aparte,
  no en este.

### 4.2 Cobertura fuera de horario (Messaging + Automations)

Hoy la regla «con asesor, sin bot» vive en cuatro lugares. Los cuatro cambian:

1. **La ingesta** no encola si hay asesor ni si el asistente está `Paused` (`InboundIngestion.cs:224-229`, `ShouldEnqueue`). Tiene que
   encolar también la conversación asignada o pausada por un motivo automático (D-6), porque la ingesta no sabe si es de noche: el
   horario lo decide el motor (D-9). La `Manual` no se encola.
2. **El motor** descarta la conversación con asesor (`RunProcessor.cs:244-248`) y la pausada (`_ => null`, `RunProcessor.cs:255-260`).
   Con D-5 y D-6, la acepta si el chatbot es `OutsideHours` (D-5b), el horario está cerrado y la pausa no es `Manual`.
3. **`HumanTookOver`** (`RunProcessor.cs:234-238`) corta la corrida viva si la conversación tiene asesor o está pausada. Con D-5 eso
   cortaría toda cobertura en el siguiente mensaje. Cambia de significado: corta si **una persona actuó después de que arrancó la
   corrida** (respondió, tomó, transfirió, pausó o resolvió). Cómo se mide «después» es del builder; `AutomationPausedAt` y
   `AutomationResumedAt` frente a `ConversationRun.StartedAt` ya cumplen ese papel (P2-R25).
4. **La base** prohíbe asistente `Active` con asesor (`CK_conversations_automation_assignment`, `automation_status <> 'Active' OR
   assigned_member_id IS NULL`), y el agregado también (`Conversation.StartAutomation`, `Conversation.cs:339-359`). Hay que permitir esa
   combinación. Si alcanza con relajar el CHECK, o si conviene una columna que diga que es una cobertura fuera de horario, lo decide el
   builder (§7).
5. **El motor** (D-16): antes de arrancar una cobertura, descarta la conversación si su pausa automática tiene `AutomationPausedAt >=
   último cierre - margen del tenant` (D-16b). Necesita el último cierre de la conexión: `BusinessHoursStatus` hoy sólo trae
   `NextChangeAt` (`Modules/Messaging/Modules.Messaging.Application/BusinessHoursPorts.cs:38`).
6. **La pausa manual** (D-16c): `POST …/automation/pause` acepta una conversación ya `Paused` por un motivo automático y la deja en
   `Manual`, con su evento `AutomationPaused`.

### 4.3 Pausa al responder (Messaging)

Hoy el asistente se pausa con `AdvisorReplied` en la autoasignación, que sólo actúa sobre una conversación **sin** asesor
(`ConversationRepository.TryAutoAssignAsync`, `ConversationRepository.cs:152-175`). Con D-5 un asesor ya asignado responde en una
conversación con el asistente `Active`: ese envío también tiene que pausar con `AdvisorReplied` y dejar su evento `AutomationPaused`
(D-8).

### 4.4 Frontend (`qep-frontend`)

- Bandeja e hilo: mostrar la cobertura (D-11) cuando la conversación tiene asesor y el asistente `Active`. Hoy esa combinación no
  existe, así que ningún componente la espera.
- Hilo: «Pausar asistente» también con el asistente `Paused` por un motivo automático (D-16c).
- Configuración: el margen de D-16b, en minutos, editable por el tenant. Dónde vive la pantalla lo decide el frontend.
- Editor de chatbots: si el texto que describe `NewConversation` dice «conversación nueva» o algo equivalente, ajustarlo a D-1.

## 5. Datos y migración

- El horario ya existe por conexión (D-9). Dato nuevo del tenant: el margen de D-16b (minutos, default 30), con su lectura y
  escritura.
- Una migración si cambia `CK_conversations_automation_assignment` o si se agrega una columna (§4.2, punto 4).
- Los chatbots publicados con `NewConversation` cambian de comportamiento sin republicar. Es intencional (D-1).

## 6. Pruebas que tienen que existir

- **El caso verificado (§1):** conversación `Open`, sin asesor, asistente en `None` y sin corrida; el entrante tiene `opened = false`
  → `started`.
- Conversación resuelta → el entrante la reabre y arranca (no regresiona).
- Última corrida `Completed` y sin resolución posterior → `skipped`; con un `Resolved` posterior (aunque la conversación figure
  `Open`) → `started` (D-12, D-12b).
- Asesor toma y libera (sin asesor, `Paused AdvisorTook`) → `started` (D-3b).
- En horario, con asesor → `skipped`.
- Fuera de horario, chatbot `OutsideHours`, con asesor y pausa `AdvisorReplied` → `started`, y `assigned_member_id` intacto.
- Fuera de horario con pausa `Manual` → `skipped`, con asesor o sin él.
- Fuera de horario, con asesor y pausa `AdvisorReplied`, chatbot `NewConversation` → `skipped`; chatbot `Keyword` con la palabra →
  `skipped` (D-5b). Las dos sin asesor siguen como D-1 y el `Keyword` de siempre.
- Cobertura viva y el asesor responde → corrida `Interrupted` y asistente `Paused AdvisorReplied`.
- Cobertura viva y empieza el horario → el flujo sigue en el siguiente entrante (D-7).
- «Pasar a un asesor» durante la cobertura → sigue asignada al mismo asesor, `Paused Handoff`, y el siguiente entrante no arranca
  (D-10, D-13b).
- Resolver durante la cobertura → `Resolved` con `assigned_member_id` intacto; el siguiente entrante la reabre con el mismo asesor
  (D-14).
- Fuera de horario, el asesor responde después del cierre y el cliente contesta → `skipped` (D-16); en la ventana fuera de horario
  siguiente, el primer entrante → `started`.
- El asesor respondió 20 min antes del cierre con margen 30 → `skipped`; 40 min antes → `started`; con margen `0`, 1 min antes →
  `started` (D-16b).
- «Pausar asistente» sobre `Paused AdvisorReplied` → `Paused Manual` con su evento, y fuera de horario el siguiente entrante →
  `skipped` (D-16c).
- Pruebas de integración de la ingesta para el nuevo `ShouldEnqueue`, y una de persistencia para el CHECK cambiado.

## 7. Lo que queda en manos del builder

- La forma de permitir «asistente activo + asesor» (§4.2, punto 4): CHECK relajado, o columna explícita de cobertura.
- Cómo medir «una persona actuó después del arranque» en `HumanTookOver`.
- Si el horario se consulta una sola vez por unidad, como hoy («El horario sólo se consulta cuando decide algo»,
  `RunProcessor.cs:273`).
- El texto exacto del evento o la marca de cobertura en el frontend.
- Cómo obtener el último cierre del horario de una conexión (D-16), y dónde se guarda el margen del tenant (D-16b).

## 8. Decisiones pendientes del owner

- ~~**DP-4.**~~ Resuelta: D-12 y D-12b.
- ~~**DP-5.**~~ Resuelta: D-13 y D-13b.
- ~~**DP-6.**~~ Resuelta: D-14.

- **DP-8.** Rango permitido del margen de D-16b (¿0 a 240 minutos?). El default de 30 ya lo decidió el owner.

- ~~**DP-1.**~~ Resuelta: D-1b.
- ~~**DP-2.**~~ Resuelta: D-5b.
- **DP-3 (el owner: se investiga; «no puede hacer cambios sin dejar huella»).** **Causa encontrada (2026-10-27):** Meta reentregó el
  2026-10-26 00:52:24 (`created_at`) un «riiniciar» escrito el 2026-10-24 12:17:36 (`occurred_at`). La ingesta reabrió la conversación
  resuelta (`InboundIngestion.cs:138` pone `Open` siempre) y fechó el `Reopened` con `occurred_at - 3 ms` (`InboundIngestion.cs:188-194`):
  quedó en el hilo **antes** del `Resolved` que deshace. `ShouldEnqueue` lo descartó por viejo (`:227`), así que tampoco hay fila en la
  cola. El rastro existe, en el lugar equivocado; se ve filtrando por `created_at`. Fix aparte de este diseño: fechar el `Reopened` con
  `now`; y el owner decide si un entrante fuera de orden reabre (DP-7). El texto de abajo es el planteo original.
- ~~**DP-7.**~~ Resuelta: **D-15 (del owner).** Un entrante fuera de orden **no reabre** una conversación `Resolved`: queda resuelta y el
  mensaje entra al hilo igual (contador de no leídos y foto de la lista con sus `CASE` por fecha de siempre). **D-15b (del redactor):**
  «fuera de orden» es el mismo criterio que ya usa `ShouldEnqueue`, `OccurredAt < last_inbound_at` (`InboundIngestion.cs:227`), para
  que la ingesta y la cola no discrepen sobre qué es un mensaje viejo. Con D-15, un `Reopened` sólo lo genera un entrante en orden, cuya
  fecha ya es la de llegada, así que no hace falta cambiar cómo se fecha. Va en un slice propio de Mensajería, independiente del resto del
  diseño, con la prueba de reproducción de la investigación (`InboundIngestionTests`: resolver, ingerir uno más viejo → sigue
  `Resolved`, sin `Reopened`).
  Lo que sigue es el caso original.
  **La reapertura sin rastro.** A las 09:44 del 2026-10-26 la conversación de §1 estaba `Open` sin evento ni auditoría que lo
  explique. El código actual sólo pone `Open` desde `Conversation.Reopen` (con evento) o la ingesta (con `opened = true`), y no hay
  triggers en la tabla. D-1 hace que deje de importar para el bot, pero un estado que cambia sin rastro es un bug de Mensajería. ¿Se
  investiga aparte, con una prueba que reproduzca el camino «el bot resuelve → entra un mensaje»?
