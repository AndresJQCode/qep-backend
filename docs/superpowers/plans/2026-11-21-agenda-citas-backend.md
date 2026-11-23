# Agenda (citas, franjas, recursos y cupos) — plan de backend

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to
> implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. El `CLAUDE.md` del repo manda sobre el proceso del skill: las
> tareas se ejecutan en **cuatro bloques**, un implementador por bloque, una revisión por bloque y la revisión fuerte una sola vez al final.
> Cada tarea conserva su commit.

## Estado y traspaso (2026-11-21)

Rama `claude/trusting-archimedes-uk0ksk`, sin mergear a `develop` ni a `main`. **Hecho: Bloques A, B y C (Tareas 1 a 10) y la Tarea 11.** Faltan las Tareas 12 y 13.

| Tarea | Estado | Commit |
| --- | --- | --- |
| 1 Llave de módulo `scheduling` | Hecha | `03798bb` |
| 2 Esqueleto, permisos, alcance, roles | Hecha | `d05482e` |
| 3 Tipos de horario, códigos, festivos | Hecha | `b773221` |
| 4 Dominio | Hecha | `13f4fad` |
| 5 Motor de disponibilidad | Hecha | `df7bff1` |
| 6 Persistencia, migración, aprovisionador | Hecha | `4fc9cbc` |
| 7 Configuración (sedes, recursos, servicios, reglas, `setup`) | Hecha | `9c7b5de92` |
| 8 Franjas, sesiones publicadas, bloqueos | Hecha | `42a3d4d0f` |
| 9 Disponibilidad, reserva, transiciones, clientes de la cita, auditoría | Hecha (sin correr `dotnet test`: orden del owner) | `c083fc0c2`, `c7abf6da6`, `714679456` |
| 10 Calendario y escritorio (lecturas BFF) | Hecha (sin correr `dotnet test`: orden del owner) | `e53a4e10b` |
| 11 Clientes: fusión y borrado | Hecha (sin correr `dotnet test`: orden del owner), más el arreglo de bloqueos por sede (corrección 30) | `bf1c15b8c`, `d60b131a9` |
| 12 Recordatorio por WhatsApp (dos commits) | Por hacer | |
| 13 Verificación final | Por hacer | |

**Lo primero que hay que correr en tu máquina** (el trabajo hasta aquí se hizo en un contenedor sin Docker, con el SDK 10.0.115 y un
Postgres 16 nativo; `global.json` no se tocó en ningún commit):

1. `dotnet restore Backend.slnx --locked-mode` y `dotnet build Backend.slnx`. Con el SDK 10.0.115 daba un solo error, en
   `MediaByteRangeTests` (Mensajería, CA1859), que no es de la Agenda; con el 10.0.400 debería no aparecer. Si aparece, no es de este trabajo.
2. `dotnet test tests/Modules/Scheduling/Modules.Scheduling.IntegrationTests`: **las 16 pruebas de persistencia nunca corrieron con
   Testcontainers**, sólo contra un Postgres 16 nativo (16 de 16). Con Postgres 18 (`postgres:18-alpine`) deberían dar igual; si alguna falla, es
   lo primero que hay que mirar.
3. Las unitarias y de arquitectura ya pasan: `Modules.Scheduling.UnitTests` (115), `ArchitectureTests` (303), `Modules.Authorization.UnitTests`
   (81), `Modules.Tenancy.UnitTests` (477).
4. Los 122 fallos de `Bootstrapper.UnitTests` que se vieron eran todos de Docker no disponible (Valkey, candado de arranque, fusión de
   clientes); con Docker no deberían estar.

**Cómo seguir:** la Tarea 7 abre el Bloque C y es la primera que necesita el host de la API y su harness (`SchedulingApiHarness`, calco de
`HrApiHarness`). Los repositorios nacen con su caso de uso (corrección 11). El aprovisionador (`ISchedulingProvisioner`) ya existe pero
nadie lo llama todavía: lo invoca cada caso de uso antes de leer, con el nombre del tenant que da un puerto de Bootstrapper (corrección 12).

**Trampas ya vividas (no repetirlas):**
- Los analizadores rechazan nombres: `*Exception` en un tipo que no lo es (CA1711), parámetros `to`/`end`/`from` en interfaces (CA1716) y
  `.First()` sobre una lista indexable (CA1826).
- Cada categoría de permiso necesita su etiqueta en `PermissionVocabulary`, y cada `(entidad, acción)` de la matriz de roles es única.
- Un rol de sistema con un permiso de núcleo (Clientes) se vuelve asignable en un tenant sin la Agenda: por eso los roles no los llevan.
- La migración inicial lleva dos sentencias a mano (`CREATE EXTENSION btree_gist` y la restricción `EX_sessions_resource_occupancy`):
  si se regenera, hay que volver a agregarlas. La prueba `TheMigrationAppliesFromScratchAndTheModelHasNoPendingChanges` lo ataja.
- Npgsql sólo escribe `DateTimeOffset` con offset cero: todo instante se convierte a UTC antes de guardar.
- `dotnet-ef` debe ser la 10.0.13 (la de EF Core del repo).

**Pendiente de decidir (owner):** la posición de «Agenda» en el menú del frontend; si cualquier cita bloquea borrar al cliente (D-11, la spec
dice que sí); y si un recordatorio por WhatsApp necesita el consentimiento del cliente (Tarea 12).

**`docs/estado.md`:** no se toca en esta rama. Al mergear a `develop`, agregar la fila «Agenda: citas, franjas, recursos y cupos» (backend,
Bloques A y B construidos) con estos commits y el enlace a la spec y a este plan, y el renglón del Registro.

**Spec:** `docs/superpowers/specs/2026-11-21-agenda-citas-design.md` (aprobada por el owner el 2026-11-21). Este plan la baja a tareas y
**corrige tres puntos de la spec** tras leer el código (sección «Correcciones a la spec»).

**Goal:** que un tenant con el módulo `scheduling` abra franjas de tiempo con servicios, recursos y cupo, y que se reserve dentro de
ellas sin doble reserva, desde el back office o desde un caso de uso que el agente de IA llamará después; con alcance por permiso
(secretaria ve todo, médico ve lo suyo), sede y reglas aprovisionadas al primer acceso, dos roles de sistema y recordatorio por WhatsApp.

**Architecture:** módulo nuevo `Scheduling` (esquema `scheduling`, cuatro capas, calcado de `Hr` y `Loans`). Agregados: `Location`,
`Resource`, `Service`, `AvailabilityBlock` (con sus servicios y recursos), `Closure`, `Session` (recurso + servicio + inicio + cupo;
**toda cita pertenece a una sesión**), `Appointment` y `SchedulingSettings` (una fila por tenant). La disponibilidad **se calcula**, no se
guarda (`AvailabilityEngine`, función pura sobre franjas, cierres, sesiones, reglas y festivos). La doble reserva la impide una
restricción de exclusión sobre `scheduling.sessions` (`btree_gist`); la reserva corre en una transacción que bloquea la fila del recurso.
Miembros, clientes, festivos, zona horaria y envío de WhatsApp entran por puertos `IScheduling*` con adaptadores en
`src/Bootstrapper/Scheduling*.cs`: Scheduling nunca referencia `Modules.Customers`, `Modules.Tenancy` ni `Modules.Messaging`.

---

## Correcciones a la spec

La spec se corrige en el mismo commit que este plan.

1. **Códigos y status.** `ApiExceptionHandler` mapea por **tipo de excepción**, no por código (plan de Hr, «Global Constraints»):
   `scheduling.appointment.slot_taken` y `scheduling.appointment.session_full` son `ResourceConflictException` (**409**);
   `…invalid_transition`, `…outside_rules`, `scheduling.closure.overlaps_appointments` y las de validación son **422**
   (`SchedulingDomainException` / `ValidationException`). **Se quita `scheduling.resource.not_in_scope`**: un recurso fuera de alcance
   responde 403 `authorization.denied` (regla de aislamiento del repo), no un código propio.
2. **Festivos (D-10).** No se mueve nada de Mensajería. Se copia el precedente de Soporte (`src/Bootstrapper/SupportBusinessCalendarSource.cs`):
   un puerto `ISchedulingHolidayCalendar` en Application y su adaptador en Bootstrapper sobre `ColombianHolidays.For(year)`, sólo con el
   país del tenant `CO`. Los tipos de rango y de día de la semana son tres `record` de una línea y se declaran en `Scheduling.Domain`.
   El plan **no toca** `Modules.Messaging.Domain`.
3. **`btree_gist`.** Verificado por precedente: `InitialMessaging` ya hace `CREATE EXTENSION IF NOT EXISTS btree_gin` (y `pg_trgm`,
   `unaccent`) en producción, y `btree_gist` es del mismo paquete `contrib`. La migración lo crea igual. El plan B (bloqueo de fila sin
   restricción) **no se implementa**; sólo se documenta como remedio si una base concreta lo negara.
4. **Auditoría.** La spec decía `IAuditRecorder` y outbox; Hr y Loans usan un `I<Modulo>AuditPublisher` propio sin eventos de outbox.
   Scheduling copia `IHrAuditPublisher` y **no publica eventos**. La tarea 9 verifica en `HrAuditPublisher` cuál de los dos caminos usa.
   **Adelantada (decisión del owner, 2026-11-22):** la auditoría de la configuración (sedes, recursos, servicios, reglas, franjas, bloqueos y sesiones publicadas) se hizo antes de la Tarea 9, con `ISchedulingAuditPublisher`, `SchedulingAuditActions` y la proyección de `audit.entries` (migración `AddSchedulingAuditProjection`, vacía). La Tarea 9 sólo añade la auditoría de las citas, con el mismo puerto.

## Correcciones durante la implementación

5. **Los roles de sistema no llevan permisos de Clientes.** Con `customers.customer.read|manage` en el rol, un tenant sin Agenda vería
   «Gestor de agenda» como asignable (`RoleAssignability` ofrece el rol si concede al menos un permiso que el tenant tiene, y los de Clientes
   son de núcleo). La prueba `TheSchedulingRolesAreAssignableOnlyWithTheSchedulingModule` lo fija. La búsqueda y el alta de clientes de la
   cita irán por la propia Agenda, como en el POS (spec 2026-10-14, decisión 4): `GET` y `POST …/scheduling/customers`, con
   `appointment.manage`, y su adaptador en Bootstrapper (calco de `PosCustomerLookup`). Entra en la Tarea 9.
6. **`SchedulingDomainException` se movió de la Tarea 3 a la 2**, porque `SchedulingLayerTests` necesita un tipo del ensamblado de dominio.
7. **`SchedulingAuthorization` no devuelve el `ResourceScope`.** Autoriza (tenant, permisos y módulo) y expone `CanSeeEveryAgenda`; resolver
   los recursos vinculados a la membresía necesita el repositorio de recursos y entra en la Tarea 7 (`ResourceScope.For`).
8. **`DateException` se llama `DateHours`**: el analizador CA1711 rechaza un tipo que no es excepción y termina en «Exception».
   El adaptador de festivos es `ISchedulingHolidayCalendar.HolidaysAsync(tenantId, firstDay, lastDay)` (CA1716 reserva `to` y `end`).
9. **Cada categoría de permiso necesita su etiqueta** en `PermissionVocabulary.CategoryLabels` (`["Agenda"] = "Agenda"`); sin ella falla
   `EveryActionAndCategoryHasALabel`. El permiso `agenda.read_all` ocupa la celda `scheduling.agenda` / `read` de la matriz de roles.

10. **El motor devuelve instantes en UTC**: Npgsql sólo escribe `timestamptz` con offset cero, y un `DateTimeOffset` con el offset del tenant
    (-05:00) habría fallado al guardar la primera sesión.
11. **Los repositorios no se crean en la Tarea 6**: cada uno nace con el caso de uso que lo usa (Tareas 7 a 10), para que ninguno exista sin
    una prueba que lo ejerza. La Tarea 6 deja el `SchedulingDbContext`, la migración, `ISchedulingUnitOfWork` y `ISchedulingProvisioner`.
12. **`ISchedulingProvisioner.EnsureAsync(tenantId, mainLocationName, ct)`** recibe el nombre de la sede: Infrastructure no conoce a Tenancy, y
    el caso de uso lo lee de `ITenantDirectory.GetDisplayNameAsync` por un puerto de Bootstrapper (Tarea 7). Con las reglas ya creadas
    devuelve sin abrir transacción: una lectura por clave.
13. **Mapeo de las listas**: los servicios y recursos de una franja van en columnas `uuid[]` (el orden de los recursos es el de la asignación
    «primero libre») y las semanas y la fecha puntual en `jsonb`. Son pocas franjas por tenant y se filtran en memoria; sus ids no tienen FK
    (los revisa Application al guardar). La proyección de `audit.entries` entra con la auditoría (Tarea 9), no antes.
14. **Verificación sin Docker**: las pruebas de integración usan Testcontainers y no corren en un contenedor sin Docker. Las de persistencia
    de la Tarea 6 se corrieron contra un Postgres 16 nativo con una copia local de `SchedulingDatabase` (no commiteada): 16 de 16 en verde. Para
    comprobar que la prueba vigila lo que dice, se desactivó la restricción de exclusión en la migración y fallaron exactamente las dos de
    solapamiento. Las de arriba de la API (Tareas 7 en adelante) sí necesitan el harness de siempre.

Tarea 9 (2026-11-22; ninguna de sus pruebas se corrió: el owner corre las suites al final):

15. **Código nuevo `scheduling.appointment.not_found` (404).** Toda entidad direccionable por id tiene el suyo y la cita no estaba en la tabla del
    contrato. Se agregó a `SchedulingErrorCodes` y a `TheContractCodesAreTheOnesTheFrontendWillMap`. Una cita del tenant fuera del alcance de quien
    la pide responde 403 (se revisa sobre lo que la base devolvió), una que no existe en el tenant, 404.
16. **El plazo de cancelación del cliente no tiene código propio:** cancelar por canal dentro de `clientCancelCutoffHours` responde 422
    `scheduling.appointment.invalid_transition` con un mensaje que dice cuántas horas. Si el frontend necesita distinguirlo, es un código nuevo
    que decide el owner. La regla vive en `AppointmentCancellationRules.ClientMayCancel` (`startsAt - now >= cutoff`; con plazo 0 el cliente cancela
    hasta la hora de inicio, no después).
17. **`source` en el cuerpo de reservar y de cancelar.** El contrato no decía cómo distingue el servidor «back office» de «canal» en la misma
    ruta. `BookAppointmentRequest.Source` y `CancelAppointmentRequest.Source` admiten `BackOffice` (por defecto) o `Channel`; el agente de IA
    de la Agenda llamará al mismo caso de uso con `Channel`, y mientras tanto la secretaria puede registrar una cita que pidieron por WhatsApp.
    Con `Channel`: el servicio debe tener `bookableByChannel` (si no, 422 `outside_rules`), la cita nace según `confirmationMode` y sin actor,
    y la cancelación aplica el plazo. `allowClientChoice` de la franja **no** se aplica todavía (el cliente por canal puede nombrar recurso).
18. **`AvailabilityQuery.IgnoreOccupancy`.** Para separar 422 `outside_rules` («las reglas no ofrecen ese inicio») de 409 («alguien lo tiene»)
    el motor responde, con esa bandera, sólo lo que dicen franjas, cierres, festivos, anticipación y horizonte (una sesión llena, o una publicada
    que el tenant quitó, cuenta como ofrecida). Una vez con el candado, el motor corre sin la bandera y con las sesiones recién leídas.
19. **Candado y orden.** `LockResourcesAsync` hace `SELECT id FROM scheduling.resources … ORDER BY id FOR UPDATE` de **todos** los recursos candidatos de
    una vez (en orden de id, así dos reservas con los mismos candidatos no se cruzan), dentro de `ISchedulingUnitOfWork.BeginTransactionAsync`
    (nuevo, calco de `ICatalogUnitOfWork`). Cancelar y reprogramar toman el mismo candado, para serializarse con las reservas del recurso.
    **409 `session_full` sólo si la sesión llena es compartida (cupo > 1); una de cupo 1 llena es `slot_taken`**, que es lo que las dos pruebas de
    concurrencia del plan esperan. Reprogramar libera el cupo viejo y guarda **antes** de tomar el nuevo (la exclusión se evalúa sentencia a
    sentencia: el rango que la cita deja estorbaría al que ocupa, con un margen o al pasar al inicio siguiente); todo va en una transacción.
20. **Migración `AddAppointmentClientRequestId`.** La idempotencia por `clientRequestId` necesita columna: `appointments.client_request_id`
    (`varchar(100)`, nula) con índice único `(tenant_id, client_request_id) WHERE client_request_id IS NOT NULL`. Una repetición se contesta antes de
    llegar al índice (dos veces: antes y después del candado); si dos primeras peticiones con la misma llave caen en recursos distintos a la vez,
    el índice rechaza a la segunda con 409 `slot_taken` y su reintento encuentra la cita de la primera. `dotnet-ef` de esta máquina es la
    10.0.11 (la librería es la 10.0.13): la migración y el snapshot salieron con aviso de versión; conviene regenerarlos con la 10.0.13 si el diff difiere.
21. **`GET availability` acota el rango a 62 días** (`AvailabilityRequestValidator.MaxDays`, 422 en `to`) para acotar el trabajo del motor.
22. **Límites conocidos, no resueltos:** (a) una sesión abierta por una reserva que se vació y se reabre conserva el rango y el cupo con que nació, aunque
    el servicio o la franja hayan cambiado después; (b) `Appointment.BlockId` no cambia al reprogramar; (c) reprogramar sólo ofrece recursos de la sede de la
    cita.

Tarea 10 (2026-11-22; ninguna de sus pruebas se corrió: el owner corre las suites al final):

23. **`freeCount` y `counters.free` son inicios libres, no cupos.** Salen de `AvailabilityEngine` (una pasada por servicio activo, con las reglas
    del tenant: anticipación y horizonte incluidos) y cuentan los inicios *distintos* por recurso: un inicio que sirve a dos servicios es un hueco.
    Lo ya pasado, o dentro de la anticipación mínima, no cuenta. `FreeSlotCounter` lo comparte entre el calendario y el escritorio.
24. **`openWindows` es el horario, no lo libre.** Franjas del recurso expandidas en la zona del tenant, sin festivos (si la franja cierra en
    festivos) y fundidas cuando se tocan; no descuenta bloqueos (viajan en `closures`) ni citas. En una franja `PublishedSessions` el recurso sólo
    abre donde hay una sesión publicada y activa, que es lo único que el motor ofrece ahí (`OpenWindows`, pura y con prueba unitaria).
25. **Qué dibuja el calendario.** Las citas canceladas no viajan ni cuentan en `appointmentCount` (liberaron su lugar). `booked`/`capacity` son los
    de la sesión de la cita. `layout` es el del tenant (`SchedulingLayout.Of`, el mismo de `GET setup`), no el del alcance de quien mira.
    `date` y `from`/`to` son excluyentes (422 en `date`); rango máximo 62 días, como `GET availability` (422 en `to`). `resourceIds` se repite en la
    query (`?resourceIds=a&resourceIds=b`); uno fuera del alcance es 403 (se revisa antes de decir si existe) y uno que no existe o está archivado,
    404 `resource.not_found`. `locationId` sólo filtra las columnas y uno inexistente es 404 `location.not_found`.
26. **Escritorio: contadores y colas.** `appointments`, `attended`, `noShow` y `upcoming` son del día pedido (por hora de inicio, zona del tenant;
    sin `date`, hoy); `pending` y `unmarked` son colas de **cualquier día** (una solicitud por confirmar o una cita vencida sin marcar no se
    caducan a medianoche), y sus contadores son los totales aunque la lista se acote a 50 filas (`GetDeskHandler.QueueLimit`). `pending` va de la
    solicitud más vieja a la más nueva; `unmarked`, de la más reciente a la más vieja. «Sin marcar» es `DeskRules.Unmarked` (expresión que el
    repositorio traduce a SQL: `Confirmed` y `ends_at < ahora`).
27. **`requestedAgo` viaja como minutos enteros** (`DeskRules.RequestedAgoMinutes`, nunca negativo) con el reloj del servidor; el contrato no decía el
    tipo. Si el frontend prefiere un instante (`requestedAt`) o un texto, es un cambio de una línea que decide el owner.
28. **Puerto nuevo en lo existente.** `ISchedulingCustomerLookup.FindNamesAsync` (batch, sobre `FindNamesByIdsAsync` de Clientes): la grilla no puede
    pedir un cliente por celda. `IAppointmentReadRepository` es aparte de `IAppointmentRepository` (escritura, con historial y seguimiento) y filtra
    por alcance en la consulta. `AvailabilityLoader.LoadAsync` ganó una sobrecarga que recibe el `TenantCalendar` ya leído (el escritorio necesita
    «hoy» antes de elegir el día).
29. **Observación, no tocada:** `Closure.AppliesTo(resource)` (y por tanto el motor) trata un bloqueo de sede completa como aplicable a *cualquier*
    recurso, sin mirar la sede. Con varias sedes, un bloqueo de una cierra los huecos de las otras en el cálculo de `freeCount` aunque el calendario
    sólo dibuje el bloqueo en las columnas de su sede. Con una sola sede no se nota; conviene corregirlo en `AvailabilityEngine.IsClosed` antes de
    activar varias sedes. **Corregida (commit `bf1c15b8c`):** ver la corrección 30.

Tarea 11 (2026-11-22; ninguna de sus pruebas se corrió: el owner corre las suites al final):

30. **Bloqueo de sede: `Closure.AppliesTo(resourceId, resourceLocation)`.** Un bloqueo con recurso aplica sólo a ese recurso; uno sin recurso, sólo a los
    recursos de **su** sede (`closure.LocationId` siempre existe: no hay bloqueo de todo el tenant, la spec dice «o toda la sede»). Para que el motor
    conozca la sede de cada recurso, `AvailabilityQuery.OfferedResources` pasó de `IReadOnlySet<ResourceId>` a `IReadOnlyDictionary<ResourceId, LocationId>`
    (lo arma `LoadedAvailability.Query`). `FreeSlotCounter`, el calendario, la disponibilidad y la reserva pasan por ahí; sin otros cambios de firma.
31. **Motivo nuevo `schedulingAppointments`** en `CustomerDeletionBlockReasons` (después de `supportTickets`, antes de `blockedContact`; está en `Ordered`).
    El frontend de Clientes tiene que mostrarlo (PD-6: cualquier cita, también la cancelada, bloquea el borrado; la salida es la fusión).
32. **Pruebas de la Tarea 11 sin correr.** Su RED fue de compilación (`CustomerDeletionBlockReasons.SchedulingAppointments` no existía). Las pruebas de integración
    siembran la cita por SQL con `session_replication_role = replica` (sin sede, recurso, servicio ni sesión), igual que el resto de las siembras de
    `CustomerDeletionAcrossModulesTests`; las réplicas de `CustomerMergeReferencesTests` llevan `scheduling.appointments` mínima. La fusión **no** suma
    citas a `CustomerMergeReferenceCounts` (la vista previa no cambia, como con soporte y cobros).

## Global Constraints

- **Rama.** El trabajo va en la rama de la sesión, `claude/trusting-archimedes-uk0ksk` (la que asigna el entorno; el `develop` local está
  desactualizado y no se usa de base). Antes de **cada** commit: `git branch --show-current` y que **no** diga `main`. No se mergea a
  `main`; el merge a `develop` lo decide el owner. Commits: conventional commits, **sin atribución de IA**.
- Comandos en PowerShell (`A; if ($?) { B }`, nunca `&&`). `Api.exe` corriendo bloquea el build: se avisa, no se detiene.
- Mensajes al usuario (`message`, `errors`) en español neutro, tuteando. Códigos, rutas, enums y permisos no se traducen. Comentarios en
  inglés que explican el porqué.
- Códigos de dominio con prefijo `scheduling.`, sólo los del «Contrato HTTP». Ningún status se arma en un handler: 404
  `ResourceNotFoundException`, 409 `ResourceConflictException`, 412 `RequestConcurrencyException("concurrency.conflict", …)`, 428
  `PreconditionRequiredException("precondition.if_match_required", …)`, 422 `SchedulingDomainException` o `ValidationException`.
  `src/Api/ApiExceptionHandler.cs` **no cambia**.
- Las claves de `errors` van en camelCase y con punto para lo anidado: todo validador hereda `SchedulingValidator<T>` (Tarea 2), calco de `HrValidator<T>`.
- **Doble capa de tenant:** la ruta lleva `{tenantId:guid}` y la política exige el permiso; el handler revalida con
  `SchedulingAuthorization.EnsureAsync` **antes** del repositorio y responde 403 `authorization.denied`, nunca 404. Todo método de
  repositorio recibe `tenantId` **y el alcance** (`ResourceScope`, Tarea 2): el repositorio filtra, el cliente no.
- **Cada `manage` exige también su `read`**; `agenda.read_all` no concede acciones, sólo amplía el alcance.
- **El inicio de una cita se recalcula en el servidor** (`AvailabilityEngine`); nunca se confía en el `startsAt` del cliente.
- Todo instante en UTC; las franjas se expanden en la zona horaria del tenant (`ITenantClock`, como Hr).
- Un permiso nuevo necesita **dos mitades**: la constante **y** su política en `AddAuthorization`; sin la política el síntoma es 500.
- TDD con evidencia literal de RED y GREEN. Si el fallo esperado es de compilación, la evidencia es la salida del build y no se
  levantan contenedores. La integración se filtra por nombre de prueba mientras se itera; la clase completa corre una vez al cierre del
  bloque. `dotnet test` va por proyecto, en primer plano, con `-- xUnit.MaxParallelThreads=4`. Nunca dos corridas de integración a la
  vez. La suite completa sólo en la Tarea 13, limitada a los proyectos tocados.
- Proyectos nuevos: `dotnet restore Backend.slnx` genera sus `packages.lock.json` y actualiza los de quien los referencia; todos se
  commitean en la tarea que agrega el proyecto (el `Dockerfile` restaura con `--locked-mode`).
- Migraciones con el factory de diseño:
  `dotnet ef migrations add <Nombre> --project src/Modules/Scheduling/Modules.Scheduling.Infrastructure --context SchedulingDbContext -o Persistence/Migrations`.
- Un `INSERT`/`UPDATE` que traduzca un error de base va en Infrastructure y discrimina por **nombre de índice**, no sólo por `SqlState`.
- **Leer antes de tocar:** `.claude/rules/messaging.md` antes de la Tarea 12, y `.claude/rules/sql-entre-modulos.md` antes de la Tarea 11.
- `docs/estado.md` **no se toca en la rama**: se actualiza en `develop` al mergear.

## Review Focus

1. **Última plaza, N peticiones a la vez.** 20 `BookAppointment` paralelos por el último espacio de un recurso → exactamente una 201 y
   diecinueve 409 `slot_taken`; en una sesión de cupo 5 con 12 peticiones → cinco 201 y siete 409 `session_full`. Prueba: Tarea 9,
   `TwentyParallelBookingsOfTheLastSlotCreateExactlyOne` y `ASessionOfFiveSeatsNeverHoldsSix`.
2. **Alcance.** Un miembro sin `agenda.read_all` pide el calendario de un recurso ajeno → 403 `authorization.denied`, **no** 404, y el
   calendario sin filtro sólo trae lo suyo. Prueba: Tarea 10, `AMemberWithoutReadAllNeverSeesAnotherResource`.
3. **Aprovisionamiento.** Dos primeras peticiones concurrentes del mismo tenant → una sola sede principal y una sola fila de reglas.
   Prueba: Tarea 6, `TwoConcurrentFirstAccessesCreateOneMainLocation`.
4. **El servidor manda sobre el inicio.** `startsAt` a las 08:10 con servicios de 30 min en una franja que abre a las 08:00 → 422
   `scheduling.appointment.outside_rules`. Prueba: Tarea 9.
5. **Zona horaria.** Franja de 08:00 en `America/Bogota` ofrece 13:00Z, y un festivo colombiano la cierra. Prueba: Tarea 5.
6. **El margen ocupa, no se muestra.** Servicio de 30 min con margen de 10: tras reservar 08:00 queda bloqueado 08:30 (ocupa hasta 08:40) y el
   siguiente inicio libre es 09:00. Prueba: Tarea 5, `TheBufferBlocksTheNextStartButIsNotShownToTheClient`.
7. **Recordatorio.** Dos pasadas del worker sobre la misma cita envían **un** mensaje; una cita cancelada entre medio no se envía. Prueba:
   Tarea 12.
8. **Clientes.** Fusionar dos clientes mueve sus citas; borrar un cliente con una cita en cualquier estado → 422. Pruebas: Tarea 11.

## Decisiones tomadas

D-1 a D-13 de la spec. Dos de este plan: **PD-1** la sesión se desactiva (`is_active = false`) cuando su último cupo se libera, salvo las
publicadas; **PD-2** el inicio válido de un servicio dentro de una franja se genera **cada `duration` minutos alineado al inicio de la
ventana**, no cada `duration + buffer` (el margen sólo ocupa; deja huecos predecibles y no cambia la rejilla que ve el cliente).

Decisiones del owner posteriores a la Tarea 8:

- **PD-3** quitar una sesión publicada sin citas la borra; con sólo citas canceladas la **desactiva** (`is_active = false`, sigue marcada como
  publicada) porque la llave foránea del historial impide borrarla; con alguna cita no cancelada responde 422 `session.has_appointments`.
  «Publicada e inactiva» distingue una sesión que el tenant quitó de una que una reserva abrió y se vació: **una reserva nunca reabre la
  primera** (`Session.Reserve` lanza; la Tarea 9 debe tratarla como no reservable, 409 `slot_taken`) y sólo volver a publicarla la
  reactiva. Quitarla de nuevo responde 404.
  Publicar de nuevo en la misma clave (recurso, servicio, inicio) **reactiva** la fila con el rango y el cupo actuales; una fila activa sigue
  dando 409 `slot_taken`. Una sesión inactiva no ocupa (la restricción `EX_sessions_resource_occupancy` ya es `WHERE (is_active)`) ni se ofrece
  (el motor ya filtra por `IsActive`). Auditoría: `scheduling.session.deactivated` y `scheduling.session.reactivated`.
- **PD-4** archivar un recurso o un servicio saca su id de `resourceIds`/`serviceIds` de **todas** las franjas del tenant, en la misma
  transacción (los ids están en `uuid[]` sin llave foránea; el filtro es en memoria). La franja que se queda sin recursos o sin servicios se
  archiva con ellos. Cada franja tocada deja `block.updated` y, si se archivó, `block.archived`. `resource.has_future_appointments` y
  `service.in_use` siguen bloqueando antes.
- **PD-5** (2026-11-22) la auditoría de editar una franja y de reactivar una sesión lista los campos que **de verdad cambiaron**
  (`BlockChanges.Between`; la reactivación sólo `endsAt`/`capacity`), no una lista fija.
- **PD-6** (2026-11-22) D-11 se mantiene: una cita en **cualquier** estado, canceladas incluidas, bloquea borrar al cliente (422). Para
  limpiar duplicados está la fusión de la Tarea 11, que mueve las citas.
- **PD-7** (2026-11-22) el recordatorio de WhatsApp (Tarea 12) no exige consentimiento explícito: quien da su teléfono para la cita acepta el
  recordatorio de esa cita. Se **omite** si el último consentimiento del cliente (`customers.data_consents`, última fila por
  `(occurred_at, sequence)`) es `Revoked`, o si tiene `MarketingOptOut`; el motivo queda en el registro del envío. **No hay baja automática
  por texto libre** (decisión P1 del owner, 2026-10-12: una palabra escrita no es consentimiento): quien responde para parar los
  recordatorios lo atiende un asesor en la bandeja. Pendiente de verificar en la Tarea 12: cómo se trata `Revoked` + `Medium = Other`, que
  no lleva identidad de canal.
- **Frontend** (2026-11-22): «Agenda» va en el grupo de ventas y atención del menú, junto a Pedidos, Cotizaciones, Embudo y Soporte. Este plan
  es sólo backend.

---

## Contrato HTTP

Todas bajo `/api/v1/tenants/{tenantId:guid}/scheduling`. `If-Match` con `version` en toda escritura sobre una fila existente (412/428).
Enums con su nombre. Colecciones de tamaño fijo completas, aun en cero.

### Rutas

| Ruta | Permiso(s) | Notas |
| --- | --- | --- |
| `GET setup` | `appointment.read` | `{ hasService, hasResource, hasBlock, layout }`; dispara el aprovisionamiento |
| `GET calendar?date\|from&to&locationId?&resourceIds?` | `appointment.read` | Una sola respuesta, filtrada por alcance (ver DTO) |
| `GET desk?date` | `appointment.read` | Vista de la secretaria; con alcance, los contadores son sólo de lo que ve |
| `GET availability?serviceId&from&to&resourceId?` | `appointment.read` | Espacios libres con cupos restantes y recursos posibles |
| `POST appointments` | `appointment.manage` | `BookAppointment`; idempotente por `clientRequestId` |
| `GET appointments/{id}` | `appointment.read` | Con historial |
| `POST appointments/{id}/confirm` · `/cancel` · `/attend` · `/no-show` | `appointment.manage` | Transiciones |
| `PUT appointments/{id}/reschedule` | `appointment.manage` | Cambia de sesión, conserva id y estado |
| `GET customers?search=`, `POST customers` | `appointment.manage` | Buscar y crear el cliente de la cita sin el padrón de Clientes (punto 5) |
| `GET/POST/PUT locations`, `…/{id}/archive` | `settings.manage` (lectura: `appointment.read`) | La principal no se archiva |
| `GET/POST/PUT resources`, `…/{id}/archive` | alta y estructura: `settings.manage`; datos y vínculo de **su** recurso: `availability.manage` | Archivar con citas futuras → 422 `resource.has_future_appointments` |
| `GET/POST/PUT services`, `…/{id}/archive` | `settings.manage` | `service.in_use` si tiene citas futuras |
| `GET/POST/PUT blocks`, `…/{id}/archive` | `availability.manage` | Dentro del alcance |
| `POST sessions`, `DELETE sessions/{id}` | `availability.manage` | Publicar o quitar una sesión (`PublishedSessions`) |
| `GET/POST closures`, `DELETE closures/{id}` | `availability.manage` | `closure.overlaps_appointments` si cruza citas vigentes |
| `GET/PUT settings` | lectura: `appointment.read`; escritura: `settings.manage` | Fila única con `version` |

### DTO del calendario (resumen)

`CalendarResponse { layout: Personal|Consolidated, timeZone, from, to, resources[] { id, displayName, locationId, appointmentCount,
freeCount, openWindows[{start,end}], closures[{id,start,end,reason}], appointments[{id,start,end,status,customerName,serviceName,source,
sessionId,booked,capacity}] } }`. `layout` y los conteos los calcula el backend; el cliente no cuenta recursos ni suma.

### DTO del escritorio (`desk`)

`{ date, counters{ appointments, pending, unmarked, attended, noShow, free }, pending[{appointmentId,customerName,serviceName,
resourceName,start,source,requestedAgo}], unmarked[{…}], upcoming[{…}] }`. «Sin marcar» = `Confirmed` con `ends_at < ahora`.

### Reglas de reserva (`settings`)

`confirmationMode`, `minNoticeMinutes`, `horizonDays`, `clientCancelCutoffHours`, `reminderEnabled`, `reminderOffsetHours`,
`reminderConnectionId`, `reminderTemplateId`, `version`. Defaults en `SchedulingDefaults`.

### Códigos de error

| Código | Status |
| --- | --- |
| `scheduling.appointment.slot_taken`, `…session_full` | 409 |
| `scheduling.appointment.invalid_transition`, `…outside_rules`, `…before_start` | 422 |
| `scheduling.closure.overlaps_appointments`, `scheduling.resource.has_future_appointments`, `scheduling.service.in_use` | 422 |
| `scheduling.resource.member_already_linked` (carrera sobre el índice único sede+miembro, traducida en Infrastructure por nombre de índice) | 422 |
| `scheduling.session.has_appointments` (una sesión con alguna cita **no cancelada** no se quita; con sólo canceladas se desactiva, ver PD-3) | 422 |
| `scheduling.location.not_found`, `scheduling.resource.not_found`, `scheduling.service.not_found`, `scheduling.block.not_found`, `scheduling.closure.not_found`, `scheduling.session.not_found`, `scheduling.appointment.not_found` | 404 |
| `scheduling.location.main_cannot_be_archived`, `scheduling.session.not_published_mode` | 422 |
| `scheduling.reminder.template_missing`, `…connection_invalid` | 422 |
| recurso fuera de alcance, sin permiso | 403 `authorization.denied` |

---

## Mapa de archivos

```
src/Modules/Scheduling/
  Modules.Scheduling.Domain/        ids; enums (AppointmentStatus, AppointmentSource, SessionMode, ConfirmationMode, AssignmentRule,
                                    BlockKind); TimeRange, WeekdayHours, DateHours; Location, Resource, Service, AvailabilityBlock,
                                    Closure, Session, Appointment, AppointmentEvent, SchedulingSettings, SchedulingDefaults;
                                    SchedulingErrorCodes, SchedulingDomainException
  Modules.Scheduling.Application/   SchedulingPermissions, SchedulingAuthorization, ResourceScope, SchedulingValidator,
                                    ISchedulingProvisioner, ISchedulingUnitOfWork, ISchedulingAuditPublisher, repositorios I*,
                                    ISchedulingMemberDirectory, ISchedulingCustomerLookup, ISchedulingHolidayCalendar,
                                    IAppointmentReminderSender, AvailabilityEngine, un archivo por caso de uso
  Modules.Scheduling.Infrastructure/ SchedulingInfrastructureExtensions, SchedulingDatabaseInitializer, Persistence/{SchedulingDbContext,
                                    SchedulingDbContextFactory, repositorios, SchedulingProvisioner, Migrations/},
                                    Workers/AppointmentReminderWorker
  Modules.Scheduling.Api/           SchedulingEndpoints
src/Bootstrapper/                   SchedulingMemberDirectory, SchedulingCustomerLookup, SchedulingHolidayCalendar,
                                    SchedulingReminderSender
tests/Modules/Scheduling/           Modules.Scheduling.UnitTests/, Modules.Scheduling.IntegrationTests/ (SchedulingApiHarness, *ApiTests)
tests/ArchitectureTests/ArchitectureTests/SchedulingLayerTests.cs
src/Modules/Messaging/              (Tarea 12) tipo de plantilla `AppointmentReminder`, aprovisionamiento y borrador
```

---

## Bloque A — Fundación (Tareas 1–3)

### Task 1: Clave de módulo `scheduling` en Tenancy

Calco del commit que agregó `hr` (migración `20261121015802_AddHrModuleKey`, `feat(tenancy): add the hr module key`).

**Files:**
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Domain/TenantModuleKey.cs` (`Scheduling` **después de `Hr`**, al final de `All`, fuera de
  `DefaultForNewTenants`, `[Scheduling] = [Customers]` en `Dependencies`)
- Modify: `src/Modules/Tenancy/Modules.Tenancy.Infrastructure/Persistence/TenancyDbContext.cs:151` (literal del CHECK: sumar `,'scheduling'`)
- Create: `…/Persistence/Migrations/<timestamp>_AddSchedulingModuleKey.cs` (+ Designer y snapshot)
- Test: `TenantModuleKeysTests`, `TenantModuleChangeBatchTests`, `OperatorReadHandlerTests`; integración `TenantModulesApiTests`,
  `OperatorConsoleApiTests`, `SeedStartupTests` (las mismas listas literales que tocó `hr`)

- [ ] **Step 1: Pruebas que fallan.** `SchedulingIsTheLastKnownKeyOutsideTheDefaults` (`Assert.Same`, último de `All`, no en
  `DefaultForNewTenants`), `SchedulingDependsOnCustomers`, `EnablingSchedulingWithoutCustomersIsInconsistent`
  (`tenancy.modules.inconsistent_dependencies`). El `Assert.Equal("hr", All[^1])` de `HrIsTheLastKnownKey…` deja de ser el último: se ajusta.
  Sumar `"scheduling"` a las listas literales que hoy terminan en `"hr"`.
- [ ] **Step 2: RED de compilación.** `dotnet build tests/Modules/Tenancy/Modules.Tenancy.UnitTests --no-restore` →
  `error CS0117: 'TenantModuleKeys' does not contain a definition for 'Scheduling'`.
- [ ] **Step 3: Declarar la clave**, ampliar el CHECK y generar la migración. `Down` borra las filas `scheduling` y repone la lista que
  termina en `'hr'`.
- [ ] **Step 4: GREEN.** Los dos `dotnet test` filtrados de Tenancy, `Passed!`.
- [ ] **Step 5: Commit** — `feat(tenancy): add the scheduling module key`

### Task 2: Esqueleto del módulo, permisos, alcance, autorización y roles de sistema

**Files:**
- Create: los cuatro `Modules.Scheduling.*.csproj`, los dos proyectos de pruebas y `SchedulingLayerTests.cs` — calco de Hr
- Modify: `Backend.slnx` (carpetas `/src/Modules/Scheduling/` y `/tests/Modules/Scheduling/`), `src/Api/Api.csproj` (referencias Api e
  Infrastructure), `src/Bootstrapper/Bootstrapper.csproj`, `src/Api/Program.cs` (`app.MapSchedulingEndpoints()` después de `MapHr…`)
- Create: `SchedulingPermissions`, `SchedulingAuthorization`, `ResourceScope`, `SchedulingValidator<T>`
- Modify: `src/Modules/Authorization/Modules.Authorization.Domain/SystemRoleKeys.cs` (dos claves nuevas en `All`),
  `src/Bootstrapper/QepServiceCollectionExtensions.cs` (cinco `PermissionDefinition` con `Shape` y `RequiredModules`, cinco políticas
  en `AddAuthorization`, permisos en la definición de `admin`, y dos `RoleDefinition`; validadores por ensamblado)

**Interfaces:**
- Produces: `SchedulingPermissions` (`scheduling.appointment.read|manage`, `scheduling.availability.manage`, `scheduling.agenda.read_all`,
  `scheduling.settings.manage`). `ResourceScope`: `All` o un conjunto de `resourceId` resueltos del miembro
  (`ISchedulingMemberDirectory.FindBySubjectAsync` + `resources.member_id`); `Includes(resourceId)`. `SchedulingAuthorization.EnsureAsync(tenantId,
  required permissions, …)` devuelve el `ResourceScope`. Roles: `scheduling_manager` («Gestor de agenda») con `appointment.*`,
  `availability.manage` y `agenda.read_all`, y **ningún** permiso de Clientes (corrección 5); `scheduling_provider` («Profesional de agenda»)
  con lo mismo **sin** `agenda.read_all`. Ninguno lleva `settings.manage`.

- [ ] **Step 1: Pruebas que fallan.** `SchedulingLayerTests` (capas y que Scheduling no referencia `Modules.Customers.*`, `Modules.Tenancy.*`
  ni `Modules.Messaging.*`); en `CompositionRootTests`: `EveryRegisteredPermissionDeclaresItsShape` y la de políticas cubren los cinco;
  `SystemRoleKeysTests`: las dos claves son reservadas; `RoleAssignabilityTests`: con `scheduling` apagado ninguno es asignable, con
  prendido sí. Unitaria de `ResourceScope`.
- [ ] **Step 2: RED** (build: no existen los tipos).
- [ ] **Step 3: Implementar**, `dotnet restore Backend.slnx` y commitear los `packages.lock.json`.
- [ ] **Step 4: GREEN.** `ArchitectureTests`, `CompositionRootTests`, `SystemRoleKeysTests`, `RoleAssignabilityTests`.
- [ ] **Step 5: Verificar** que `admin` recibe los permisos (la definición de `admin` los lista, como los de Hr) y que los permisos de
  Clientes: ninguno (corrección 5).
- [ ] **Step 6: Commit** — `feat(scheduling): module skeleton, permissions, scope and system roles`

### Task 3: Tipos puros, calendario de festivos y reloj

**Files:**
- Create: `TimeRange`, `WeekdayHours`, `DateHours`, `SchedulingErrorCodes`
- Create: `ISchedulingHolidayCalendar` (Application) y `src/Bootstrapper/SchedulingHolidayCalendar.cs`, calco de
  `SupportBusinessCalendarSource` (festivos sólo con país `CO`)
- Test: `tests/Bootstrapper/Bootstrapper.UnitTests/SchedulingHolidayCalendarTests.cs`

- [ ] **Step 1:** Prueba `ColombianHolidaysAreClosedOnlyForTenantsInColombia` (calco de `SupportBusinessCalendarTests`) y de `TimeRange`
  (`From < To`, 0..1440). **Step 2:** RED. **Step 3:** implementar. **Step 4:** GREEN.
- [ ] **Step 5: Commit** — `feat(scheduling): schedule value types and the holiday calendar port`

---

## Bloque B — Dominio y persistencia (Tareas 4–6)

### Task 4: Dominio

**Files:** `Location`, `Resource`, `Service`, `AvailabilityBlock`, `Closure`, `Session`, `Appointment`, `AppointmentEvent`,
`SchedulingSettings`, `SchedulingDefaults` y los ids/enums del mapa de archivos.

**Reglas que las pruebas fijan:**
- `Location`: exactamente una principal; no se archiva. `Resource`: nombre no vacío; el vínculo a miembro es opcional.
- `Service`: `duration > 0`, `buffer >= 0`, la duración múltiplo de 5.
- `AvailabilityBlock`: al menos un servicio y un recurso; rangos dentro de 0..1440 y sin solaparse en el mismo día; `capacity >= 1`;
  `session_mode` sólo cuenta con `capacity > 1`; `valid_from <= valid_to`; una franja `OneOff` lleva exactamente una fecha.
- `Session`: `booked <= capacity`; `Reserve` y `Release` suben y bajan `booked`; al llegar a 0 se desactiva salvo que sea publicada (PD-1).
- `Appointment`: máquina de estados de la spec; `Completed` y `NoShow` sólo con `now >= starts_at`; `Reschedule` valida y deja evento.
- `SchedulingSettings.Create`: los valores de `SchedulingDefaults`; `min_notice >= 0`, `horizon_days` 1..365, `reminder_offset` 1..168.

- [ ] **Step 1: Pruebas que fallan** (unitarias, una clase por agregado; nombres descriptivos, p. ej. `ASessionNeverHoldsMoreThanItsCapacity`,
  `ANoShowBeforeTheStartIsRejected`, `ACancelledAppointmentCannotBeConfirmed`).
- [ ] **Step 2: RED** (build). **Step 3: Implementar.** **Step 4: GREEN** (`Modules.Scheduling.UnitTests`).
- [ ] **Step 5: Commit** — `feat(scheduling): domain aggregates`

### Task 5: `AvailabilityEngine`

Función pura: entra `service`, rango, franjas vigentes, cierres, sesiones activas, reglas, `now` y el conjunto de festivos; sale la lista de
inicios con cupos restantes y recursos posibles. Sin acceso a datos.

- [ ] **Step 1: Pruebas que fallan**, entre otras:
  `AWeeklyBlockExpandsInTheTenantTimeZone` (08:00 en `America/Bogota` ⇒ 13:00Z), `AHolidayClosesABlockThatClosesOnHolidays`,
  `AClosureRemovesItsRangeOnlyForItsResource`, `StartsAreGeneratedEveryServiceDurationAlignedToTheWindow` (PD-2),
  `TheBufferBlocksTheNextStartButIsNotShownToTheClient`, `MinNoticeAndHorizonCutTheEdges`,
  `ThreeResourcesWithOneSeatEachOfferThreeSeatsAtTheSameStart`, `FirstBookingOpensJoinsAnOpenSessionOfTheSameServiceAndStart`,
  `PublishedSessionsOnlyOffersThePublishedOnes`, `ADifferentOpenSessionBlocksTheOverlap`, `AFixedClassIsAWindowAsLongAsTheService`.
- [ ] **Step 2: RED. Step 3: Implementar. Step 4: GREEN.**
- [ ] **Step 5: Commit** — `feat(scheduling): availability engine`

### Task 6: Persistencia, migración inicial y aprovisionador

**Files:** `SchedulingDbContext` (+ factory de diseño), configuraciones, repositorios, `SchedulingUnitOfWork`, `SchedulingProvisioner`,
`SchedulingInfrastructureExtensions`, `SchedulingDatabaseInitializer`, migración `InitialScheduling`; `Program.cs`
(`InitializeSchedulingDatabaseAsync` **después de Audit**, como Hr).

**Migración (a mano sobre lo generado):**
- `CREATE EXTENSION IF NOT EXISTS btree_gist;` antes de las tablas.
- `scheduling.sessions`: restricción de exclusión
  `EXCLUDE USING gist (resource_id WITH =, tstzrange(starts_at, occupies_until, '[)') WITH &&) WHERE (is_active)` y único
  `(resource_id, service_id, starts_at)`. `occupies_until` = `ends_at + buffer`, columna calculada al insertar.
- `locations`: único parcial `(tenant_id) WHERE is_main`. `resources`: único `(location_id, member_id) WHERE member_id IS NOT NULL`.
  `settings`: único `(tenant_id)`.
- Todo con `tenant_id` y `version` donde la spec lo pide. FK reales entre las tablas del esquema; ninguna hacia otros módulos.

**`SchedulingProvisioner.EnsureAsync`:** calco de `PipelineProvisioner` (`INSERT … ON CONFLICT DO NOTHING RETURNING id`, SQL crudo en una
transacción propia): crea la sede principal con el nombre del tenant y la fila de reglas; sólo la llamada que insertó siembra el resto.

- [ ] **Step 1: Pruebas que fallan** (integración, base real): `TwoConcurrentFirstAccessesCreateOneMainLocation`,
  `ProvisioningTwiceChangesNothing`, `TheExclusionConstraintRejectsAnOverlapOfTheSameResource`,
  `TheConstraintAllowsTheSameRangeOnAnotherResource`, `AnInactiveSessionDoesNotBlockItsRange`, `TheMigrationAppliesFromScratch`.
- [ ] **Step 2: RED. Step 3: Implementar y generar la migración.** **Step 4: GREEN.**
- [ ] **Step 5: Traducción de errores.** El adaptador de Infrastructure traduce `23P01` (exclusión) por **nombre de índice** a
  `ResourceConflictException("scheduling.appointment.slot_taken")`; el resto de únicos, a su código. Prueba por cada índice.
- [ ] **Step 6: Commit** — `feat(scheduling): persistence, initial migration and provisioner`

---

## Bloque C — Casos de uso y rutas (Tareas 7–10)

### Task 7: Configuración — sedes, recursos, servicios, reglas y `setup`

**Files:** un archivo por caso de uso en Application, `SchedulingEndpoints` (grupo base y esta parte), validadores, DTOs, `ISchedulingMemberDirectory`
con su adaptador `src/Bootstrapper/SchedulingMemberDirectory.cs` (calco de `HrMemberDirectory`).

- [ ] **Step 1: Pruebas que fallan** (`SchedulingApiHarness`, calco de `HrApiHarness`): el primer `GET setup` aprovisiona; crear un servicio,
  un recurso y editar las reglas con `If-Match`; `ASecondMainLocationCannotBeCreated`; `TheMainLocationCannotBeArchived`;
  `AResourceCannotBeArchivedWithFutureAppointments`; un miembro de otro tenant como vínculo ⇒ 422; sin `settings.manage` ⇒ 403;
  `LayoutIsPersonalWithOneLocationAndOneResource` y `LayoutBecomesConsolidatedWithTheSecondResource`.
- [ ] **Step 2: RED. Step 3: Implementar. Step 4: GREEN** (por nombre de prueba).
- [ ] **Step 5: Commit** — `feat(scheduling): locations, resources, services and settings`

### Task 8: Franjas, sesiones publicadas y bloqueos

- [ ] **Step 1: Pruebas que fallan:** crear franja semanal con varios recursos y servicios; el alcance (un médico sin `read_all` sólo
  crea franjas de **su** recurso; una con un recurso ajeno ⇒ 403); `ABlockWithAnEmptyResourceListIsRejected`; publicar una sesión sólo con
  `PublishedSessions` (`scheduling.session.not_published_mode`); `AClosureOverlappingAnActiveAppointmentIsRejected` (D-12); colección de
  recursos de la franja vuelve entera y en orden.
- [ ] **Step 2: RED. Step 3: Implementar. Step 4: GREEN.**
- [ ] **Step 5: Commit** — `feat(scheduling): availability blocks, published sessions and closures`

### Task 9: Disponibilidad, reserva y transiciones

**Files:** `ListAvailability`, `BookAppointment`, `ConfirmAppointment`, `CancelAppointment`, `RescheduleAppointment`, `MarkAttended`,
`MarkNoShow`, historial y auditoría (`ISchedulingAuditPublisher`, calco de `IHrAuditPublisher`; **verificar primero en `HrAuditPublisher`
cuál de los dos caminos de auditoría usa y copiar ese**), `IAppointmentRepository`, `ISchedulingCustomerLookup` con su adaptador
`src/Bootstrapper/SchedulingCustomerLookup.cs` (valida que el cliente sea del tenant y devuelve nombre y teléfono).

**`BookAppointment`, en una transacción:** autorización y alcance → `ListAvailability` recalcula que el inicio es válido → `SELECT … FOR
UPDATE` de la fila del recurso → busca o crea la sesión → comprueba cupo → inserta cita e historial → auditoría. Sin recurso elegido, la
asignación (`FirstFree`) ocurre aquí. Idempotente por `clientRequestId` (misma respuesta, sin segunda cita).

- [ ] **Step 1: Pruebas que fallan:** `TwentyParallelBookingsOfTheLastSlotCreateExactlyOne`, `ASessionOfFiveSeatsNeverHoldsSix`,
  `AStartThatIsNotAValidStartIs422OutsideRules`, `ARepeatedClientRequestIdReturnsTheSameAppointment`,
  `ABackOfficeBookingIsConfirmedAndAChannelOneFollowsTheMode`, `APendingAppointmentAlreadyHoldsItsSeat`,
  `CancellingTheLastBookingFreesTheRange`, `RescheduleKeepsIdAndStatusAndLeavesAnEvent`,
  `AClientCannotCancelInsideTheCutoffButTheBackOfficeCan`, `AnAppointmentWritesItsAuditAndHistory`,
  `NoShowBeforeTheStartIs422`. Cada una verifica también historial y auditoría, no sólo el status.
- [ ] **Step 2: RED. Step 3: Implementar. Step 4: GREEN** (la concurrencia corre sola, con `MaxParallelThreads=4`).
- [ ] **Step 5: Commit** — `feat(scheduling): availability, booking and appointment transitions`

### Task 10: Lecturas BFF — calendario y escritorio

- [x] **Step 1: Pruebas que fallan:** `AMemberWithoutReadAllNeverSeesAnotherResource` (Review Focus 2), `ReadAllSeesEveryResource`,
  `CalendarReturnsOneResponseWithLayoutAndCounts`, `DeskCountersMatchWhatTheScopeSees`, `UnmarkedAreConfirmedAppointmentsAlreadyOver`,
  `ThePendingQueueIsOrderedByRequestTime`, `TheCountersOfEveryStatusTravelEvenAtZero`, calendario con `from > to` ⇒ 422.
- [x] **Step 2: RED. Step 3: Implementar** (las consultas filtran por `ResourceScope` en el repositorio). **Step 4: GREEN.** RED y GREEN fueron
  `dotnet build` (sin `dotnet test`: orden del owner).
- [ ] **Step 5: Cerrar el bloque:** correr las clases completas `*ApiTests` de Scheduling, en primer plano, y pegar la salida. **Pendiente: lo corre
  el owner con las suites al final.** Pruebas nuevas sin correr: `SchedulingCalendarApiTests` (10), `SchedulingDeskApiTests` (8) y
  `SchedulingCalendarReadTests` (unitarias).
- [x] **Step 6: Commit** — `feat(scheduling): calendar and desk reads`

---

## Bloque D — Integraciones y cierre (Tareas 11–13)

### Task 11: Clientes — fusión y borrado

**Antes:** leer `.claude/rules/sql-entre-modulos.md` (se carga sola al abrir `src/Bootstrapper/CustomerMergeReferences.cs`).

**Files:** `src/Bootstrapper/CustomerMergeReferences.cs` (`UPDATE scheduling.appointments SET customer_id`, sin subir `version`),
`src/Bootstrapper/CustomerDeletionReferences.cs` y la lista de bloqueos del borrado (`schedulingAppointments`, cualquier estado).

- [ ] **Step 1: Pruebas que fallan:** `AMergeMovesTheAppointmentsOfTheLoser`, `ADeleteIsBlockedByAnAppointmentInAnyState`; **sumar** el esquema
  `scheduling` a `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema` y a `CustomerDeletionAcrossModulesTests`,
  y a las réplicas de `CustomerMergeReferencesTests`.
- [ ] **Step 2: RED. Step 3: Implementar. Step 4: GREEN.**
- [ ] **Step 5: Commit** — `feat(scheduling): customer merge and deletion references`

### Task 12: Recordatorio por WhatsApp

**Antes:** leer `.claude/rules/messaging.md` y los precedentes recientes de plantillas del sistema: `OrderTemplateProvisioning`,
`WholesaleInvitationNoticeTemplateProvisioning` y `WholesaleInvitationNoticeOutbound` (Messaging), y `SupportReplyChannels.SendTemplateAsync`
(Bootstrapper). Esta tarea toca Mensajería: se **divide en dos commits** y la revisión del bloque mira los dos.

**12a — Mensajería:** `MessageTemplateKind.AppointmentReminder` (calco de la migración `WidenMessageTemplateKind` /
`AddWholesaleInvitationNoticeTemplateKind` para el CHECK de `kind`), variables en `SystemTemplateVariables` (`nombre_cliente`, `servicio`,
`fecha_hora`, `recurso`, `sede`) con su valor en el emisor, aprovisionamiento y borrador
(`POST/GET …/template-management/appointment-reminder`). Pruebas calco de las de `WholesaleInvitationNotice`.
- [ ] Commit — `feat(messaging): appointment reminder system template`

**12b — Agenda:** `IAppointmentReminderSender` (Application), adaptador `src/Bootstrapper/SchedulingReminderSender.cs` sobre el emisor de
Mensajería (calco de `SupportReplyChannels`), y `AppointmentReminderWorker` (`AddHostedService`, calco de `WebhookDeliveryWorker`).
Selecciona citas `Confirmed` con `starts_at - offset <= now` y `reminder_sent_at IS NULL`, de tenants con `scheduling` **y** `messaging`
prendidos, y `reminder_enabled`; envía con `clientId` `appointment-reminder:{appointmentId}`; marca `reminder_sent_at` y deja la entrada
«Recordatorio enviado» del historial; un fallo guarda código y reintenta con tope; cliente sin teléfono válido se omite con motivo.

- [ ] **Step 1: Pruebas que fallan:** `TwoPassesSendOneReminder`, `ACancelledAppointmentIsNotReminded`, `AnAppointmentThatAlreadyStartedIsNotReminded`,
  `ATenantWithoutMessagingNeverReminds`, `ACustomerWithoutAPhoneIsSkippedWithAReason`, `AFailureRetriesUpToTheCap`, `TheReminderLeavesAHistoryEntry`.
- [ ] **Step 2: RED. Step 3: Implementar. Step 4: GREEN.**
- [ ] **Step 5: Commit** — `feat(scheduling): appointment reminder worker`

### Task 13: Verificación final

- [ ] `dotnet format Backend.slnx --verify-no-changes`, `dotnet build Backend.slnx -warnaserror`.
- [ ] `dotnet test` por proyecto **sólo** de lo tocado: `Modules.Scheduling.UnitTests`, `Modules.Scheduling.IntegrationTests`,
  `Modules.Tenancy.*`, `Modules.Authorization.*`, `Modules.Messaging.*` (plantilla nueva), `Bootstrapper.UnitTests`, los de
  Customers que cubren la fusión y el borrado, y `ArchitectureTests`. Pegar `Passed!` de cada uno.
- [ ] Revisión fuerte de toda la rama, una sola tanda de correcciones.
- [ ] Revisar `README` (permisos y llaves) si lista módulos. **No** tocar `docs/estado.md` en la rama.

## Lo que queda fuera de este plan

El frontend (spec y plan propios en `qep-frontend`, con la pantalla de Franjas, el calendario, la vista de la secretaria, el asistente de
primer uso y la posición de «Agenda» en el menú, que decide el owner), el agente de IA que agenda por WhatsApp, y la lista de espera.
