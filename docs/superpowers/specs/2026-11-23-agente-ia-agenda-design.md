# Agente IA que agenda citas: herramientas de la Agenda para el agente (backend)

- **Fecha:** 2026-11-23.
- **Módulos:** `Ai` (seis herramientas nuevas, `config.tools.scheduling`, validador, prompt, playground), `Scheduling`
  (extracción del núcleo de disponibilidad, reserva, cancelación y cambio de hora; servicio sin sesión para el agente;
  auditoría por agente), `Bootstrapper` (adaptador del puerto de Ai, cliente de la conversación). Mensajería y Clientes
  no cambian: se reusa lo que ya existe. Frontend en `qep-frontend` (spec aparte; el contrato está al final).
- **Estado:** borrador para aprobación del owner. **Sin código.**
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
- Recordatorios: ya existen (plan de la Agenda, Tarea 12); este spec no los cambia (ver P-5).
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
| Códigos de traspaso | `AgentHandoffCodes.All`, `HandoffCauseKindParityTests`, `ConversationAutomation.IsValidCode` | Un código nuevo entra a la lista y pasa la limpieza de Mensajería |

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

Las D-1 a D-8 son del owner y **no se reabren**; donde el código las complica, la nota lo dice y la pregunta va a §12.
Las D-9 en adelante son de este spec.

| # | Decisión | Fuente |
| --- | --- | --- |
| D-1 | Seis herramientas detrás de un interruptor nuevo `config.tools.scheduling` (por defecto `false`); los documentos guardados antes se siguen leyendo | owner |
| D-2 | Reglas en el backend: sólo citas del cliente de la conversación; una cita nace con `Source = Channel` y `ConversationId`, según `confirmationMode`; cancelar usa `Source = Channel` para que rija `clientCancelCutoffHours`; sólo servicios con `bookableByChannel`; anticipación, horizonte y disponibilidad del mismo motor que el back office; sigue la regla de una herramienta que escribe por respuesta | owner |
| D-3 | Cuando el plazo bloquea una cancelación (o cualquier `invalid_transition`), el resultado de la herramienta termina el turno con un traspaso a una persona, con el mecanismo determinista del borrador de cotización | owner |
| D-4 | Llave de idempotencia de la reserva derivada del trabajo y de la llamada (ver la nota de §7.3 y P-1) | owner |
| D-5 | Autorización: el núcleo de disponibilidad, reserva, cancelación y cambio de hora se extrae a servicios internos de Scheduling que usan los handlers (después de autorizar) y un puerto nuevo para el agente; Ai declara su puerto (patrón de `IAiCatalogSearch`) con adaptador en Bootstrapper; **no** se corren los handlers bajo un principal sintético. Capacidades nuevas en `AiServicePrincipal` | owner |
| D-6 | Contacto sin cliente: la reserva crea el cliente con el nombre y el teléfono del contacto y lo cuelga de la conversación (ver §7.6: se hace con `ConversationCustomerResolver`) | owner |
| D-7 | Playground: las tres que escriben se simulan como `SimulatedQuotationDraftTool` (validan, devuelven datos ficticios, no escriben); las lecturas leen datos reales, y las citas sólo del cliente de prueba si la prueba fijó uno | owner |
| D-8 | Auditoría a nombre del agente (sin miembro), con nombre de acción claro; el historial de la cita queda con la semántica «Cliente por WhatsApp» que ya existe (`Source = Channel`, actor nulo) | owner |
| D-9 | En cada llamada del canal real, el adaptador **relee** el cliente de la conversación (por `ConversationId`) en vez de usar el del inicio del trabajo: una reserva que crea el cliente lo deja visible para la herramienta siguiente del mismo trabajo, y una fusión en el medio no apunta a un cliente inactivo. Mismo criterio que `AutomationDataConsents` | spec |
| D-10 | Las horas viajan **en hora local del tenant** con formato fijo `yyyy-MM-ddTHH:mm`, sin desfase, y una sola `timeZone` por resultado. La conversión a UTC la hace Scheduling con la zona de `ITenantClock`, la misma que el prompt ya muestra | spec |
| D-11 | En el camino del agente rige `allowClientChoice` de la franja: los recursos se muestran y se aceptan sólo en los inicios cuya opción lo permite. El back office no cambia (plan, corrección 17). Confirmar en P-3 | spec |
| D-12 | Repetir una cancelación ya hecha o un cambio a la misma hora en el **mismo** trabajo responde éxito (`alreadyDone: true`), no `invalid_transition`: un trabajo retomado no debe traspasar por su propia escritura anterior | spec |
| D-13 | El módulo `scheduling` apagado no quita las herramientas del modelo (el perfil no conoce los módulos); responden `scheduling_unavailable`. Publicar con `tools.scheduling` y el módulo apagado es error del validador | spec |

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
                      "endsAt": "2026-11-24T09:30", "serviceName": "Consulta general", "resourceName": "Dra. Pérez",
                      "locationName": "Sede principal", "canChangeByChat": true, "changeByChatUntil": "2026-11-23T09:00" } ],
  "truncated": false }
```

Citas `Pending` o `Confirmed` del cliente que empiezan desde ahora, por inicio, tope 10. `canChangeByChat` y
`changeByChatUntil` (inicio menos `clientCancelCutoffHours`) los calcula el backend con `AppointmentCancellationRules`:
el modelo no hace aritmética de plazos. Sin cliente en la conversación responde `appointments: []`, no un error (un
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
  `appointment_change_blocked` traspasa. El cambio de hora sólo ofrece recursos de la misma sede (corrección 22c, ya en el
  núcleo).

### 5.7 Lo que cambia en el contrato común

- `AgentToolNames`: las seis constantes. `Writes(name)` pasa a ser `create_quotation_draft`, `book_appointment`,
  `cancel_appointment` y `reschedule_appointment`: la regla de una escritura por respuesta las cubre sin tocar el bucle.
- `AgentToolInvocation` gana `AgentId` y `ConversationId` (nulo en el playground); ya están en `AgentTurnContext`.
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
| `SchedulingActor` | — | Quién escribe: `HistoryMemberId` (nulo en canal) y el actor de auditoría, `Human(subjectId)` o `Agent(agentId)` |
| `AvailabilitySearch` | `ListAvailabilityHandler` desde `loader.LoadAsync` | `SearchAsync(tenantId, scope, service, from, to, onlyResource)` → zona + inicios con sus `SlotOption` (incluido `AllowClientChoice`). El handler lo mapea a `AvailabilityDto` como hoy |
| `AppointmentBooking` | `BookAppointmentHandler` desde la repetición por `clientRequestId` | `BookAsync(BookingInput, scope, actor)` → `(Appointment, Replayed)`. Mantiene el doble chequeo de la llave, `bookableByChannel`, el candado, `PickFree`, `TakeSeatAsync`, el estado por `confirmationMode` y la auditoría |
| `AppointmentCancellation` | `CancelAppointmentHandler` desde el plazo | `CancelAsync(tenantId, appointment, reason, byClient, actor)` |
| `AppointmentRescheduling` | `RescheduleAppointmentHandler` desde el chequeo de estado | `RescheduleAsync(tenantId, appointment, scope, resourceId, startsAt, actor, byClient)` |

`SchedulingAudit.Publish` y `AppointmentTransitions.SaveAsync` reciben el `SchedulingActor`. `ISchedulingAuditPublisher` gana
`PublishByAgent(tenantId, agentId, action, resourceType, resourceId, outcome, changedFields, occurredAt)`, que
`SchedulingAuditPublisher` escribe con `AuditActorType.AiAgent` y el id del agente como actor (precedente:
`AiAuditRecorder.RecordByAgent`). Las acciones son las de siempre (`scheduling.appointment.created`, `…cancelled`,
`…rescheduled`): el tipo de actor `AiAgent` es lo que dice que fue el agente (D-8).

`byClient` en el cambio de hora sólo decide si rige el plazo (P-2); el handler HTTP pasa `false`, como hoy.

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
- D-11: con `resourceId` pedido, la opción de ese recurso en ese inicio tiene que tener `AllowClientChoice`; si no, un
  código propio del camino del agente (el adaptador lo lee como `resource_not_choosable`). Hoy no existe en
  `SchedulingErrorCodes`; se agrega `scheduling.appointment.resource_not_choosable` sólo si el owner acepta D-11 (P-3).
- Las horas entran como `DateTime` local sin zona y salen como `DateTimeOffset` en la zona del tenant (D-10). Una hora
  local que no existe en la zona (cambio de horario) es `outside_rules`.
- Lectura nueva: `IAppointmentReadRepository.ListUpcomingByCustomerAsync(tenantId, customerId, now, take)` (sin
  seguimiento, `Pending`/`Confirmed`, `starts_at >= now`, por inicio). No hay migración: `appointments.customer_id` ya
  existe; se mira el plan de ejecución antes de decidir un índice `(tenant_id, customer_id, starts_at)`.
- Lectura nueva para el editor: `GetReadinessAsync(tenantId)` → módulo prendido y cuántos servicios activos con
  `bookableByChannel` ofrece alguna franja no archivada con algún recurso activo
  (`IAvailabilityBlockRepository.ListAsync` con `ResourceScope.All`, `ServiceIds`, `ResourceIds`).

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
un cliente que pide dos citas en el mismo turno recibiría la primera dos veces. **Propuesta de este spec:**
`ai:{jobId:N}:book:{n}`, donde `n` es el ordinal de la llamada a `book_appointment` **dentro del trabajo** (1, 2…), que el
perfil cuenta en memoria. Un trabajo retomado vuelve a contar desde 1 y encuentra sus citas; una segunda cita del mismo
turno lleva `n = 2`. El resultado con `alreadyBooked: true` describe la cita guardada, así el modelo nunca le confirma al
cliente una hora distinta de la reservada. Hasta que el owner responda P-1 el plan usa esta llave.

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
| Cancelar o cambiar hora con el plazo vencido, o una cita cerrada (`invalid_transition`) | `appointment_change_blocked` | `appointment_change_blocked` | Destino configurado del agente (`MemberId: null`) |
| Reservar sin cliente posible (`ConversationCustomer.NoUser` o `Unavailable`) | `customer_unknown` | `appointment_customer_unavailable` | Destino configurado |

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
| `scheduling.appointment.invalid_transition` (plazo, cita cerrada) | `appointment_change_blocked` | **Sí** (§7.5) | — |
| `scheduling.appointment.before_start` | `tool_failed` | No | No es alcanzable por estas herramientas (sólo asistencia y «no asistió»); se traduce a falla genérica por defensa |
| `scheduling.appointment.not_found`, cita de otro cliente | `appointment_not_found` | No | Relee `list_my_appointments` |
| `scheduling.service.not_found`, archivado o sin `bookableByChannel` (`ValidationException` del campo `serviceId`) | `service_not_found` | No | Relee `list_bookable_services` |
| `scheduling.resource.not_found` o archivado | `resource_not_choosable` | No | Reintenta sin recurso |
| `scheduling.appointment.resource_not_choosable` (si P-3 se acepta) | `resource_not_choosable` | No | Igual |
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

`AgentEditorDto` gana `Scheduling` (`AgentSchedulingReadinessDto`, siempre presente desde este backend): ver §13.

### 9.4 Exportar e importar

`AgentExport.Portable` no cambia: `tools.scheduling` es un booleano sin ids del tenant y viaja tal cual. `schema` sigue en
`1`: un archivo viejo sin la clave se importa con `false` (§9.1); uno con `true` en un tenant sin la Agenda se importa y el
panel de problemas muestra `scheduling_module_disabled`. `ImportAgentValidator` no cambia.

### 9.5 Actividad del día

`AgentTodayDto` gana `Appointments`: reservas que el agente hizo hoy (llamadas `book_appointment` con `ok` y
`alreadyBooked: false`, sin las del playground), calco de `QuotationDrafts` en `AgentActivityReader`.

## 10. Historial y auditoría (D-8)

- **Cita creada por el agente:** `Source = Channel`, `ConversationId`, historial `Created` con actor nulo → la tarjeta
  del frontend ya pinta «Cliente por WhatsApp» (`appointment-history.tsx`: sólo `Created` y `Cancelled` de una cita
  `Channel` con actor nulo).
- **Cancelación por el agente de una cita `Channel`:** actor nulo → «Cliente por WhatsApp». Bien.
- **Lo que no queda bien con el código de hoy:** (a) un **cambio de hora** por el agente sale con actor nulo y el frontend lo
  pinta «Sistema»; (b) cancelar o cambiar la hora de una cita que nació en **back office** (`Source = BackOffice`) también
  sale «Sistema», porque la regla del frontend mira el origen de la cita, no el del evento. Ver P-4.
- **Auditoría:** `AuditActorType.AiAgent`, actor = id del agente, acciones `scheduling.appointment.created|cancelled|
  rescheduled`, `source = "scheduling"`, campos cambiados por nombre (nunca valores). En la misma transacción del cambio,
  como hoy.

## 11. Pruebas (TDD, RED antes que GREEN, con el presupuesto del workspace)

Bloques de implementación sugeridos (cada tarea con su commit, una revisión por bloque):

1. **Scheduling, extracción (refactor sin cambio de comportamiento).** RED: no aplica a lo que se mueve; la red son
   `SchedulingBookingApiTests`, `SchedulingAvailabilityApiTests`, `SchedulingAppointmentTransitionsApiTests` y
   `SchedulingAuditApiTests`, corridas **una vez al final del bloque**, por clase, con `-- xUnit.MaxParallelThreads=4`.
2. **Scheduling, servicio del agente.** RED por compilación (`IChannelScheduling` no existe). Integración nueva
   `SchedulingChannelServiceTests` (harness de `SchedulingApiHarness`), iterando con
   `--filter "FullyQualifiedName~<Prueba>"`: nace `Pending`/`Confirmed` según el modo con `Source = Channel` y
   `ConversationId`; la misma llave en paralelo da una sola cita; sin `bookableByChannel` es `outside_rules`; la auditoría
   lleva `AiAgent` y el id del agente; la cita de otro cliente es `not_found`; el plazo bloquea la cancelación; repetir la
   cancelación es `alreadyDone`; `allowClientChoice` (si P-3); `ListUpcomingByCustomerAsync` no trae canceladas ni pasadas;
   módulo apagado.
3. **Ai.** Unitarias (`Modules.Ai.UnitTests`, sin contenedores): validación de argumentos de las seis; `IsOfferedTo` real
   y simulada; `Writes` incluye las tres; llave `ai:{jobId}:book:{n}` (dos reservas del mismo trabajo, trabajo retomado);
   traspaso por `appointment_change_blocked` y por cliente; simuladas que no llaman al puerto de escritura; `AgentTools` sin
   la clave se lee `false`; `AgentConfigValidatorTests` con los dos códigos; `AgentExportTests`; `AgentPromptBuilderTests`
   (la sección sólo con la herramienta, el `system` idéntico entre llamadas); conteo de herramientas y uniones `strict`
   dentro de los topes de Anthropic.
4. **Bootstrapper.** `Bootstrapper.UnitTests`: `AiSchedulingTests` (traducción de cada fila de §8, cliente releído por
   conversación, prueba sin conversación usa el cliente de la prueba) y `HandoffCauseKindParityTests` con los dos códigos
   nuevos. `ArchitectureTests`: `AiLayerTests` y `SchedulingLayerTests` sin cambios de reglas (deben seguir verdes).

Antes de mergear a `develop`: la suite de los proyectos tocados (`Modules.Scheduling.*`, `Modules.Ai.UnitTests`,
`Modules.Ai.IntegrationTests` sólo si se tocó persistencia de Ai, `Bootstrapper.UnitTests`, `ArchitectureTests`), uno a la
vez, mirando antes que no haya otro `testhost` corriendo. No se corre el eval sin presupuesto del owner.

## 12. Despliegue

- **Sin migraciones** salvo que P-4 elija la columna nueva del historial (expand: `ADD COLUMN … DEFAULT false`, compatible).
- **Orden:** backend primero. Hasta que el frontend muestre el interruptor nadie lo prende, así que el backend nuevo no
  cambia nada en producción.
- **Riesgo de frontend viejo:** una pestaña vieja del editor que guarde el borrador manda `tools` sin `scheduling` y lo
  apaga sin aviso (el `PUT` reemplaza el documento). Mitigación: el frontend del interruptor se despliega antes de
  anunciar la función; no hace falta contrato expand/contract en el backend.
- **Rollback:** revertir el código. Un `config` con `"scheduling": true` lo ignora el código viejo (propiedad desconocida,
  `JsonSerializerDefaults.Web` no la rechaza). Las citas creadas por el agente quedan como citas de canal normales.
- **Códigos de traspaso nuevos:** se guardan en el evento del hilo por la limpieza de `ConversationAutomation`; el
  frontend necesita su texto (§13), si no muestra el código crudo.

## 13. Preguntas abiertas para el owner

- **P-1. Llave de idempotencia (D-4).** «Trabajo + id de la llamada» duplica la cita cuando un trabajo se retoma (el id de
  la llamada cambia; es el hallazgo 1 de la revisión final de agentes, D-A16). **Recomendación:** `ai:{jobId}:book:{n}`,
  `n` el ordinal de la reserva dentro del trabajo (§7.3).
- **P-2. Plazo del cliente al cambiar la hora.** El código sólo aplica `clientCancelCutoffHours` a la cancelación; mover una
  cita de mañana a la semana que viene libera el cupo de mañana igual que cancelarla. **Recomendación:** el cambio de hora
  por el agente respeta el mismo plazo (`byClient: true` en `AppointmentRescheduling`) y, si bloquea, traspasa con
  `appointment_change_blocked`; también exigir `bookableByChannel` del servicio de la cita para moverla por chat.
- **P-3. `allowClientChoice` (D-11).** El plan dejó la bandera sin aplicar (corrección 17). **Recomendación:** aplicarla
  sólo en el camino del agente, con el código `scheduling.appointment.resource_not_choosable`; el back office sigue igual.
- **P-4. «Cliente por WhatsApp» en el historial (D-8).** Hoy el frontend lo deduce de `appointment.source` y sólo para
  `Created` y `Cancelled`; un cambio de hora por chat y cualquier cambio por chat sobre una cita de back office se pintan
  «Sistema». **Recomendación:** columna `appointment_events.by_channel` (expand, `false` por defecto) expuesta como
  `AppointmentEventDto.ByChannel`, y el frontend pinta «Cliente por WhatsApp» con ella en vez de deducirlo.
- **P-5. Recordatorio de citas del agente.** Una cita de canal confirmada sobre un recurso sin miembro vinculado nunca se
  recuerda (`no_sender_member`, plan corrección 34, decisión pendiente desde el 2026-11-22). Con el agente estas citas
  serán las más comunes. **Recomendación:** resolver primero `reminderSenderMemberId` en las reglas de la Agenda.
- **P-6. A quién va el traspaso de `appointment_change_blocked`.** El spec usa el destino configurado del agente.
  Alternativa: el miembro vinculado al recurso de la cita, si está activo y puede responder conversaciones (como P-4b manda
  el borrador a su asesora). **Recomendación:** destino configurado, por simple; cambiarlo si la secretaria es quien debe
  verlo.

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

**Cita** (pantallas de la Agenda): una cita del agente llega con `source: "Channel"` y `conversationId` con valor; si P-4
se acepta, `history[].byChannel`.
