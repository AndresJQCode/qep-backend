# Agente IA que agenda citas: herramientas de la Agenda para el agente (backend)

- **Fecha:** 2026-11-23.
- **Módulos:** `Ai` (seis herramientas nuevas, `config.tools.scheduling`, validador, prompt, playground), `Scheduling`
  (extracción del núcleo de disponibilidad, reserva, cancelación y cambio de hora; servicio sin sesión para el agente;
  auditoría por agente; `by_channel` en el historial; remitente por defecto del recordatorio), `Bootstrapper` (adaptador
  del puerto de Ai, cliente de la conversación). Mensajería y Clientes no cambian: se reusa lo que ya existe. Frontend en
  `qep-frontend` (spec aparte; el contrato está al final).
- **Estado:** aprobado. El owner cerró las preguntas abiertas el 2026-11-23 (D-14 a D-19). **Sin código.** Plan:
  [`plans/2026-11-23-agente-ia-agenda-backend.md`](../plans/2026-11-23-agente-ia-agenda-backend.md).
- **Se apoya en:** [`2026-10-29-agentes-ia-design.md`](2026-10-29-agentes-ia-design.md) (§7 playground, §8 herramientas y
  principal de servicio, D-A16, D-A30), [`2026-11-17-agente-ia-en-chatbots-design.md`](2026-11-17-agente-ia-en-chatbots-design.md)
  (modo bloque, traspasos), [`2026-11-21-agenda-citas-design.md`](2026-11-21-agenda-citas-design.md) y su plan
  [`plans/2026-11-21-agenda-citas-backend.md`](../plans/2026-11-21-agenda-citas-backend.md) (correcciones 16 a 22 y 34).
  El spec de la Agenda dejó listos `ListAvailability` y `BookAppointment` «para el agente»; éste los conecta.
- **Verificado contra el código el 2026-11-23:** rama `feature/agenda-agente-ia` (desde `develop` `c4fb227c`). Donde este
  documento y el código discrepen, gana el código.

## 1. Intención

Un contacto de WhatsApp (o Instagram) le escribe al negocio y el **agente IA** le agenda, le muestra, le cancela o le
cambia la hora de sus citas, contra la misma Agenda que usa la secretaria, sin que nadie del equipo intervenga mientras
todo esté dentro de las reglas del tenant. Lo que se sale de las reglas termina en una persona.

**Metas**

1. Seis herramientas del agente: servicios reservables, disponibilidad, reservar, mis citas, cancelar, cambiar hora.
2. **Las reglas las aplica el backend, nunca el prompt:** cliente de la conversación, servicios reservables por canal,
   anticipación, horizonte, cupos, modo de confirmación y plazo de cancelación salen del mismo motor y de los mismos
   casos de uso que el back office.
3. Sin principal todopoderoso: el agente no corre los handlers de la Agenda con permisos sintéticos.
4. El playground prueba el flujo completo sin escribir en la Agenda.

**Fuera de alcance**

- Que el agente confirme citas `Pending`, marque asistencia o «no asistió»: es trabajo de la secretaria.
- Lista de espera, varios cupos por reserva, cobro o anticipo.
- Recordatorios: ya existen (plan de la Agenda, Tarea 12). Este spec sólo agrega el remitente por defecto (D-18, §6.4);
  el resto del envío no cambia.
- Que el agente agende para otra persona que no sea el cliente de la conversación.
- Evals nuevos del agente (`Modules.Ai.Eval`): gastan dinero real y necesitan presupuesto aprobado (regla `ai-copiloto`).

## 2. Lo que existe (verificado)

| Pieza | Dónde | Qué importa aquí |
| --- | --- | --- |
| Herramientas del agente | `Modules.Ai.Application/AgentTools.cs` | `IAgentTool` (`Definition`, `Capability`, `IsOfferedTo(tools, isTest)`, `ExecuteAsync`), `AgentToolInvocation(TenantId, JobId, CallId, CustomerId, IsTest, QuotationDraft)`, `AgentToolResult` con `Handoff` que termina el turno, `AgentToolErrors`, `AgentToolNames.Writes` |
| Principal de servicio | `AiServicePrincipal` (mismo archivo) | Conjunto fijo: `catalog.read`, `orders.read_own`, `quotations.draft_create` |
| Bucle | `ModelToolLoop.cs` | A lo sumo **una** herramienta que escribe por respuesta (`one_write_per_response`); una herramienta con `Handoff` termina el turno sin otra vuelta |
| Perfil del agente | `AgentTurnRunner.AgentProfile` | Filtra por capacidad **y** `config.tools`; arma la invocación con `_context.CustomerId` |
| Cliente del trabajo | `AgentSessionJobProcessor` | `conversation?.CustomerId` leído por trabajo (`IAiConversations.GetAsync`); `AgentTurnContext.ConversationId` existe pero no llega a la invocación |
| Topes por trabajo | `AiModelCallOptions` (`Ai:Model`) | 6 llamadas al modelo, 8 herramientas, 120 s |
| Hora del negocio en el prompt | `AgentPromptBuilder.VolatileOf` | «Fecha y hora del negocio: yyyy-MM-dd HH:mm (zona)» después del punto de caché (C-12) |
| Precedente de escritura sin sesión | `CreateQuotationDraftTool` → `IAiQuotationDrafts` → `Bootstrapper/AiQuotationDrafts.cs` → `IExternalQuotationWriter` (`ExternalQuotationService`) | Puerto en Ai, adaptador en Bootstrapper que traduce excepciones a códigos, servicio sin sesión dentro del módulo dueño |
| Simulación del playground | `SimulatedQuotationDraftTool` | Valida igual que la real, devuelve `PRUEBA-…`, no escribe, termina con el mismo traspaso |
| Reserva | `BookAppointmentHandler` | Autoriza (`appointment.read` + `manage`), valida, alcance, `clientRequestId` (antes y después del candado), `bookableByChannel` con `Source = Channel`, candado de recursos, sesión, estado según `confirmationMode`, actor nulo en canal, auditoría |
| Disponibilidad | `ListAvailabilityHandler`, `AvailabilityLoader`, `AvailabilityEngine` | El loader y el motor **no** miran el `IExecutionContext`: sólo el `ResourceScope` que reciben. `SlotOption.AllowClientChoice` existe pero `AvailabilityDto` no lo expone y la reserva no lo aplica (plan, corrección 17) |
| Cancelar | `CancelAppointmentHandler`, `AppointmentCancellationRules` | Con `Source = Channel` aplica `clientCancelCutoffHours` → 422 `invalid_transition`; actor nulo |
| Cambiar hora | `RescheduleAppointmentHandler` | Sin `Source`: siempre como back office (`actsAsClient: false`), sin plazo |
| Carga y autorización de una cita | `AppointmentTransitions.LoadAsync` | Permisos, `If-Match` obligatorio, alcance, versión |
| Auditoría | `ISchedulingAuditPublisher` / `SchedulingAuditPublisher` | Siempre `AuditActorType.Human` con el `SubjectId`; `AuditActorType.AiAgent` existe (lo usa `AiAuditRecorder.RecordByAgent` con el id del agente) |
| Historial | `AppointmentEvent.ActorMemberId` | Nulo = sistema o cliente por canal |
| Cliente de la conversación | `Bootstrapper/ConversationCustomerResolver.cs` | Con cliente, lo devuelve; sin él y con BSUID, `IMessagingCustomerDirectory.EnsureAsync` (idempotente por BSUID) + `IConversationRepository.LinkCustomerAsync` + aviso a la bandeja. Lo comparten «Política de datos» y «Acción de CRM» |
| Alta desde la Agenda | `Bootstrapper/SchedulingCustomerLookup.CreateAsync` | Usa `executionContext.SubjectId`: sirve a un miembro, **no** al agente |
| Ingesta | `WebhookDeliveryProcessor` (`customers.EnsureAsync`) | Todo entrante ya asegura un cliente por BSUID; una conversación sin cliente es la excepción (falla al asegurarlo, conversación vieja) |
| Códigos de traspaso | `AgentHandoffCodes.All`, `HandoffCauseKindParityTests`, `AutomationHandoff.IsValidCode` (en `Messaging.Domain/ConversationAutomation.cs`) | Un código nuevo entra a la lista y pasa la limpieza de Mensajería |
| Remitente del recordatorio | `AppointmentReminderProcessor.SenderOfAsync` | Miembro vinculado al recurso → último actor activo del historial → si ninguno, omite con `no_sender_member` |
| Validación de miembros | `ISchedulingMemberDirectory.FindAsync` | Devuelve las membresías del tenant pedidas con `IsActive`; la de otro tenant no aparece |

## 3. Restricciones

- **Capas:** Ai no referencia Scheduling, Messaging ni Customers (`AiLayerTests`); Scheduling no referencia Ai ni
  Messaging (`SchedulingLayerTests`). Todo cruce va por un puerto con adaptador en Bootstrapper.
- **Anthropic estricto:** a lo sumo 20 herramientas `strict` por request, 24 parámetros opcionales y 16 uniones
  (`["string","null"]` cuenta) sumando todas (regla `ai-copiloto`). Hoy el agente manda 3 + 3 finales y 3 uniones. Con
  este spec: 9 + 3 finales = **12** herramientas y **8** uniones, sin opcionales (en `strict` todo va en `required`).
- **El tenant y el cliente nunca salen de un argumento del modelo** (D-A30).
- **El texto del modelo no decide reglas:** todo lo que el prompt dice sobre la Agenda es guía; si el modelo la ignora, el
  backend responde con un código.
- **Presupuesto de pruebas del workspace:** RED de compilación sin contenedores, integración filtrada por nombre mientras se
  itera, una sola corrida de Testcontainers a la vez, suite de los proyectos tocados sólo antes de mergear (§11).

## 4. Decisiones

Las D-1 a D-8 y D-14 a D-19 son del owner y **no se reabren** (las D-14 a D-19 cierran las preguntas P-1 a P-6 el
2026-11-23). Las D-9 a D-13 son de este spec.

| # | Decisión | Fuente |
| --- | --- | --- |
| D-1 | Seis herramientas detrás de un interruptor nuevo `config.tools.scheduling` (por defecto `false`); los documentos guardados antes se siguen leyendo | owner |
| D-2 | Reglas en el backend: sólo citas del cliente de la conversación; una cita nace con `Source = Channel` y `ConversationId`, según `confirmationMode`; cancelar usa `Source = Channel` para que rija `clientCancelCutoffHours`; sólo servicios con `bookableByChannel`; anticipación, horizonte y disponibilidad del mismo motor que el back office; sigue la regla de una herramienta que escribe por respuesta | owner |
| D-3 | Cuando el plazo bloquea una cancelación (o cualquier `invalid_transition`), el resultado de la herramienta termina el turno con un traspaso a una persona, con el mecanismo determinista del borrador de cotización | owner |
| D-4 | Llave de idempotencia de la reserva derivada del trabajo y de la llamada; la forma exacta la fija D-14 | owner |
| D-5 | Autorización: el núcleo de disponibilidad, reserva, cancelación y cambio de hora se extrae a servicios internos de Scheduling que usan los handlers (después de autorizar) y un puerto nuevo para el agente; Ai declara su puerto (patrón de `IAiCatalogSearch`) con adaptador en Bootstrapper; **no** se corren los handlers bajo un principal sintético. Capacidades nuevas en `AiServicePrincipal` | owner |
| D-6 | Contacto sin cliente: la reserva crea el cliente con el nombre y el teléfono del contacto y lo cuelga de la conversación (ver §7.6: se hace con `ConversationCustomerResolver`) | owner |
| D-7 | Playground: las tres que escriben se simulan como `SimulatedQuotationDraftTool` (validan, devuelven datos ficticios, no escriben); las lecturas leen datos reales, y las citas sólo del cliente de prueba si la prueba fijó uno | owner |
| D-8 | Auditoría a nombre del agente (sin miembro), con nombre de acción claro; el historial de la cita queda con la semántica «Cliente por WhatsApp» que ya existe (`Source = Channel`, actor nulo) | owner |
| D-9 | En cada llamada del canal real, el adaptador **relee** el cliente de la conversación (por `ConversationId`) en vez de usar el del inicio del trabajo: una reserva que crea el cliente lo deja visible para la herramienta siguiente del mismo trabajo, y una fusión en el medio no apunta a un cliente inactivo. Mismo criterio que `AutomationDataConsents` | spec |
| D-10 | Las horas viajan **en hora local del tenant** con formato fijo `yyyy-MM-ddTHH:mm`, sin desfase, y una sola `timeZone` por resultado. La conversión a UTC la hace Scheduling con la zona de `ITenantClock`, la misma que el prompt ya muestra | spec |
| D-11 | En el camino del agente rige `allowClientChoice` de la franja: los recursos se muestran y se aceptan sólo en los inicios cuya opción lo permite. El back office no cambia (plan, corrección 17). Confirmada por D-16 | spec |
| D-12 | Repetir una cancelación ya hecha o un cambio a la misma hora en el **mismo** trabajo responde éxito (`alreadyDone: true`), no `invalid_transition`: un trabajo retomado no debe traspasar por su propia escritura anterior | spec |
| D-13 | El módulo `scheduling` apagado no quita las herramientas del modelo (el perfil no conoce los módulos); responden `scheduling_unavailable`. Publicar con `tools.scheduling` y el módulo apagado es error del validador | spec |
| D-14 | (P-1) Llave de la reserva: `ai:{jobId:N}:book:{n}`, con `n` el ordinal de la reserva dentro del trabajo, 1 + las llamadas a `book_appointment` del mismo trabajo que ya devolvieron una cita (nueva o repetida). Una llamada que falla no consume número: así un trabajo retomado cuyo primer intento sale distinto (el modelo elige otra hora porque la primera ya no está) sigue encontrando la cita que reservó antes con `n = 1`, y el resultado `alreadyBooked: true` describe esa cita. Lo cuenta en memoria el perfil del agente, que es uno por corrida del trabajo (§7.3) | owner |
| D-15 | (P-2) El cambio de hora por el agente respeta `clientCancelCutoffHours` (`byClient: true` en `AppointmentRescheduling`); fuera del plazo es `invalid_transition` y la herramienta traspasa con `appointment_change_blocked`. Además, mover una cita por chat exige que su servicio tenga `bookableByChannel`; si no, también `invalid_transition` con traspaso (una persona la mueve desde la agenda). Cancelar no exige `bookableByChannel` (D-2 no lo pide) | owner |
| D-16 | (P-3) `allowClientChoice` se aplica sólo en el camino del agente, con el código nuevo `scheduling.appointment.resource_not_choosable` (422, `SchedulingDomainException`). El back office sigue igual | owner |
| D-17 | (P-4) Columna `scheduling.appointment_events.by_channel` (`boolean NOT NULL DEFAULT false`, migración expand) expuesta como `AppointmentEventDto.ByChannel`. La ponen en `true` **sólo** las escrituras del agente (crear, cancelar, cambiar hora por `IChannelScheduling`); ninguna escritura por HTTP la pone, tampoco una con `source: Channel` (ningún cliente la manda hoy); el recordatorio tampoco. Las filas que ya existen quedan en `false`. El frontend pinta «Cliente por WhatsApp» con ella en vez de deducirlo del origen de la cita | owner |
| D-18 | (P-5) Las reglas de la Agenda ganan `reminderSenderMemberId` (`Guid?`, id de **membresía**) en `SettingsRequest` y `SettingsDto` (este además con `reminderSenderMemberName`, el nombre ya resuelto, también de un miembro suspendido; no viaja en el request), guardado en `scheduling.settings.reminder_sender_member_id` (nullable, migración expand). Se valida contra los miembros activos del tenant con `ISchedulingMemberDirectory`; uno inexistente, de otro tenant o inactivo es 422 con el campo `reminderSenderMemberId`. Se guarda aunque el recordatorio esté apagado; sólo se usa al enviar. Remitente del recordatorio: miembro activo vinculado al recurso → último actor activo de la cita → `reminderSenderMemberId` si está activo → si no, se omite con `no_sender_member` como hoy (§6.4) | owner |
| D-19 | (P-6) El traspaso de `appointment_change_blocked` (y de `appointment_customer_unavailable`) va al destino configurado del agente (`MemberId: null`) | owner |

## 5. Las herramientas (lo que ve el modelo)

Todas `strict` (por defecto en `AiToolDefinition`), con todos los campos en `required`. Las descripciones van en español,
como las existentes. Los argumentos se revalidan en la herramienta con el mismo rigor que un cuerpo HTTP (D-A30); un
argumento malo es `invalid_arguments`.

### 5.1 `list_bookable_services` — capacidad `scheduling.read`

- **Descripción:** «Lista los servicios de la agenda del negocio que el cliente puede reservar por este chat, con su
  duración.»
- **Argumentos:** `{"type":"object","properties":{},"required":[],"additionalProperties":false}`
- **Resultado:**

```json
{ "timeZone": "America/Bogota",
  "services": [ { "serviceId": "0b9c…", "name": "Consulta general", "durationMinutes": 30 } ],
  "truncated": false }
```

Sólo servicios activos con `bookableByChannel` que alguna franja activa ofrece con algún recurso activo. Tope 20, por
nombre; `truncated` si hay más. Lista vacía no es error.

### 5.2 `check_availability` — capacidad `scheduling.read`

- **Descripción:** «Consulta los horarios libres de un servicio entre dos días (hora local del negocio). Ofrece al cliente
  sólo horarios que devuelva esta herramienta.»
- **Argumentos:**

```json
{"type":"object","properties":{
  "serviceId":{"type":"string","description":"El serviceId que devolvió list_bookable_services."},
  "fromDate":{"type":"string","description":"Primer día, yyyy-MM-dd, en la fecha del negocio."},
  "toDate":{"type":"string","description":"Último día, yyyy-MM-dd, inclusive. Máximo 14 días desde fromDate."},
  "resourceId":{"type":["string","null"],"description":"Con quién, si el cliente eligió uno de los que devolvió esta herramienta; null para cualquiera."}},
 "required":["serviceId","fromDate","toDate","resourceId"],"additionalProperties":false}
```

- **Resultado:**

```json
{ "timeZone": "America/Bogota", "serviceId": "0b9c…",
  "slots": [ { "startsAt": "2026-11-24T09:00", "endsAt": "2026-11-24T09:30", "seatsLeft": 1,
               "resources": [ { "resourceId": "5e1a…", "name": "Dra. Pérez" } ] } ],
  "truncated": false }
```

Hasta 30 inicios, los más próximos primero; `truncated` si hay más. `resources` va vacío en un inicio cuya franja no
deja elegir (D-11). Un rango de más de 14 días o al revés es `invalid_arguments`; uno fuera del horizonte devuelve
`slots: []` (el motor ya lo recorta). Un servicio archivado, de otro tenant o sin `bookableByChannel` es
`service_not_found`; un `resourceId` que no se puede elegir, `resource_not_choosable`.

### 5.3 `book_appointment` — capacidad `scheduling.book_own` — **escribe**

- **Descripción:** «Reserva una cita para el cliente de esta conversación en un horario que devolvió check_availability.
  Confirma antes con el cliente el servicio, el día y la hora.»
- **Argumentos:**

```json
{"type":"object","properties":{
  "serviceId":{"type":"string","description":"El serviceId del servicio."},
  "startsAt":{"type":"string","description":"Inicio, yyyy-MM-ddTHH:mm en hora local del negocio, tal como lo devolvió check_availability."},
  "resourceId":{"type":["string","null"],"description":"Con quién, sólo si el cliente eligió uno de los que ofreció check_availability; null para el primero libre."},
  "note":{"type":["string","null"],"description":"Nota para el equipo, máximo 500 caracteres. Nunca datos sensibles."}},
 "required":["serviceId","startsAt","resourceId","note"],"additionalProperties":false}
```

- **Resultado:**

```json
{ "appointmentId": "7f3d…", "status": "Pending", "startsAt": "2026-11-24T09:00", "endsAt": "2026-11-24T09:30",
  "timeZone": "America/Bogota", "serviceName": "Consulta general", "resourceName": "Dra. Pérez",
  "locationName": "Sede principal", "alreadyBooked": false }
```

`status` es `Pending` o `Confirmed` según `confirmationMode`. `alreadyBooked: true` cuando la llave ya tenía una cita:
el resultado describe **esa** cita (la verdad), aunque los argumentos de esta llamada sean otros. No termina el turno: el
modelo sigue y le confirma al cliente con `send_reply`. Nota: 500 en vez de los 1000 del cuerpo HTTP, como la nota del
borrador de cotización.

### 5.4 `list_my_appointments` — capacidad `scheduling.read_own`

- **Descripción:** «Lista las próximas citas del cliente de esta conversación (pendientes y confirmadas).»
- **Argumentos:** `{"type":"object","properties":{},"required":[],"additionalProperties":false}`
- **Resultado:**

```json
{ "timeZone": "America/Bogota",
  "appointments": [ { "appointmentId": "7f3d…", "status": "Confirmed", "startsAt": "2026-11-24T09:00",
                      "endsAt": "2026-11-24T09:30", "serviceId": "1b2c…", "serviceName": "Consulta general", "resourceName": "Dra. Pérez",
                      "locationName": "Sede principal", "canChangeByChat": true, "canRescheduleByChat": true,
                      "changeByChatUntil": "2026-11-23T09:00" } ],
  "truncated": false }
```

Citas `Pending` o `Confirmed` del cliente que empiezan desde ahora, por inicio, tope 10. `canChangeByChat` y
`changeByChatUntil` (inicio menos `clientCancelCutoffHours`) los calcula el backend con `AppointmentCancellationRules`:
el modelo no hace aritmética de plazos. `canRescheduleByChat` es `canChangeByChat` y además el servicio de la cita con
`bookableByChannel` (D-15). Sin cliente en la conversación responde `appointments: []`, no un error (un
contacto sin ficha no tiene citas).

### 5.5 `cancel_appointment` — capacidad `scheduling.change_own` — **escribe**

- **Descripción:** «Cancela una cita del cliente de esta conversación. Confirma antes con el cliente cuál cita.»
- **Argumentos:** `appointmentId` (string, de `list_my_appointments`), `reason` (`["string","null"]`, máximo 500).
- **Resultado:** `{ "appointmentId": "7f3d…", "status": "Cancelled", "alreadyDone": false }`.
- Cita de otro cliente o inexistente: `appointment_not_found` (misma respuesta, como `get_order_status`). Plazo vencido o
  estado cerrado: `appointment_change_blocked` **con traspaso** (D-3, §7.5).

### 5.6 `reschedule_appointment` — capacidad `scheduling.change_own` — **escribe**

- **Descripción:** «Cambia la hora de una cita del cliente de esta conversación a un horario que devolvió
  check_availability para el mismo servicio.»
- **Argumentos:** `appointmentId` (string), `startsAt` (string, local), `resourceId` (`["string","null"]`).
- **Resultado:** la misma forma de `book_appointment` sin `alreadyBooked`, con `alreadyDone`.
- Errores: los de reservar (`slot_taken`, `session_full`, `outside_rules`, `resource_not_choosable`) vuelven al modelo;
  `appointment_change_blocked` traspasa: plazo vencido, cita cerrada o servicio sin `bookableByChannel` (D-15). El cambio
  de hora sólo ofrece recursos de la misma sede (corrección 22c, ya en el núcleo).

### 5.7 Lo que cambia en el contrato común

- `AgentToolNames`: las seis constantes. `Writes(name)` pasa a ser `create_quotation_draft`, `book_appointment`,
  `cancel_appointment` y `reschedule_appointment`: la regla de una escritura por respuesta las cubre sin tocar el bucle.
- `AgentToolInvocation` gana `AgentId`, `ConversationId` (nulo en el playground; ya están en `AgentTurnContext`) y
  `BookingOrdinal` (el `n` de D-14), como parámetros opcionales al final para no tocar las llamadas existentes.
- `AiServicePrincipal.Capabilities` gana `scheduling.read`, `scheduling.read_own`, `scheduling.book_own` y
  `scheduling.change_own` (mismo estilo que `orders.read_own`; no son permisos de Authorization y no necesitan política).
- `AgentToolErrors` gana los códigos de §8.
- `AgentHandoffCodes` gana `appointment_change_blocked` y `appointment_customer_unavailable` (y `All` los lista).

## 6. Scheduling: extracción del núcleo

Hoy cada handler mezcla «quién pide» (permisos, `If-Match`, alcance del miembro, miembro por `SubjectId`) con «qué pasa»
(motor, candado, sesión, cupo, historial, auditoría). Se separan; las respuestas HTTP **no cambian** y las pruebas de API
existentes son la red.

### 6.1 Lo que se queda en los handlers (sólo endpoints)

`SchedulingAuthorization.EnsureAsync`, los validadores de FluentValidation, `ResourceScopeResolver`,
`AppointmentTransitions.LoadAsync` (`If-Match`, alcance, versión), la búsqueda del miembro por `SubjectId` y el armado del
`AppointmentDto` (`AppointmentComposer`). `ConfirmAppointmentHandler`, `MarkAttendedHandler` y `MarkNoShowHandler` no se
tocan.

### 6.2 Núcleo nuevo (clases públicas de `Modules.Scheduling.Application`, registradas junto a `AvailabilityLoader`)

| Clase | Sale de | Firma (idea) |
| --- | --- | --- |
| `SchedulingActor` | — | Quién escribe: `HistoryMemberId` (nulo en canal), `ByChannel` (D-17: `true` sólo en `Agent`) y el actor de auditoría, `Human(subjectId, memberId)` o `Agent(agentId)` |
| `AvailabilitySearch` | `ListAvailabilityHandler` desde `loader.LoadAsync` | `SearchAsync(tenantId, scope, service, from, to, onlyResource)` → zona + inicios con sus `SlotOption` (incluido `AllowClientChoice`). El handler lo mapea a `AvailabilityDto` como hoy |
| `AppointmentBooking` | `BookAppointmentHandler` desde la repetición por `clientRequestId` | `BookAsync(BookingInput, scope, actor)` → `(Appointment, Replayed)`. Mantiene el doble chequeo de la llave, `bookableByChannel`, el candado, `PickFree`, `TakeSeatAsync`, el estado por `confirmationMode` y la auditoría |
| `AppointmentCancellation` | `CancelAppointmentHandler` desde el plazo | `CancelAsync(tenantId, appointment, reason, byClient, actor)` |
| `AppointmentRescheduling` | `RescheduleAppointmentHandler` desde el chequeo de estado | `RescheduleAsync(tenantId, appointment, scope, resourceId, startsAt, actor, byClient)` |

`SchedulingAudit.Publish` y `AppointmentTransitions.SaveAsync` reciben el `SchedulingActor`. `ISchedulingAuditPublisher` gana
`PublishByAgent(tenantId, agentId, action, resourceType, resourceId, outcome, changedFields, occurredAt)`, que
`SchedulingAuditPublisher` escribe con `AuditActorType.AiAgent` y el id del agente como actor (precedente:
`AiAuditRecorder.RecordByAgent`). Las acciones son las de siempre (`scheduling.appointment.created`, `…cancelled`,
`…rescheduled`): el tipo de actor `AiAgent` es lo que dice que fue el agente (D-8).

`byClient` en el cambio de hora decide si rige el plazo y si se exige `bookableByChannel` (D-15); el handler HTTP pasa
`false`, como hoy. El dominio recibe `byChannel` en `Appointment.Create`, `Cancel` y `Reschedule` (parámetro opcional,
`false` por defecto) y lo copia a `AppointmentEvent.ByChannel` (D-17); `AppointmentComposer` lo mapea a
`AppointmentEventDto.ByChannel`.

### 6.3 Servicio sin sesión para el agente

`IChannelScheduling` (interfaz pública en `Modules.Scheduling.Application`, implementación `ChannelScheduling`), con la
doctrina de `IExternalQuotationWriter`: «el único camino para operar la Agenda sin sesión; un módulo consumidor declara su
propio puerto y su adaptador en Bootstrapper llama a éste».

- Cada método: `TenantModuleGuard.EnsureEnabledAsync(…, TenantModuleKeys.Scheduling, …)` → `SchedulingFirstAccess.EnsureAsync`
  → `ResourceScope.All` (el agente atiende la agenda entera del tenant; el alcance por miembro no aplica a un cliente).
- `ListBookableServicesAsync`, `SearchAvailabilityAsync` (rechaza servicios sin `bookableByChannel`), `BookAsync`
  (`Source = Channel`, `ConversationId`, `ClientRequestId`, actor `Agent`), `ListCustomerAppointmentsAsync`,
  `CancelAsync` (`byClient: true`), `RescheduleAsync`. Cancelar y cambiar hora cargan la cita con
  `IAppointmentRepository.FindAsync`, **sin** `If-Match`, y exigen `appointment.CustomerId == customerId`; si no, el mismo
  `scheduling.appointment.not_found` que una cita que no existe.
- D-11 y D-16: con `resourceId` pedido, la opción de ese recurso en ese inicio tiene que tener `AllowClientChoice`; si
  no, `SchedulingErrorCodes.ResourceNotChoosable` = `scheduling.appointment.resource_not_choosable` (422; el adaptador lo
  lee como `resource_not_choosable`). En la búsqueda, un `resourceId` que ninguna opción del rango deja elegir responde
  el mismo código.
- Lecturas para el playground (D-7): `CheckStartAsync` (lo que comprobaría la reserva, sin candado ni escritura: servicio
  reservable, inicio ofrecido por el motor con ocupación, recurso elegible, y el estado que daría `confirmationMode`) y
  `FindCustomerAppointmentAsync` (una cita del cliente con sus banderas de §5.4, o nada si no es suya).
- Las horas entran como `DateTime` local sin zona y salen como `DateTimeOffset` en la zona del tenant (D-10). Una hora
  local que no existe en la zona (cambio de horario) es `outside_rules`.
- Lectura nueva: `IAppointmentReadRepository.ListUpcomingByCustomerAsync(tenantId, customerId, now, take)` (sin
  seguimiento, `Pending`/`Confirmed`, `starts_at >= now`, por inicio). No hay migración: `appointments.customer_id` ya
  existe; se mira el plan de ejecución antes de decidir un índice `(tenant_id, customer_id, starts_at)`.
- Lectura nueva para el editor: `GetReadinessAsync(tenantId)` → módulo prendido y cuántos servicios activos con
  `bookableByChannel` ofrece alguna franja no archivada con algún recurso activo
  (`IAvailabilityBlockRepository.ListAsync` con `ResourceScope.All`, `ServiceIds`, `ResourceIds`).

### 6.4 Reglas de la Agenda: remitente por defecto del recordatorio (D-18)

- **Dominio:** `SchedulingSettings.ReminderSenderMemberId` (`Guid?`). `Update` lo recibe como parámetro aparte y lo
  guarda **siempre**, con el recordatorio prendido o apagado (a diferencia de `ReminderConnectionId`, que se suelta al
  apagar).
- **Contrato:** `SettingsRequest.ReminderSenderMemberId` (`Guid?`, al final) y, en `SettingsDto`,
  `ReminderSenderMemberId` (`Guid?`) y `ReminderSenderMemberName` (`string?`). El nombre lo resuelve el backend con
  `ISchedulingMemberDirectory.FindAsync`, como `ResourceDto.MemberName`: la pantalla no consulta Personas. Es `null` sin
  remitente; con un remitente que después quedó suspendido **se sigue devolviendo** su nombre (el directorio devuelve
  activos e inactivos), para que la pantalla lo nombre. El nombre no viaja en `SettingsRequest`.
- **Validación (`UpdateSettingsHandler`):** con valor, `ISchedulingMemberDirectory.FindAsync(tenantId, [id])` tiene que
  devolverlo con `IsActive`; si no (inexistente, de otro tenant, inactivo), `SchedulingFieldErrors.Throw(
  "reminderSenderMemberId", "Elige un miembro activo de la organización.")` → 422 `validation.failed` con el campo. Un
  `PUT` que deja el mismo id de un miembro que ya se suspendió también es 422: el formulario obliga a elegir otro o
  vaciarlo. Cambiarlo agrega `reminderSenderMemberId` a los campos de la auditoría `scheduling.settings.updated`.
- **Envío (`AppointmentReminderProcessor.SenderOfAsync`):** el valor de la fila entra **último** a la lista de
  candidatos (después del miembro del recurso y de los actores del historial), con el mismo filtro de activos. Ninguno
  activo sigue siendo `no_sender_member`.
- **Reglas de la revisión del Bloque 1 (2026-11-23).**
  1. **Cancelar y cambiar hora son seguros ante una carrera (D-12).** El estado de la cita se lee antes del candado, así que el núcleo la
     relee **bajo** el candado (`IAppointmentRepository.RefreshAsync`): una cita que otra petición cerró devuelve `invalid_transition` en
     vez de liberar un cupo ya libre (`InvalidOperationException`, o un descuento doble). `ChannelScheduling` lee la cita otra vez
     (`FindFreshAsync`) y responde `alreadyDone` si lo que se escribió en medio es justo lo pedido (cita ya cancelada; cita ya en ese
     inicio, `AppointmentRescheduling` devuelve `false` sin escribir); cualquier otra cosa sube como vino. Una pérdida de versión al
     guardar (`RequestConcurrencyException`) sigue el mismo camino y, si no es lo pedido, el adaptador la lee como `appointment_changed`.
     Un cambio de hora o una cancelación sobre una cita que otro ya movió se aplican a la cita tal como está ahora.
  2. **Una llave repetida nunca describe la cita de otro cliente.** Con la llave ya usada, si `CustomerId` de la cita guardada no es el de
     la reserva (también en el camino de `slot_taken` por la carrera de la llave) es un 422 `validation.failed` con el campo
     `clientRequestId`, sin datos de la cita guardada; el adaptador de Ai lo lee como `scheduling_unavailable`. El mismo cliente con otro
     servicio u hora sigue recibiendo la cita guardada (`alreadyBooked: true`): es la verdad que el modelo debe leer.
  3. **`reminderSenderMemberId` en el `PUT`.** El formulario manda las reglas enteras: `null` (o el campo ausente) **borra** el
     remitente, así que el cliente tiene que devolverlo tal como lo leyó. Un id igual al guardado no se valida otra vez (un remitente
     suspendido después de guardarlo no bloquea editar otra regla); un id distinto sí tiene que ser un miembro activo.
- **Persistencia:** `scheduling.settings.reminder_sender_member_id uuid NULL`, sin llave foránea (las membresías son de
  Tenancy), en la misma migración expand que `by_channel` (§12).

## 7. Ai: puerto, herramientas y adaptador

### 7.1 Puerto

`IAiScheduling` en `Modules.Ai.Application` (archivo nuevo `AgentSchedulingTools.cs`, junto a sus registros `AiScheduling…`).
Métodos con los mismos verbos de §6.3, que reciben `AiSchedulingCaller(TenantId, AgentId, JobId, ConversationId?,
CustomerId?, IsTest)` y devuelven un `AiSchedulingResult<T>(T? Value, string? ErrorCode, string? HandoffCode)`.

### 7.2 Adaptador (`Bootstrapper/AiScheduling.cs`)

Calco de `AiQuotationDrafts`: llama a `IChannelScheduling` y traduce excepciones a códigos para el modelo (tabla de §8).
Resuelve el cliente:

- **Canal real (`ConversationId` presente):** relee la conversación con `IConversationQueries.FindAsync` (D-9). Para
  `book_appointment` usa `ConversationCustomerResolver.ResolveAsync` (crea y cuelga si falta, D-6); para las demás, sólo
  `row.CustomerId`.
- **Playground (`ConversationId` nulo):** el `CustomerId` de la prueba, nunca otro.

### 7.3 Idempotencia de la reserva (D-4)

`ClientRequestId` (tope 100, índice único parcial ya creado por `AddAppointmentClientRequestId`).

**Nota sobre D-4.** La llave «trabajo + id de la llamada» repite el bug que D-A16 corrigió en el borrador de cotización:
el id de la llamada lo inventa el modelo y **cambia** cuando un trabajo se retoma (lease vencido, proceso caído antes de
`ModelJob.RememberResult`), así que el reintento reservaría **otro cupo** con otra llave (regla `ai-copiloto`, «El borrador
de cotización… no se duplica por el índice»). La llave sólo de trabajo (`ai:{jobId}`, la del borrador) no sirve tampoco:
un cliente que pide dos citas en el mismo turno recibiría la primera dos veces. **Decisión (D-14):**
`ai:{jobId:N}:book:{n}`, donde `n` es el ordinal de la reserva **dentro del trabajo**: `AgentProfile` (uno por corrida del
trabajo) guarda cuántas llamadas a `book_appointment` devolvieron una cita y pasa `n = esas + 1` en
`AgentToolInvocation.BookingOrdinal`. Un trabajo retomado vuelve a contar desde 1 y encuentra sus citas; una segunda cita
del mismo turno lleva `n = 2`; una llamada con error no consume número. El resultado con `alreadyBooked: true` describe
la cita guardada, así el modelo nunca le confirma al cliente una hora distinta de la reservada.

**Carrera con la misma llave:** dos reservas con la misma llave sobre recursos distintos chocan en el índice único y
`SchedulingUnitOfWork` responde `slot_taken`. En el camino del agente, `ChannelScheduling.BookAsync` busca la llave una vez
más al recibir ese 409 y, si ya hay cita, la devuelve como repetida (`alreadyBooked: true`) en vez del error.

`cancel_appointment` y `reschedule_appointment` no llevan llave: D-12 los hace seguros al repetirse (cita ya cancelada →
`alreadyDone`; cambio a la hora que ya tiene → `alreadyDone`).

### 7.4 Una escritura por respuesta

Se conserva sin cambios en el bucle (D-2): las tres escrituras entran a `AgentToolNames.Writes`. Un flujo típico cabe en los
topes de `Ai:Model`: servicios → disponibilidad → reservar → `send_reply` son 3 herramientas y 4 llamadas al modelo (tope
8 y 6); cambiar hora con «mis citas» son 3 herramientas y 4 llamadas.

### 7.5 Traspaso determinista (D-3)

Con el mismo `AgentToolResult.Handoff` de `CreateQuotationDraftTool` (termina el turno sin otra vuelta del modelo; las
demás llamadas de la vuelta ya corrieron):

| Caso | Error al modelo (queda en `ai.tool_calls`) | Código del traspaso | A quién |
| --- | --- | --- | --- |
| Cancelar o cambiar hora con el plazo vencido, una cita cerrada, o cambiar la hora de una cita cuyo servicio no tiene `bookableByChannel` (`invalid_transition`, D-15) | `appointment_change_blocked` | `appointment_change_blocked` | Destino configurado del agente (`MemberId: null`, D-19) |
| Reservar sin cliente posible (`ConversationCustomer.NoUser` o `Unavailable`) o con el cliente inactivo | `customer_unknown` | `appointment_customer_unavailable` | Destino configurado (D-19) |

El evento del hilo los muestra con `HandoffCauseKind.AgentRequested` y el código, como `quotation_customer_not_ready`.

### 7.6 Contacto sin cliente (D-6)

Se cumple la intención de D-6 con `ConversationCustomerResolver`, no con `SchedulingCustomerLookup.CreateAsync`:

- La ingesta ya asegura un cliente por BSUID en cada entrante (`WebhookDeliveryProcessor`), así que el caso es raro.
- El resolvedor crea la ficha **incompleta** con el nombre de perfil y el `waId` del contacto
  (`IMessagingCustomerDirectory.EnsureAsync`, idempotente por BSUID), la cuelga con `LinkCustomerAsync` y avisa a la
  bandeja: no duplica por un teléfono que ya tiene otra ficha con el mismo BSUID, y es el camino que ya usan «Política de
  datos» y «Acción de CRM».
- `SchedulingCustomerLookup.CreateAsync` firma el alta con `executionContext.SubjectId`, que el agente no tiene, y no
  cuelga la ficha de la conversación.
- Consentimiento: agendar no registra ni exige consentimiento de datos; el recordatorio ya omite con `consent_revoked`.
- Instagram: el resolvedor también cubre la conversación de Instagram; la ficha queda sin teléfono y la cita sin
  recordatorio (la Agenda ya lo tolera).

### 7.7 Playground (D-7)

| Herramienta | En una prueba |
| --- | --- |
| `list_bookable_services`, `check_availability` | Datos reales del tenant (sólo lectura) |
| `list_my_appointments` | Las del cliente de la prueba; sin cliente fijado, `appointments: []` |
| `book_appointment` (simulada) | Valida argumentos y, con una lectura, que el inicio lo ofrezca el motor (`outside_rules`, `service_not_found`, `resource_not_choosable` como la real). Devuelve `appointmentId: "PRUEBA-XXXXXXXX"` (8 primeros del job, como el borrador) y el estado que daría `confirmationMode`. No escribe, no crea cliente |
| `cancel_appointment`, `reschedule_appointment` (simuladas) | Un id `PRUEBA-…` se da por encontrado. Un id real tiene que ser de una cita del cliente de prueba; el plazo se evalúa con una lectura y, si bloquea, termina con el mismo traspaso que en el canal. No escriben |

Las simuladas comparten con las reales las definiciones (`…Arguments.Definition`) y la validación de argumentos, como
`QuotationDraftArguments`. `IsOfferedTo`: las reales con `!isTest && tools.Scheduling`, las simuladas con
`isTest && tools.Scheduling`, las de lectura con `tools.Scheduling`.

### 7.8 Prompt

`AgentPromptBuilder.SystemPromptOf` agrega una sección fija **sólo** con `tools.scheduling` (no cambia por llamada, así que
respeta el punto de caché de C-12). Es contrato de prompt; el texto propuesto:

```
## Agenda
- Las horas de la agenda están en la hora local del negocio (la fecha y la zona que ves arriba). Escribe al cliente días y horas en esa hora local, nunca en UTC.
- Nunca ofrezcas ni confirmes un horario que no haya devuelto check_availability en este turno. Si no hay horarios, dilo y ofrece otro rango o pasar con una persona.
- Antes de reservar, cancelar o cambiar una cita, confirma con el cliente el servicio, el día y la hora (o cuál cita).
- Si la reserva queda en estado Pending, dile que el negocio la confirmará; no digas que ya está confirmada.
- Sólo ves y cambias las citas de este cliente. Si pide algo de otra persona, ofrece pasar con alguien del equipo.
- Si una herramienta responde slot_taken o session_full, vuelve a consultar la disponibilidad antes de proponer otro horario.
```

La fecha y la zona ya están en `VolatileContext`; no se repiten.

## 8. Errores

El adaptador traduce; el modelo sólo ve el código corto. `ai.tool_calls.error_code` guarda el mismo.

| Origen (Scheduling) | Código al modelo | ¿Traspasa? | Qué hace el modelo |
| --- | --- | --- | --- |
| Módulo `scheduling` apagado (`TenantModuleGuard`) | `scheduling_unavailable` | No | Dice que no puede agendar y ofrece `handoff_to_human` |
| `ResourceConflictException` `scheduling.appointment.slot_taken` | `slot_taken` | No | Vuelve a consultar disponibilidad |
| `ResourceConflictException` `scheduling.appointment.session_full` | `session_full` | No | Igual |
| `scheduling.appointment.outside_rules` (inicio que las reglas no ofrecen, servicio no reservable por canal, hora local inexistente) | `outside_rules` | No | Igual |
| `scheduling.appointment.invalid_transition` (plazo, cita cerrada, servicio sin `bookableByChannel` al cambiar hora) | `appointment_change_blocked` | **Sí** (§7.5) | — |
| `scheduling.appointment.before_start` | `tool_failed` | No | No es alcanzable por estas herramientas (sólo asistencia y «no asistió»); se traduce a falla genérica por defensa |
| `scheduling.appointment.not_found`, cita de otro cliente | `appointment_not_found` | No | Relee `list_my_appointments` |
| `scheduling.service.not_found`, archivado o sin `bookableByChannel` (`ValidationException` del campo `serviceId`) | `service_not_found` | No | Relee `list_bookable_services` |
| `scheduling.resource.not_found` o archivado | `resource_not_choosable` | No | Reintenta sin recurso |
| `scheduling.appointment.resource_not_choosable` (D-16) | `resource_not_choosable` | No | Igual |
| `ResourceConflictException` `slot_taken` por la llave repetida (índice `IX_appointments_client_request`) | — (se devuelve la cita con `alreadyBooked: true`, §7.3) | No | — |
| Cliente inactivo (`ValidationException` del campo `customerId`) | `customer_unknown` | **Sí** (`appointment_customer_unavailable`) | — |
| Sin cliente posible (resolvedor) | `customer_unknown` | **Sí** (book) / No, lista vacía (list) / `appointment_not_found` (cancel, reschedule) | — |
| `RequestConcurrencyException` al guardar | `appointment_changed` | No | Relee y reintenta una vez |
| Argumentos inválidos | `invalid_arguments` | No | Corrige |
| Segunda escritura en la misma respuesta | `one_write_per_response` | No | (existente) |
| Cualquier otra excepción | `tool_failed` | No | (existente; detalle al log, nunca al modelo) |

## 9. Configuración, validador y editor

### 9.1 `config.tools.scheduling`

`AgentTools(bool Catalog, bool OrderStatus, bool QuotationDraft, bool Scheduling = false)`. `AgentConfig` se lee con
`System.Text.Json` (`JsonSerializerDefaults.Web`): un documento sin la propiedad toma el valor por defecto del parámetro, así
que borradores y versiones publicadas antes del cambio se leen con `false` (D-1). `AgentConfig.Default` lo pone en
`false`. Una prueba amarra la lectura de un JSON sin la clave (en `AgentExportTests`, que ya lee documentos guardados).

### 9.2 Validador

`AgentValidationContext` gana `AgentSchedulingReadiness? Scheduling` (nulo con la herramienta apagada), que arma
`AgentEditorBuilder.ValidateAsync` desde `IAiScheduling.GetReadinessAsync`. `AgentConfigValidator`:

| Código | Severidad | Ruta | Mensaje | Cuándo |
| --- | --- | --- | --- | --- |
| `scheduling_module_disabled` | Error | `tools.scheduling` | «Activa el módulo Agenda para que el agente pueda agendar citas.» | Herramienta prendida, módulo apagado |
| `scheduling_no_bookable_service` | Error | `tools.scheduling` | «Ningún servicio de la Agenda se puede reservar por WhatsApp en una franja activa: marca uno como «Se puede reservar por WhatsApp» y ofrécelo en una franja.» | Prendida y la cuenta de §6.3 en cero |

Como todo el validador (D-A7): guardar nunca falla por ellos, publicar sí. Que un servicio deje de ser reservable después de
publicar no detiene al agente: `list_bookable_services` responde vacío y el modelo lo dice. La etiqueta citada es la del
diálogo de servicio del frontend (`service-dialog.tsx`).

### 9.3 Editor

`AgentEditorDto` gana `Scheduling` (`AgentSchedulingReadinessDto`, siempre presente desde este backend): ver §14.

### 9.4 Exportar e importar

`AgentExport.Portable` no cambia: `tools.scheduling` es un booleano sin ids del tenant y viaja tal cual. `schema` sigue en
`1`: un archivo viejo sin la clave se importa con `false` (§9.1); uno con `true` en un tenant sin la Agenda se importa y el
panel de problemas muestra `scheduling_module_disabled`. `ImportAgentValidator` no cambia.

### 9.5 Actividad del día

`AgentTodayDto` gana `Appointments`: reservas que el agente hizo hoy (llamadas `book_appointment` con `ok` y
`alreadyBooked: false`, sin las del playground), calco de `QuotationDrafts` en `AgentActivityReader`. La repetición se
descarta en la consulta con `result_json` que contiene `"alreadyBooked":false` (el resultado lo serializa
`AgentToolJson`, sin espacios).

## 10. Historial y auditoría (D-8)

- **Cita creada por el agente:** `Source = Channel`, `ConversationId`, historial `Created` con actor nulo y
  `byChannel: true`.
- **Cancelación o cambio de hora por el agente:** actor nulo y `byChannel: true`, también sobre una cita que nació en back
  office.
- **Por qué la columna (D-17):** hoy el frontend deduce «Cliente por WhatsApp» del origen de la cita y sólo para `Created`
  y `Cancelled` (`appointment-history.tsx`); un cambio de hora por chat, o cualquier cambio por chat sobre una cita de back
  office, salía «Sistema». Con `byChannel` lo dice el evento. Las filas viejas quedan en `false`; las escrituras por HTTP
  (back office, y las que declaran `source: Channel`) y las del recordatorio nunca la ponen.
- **Auditoría:** `AuditActorType.AiAgent`, actor = id del agente, acciones `scheduling.appointment.created|cancelled|
  rescheduled`, `source = "scheduling"`, campos cambiados por nombre (nunca valores). En la misma transacción del cambio,
  como hoy.

## 11. Pruebas (TDD, RED antes que GREEN, con el presupuesto del workspace)

Bloques de implementación (cada tarea con su commit, una revisión por bloque; el detalle está en el plan):

1. **Scheduling.** (a) Extracción (refactor sin cambio de comportamiento): RED no aplica a lo que se mueve; la red son
   `SchedulingBookingApiTests`, `SchedulingAvailabilityApiTests`, `SchedulingAppointmentTransitionsApiTests` y
   `SchedulingAuditApiTests`, corridas **una vez al final del bloque**. (b) `by_channel` (D-17): unitarias de dominio en
   `AppointmentTests` (`Create`/`Cancel`/`Reschedule` con y sin `byChannel`; el recordatorio siempre `false`) y una de API
   que comprueba `history[].byChannel == false` en una escritura de back office. (c) Remitente por defecto (D-18):
   `SchedulingSettingsTests` (se guarda con el recordatorio apagado), `SchedulingSettingsApiTests` (ida y vuelta con el
   nombre resuelto, el nombre de un miembro suspendido se sigue devolviendo, 422 con el campo para uno inactivo, de otro
   tenant o inexistente, `null` lo borra) y `AppointmentReminderWorkerTests` (sin miembro del recurso ni actor sale con el
   remitente por defecto; el del recurso le gana; uno suspendido sigue en `no_sender_member`). (d) Servicio del agente: RED
   por compilación (`IChannelScheduling` no existe); integración nueva `SchedulingChannelServiceTests` (harness de
   `SchedulingApiHarness`), iterando con `--filter "FullyQualifiedName~<Prueba>"`: nace `Pending`/`Confirmed` según el modo
   con `Source = Channel`, `ConversationId` y `byChannel`; la misma llave en paralelo da una sola cita y las dos respuestas la
   describen; sin `bookableByChannel` es `outside_rules`; la auditoría lleva `AiAgent` y el id del agente; la cita de otro
   cliente es `not_found`; el plazo bloquea la cancelación y el cambio de hora; mover una cita de un servicio sin
   `bookableByChannel` es `invalid_transition`; repetir la cancelación o el cambio a la misma hora es `alreadyDone`;
   `allowClientChoice` en la reserva y en la búsqueda; `ListUpcomingByCustomerAsync` no trae canceladas ni pasadas;
   módulo apagado.
2. **Ai y Bootstrapper.** Unitarias (`Modules.Ai.UnitTests`, sin contenedores): validación de argumentos de las seis;
   `IsOfferedTo` real y simulada; `Writes` incluye las tres; llave `ai:{jobId:N}:book:{n}` (dos reservas del mismo trabajo,
   trabajo retomado, una llamada fallida no consume número); traspaso por `appointment_change_blocked` y por cliente;
   simuladas que no llaman al puerto de escritura; `AgentTools` sin la clave se lee `false`; `AgentConfigValidatorTests`
   con los dos códigos; `AgentExportTests`; `AgentPromptBuilderTests` (la sección sólo con la herramienta, el `system`
   idéntico entre llamadas); conteo de herramientas y uniones `strict` dentro de los topes de Anthropic.
   `Bootstrapper.UnitTests`: `AiSchedulingTests` (traducción de cada fila de §8, cliente releído por conversación, prueba
   sin conversación usa el cliente de la prueba) y `HandoffCauseKindParityTests` con los dos códigos nuevos.
   `Modules.Ai.IntegrationTests`: `AgentActivityApiTests` cuenta las reservas de hoy sin las repetidas.
   `ArchitectureTests`: `AiLayerTests` y `SchedulingLayerTests` sin cambios de reglas (deben seguir verdes).

Antes de mergear a `develop`: la suite de los proyectos tocados (`Modules.Scheduling.UnitTests`,
`Modules.Scheduling.IntegrationTests`, `Modules.Ai.UnitTests`, `Modules.Ai.IntegrationTests`, `Bootstrapper.UnitTests`,
`ArchitectureTests`, y `Modules.Messaging.IntegrationTests` y `Modules.Customers.IntegrationTests` por el resolvedor que se
reusa), uno a la vez, mirando antes que no haya otro `testhost` corriendo. No se corre el eval sin presupuesto del owner.

## 12. Despliegue

- **Una migración de Scheduling, sólo expand** (`AddAppointmentEventByChannelAndReminderSender`):
  `appointment_events.by_channel boolean NOT NULL DEFAULT false` (D-17) y `settings.reminder_sender_member_id uuid NULL`
  (D-18). Pods viejos durante el rollout: no conocen las columnas; un evento que escriben toma el `DEFAULT false` (correcto:
  no son escrituras del agente) y la fila de reglas que guardan conserva el remitente porque EF sólo actualiza las columnas
  que conoce. No hay contract: nada se borra ni se renombra.
- **Orden:** backend primero. Hasta que el frontend muestre el interruptor nadie lo prende, así que el backend nuevo no
  cambia nada en producción. El campo `byChannel` y el remitente aparecen en las respuestas desde el primer pod nuevo; un
  frontend viejo los ignora.
- **Riesgo de frontend viejo (dos casos, misma mitigación):** (a) una pestaña vieja del editor de agentes que guarde el
  borrador manda `tools` sin `scheduling` y lo apaga (el `PUT` reemplaza el documento); (b) una pestaña vieja de las
  reglas de la Agenda que guarde manda `SettingsRequest` sin `reminderSenderMemberId` y lo borra (un `Guid?` ausente se lee
  `null`). Mitigación: el frontend se despliega antes de anunciar las funciones; no hace falta contrato expand/contract en
  el backend.
- **Rollback:** revertir el código; las columnas quedan (expand). Un `config` con `"scheduling": true` lo ignora el código
  viejo (propiedad desconocida, `JsonSerializerDefaults.Web` no la rechaza). Las citas creadas por el agente quedan como
  citas de canal normales.
- **Códigos de traspaso nuevos:** se guardan en el evento del hilo por la limpieza de `AutomationHandoff`; el frontend
  necesita su texto (§14), si no muestra el código crudo.
- **`docs/estado.md`** se actualiza en `develop` al mergear, no en la rama.

## 13. Preguntas cerradas

El owner las cerró el 2026-11-23: P-1 → D-14, P-2 → D-15, P-3 → D-16, P-4 → D-17, P-5 → D-18 (con el alcance ampliado
al remitente por defecto y su nombre en `SettingsDto`), P-6 → D-19. No queda ninguna abierta.

## 14. Contrato para el frontend

**Configuración del agente** (`draft.config` y versiones):

```json
{ "tools": { "catalog": false, "orderStatus": false, "quotationDraft": false, "scheduling": true } }
```

Ausente (backend viejo o documento viejo) ⇒ `false`.

**Editor** (`GET /api/v1/tenants/{tenantId}/ai/agents/{agentId}`), campo nuevo en `AgentEditorDto`:

```json
{ "scheduling": { "moduleEnabled": true, "bookableServices": 2, "ready": true } }
```

`ready = moduleEnabled && bookableServices > 0`; lo calcula el backend. Ausente ⇒ backend viejo (no mostrar el
interruptor). Los problemas llegan como siempre en `issues`: `scheduling_module_disabled` y
`scheduling_no_bookable_service`, los dos con `path: "tools.scheduling"`.

**Nombres de herramienta** (en `toolCalls[].tool` del playground y de la actividad, y en `sources[].label` con
`kind: "tool"`): `list_bookable_services`, `check_availability`, `book_appointment`, `list_my_appointments`,
`cancel_appointment`, `reschedule_appointment`. `arguments` y `result` son los JSON de §5 tal cual; en el playground
`book_appointment` devuelve `appointmentId: "PRUEBA-XXXXXXXX"`.

**Códigos de error** en `toolCalls[].errorCode`: `scheduling_unavailable`, `slot_taken`, `session_full`, `outside_rules`,
`appointment_change_blocked`, `appointment_not_found`, `service_not_found`, `resource_not_choosable`, `customer_unknown`,
`appointment_changed`, más los existentes (`invalid_arguments`, `one_write_per_response`, `tool_failed`).

**Códigos de traspaso** (transcripción del playground `role: "handoff"`, `code`; evento del hilo en Mensajería):
`appointment_change_blocked` («El cliente quiso cancelar o cambiar una cita fuera del plazo») y
`appointment_customer_unavailable` («No se pudo identificar al cliente para agendar»). Textos sugeridos; los decide el
spec del frontend.

**Actividad del día:** `AgentTodayDto.appointments` (entero; ausente ⇒ backend viejo, no mostrar).

**Cita** (pantallas de la Agenda): una cita del agente llega con `source: "Channel"` y `conversationId` con valor. Cada
entrada del historial trae `byChannel` (D-17):

```json
{ "history": [ { "action": "Rescheduled", "at": "2026-11-23T14:05:00+00:00", "actorName": null, "detail": null,
                 "byChannel": true } ] }
```

`byChannel: true` ⇒ «Cliente por WhatsApp» (también para `Rescheduled` y sobre una cita de back office). Ausente ⇒
backend viejo: seguir deduciendo como hoy.

**Reglas de la Agenda** (`GET` y `PUT /api/v1/tenants/{tenantId}/scheduling/settings`, D-18):

```json
{ "reminderEnabled": true, "reminderConnectionId": "…", "reminderSenderMemberId": "3c1f…",
  "reminderSenderMemberName": "Laura Gómez", "version": 7 }
```

- `SettingsDto.reminderSenderMemberId` (`string|null`): la membresía que firma el recordatorio cuando la cita no tiene
  miembro del recurso ni actor activo. `SettingsDto.reminderSenderMemberName` (`string|null`): su nombre ya resuelto;
  `null` sin remitente. Se devuelve aunque el miembro esté suspendido.
- `SettingsRequest.reminderSenderMemberId` (`string|null`): se manda **siempre** (ausente se lee `null` y lo borra). El
  nombre no se manda.
- 422 `validation.failed` con `errors.reminderSenderMemberId` si el miembro no existe en el tenant o no está activo.
- Se puede guardar con el recordatorio apagado.
- Ausente en el `GET` ⇒ backend viejo: no mostrar el campo.
