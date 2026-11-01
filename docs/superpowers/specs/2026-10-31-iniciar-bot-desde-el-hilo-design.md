# Automatizaciones: iniciar el bot desde el hilo

**Fecha:** 2026-10-31
**Módulos:** Automations (motor), Messaging (conversación, resumen del hilo, eventos), Bootstrapper (adaptadores). **Frontend:**
`qep-frontend`, `features/messaging/` (encabezado del hilo).
**Estado:** borrador para el owner. Las decisiones «del owner» vienen de la conversación del 2026-10-31; las «del redactor» las tomé yo y
se pueden discutir. **No se escribe código hasta que el developer apruebe este documento.**
**Depende de** `2026-10-26-cuando-toma-el-bot-la-conversacion-design.md` (parte en horario, ya en `develop`). No depende de la cobertura
fuera de horario de ese spec (D-5 a D-16).

## 1. Contexto

A veces alguien escribe y el bot no contesta: un fallo, o el tenant no había dejado listo el chatbot. Cuando eso se arregla, el cliente
sigue esperando, y el bot no le va a hablar hasta que vuelva a escribir, cosa que quizá no haga.

Hoy un asesor no tiene cómo resolverlo. «Devolver al asistente» (`POST …/automation/resume`) no manda nada: deja la conversación `Active`,
y el flujo arranca desde el primer bloque con el **siguiente entrante** (`RunProcessor.ResumeAsync`). Además, sólo aparece con el asistente
`Paused`.

**Pausar, tomar y devolver no cierran la corrida.** Esas acciones sólo tocan Messaging: la corrida sigue `Waiting` hasta que llega un
entrante y `HumanTookOver` la termina `Interrupted`, o hasta que la cierra el barrido de abandonadas a las 24 h. Así que una conversación
pausada casi siempre tiene una corrida `Waiting` que ya nadie va a continuar.

**Pedido del owner:** que un asesor pueda entrar al hilo y poner al bot a enviar el primer mensaje del flujo.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Un asesor, desde el hilo, arranca el flujo y el primer mensaje sale de inmediato, sin esperar al cliente.
- Si no se puede, el asesor lo sabe en la pantalla y sabe por qué.

**No objetivos**

- Arrancar el bot en una conversación con asesor. Eso exige permitir «bot activo + asesor», prohibido por
  `CK_conversations_automation_assignment`; es la pieza de D-5 del spec 2026-10-26.
- Elegir qué chatbot corre, o empezar en un bloque que no sea el primero.
- Reactivar con plantilla a un cliente fuera de la ventana de 24 horas.
- Arranque masivo sobre varias conversaciones.

**Anti-objetivos** (lo que haría fallar el cambio aunque funcione)

- Que el botón parezca funcionar y el cliente no reciba nada.
- Que dos clics, o dos asesores a la vez, le manden el flujo dos veces al cliente.
- Que el bot le reinicie el flujo a un cliente que ya va en medio de una corrida.

## 3. Decisiones

**D-1 (del owner).** La conversación tiene que estar **liberada**: sin asesor asignado.

**D-2 (del owner).** Al presionar, el bot manda de inmediato el primer mensaje del flujo. Lo que sigue es el flujo normal: el cliente
responde y la corrida continúa.

**D-3 (del owner).** Se puede usar también con el asistente `Paused` por `Manual` y por `Handoff`, y por las pausas automáticas. Es
una persona decidiéndolo a propósito, así que estas pausas no lo frenan. Quita la pausa.

**D-4 (del redactor).** Corre el chatbot `Active` de la conexión de la conversación, en su versión publicada. El asesor no elige. Sin
chatbot activo no se puede.

**D-5 (del redactor).** Fuera de la ventana de 24 horas no se puede: WhatsApp no deja mandar texto libre. El botón se ve deshabilitado y
dice por qué.

**D-6 (del redactor).** No aplica la regla D-12 del spec 2026-10-26. Esa regla evita que el bot arranque solo con un «gracias» después del
final de un flujo; aquí lo decide una persona.

**D-7 (del redactor, derivada de D-3).** No se puede sólo si **el bot está atendiendo de verdad**: una corrida viva y `HumanTookOver`
falso. Es la misma regla con la que el motor decide si una corrida sigue, para que el botón y el motor no discrepen.

Una corrida viva con `HumanTookOver` verdadero ya está muerta, aunque nadie la haya cerrado (ver §1): pasa con toda pausa y con lo que deja
«Devolver al asistente». Ahí se puede: el arranque la cierra `Interrupted`, bajo el candado y con su `WriteClosed`, y empieza una corrida
nueva desde el primer bloque. Sin ese cierre, quitar la pausa haría que el siguiente entrante continuara la corrida vieja. El tercer
anti-objetivo protege al cliente que el bot está atendiendo, no a uno de una corrida que una persona ya detuvo.

Una corrida `Running` cuya unidad murió cuenta como viva: no se trata en este spec.

**D-8 (del redactor).** Sólo una conversación `Open`. Una `Resolved` se reabre primero. Tampoco en un contacto bloqueado ni en Instagram,
que no tiene motor (D-M6).

**D-9 (del redactor).** Mismo permiso que «Devolver al asistente»: `messaging.conversation.manage`, con su doble capa de tenant y permiso,
la validación de membresía, y `If-Match` contra la versión de la conversación.

**D-10 (del redactor).** En una conversación sin asesor y `Paused`, si «Iniciar asistente» **está disponible**, el encabezado muestra sólo
ese botón, para no poner dos acciones casi iguales. En cualquier otro caso «Devolver al asistente» se queda como hoy (`Paused` + `Open`):
con asesor, donde además libera, y también si «Iniciar» está deshabilitado u oculto. Fuera de la ventana sigue siendo la forma de dejar el
bot listo para cuando el cliente vuelva a escribir: un entrante con pausa `Manual` o `Handoff` no se encola, y quitarlo sería una regresión.

**D-11 (del redactor).** Texto del botón: «Iniciar asistente». Si no se puede, deshabilitado con el motivo en el `title`:
«Ya pasaron 24 horas desde el último mensaje del cliente» o «Esta conexión no tiene un chatbot activo». Con una corrida viva, el contacto
bloqueado, un asesor asignado, una resuelta o Instagram, el botón no se muestra.

**D-12 (del redactor).** Queda rastro de quién lo hizo: evento en el hilo con el actor, y auditoría, igual que `AutomationResumed`. El
arranque de la corrida se sigue auditando en Automations (`automations.run.started`).

**D-13 (del redactor).** El frontend no calcula si se puede. El backend lo dice en el resumen de la conversación, porque la pantalla no
conoce el chatbot de la conexión ni debe reimplementar la ventana (regla de BFF del repositorio). Por ejemplo, un campo con el estado
`Available`, `WindowClosed` o `NoChatbot`, más lo que el encabezado necesite para ocultarlo; el motivo de la forma se escribe en el DTO.

## 4. Qué cambia en el código

Rutas relativas a `qep-backend/src/`.

### 4.1 Acción nueva

`POST /api/v1/tenants/{tenantId}/conversations/{conversationId}/automation/start`, con `If-Match`, junto a `pause` y `resume`, y las
mismas respuestas: `ConversationSummary`, 403, 404, 412, 422 y 428. Los errores de dominio van por `ApiExceptionHandler`, con un código por
caso; los que falten se declaran en `MessagingErrorCodes`. Una ventana cerrada reutiliza `MessagingErrorCodes.WindowClosed`.

### 4.2 Motor

`RunProcessor` no tiene hoy un arranque sin entrante: `StartRunAsync` recibe un `InboundQueueItem` y deriva de `MessageId` el id de la
corrida (`DeterministicClientId.RunIdFor`), que es lo que hace idempotente un reintento. Hay que dar un camino que:

1. Tome `TryLockConversationAsync` y lea la corrida viva (`FindLiveForUpdateAsync`), igual que `ProcessUnitAsync`. Si el bot está atendiendo
   (D-7), rechaza. Es lo que cierra el anti-objetivo de los dos clics, junto con `If-Match`.
2. Si hay una corrida muerta (D-7), la cierre `Interrupted`, antes de cambiar la conversación: `HumanTookOver` se lee sobre el estado de antes.
3. Pase la conversación a `Active` con el chatbot de D-4.
4. Corra `EngineInput.Start` sobre una corrida nueva.

### 4.3 Dominio de la conversación

`Conversation.StartAutomation` rechaza hoy `Manual` y `Handoff` a propósito: esa regla protege el arranque **automático** y se queda. Lo
de D-3 necesita un camino aparte, por ejemplo un método que reciba al actor y devuelva el evento, como `ResumeAutomation`. Que el motor
automático siga sin poder saltarse esas pausas tiene su prueba.

**Cuidado con `AutomationResumedAt`:** `HumanTookOver` corta la corrida si `AutomationResumedAt > run.StartedAt`. Si ese camino lo fija,
tiene que ser antes del `StartedAt` de la corrida, o el siguiente entrante mata la corrida recién creada.

### 4.4 Resumen de la conversación

Lo de D-13. El resumen vive en Messaging, que no ve Automations: si la conexión tiene chatbot activo y si el bot está atendiendo (D-7)
entran por un puerto con adaptador en Bootstrapper (`IAutomationsChatbotNames` es el precedente). Esa segunda respuesta tiene que salir de
la misma regla que usa el motor, sin copiarla: si difieren, el botón se muestra y responde 422, o no se muestra cuando debía. Si el costo
importa en la lista de la bandeja, puede ir sólo en la lectura de una conversación.

### 4.5 Frontend (`qep-frontend`)

`thread-header.tsx`: el botón de D-10 y D-11, con la misma forma que «Pausar asistente» (`canManage`, `isAutomating`). Una mutación
nueva, junto a la de `automation/resume`, con el `If-Match` de siempre. Un error del servidor se muestra como los demás de esas acciones.

## 5. Datos y migración

No hace falta cambiar `CK_conversations_automation_assignment`. Si el diseño del builder necesita una columna (§7), lleva su migración y
se avisa antes de desplegar. Un tipo de evento nuevo va como texto en `details` y no requiere migración, pero el frontend tiene que saber
pintarlo. El frontend y el backend se despliegan en ese orden: backend primero, porque el botón depende del campo nuevo del resumen.

## 6. Pruebas que tienen que existir

- Sin asesor, sin asistente, dentro de la ventana y con chatbot activo → sale el primer mensaje, corrida viva, conversación `Active`.
- El cliente responde → la corrida continúa, y `HumanTookOver` no la corta.
- `Paused` por `Manual`, por `Handoff` y por un motivo automático, cada uno con y sin corrida `Waiting` → arranca, limpia la pausa y la
  corrida vieja queda `Interrupted`.
- Lo que deja «Devolver al asistente», con su corrida `Waiting` → arranca y cierra la vieja.
- `Active` sin corrida → arranca.
- Después de arrancar sobre una corrida muerta, el siguiente entrante sigue la corrida **nueva**, no la vieja.
- Última corrida `Completed` sin resolución posterior → arranca (D-6), y el arranque automático sigue sin hacerlo.
- Bot atendiendo (corrida viva y `HumanTookOver` falso) → rechaza, sin mensaje.
- Con asesor, resuelta, bloqueado, Instagram, sin chatbot activo, fuera de la ventana → error con su código y sin mensaje.
- Dos llamadas seguidas o simultáneas → un solo flujo enviado.
- `Conversation.StartAutomation` sigue rechazando `Manual` y `Handoff`.
- Otro tenant → 403, nunca 404. Sin permiso → 403. Sin `If-Match` → 428. Versión vieja → 412.
- El efecto importa, no sólo el status: el evento del hilo con su actor, la auditoría y `automations.run.started`.
- Resumen: cada valor de D-13, y que coincida con la decisión del motor en los casos de D-7.
- Frontend: el botón en cada estado de D-10 y D-11, y «Devolver al asistente» visible cuando «Iniciar» está deshabilitado u oculto.

## 7. Lo que queda en manos del builder

- **Dónde corre la primera unidad:** en el request, lo que devuelve el error directo, o encolada para el worker, lo que evita un request
  largo: un primer bloque HTTP puede tardar decenas de segundos. Lo no negociable es que los casos de D-1 a D-8 se le digan al asesor al
  presionar. Si va encolada, cuánto y cómo se le avisa de un fallo posterior lo decide el builder, sin dejarlo en silencio.
- De dónde sale el id de la corrida sin `MessageId`, manteniendo la idempotencia.
- En qué módulo cuelga el endpoint y cómo cruza Messaging y Automations, respetando `ArchitectureTests`.
- El nombre del tipo de evento y su texto en el hilo.
- La forma exacta del campo de D-13.
