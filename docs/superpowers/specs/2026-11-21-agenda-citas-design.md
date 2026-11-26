# Agenda: citas y reservas por franjas, recursos y cupos

Fecha: 2026-11-21. Estado: diseño conversado con el owner el mismo día; **pendiente de su visto bueno, sin código**.
Repos: `qep-backend` (este documento) y `qep-frontend` (spec propia de pantallas, por escribir). El contrato HTTP exacto
vivirá en el plan de backend («Contrato HTTP»), como en RRHH. Mockup: lienzo «Agenda – mockup» (sesión del 2026-11-21).

## Intención

Un módulo nuevo, **Agenda** (`scheduling`), para que un tenant gestione sus citas y su agendamiento: un médico que se
administra solo, un consultorio con varios médicos y una secretaria, una barbería con varios barberos, o un instructor con
clases de muchos cupos. El tenant abre bloques de tiempo (**franjas**), y los clientes reservan dentro de ellos, desde el
back office o, en una feature posterior, por WhatsApp con un agente de IA.

Dos metas mandan el diseño:

1. **Flexible**: el mismo modelo sirve a los cuatro casos; lo único que cambia entre un médico (una persona por franja) y un
   instructor (quince) es el **cupo**.
2. **Poca parametrización inicial**: al primer acceso el módulo ya tiene sede y reglas por defecto, y la pantalla se
   adapta sola a cuántas agendas hay. Todo lo demás lo decide el administrador.

Este documento **no construye el agente de IA**. Sí deja listos los dos casos de uso que el agente usará (consultar
espacios libres y reservar), con las mismas reglas que el back office.

## Fuera de alcance de esta fase

- El agente de IA que agenda por WhatsApp (feature aparte; consumirá `ListAvailability` y `BookAppointment`).
- Lista de espera cuando una sesión se llena.
- Cobro, paquetes de clases o vínculo del servicio con un producto del Catálogo (el servicio es propio de la Agenda; el
  vínculo queda como campo futuro, no se crea hoy).
- Varios cupos por reserva (un alumno que inscribe a un amigo hace dos citas).
- Mezclar servicios distintos dentro del mismo cupo de un recurso.
- Festivos de otros países: hoy sólo Colombia, como en los horarios de atención de Mensajería.
- Sincronizar con Google Calendar u otros calendarios externos.
- Recordatorios por otro canal que no sea WhatsApp.

## Lo que no debe pasar

- **Doble reserva.** Dos peticiones simultáneas por el último cupo: una gana y la otra recibe `scheduling.appointment.slot_taken`.
  Lo impide la base de datos, no sólo el código (D-8).
- **Ver o tocar la agenda de otro.** Quien no tiene `scheduling.agenda.read_all` sólo ve y administra los recursos vinculados
  a su membresía. Pedir otro recurso responde **403, nunca 404**.
- **Dos sedes principales** en un tenant, ni dos aprovisionamientos concurrentes que dupliquen datos.
- **Confiar en el cliente.** El inicio de una cita se recalcula en el servidor contra franjas, cierres, reglas y cupo; un
  `startsAt` que no es un inicio válido se rechaza aunque el cliente lo haya visto en pantalla.
- **Recordatorio duplicado, o para una cita cancelada**, o enviado dos veces por un reintento.
- **Desfase de horas.** Todo instante se guarda en UTC; las franjas se expanden en la zona horaria del tenant.
- **Una cita que choca con un cierre posterior.** Crear un bloqueo que cruza citas vigentes no las borra ni las mueve: se
  rechaza hasta que se reprogramen (D-12).

## Diseño

### Módulo, llave y permisos

Módulo `Scheduling` (`src/Modules/Scheduling/Modules.Scheduling.{Domain,Application,Infrastructure,Api}`, esquema
`scheduling`, más `tests/Modules/Scheduling/` y `tests/ArchitectureTests/SchedulingLayerTests.cs`).

- Llave `TenantModuleKeys.Scheduling = Define("scheduling")`: al final de `All`, **fuera** de `DefaultForNewTenants`, con
  `Dependencies[Scheduling] = [Customers]` (toda cita es de un cliente). La dependencia con `messaging` es **opcional**: sin
  Mensajería la Agenda funciona y el recordatorio no se ofrece.
- Permisos, tres segmentos, y cada `manage` exige también su `read` (como D-1 de RRHH). Cada uno necesita su constante en
  `SchedulingPermissions` **y** su política en `AddAuthorization`, o el síntoma es un 500:

| Permiso | Concede |
| --- | --- |
| `scheduling.appointment.read` | Ver citas y la disponibilidad |
| `scheduling.appointment.manage` | Crear, confirmar, mover, cancelar y marcar citas |
| `scheduling.availability.manage` | Franjas, bloqueos y datos de los recursos dentro del alcance |
| `scheduling.agenda.read_all` | **Amplía el alcance** a todos los recursos de las sedes del tenant; sin él, sólo los recursos vinculados a la membresía |
| `scheduling.settings.manage` | Estructura (sedes y recursos nuevos), servicios, reglas de reserva y recordatorio |

`read_all` no concede acciones: sólo cambia sobre **qué** recursos valen `read`, `manage` y `availability.manage`. Los cinco
llevan `RequiredModules: [TenantModuleKeys.Scheduling]` y `Shape` (entidad y acción de la matriz de roles).

### Roles de sistema

Como `cashier` (spec 2026-10-07), son definiciones en código registradas en `QepServiceCollectionExtensions`, y sus claves
entran en `SystemRoleKeys.All` para que un rol custom no las pise. **No se crean al activar el módulo**: existen para todos
los tenants y la máscara por módulos los hace asignables sólo con `scheduling` prendido (`RoleAssignability`). Nadie los
recibe solo; el administrador los asigna.

| Clave | Nombre | Permisos |
| --- | --- | --- |
| `scheduling_manager` | Gestor de agenda | `appointment.read/manage`, `availability.manage` y `agenda.read_all` |
| `scheduling_provider` | Profesional de agenda | Los mismos, **sin** `agenda.read_all`: ve y administra sólo su recurso |

Ninguno lleva `settings.manage`: servicios, reglas y estructura son del administrador. **Tampoco llevan permisos de Clientes**: uno de
núcleo haría asignable «Gestor de agenda» en un tenant sin la Agenda. Buscar y crear el cliente de la cita va por rutas propias de la
Agenda (`GET` y `POST …/scheduling/customers`, con `appointment.manage`), como el POS con `/pos/customers`. Un tenant que quiera otra
combinación crea un rol custom en «Roles y permisos». 

### Aprovisionamiento: sede principal y reglas por defecto

Tenancy no avisa cuando se activa un módulo (H1 del embudo), así que, como `PipelineProvisioner`, **se crea en el primer
acceso**: `ISchedulingProvisioner.EnsureAsync(tenantId)` hace `INSERT … ON CONFLICT DO NOTHING` sobre un índice único, y
sólo la llamada que insertó siembra el resto. Todo caso de uso de la Agenda lo invoca antes de leer.

Crea, por tenant:

- La **sede principal** (`is_main`, índice único parcial por `tenant_id`), con el nombre del tenant, que **no se puede
  borrar ni desactivar**.
- La fila de **reglas** con los valores de `SchedulingDefaults` (un solo lugar, como `PipelineSeed`): confirmación
  automática, anticipación mínima 120 min, horizonte 30 días, cancelación del cliente hasta 24 h antes, recordatorio
  apagado (necesita una plantilla aprobada por Meta que el tenant aún no tiene).

**No** siembra servicios, recursos ni franjas: ningún valor sirve a la vez a un odontólogo y a una barbería. Apagar y volver a
prender el módulo conserva todo y no duplica nada.

### Estructura automática (D-4)

Sin interruptor. El backend responde `layout` en el calendario: `Personal` cuando el tenant tiene **una** sede activa y
**un** recurso activo, `Consolidated` en cualquier otro caso. El frontend no cuenta recursos para decidirlo (regla BFF).
Pasar de uno a otro ocurre al crear o archivar un recurso o una sede, sin pantalla de configuración; la spec del frontend
define el aviso que se muestra al crear el segundo.

### Sedes y recursos

- **Sede** (`locations`): nombre, activa, `is_main`. Más sedes sólo con `settings.manage`.
- **Recurso** (`resources`): quien atiende o lo que se reserva (un médico, un barbero, una silla, un salón). Pertenece a una
  sede. Tiene `display_name` libre (lo que ve el cliente) y un **miembro vinculado opcional**
  (`member_id`; índice único por sede) que define el alcance de quien no tiene `read_all`. **No depende de RRHH.** Archivar
  un recurso no borra su historial; se rechaza mientras tenga citas futuras vigentes.
- El miembro se valida contra el tenant por un puerto `ISchedulingMemberDirectory` (adaptador en Bootstrapper, forma de
  `IHrMemberDirectory`).

### Servicios

`services`: nombre, `duration_minutes`, `buffer_minutes` (margen después, ocupa el recurso pero no se le muestra al
cliente), `bookable_by_channel` (si el agente puede reservarlo), activo. **Sin cupo ni modo**: eso es de la franja (D-3).

### Franjas (`availability_blocks`)

Lo que el tenant abre. Cada franja declara:

- **Servicios** que se reservan en ella (uno o varios) y **recursos** que la atienden (uno o varios, ordenados).
- **Repetición**: semanal (por día de la semana, uno o varios rangos de minutos locales `[from, to)`, 0..1440) o **puntual**
  (una fecha), con vigencia `valid_from` / `valid_to` opcional y `closes_on_holidays` (festivos de Colombia).
  El formato se parece al de los horarios de atención de Mensajería, pero Scheduling no puede depender de Messaging: declara sus
  propios `TimeRange`, `WeekdayHours` y `DateException` (D-10).
- **`capacity_per_resource`** (≥ 1). Con 3 recursos y cupo 1 caben 3 citas a la misma hora, una por recurso.
- **`session_mode`**, sólo con cupo > 1 y una franja más larga que el servicio:
  `FirstBookingOpens` (la primera reserva abre la sesión; las demás se suman al mismo servicio e inicio) o
  `PublishedSessions` (el tenant publica las sesiones; sólo ésas se reservan). Con la franja del mismo largo que el servicio
  (una clase de 19:00 a 20:00) hay una sola sesión posible y el modo no importa.
- **`allow_client_choice`** y **`assignment_rule`** (hoy sólo `FirstFree`): si el cliente elige con quién y, cuando no
  elige, a quién se le asigna.

Los **bloqueos** (`closures`) cierran un recurso (o toda la sede, `resource_id` nulo) en un rango; son lo que el calendario
dibuja como «Bloqueado».

### Sesiones y cupos (D-2, D-8)

Una **sesión** (`sessions`) es `(recurso, servicio, inicio, fin, cupo, reservas)`. **Toda cita pertenece a una sesión**; una
cita individual es una sesión de cupo 1. Esto unifica los casos y deja una sola restricción en la base:

- Dos sesiones activas del mismo recurso **no pueden solaparse**: rango de ocupación `[starts_at, ends_at + buffer)`. Se
  impone con una *exclusion constraint* (`btree_gist`, `tstzrange`) y `WHERE is_active`. La sesión es única por
  `(resource_id, service_id, starts_at)`.
- Una sesión de cupo 1 o abierta por la primera reserva **se desactiva** al quedarse sin citas vigentes y libera el
  tiempo; una publicada permanece activa.
- `booked_count` sube y baja con concurrencia optimista (`version`), y nunca supera `capacity`.

### Disponibilidad

`ListAvailability(servicio, rango, recurso?)` calcula, **no guarda**, los espacios libres:

1. Expande las franjas vigentes en la zona horaria del tenant (festivos y cierres restados).
2. Por recurso, genera inicios cada `duration` del servicio, alineados al inicio del rango; la ocupación de cada uno es
   `duration + buffer`.
3. Descarta lo que choca con una sesión activa **distinta** (otra hora o servicio), lo anterior a `now + min_notice` y lo
   posterior a `now + horizon`.
4. Con cupo > 1 suma las sesiones abiertas del mismo servicio e inicio que aún tienen cupos (`FirstBookingOpens`) o devuelve
   sólo las publicadas (`PublishedSessions`).
5. Devuelve por inicio cuántos cupos quedan y, si `allow_client_choice`, con qué recursos.

Es el mismo cálculo para la pantalla «Nueva cita» y para el agente.

### Citas

`appointments`: sede, recurso, servicio, cliente (`customer_id`), sesión, franja de origen, `starts_at`/`ends_at` (UTC),
`status`, `source` (`BackOffice` | `Channel`), `conversation_id` opcional (enlace a Mensajería, sin FK), nota, motivo de
cancelación, quién la creó, `reminder_sent_at`, `version`.

Estados `Pending`, `Confirmed`, `Cancelled`, `Completed`, `NoShow`:

- `Pending → Confirmed | Cancelled`; `Confirmed → Cancelled | Completed | NoShow`; los tres últimos son terminales.
- `Completed` y `NoShow` sólo después de la hora de inicio.
- **Reprogramar** conserva el id y el estado, cambia de sesión y deja entrada de historial; vuelve a validar todo.
- Una cita del back office nace `Confirmed`. Una `Channel` nace según `confirmation_mode`; en ambos casos **aparta el
  cupo** desde el primer momento.
- El cliente por canal puede cancelar hasta `client_cancel_cutoff_hours` antes; el back office no tiene ese tope.

**`BookAppointment`** es una transacción: valida permiso, tenant y alcance → recalcula que el inicio es válido (reglas
incluidas) → bloquea la fila del recurso → busca o crea la sesión → comprueba cupo → inserta la cita, su historial y
la auditoría en la misma transacción. Admite `clientRequestId` idempotente para el
agente. Cuando el cliente no elige recurso, **la asignación se hace aquí**, dentro de la transacción, y no al leer la
disponibilidad: lo contrario deja a dos clientes con el mismo «primero libre».

### Reglas de reserva (`scheduling.settings`, una fila por tenant, con `version`)

`confirmation_mode` (`Automatic` | `Manual`), `min_notice_minutes`, `horizon_days`, `client_cancel_cutoff_hours`,
`reminder_enabled`, `reminder_offset_hours`, `reminder_connection_id`, `reminder_template_id`. Los cuatro primeros son de
esta fase por decisión del owner; todas se escriben con `If-Match` y `settings.manage`.

### Recordatorio por WhatsApp (D-9)

- Mensajería gana `MessageTemplateKind.Appointment` y su alta en `…/template-management/appointment`, con la misma mecánica
  que la plantilla de pedido. Variables: nombre del cliente, servicio, fecha y hora, recurso y sede.
- Un `AppointmentReminderWorker` (servicio en segundo plano, forma de los barredores existentes) busca citas `Confirmed` con
  `starts_at − reminder_offset ≤ now` y `reminder_sent_at` nulo, y las envía por un puerto `IAppointmentReminderSender` cuyo
  adaptador vive en Bootstrapper sobre Mensajería. Idempotente por `appointment-reminder:{id}`; un fallo guarda un código y
  reintenta con tope; **nunca** envía si el estado cambió o la cita ya pasó. Éxito deja la entrada «Recordatorio enviado».
- Cliente sin teléfono válido: se omite con motivo visible en el historial.
- Sin el módulo `messaging`, la opción no se ofrece y el worker no corre para ese tenant.

### Auditoría e historial

Cada transición de una cita escribe una entrada de historial (`appointment_events`, la que dibuja la tarjeta «Historial») y
una auditoría con el mismo mecanismo que Hr (`ISchedulingAuditPublisher`, calco de `IHrAuditPublisher`); el módulo no publica
eventos. Las acciones viven en `SchedulingAuditActions`.

### Integración con Clientes (gotcha de SQL entre módulos)

`appointments` guarda `customer_id`, así que, por la regla de `sql-entre-modulos`, hay que sumar el esquema `scheduling` a:

- `CustomerMergeReferences.MoveAsync` (sin subir `version`, como `support.tickets`);
- `CustomerDeletionReferences`, y a la lista de bloqueos del borrado (D-11: **cualquier cita**, en cualquier estado, bloquea
  borrar al cliente, como los tickets de soporte, con el motivo `schedulingAppointments`);
- sus dos pruebas amarradas (`CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema` y
  `CustomerDeletionAcrossModulesTests`).

Sin esto, la fusión deja citas apuntando a un cliente inactivo.

### BFF: lo que la pantalla necesita en una llamada

Rutas bajo `/api/v1/tenants/{tenantId}/scheduling/…`, política por permiso **y** revalidación en el handler
(`SchedulingAuthorization`, forma de `CatalogAuthorization`). El detalle exacto va en el plan; las lecturas que la pantalla
exige son:

| Lectura | Qué devuelve y por qué |
| --- | --- |
| `GET calendar?date|from&to&locationId&resourceIds` | **Una** respuesta con los recursos del alcance, sus citas, bloqueos, ventanas abiertas, `layout` y el conteo por recurso. La pantalla no compone nada |
| `GET desk?date` | La vista de la secretaria: contadores del día (citas, por confirmar, sin marcar, asistieron, no asistieron, libres), la cola «Por confirmar» y las citas pasadas sin marcar, ya calculados. Un total que depende de la página que se mira no se suma en el cliente |
| `GET availability?serviceId&from&to&resourceId?` | Espacios libres, con cupos restantes y recursos posibles |
| `GET setup` | `hasService`, `hasResource`, `hasBlock`, para el asistente de primer uso |

Las colecciones de tamaño fijo viajan completas, aun en cero (los contadores y los cinco estados); los enums viajan con su
nombre (`Pending`, `FirstBookingOpens`); una colección editable vuelve entera y en orden (los recursos de una franja). El 422
de validación lleva el mapa `errors`. Cada escritura lleva `If-Match` con la `version` de la fila. Ningún status se arma a
mano en un handler.

Códigos nuevos, entre otros: `scheduling.appointment.slot_taken` y `…session_full` (409, `ResourceConflictException`);
`scheduling.appointment.outside_rules`, `…invalid_transition`, `scheduling.resource.has_future_appointments`,
`scheduling.closure.overlaps_appointments` y `scheduling.service.in_use` (422). Un recurso fuera de alcance no tiene código
propio: responde 403 `authorization.denied`. El mapeo de status es por **tipo de excepción** y `ApiExceptionHandler` no cambia.

## Decisiones

| # | Decisión | Fecha |
| --- | --- | --- |
| D-1 | Módulo `scheduling`, nombre en pantalla **«Agenda»**, dependiente de `customers`; Mensajería es opcional | 2026-11-21 |
| D-2 | Modelo: **sede → recurso → franja → sesión → cita**. El cupo es de la franja (por recurso) | 2026-11-21 |
| D-3 | El servicio es propio de la Agenda y define duración y margen; el vínculo con Catálogo queda para después | 2026-11-21 |
| D-4 | Estructura **automática** (`layout`), sin interruptor. La sede principal y las reglas se aprovisionan en el primer acceso | 2026-11-21 |
| D-5 | Confirmación automática o por confirmar, la elige el tenant; en ambos casos la cita aparta el cupo | 2026-11-21 |
| D-6 | Entran las cuatro reglas: anticipación mínima, horizonte, margen entre citas y plazo de cancelación del cliente | 2026-11-21 |
| D-7 | Alcance por permiso `scheduling.agenda.read_all` + miembro vinculado al recurso; el médico administra su agenda completa | 2026-11-21 |
| D-8 | Doble reserva imposible por una restricción de exclusión sobre sesiones, no sólo por código | 2026-11-21 |
| D-9 | **El recordatorio por WhatsApp entra en esta fase** (plantilla `Appointment`, trabajo programado) | 2026-11-21 |
| D-10 | Los festivos de Colombia entran por un puerto con adaptador en Bootstrapper (como Soporte); los tipos de rango se declaran en Scheduling. No se mueve nada de Mensajería | 2026-11-21 |
| D-11 | Cualquier cita bloquea el borrado del cliente; la fusión mueve las citas | 2026-11-21 |
| D-12 | Un bloqueo que cruza citas vigentes se rechaza hasta reprogramarlas; nada se mueve en silencio | 2026-11-21 |
| D-13 | Dos roles de sistema, `scheduling_manager` y `scheduling_provider`, definidos en código; ninguno lleva `settings.manage`. Reemplazada por `2026-11-25-roles-por-tenant-design.md`: roles sembrados por tenant | 2026-11-21 |

## Lo que queda al implementador

Cosas que la spec no resuelve a propósito y que el plan debe verificar contra el código **antes** de escribir tareas:

1. **`btree_gist`: resuelto.** Las migraciones de Mensajería ya hacen `CREATE EXTENSION IF NOT EXISTS btree_gin` en producción y
   `btree_gist` es del mismo paquete; la migración lo crea igual. Si una base concreta lo negara, el remedio es bloquear la fila del
   recurso sin restricción, y la decisión D-8 se reescribe.
2. **Tipos compartidos con Mensajería: resuelto (D-10).** No se mueve ni se copia nada: los festivos entran por
   `ISchedulingHolidayCalendar`, con el adaptador de Bootstrapper sobre `ColombianHolidays`, calco de `SupportBusinessCalendarSource`.
3. **Permisos de Clientes y de `admin`: resuelto.** Los roles no llevan permisos de Clientes, y `admin` recibe los cinco porque se
   listan, como los de Hr, en su definición.
4. **Mapeo de errores: resuelto en el plan.** `slot_taken` y `session_full` son 409; el resto, 422; fuera de alcance, 403. La
   restricción de exclusión se traduce en Infrastructure por **nombre de índice**, no sólo por `SqlState`.
5. **Política de WhatsApp** para un recordatorio como plantilla de utilidad: cómo trata Mensajería el consentimiento y la
   ventana de 24 horas, y si hace falta registrar un consentimiento del cliente. Si hay un requisito, entra al plan.
6. **Orden del ítem «Agenda» en el menú lateral.** Lo decide el owner y lo fija `sidebar-nav-items.test.ts` en el
   frontend; no se resuelve aquí.
7. **Registro y arranque:** `AddSchedulingModule`, `MapSchedulingEndpoints`, la migración inicial, `InitializeSchedulingDatabaseAsync`
   en `Program.cs` después de Audit, y `SchedulingLayerTests.cs`.

## Pruebas (TDD, RED antes que GREEN)

- **Dominio:** expansión de franjas con festivos y vigencia; cupo, sesiones y solapamientos; máquina de estados;
  anticipación, horizonte y margen.
- **Integración:** reserva concurrente por el último cupo (N peticiones paralelas, una sola gana); cupo N sin
  sobrepasarse; alcance con y sin `read_all` (**403, no 404**); aprovisionador idempotente y concurrente; un tenant no ve
  datos de otro; recordatorio una sola vez; fusión y borrado de clientes con citas.
- **Arquitectura:** `SchedulingLayerTests`, y que `CompositionRootTests` confirme que cada permiso declara su forma y tiene
  su política.
- Toda prueba que mira un efecto verifica **también** la auditoría y el historial, no sólo el status HTTP.
