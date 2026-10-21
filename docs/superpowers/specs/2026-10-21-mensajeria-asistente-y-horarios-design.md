# Mensajería: asistente en la conversación y horarios de atención

**Fecha:** 2026-10-21
**Módulos:** Messaging (dominio, persistencia con migración, endpoints, permiso nuevo), Bootstrapper (rol de
fábrica, definición y política del permiso, adaptadores). **Frontend:** `qep-frontend`, `features/messaging/`,
`features/tenant-settings/` y `routes/_authenticated/settings/`.
**Estado:** revisado con el developer el 2026-10-21. Las decisiones de §2 (D-A1 a D-A10 y D-H1 a D-H15) quedaron
ratificadas una por una; la única que cambió fue D-H9 (festivos de Colombia por interruptor, antes «fuera de
alcance»). Lo que sigue es el plan de implementación. **No se escribe código hasta que el developer lo diga.**
**Es un delta sobre** `2026-10-10-mensajeria-asignacion-bsuid-design.md` (asignación y eventos del historial) y
`2026-10-15-mensajeria-motivo-de-resolucion-design.md` (patrón de catálogo con `If-Match`). Lo que este
documento no toca sigue como está ahí.
**Concepto y mockup:** `2026-10-21-asistentes-investigacion.md` y https://claude.ai/artifact/G4YgH6Mq9wozG9iBBziCWh
(pantallas 4 y 5).
**Ramas:** `feature/mensajeria-asistente-y-horarios` desde `develop` en los dos repos.

## 1. Contexto y alcance

Es la **primera entrega** del módulo Asistentes (bots de flujo) y deja Messaging listo para recibirlo sin que el
bot exista todavía. Dos piezas que no dependen de nada y que el constructor va a necesitar:

1. **La conversación sabe quién la atiende.** Hoy sólo sabe si tiene asesor (`assigned_member_id`). Se agrega un
   estado de automatización: *sin asistente*, *asistente activo*, *asistente pausado*. La regla única del motor
   futuro es: **un asistente responde sólo si está activo y la conversación no tiene asesor**. Tomar la
   conversación o responderla pausa al asistente; se puede pausar sin tomar y devolver al asistente. Todo queda en
   el hilo como evento del sistema y en la bandeja como marca y filtro.
2. **Horarios de atención** con nombre, por tenant, asignados por conexión, con duplicar y predeterminado, y
   «abierto ahora». Viven en Messaging (la asignación es por `connection_id`), se administran en Configuración, y
   se exponen por un puerto para que el bloque «Horario de atención» del constructor y el mensaje fuera de horario
   los consulten.

**Dentro:**

- Columnas de automatización en `conversations`, con sus invariantes; método de dominio para que el motor futuro
  active el asistente; pausa automática al tomar y al responder; pausa y reanudación manuales con `If-Match`.
- Dos eventos del historial (`AutomationPaused`, `AutomationResumed`) y su texto en el hilo.
- `automation` en el resumen de la conversación, filtro `automation` en la lista y contador `automated`.
- Catálogo de horarios: CRUD, duplicar, predeterminado, asignación de conexiones, `openNow`; permiso nuevo.
- Festivos de Colombia calculados por el backend, por interruptor en el horario (D-H9).
- Puerto `IBusinessHours` para consumidores internos.
- Frontend: chip y botones en el encabezado del hilo, marca en la fila y filtro en la bandeja, textos de los
  eventos, página «Horarios de atención» en Configuración.

**Fuera** (§8): el motor del bot, los interactivos salientes, la autoría «bot» en los mensajes, el bloque de
horario en el constructor, festivos de otros países, el envío proactivo.

## 2. Decisiones

### 2.1 Asistente en la conversación

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-A1 | Tres estados: `None`, `Active`, `Paused`, en la **conversación**, no en la conexión. | La decisión «esta la atiende un humano» es por persona. La conexión dice si hay asistente; la conversación dice si lo está atendiendo. |
| D-A2 | **Nada pone `Active` en esta entrega.** Lo hará el motor (spec 4) con `Conversation.StartAutomation`. Las pruebas lo activan con un helper del harness. | Sin motor no hay quién decida que el asistente atiende. Dejar el método sin usar en producción es deliberado: fija el contrato. |
| D-A3 | **Tomar pausa; responder pausa; asignar por transferencia pausa.** La pausa por responder cubre el envío con autoasignación (`TryAutoAssignAsync`) y el envío de quien ya es el asignado. | Intercom y Chatwoot: lo que apaga al bot es que un humano actúe. Una conversación con asesor nunca tiene asistente activo (invariante I-1). |
| D-A4 | **Liberar no reanuda.** La conversación liberada queda `Paused`: pendiente de un humano. **Reanudar es explícito** (`POST …/automation/resume`). | Liberar significa «que la tome otro», no «que la retome el bot». |
| D-A5 | **Resolver pone `None`.** Al reabrirse (por `POST …/reopen` o por un entrante) sigue `None`; el motor decide al recibir el siguiente mensaje. | Chatwoot evalúa audiencia y horario al empezar y al reabrir. Una conversación resuelta no está siendo atendida por nadie. |
| D-A6 | **Reanudar libera la asignación** si la había, con dos eventos (`Released`, `AutomationResumed`). Reanudar exige `Open` y estado `Paused`; en `None` responde 422 `messaging.automation.not_available`. | Invariante I-1: no puede quedar activo con asesor. Hacerlo en un solo paso evita un estado intermedio. |
| D-A7 | **Pausar manual** exige `Active`; en `Paused` o `None` responde 422 (`already_paused` / `not_available`). No asigna. | Es «que el bot se calle», no «es mía». Para «es mía» está Tomar, que ya pausa. |
| D-A8 | Pausar y reanudar piden **`messaging.conversation.manage`**, con `If-Match`, como tomar y liberar. Sin permiso nuevo. | Son acciones sobre la conversación, de quien puede atenderla. |
| D-A9 | La pausa guarda **quién** (membresía, o nadie si fue el sistema) y **por qué** (`AdvisorTook`, `AdvisorReplied`, `AdvisorTransferred`, `Manual`, `Handoff`). `Handoff` lo usará el motor (traspaso desde el flujo). | El asesor que abre el hilo tiene que ver por qué está como está. |
| D-A10 | **Sin asistentes, la bandeja no cambia.** La marca y los botones del hilo aparecen sólo cuando `automation` no es `null`; el filtro «Asistente» y su contador aparecen sólo cuando `counts.automated > 0` o el filtro ya está puesto. Mientras no exista el motor (spec 4), nada de esto se ve en producción. | Decisión del developer (2026-10-21): no mostrar opciones de algo que el tenant no tiene. Lo construido queda listo y las pruebas lo ejercitan con el helper del harness. |

### 2.2 Horarios de atención

| ID | Decisión | Motivo |
| --- | --- | --- |
| D-H1 | Entidad `BusinessHoursSchedule` **en Messaging**, por tenant, con nombre; asignación por `connection_id` en una tabla propia de Messaging. | Integrations no puede referenciar Messaging y sus `Fields` son del proveedor (`field_unknown`). La asignación por conexión es dato de Messaging. |
| D-H2 | Semana: por día, hasta **3 franjas** `[desde, hasta)` en minutos locales, sin solaparse; un día sin franjas no atiende. **Excepciones** por fecha (hasta 100): sin franjas = no atiende, o franjas propias. | Mañana y tarde es común en Colombia; tres cubre un turno extra. Las excepciones son los festivos y los días especiales. |
| D-H3 | La **zona horaria es la del tenant** (`Tenant.TimeZone`, vía `ITenantClock`), no del horario. | Un solo huso por tenant; el horario se interpreta ahí. La página lo muestra en lectura con enlace a General. |
| D-H4 | **Predeterminado:** a lo sumo uno por tenant. Una conexión sin asignación usa el predeterminado; sin predeterminado, **se considera siempre abierto**. | Que el primer día funcione sin configurar nada; y que «sin horario» no cierre nada por accidente. |
| D-H5 | Asignación **desde la página de horarios**: cada horario lista las conexiones de Messaging y se marcan las que lo usan; marcar una la quita del horario anterior. Integraciones no cambia. | Evita que la feature de Integraciones importe la API de Messaging y que `PUT /connections/{id}` acepte un campo que no es del proveedor. |
| D-H6 | No se borra un horario **en uso** (422 `messaging.business_hours.in_use`) ni el **predeterminado** (422 `is_default`); primero se reasigna o se cambia el predeterminado. Borrado físico. | Igual que los motivos de resolución referenciados. Sin `deleted_at`: nada histórico apunta a un horario. |
| D-H7 | **Duplicar** crea «{nombre} (copia)» con la misma semana y excepciones, sin conexiones y sin predeterminado. | Lo que pidió el developer: no volver a marcar siete días. |
| D-H8 | Permiso nuevo **`messaging.business_hours.manage`** para escribir; leer con `messaging.conversation.read`. De fábrica en `admin`; **no** en `advisor`. | Misma regla que respuestas rápidas y motivos; el horario es configuración del negocio. |
| D-H9 | **Festivos de Colombia por interruptor.** El horario guarda `holiday_country` (`CO` o ninguno) y el backend calcula los festivos del año al evaluar «abierto»: un festivo cierra el día entero salvo que haya una **excepción con franjas** para esa fecha, que gana. No se materializan como excepciones; la página muestra los próximos en lectura (`UpcomingHolidays`). Un solo país por ahora; otros países quedan fuera (§8). | Decisión del developer (2026-10-21): entra ahora. El interruptor nunca vence, no gasta las 100 fechas y no hay que repetirlo cada año; un botón que copiara las 18 fechas obligaría a volver cada enero. La regla es ley (Ley 51 de 1983) y cabe en una función pura. |
| D-H10 | Las conexiones se validan y se listan con **`ListAllAsync`** (método nuevo del directorio: todas las del tenant, en cualquier estado, con nombre, canal y estado), no con `ListActiveAsync`. **El `GET /business-hours` las devuelve** (`Connections` del `BusinessHoursListDto`) y el editor las toma de ahí, no de `GET /messaging/connections`. Una fila de `connection_business_hours` cuya conexión ya no existe en Integrations es **huérfana**: no cuenta para `in_use` y se borra en la misma transacción del `DELETE` o del `PUT`. | `ListActiveAsync` —y por lo tanto `GET /messaging/connections`, que la página usaba en el borrador— sólo devuelve las `Active` con token: una conexión pausada desaparecería de la página y «Guardar» la desasignaría en silencio; una borrada bloquearía el `DELETE` sin nada que quitar. |
| D-H11 | **Un solo «Guardar»:** `SaveBusinessHoursRequest` lleva `ConnectionIds`; no hay `PUT /{id}/connections`. `POST /{id}/default` sube la versión del nuevo predeterminado **y** del anterior; la página relee la lista después de cualquier mutación y nunca encadena dos acciones con la misma versión congelada. | Dos requests por un formulario abren un estado a medias si el segundo falla, y cualquier bump de versión con la versión congelada da 412 en la siguiente acción. |
| D-H12 | **Franjas que cruzan medianoche no se soportan:** un turno 22:00–02:00 se escribe como 22:00–24:00 y 00:00–02:00 del día siguiente. El validador lo dice en el mensaje del campo. | Mantiene `[from, to)` dentro del día y `IsOpenAt` simple. Si un tenant lo necesita, es una extensión del dominio, no de la pantalla. |
| D-H13 | Sin modo lectura: la página exige `messaging.business_hours.manage`, como motivos y respuestas rápidas; la sección del menú sólo aparece con ese permiso. | Precedente del repo (`resolution-reasons-access-gate.tsx`). Una vista de lectura es una pantalla entera para alguien que sólo llegaría por URL. |
| D-H14 | **Duplicar numera:** «{nombre} (copia)», «{nombre} (copia 2)», … hasta encontrar uno libre, en el handler. | Duplicar dos veces no puede dar 409 sin que el usuario haya escrito nada. |
| D-H15 | Las **excepciones pasadas se podan** al guardar: el dominio descarta las de fecha anterior a «hoy» del tenant (`TenantCalendar.Today`) antes de aplicar el tope de 100. | Los festivos se acumulan unos 18 por año; sin poda el `PUT` termina fallando por el tope. |

## 3. Trazabilidad: historias → componentes

| # | Historia | Componentes |
| --- | --- | --- |
| 1 | «Como asesor, veo en la bandeja y en el hilo si una conversación la está atendiendo el asistente» | `ConversationAutomationDto` en `ConversationSummary`; `AutomationChip` en fila y encabezado |
| 2 | «Como asesor, al tomar o responder, el asistente se calla en esa conversación» | `Conversation.Take/TransferTo` + `PauseAutomation`; `OutboundGuards.AdmitAsync`; `TryAutoAssignAsync` (SQL) |
| 3 | «Como asesor, pauso el asistente sin tomar la conversación, y la devuelvo al asistente» | `POST …/automation/pause`, `POST …/automation/resume`; `useConversationAutomation` |
| 4 | «Como asesor, veo en el hilo quién pausó o reanudó y por qué» | `ConversationEventType.AutomationPaused/Resumed`; `describeConversationEvent` |
| 5 | «Como supervisor, filtro las conversaciones que tiene el asistente» | `ListConversationsQuery.Automation`; `counts.automated`; `FilterPopover` |
| 6 | «Como admin, defino los horarios del negocio con nombre y los asigno a cada número» | `/messaging/business-hours/*`; página `/settings/business-hours` |
| 7 | «Como admin, duplico un horario para no repetir la configuración» | `POST …/business-hours/{id}/duplicate` |
| 8 | «Como admin, veo si un número está abierto ahora» | `openNow`, `nextChangeAt` en `MessagingConnectionDto` y en el horario |
| 9 | «Como motor del bot (futuro), pregunto si la conexión está en horario» | `IBusinessHours.GetStatusAsync(tenantId, connectionId)` |
| 10 | «Como admin, prendo los festivos de Colombia y el número cierra esos días sin que yo los cargue» | `holiday_country`, `ColombianHolidays.For(year)`, `Switch` «Festivos de Colombia», `UpcomingHolidays` |

## 4. Backend: asistente en la conversación

### 4.1 Modelo

Migración `AddConversationAutomation` en Messaging (después de `20261019234726_AddCampaignRecipientsMessageIndex`):

```sql
ALTER TABLE messaging.conversations
  ADD COLUMN automation_status varchar(16) NOT NULL DEFAULT 'None',
  ADD COLUMN automation_paused_at timestamptz NULL,
  ADD COLUMN automation_paused_by_member_id uuid NULL,   -- referencia blanda a la membresía; NULL = sistema
  ADD COLUMN automation_pause_reason varchar(32) NULL,
  ADD COLUMN automation_started_at timestamptz NULL;

ALTER TABLE messaging.conversations
  ADD CONSTRAINT "CK_conversations_automation_status"
    CHECK (automation_status IN ('None','Active','Paused')),
  ADD CONSTRAINT "CK_conversations_automation_pause"
    CHECK ((automation_status = 'Paused') = (automation_paused_at IS NOT NULL AND automation_pause_reason IS NOT NULL)),
  ADD CONSTRAINT "CK_conversations_automation_started"
    CHECK ((automation_status = 'None') = (automation_started_at IS NULL)),
  ADD CONSTRAINT "CK_conversations_automation_reason"
    CHECK (automation_pause_reason IS NULL OR automation_pause_reason IN ('AdvisorTook','AdvisorReplied','AdvisorTransferred','Manual','Handoff')),
  -- I-1: nunca activo con asesor.
  ADD CONSTRAINT "CK_conversations_automation_assignment"
    CHECK (automation_status <> 'Active' OR assigned_member_id IS NULL),
  -- I-2 (D-A5): una resuelta no la atiende nadie. Sólo Resolve() la garantiza en el código; el CHECK es la red.
  ADD CONSTRAINT "CK_conversations_automation_resolved"
    CHECK (status <> 'Resolved' OR automation_status = 'None');

CREATE INDEX "IX_conversations_tenant_automation_activity"
  ON messaging.conversations (tenant_id, automation_status, last_activity_at DESC)
  WHERE automation_status <> 'None';
```

`automation_assistant_id` **no** se agrega ahora: el agregado Asistente no existe y una columna sin referente
confunde. El motor la agrega en su migración (spec 4), junto con el «paso n de m» que muestra el mockup.

### 4.2 Dominio (`Conversation.cs`)

Propiedades nuevas, con setter privado: `AutomationStatus` (enum `ConversationAutomationStatus`
`None | Active | Paused`), `AutomationStartedAt`, `AutomationPausedAt`, `AutomationPausedByMemberId`,
`AutomationPauseReason` (enum `AutomationPauseReason`).

Métodos:

```csharp
// Lo llama el motor (spec 4). Exige Open, sin asesor y None. Pone Active y StartedAt; Touch().
public void StartAutomation(DateTimeOffset now);

// Pausa manual o automática. Exige Active. Devuelve el evento AutomationPaused(actor?, reason).
public ConversationEvent PauseAutomation(Guid? actorMemberId, AutomationPauseReason reason, DateTimeOffset now);

// Reanudar. Exige Open y Paused. Si hay asesor, lo libera (evento Released) y después Active (evento AutomationResumed).
public IReadOnlyList<ConversationEvent> ResumeAutomation(Guid actorMemberId, DateTimeOffset now);

// Lo llama Resolve(): None y limpia las cinco columnas. No emite evento propio: el Resolved ya cuenta la historia.
private void ClearAutomation();
```

Cambios en los existentes:

- `Take(memberId, now)`: si `Active`, además de asignar, pausa con `AdvisorTook` y actor `memberId`. Devuelve
  **dos** eventos (`Taken`, `AutomationPaused`); hoy devuelve uno o `null`, así que la firma pasa a
  `IReadOnlyList<ConversationEvent>` (vacía = no-op). `TransferTo` igual, con `AdvisorTransferred` y actor el
  que transfiere. `Release` no toca la automatización (D-A4).
- `Resolve`: llama a `ClearAutomation()`. `Reopen`: no toca nada (queda `None`).
- Invariante **I-1** en el agregado: `Active ⇒ AssignedMemberId is null`, además del `CHECK`.

Errores nuevos en `MessagingErrorCodes`: `messaging.automation.not_available` (está en `None`),
`messaging.automation.already_paused`, `messaging.automation.already_active`. Reanudar o pausar una resuelta
responde el existente `messaging.conversation.not_open`, y **se comprueba primero** (`Open` antes que el estado
de automatización): por I-2 una resuelta siempre está en `None`, y sin ese orden el cliente recibiría
`not_available` para una conversación que simplemente está cerrada.

**Campañas:** `CampaignDispatchStore` escribe en una conversación existente sin tocar asignación ni
automatización. Un envío de campaña a una conversación `Active` deja al asistente activo: es coherente con D-A3
(lo que apaga al asistente es que un humano actúe) y se deja escrito para que nadie lo «arregle».

### 4.3 Eventos del historial

`ConversationEventType` suma `AutomationPaused` y `AutomationResumed`. `ConversationEvent` suma
`AutomationPauseReason? PauseReason`. `ConversationEventJson` escribe `"pauseReason":"AdvisorTook"` cuando viene,
y `Parse` lo lee (un `type` desconocido sigue dando `null`, como hoy).

`MessageEventDto` suma `string? PauseReason = null` (nombre del enum, el diccionario lo tiene el frontend).
`Actor` es la membresía que pausó o reanudó; `null` cuando pausó el sistema (`Handoff`, futuro).

### 4.4 Endpoints

En `MessagingEndpoints`, grupo `/api/v1/tenants/{tenantId:guid}/messaging`, política
`messaging.conversation.manage`, `If-Match` obligatorio (`RequireVersion`), respuesta 200 con `ConversationSummary`:

| Ruta | Efecto | 422 |
| --- | --- | --- |
| `POST /conversations/{id}/automation/pause` | `PauseAutomation(caller, Manual)` | `not_available`, `already_paused`, `conversation.not_open` |
| `POST /conversations/{id}/automation/resume` | `ResumeAutomation(caller)` | `not_available`, `already_active`, `conversation.not_open` |

Handler nuevo `ConversationAutomation.cs` en Application, con los mismos siete pasos de
`ConversationAssignmentSteps` (tenant y permiso, cargar, membresía activa, `EnsureVersion`, método del agregado,
`events.Record` + `audit.Record` + `SaveChanges`, `notifier.ConversationChangedAsync`, resumen). 412 y 428 como
en `take`.

**Autoasignación al responder** (`ConversationRepository.TryAutoAssignAsync`, SQL): el `UPDATE … WHERE
assigned_member_id IS NULL` pasa a poner también `automation_status = 'Paused'`, `automation_paused_at = now`,
`automation_paused_by_member_id = member`, `automation_pause_reason = 'AdvisorReplied'` **cuando estaba
`Active`**, y en ese caso inserta la fila de evento `AutomationPaused` junto a la de `AutoTaken`, en la misma
transacción corta, con auditoría `messaging.conversation.automation_paused`. **Responder siendo ya el asignado**
no necesita nada: por I-1 la conversación ya estaba pausada o en `None`.

**Ingesta:** sin cambios en esta entrega. El punto donde el motor preguntará «¿respondo?» es después de
`ingested is not null` en `WebhookDeliveryProcessor` (y su gemelo de Instagram); el `RETURNING` del `UPDATE` de
`InboundIngestion` tendrá que devolver `automation_status` entonces. Se deja anotado, no se construye.

### 4.5 Lectura

- `ConversationSummary` suma `ConversationAutomationDto? Automation` (`null` cuando `None`):
  `ConversationAutomationDto(string Status, DateTimeOffset StartedAt, DateTimeOffset? PausedAt, MemberRefDto? PausedBy, string? PauseReason)`.
  `ConversationRow` y `ConversationSummaryBuilder.ToSummary` suman las columnas; `PausedBy` resuelve el nombre
  como `AssignedTo` (ausente → «Miembro eliminado»).
- `ListConversationsQuery` suma `string? Automation` (`active | paused | any | none`); `ConversationListFilter` y
  el SQL de `ConversationQueries` lo aplican (`any` = `<> 'None'`).
- `ConversationCountsDto` suma `int Automated` (`<> 'None'` y `Open`, del tenant, sin búsqueda, como los demás).
  Colección fija: viaja siempre, en cero también.

### 4.6 Auditoría y tiempo real

Acciones `messaging.conversation.automation_paused` y `messaging.conversation.automation_resumed` por
`IMessagingAuditRecorder.Record` (humano). La variante con actor de sistema para cuando el motor pause por `Handoff`
(un `RecordBySystem` con la forma de `RecordCampaign(bySystem: true)`) **la agrega el spec 4**, que es quien la llama:
en esta entrega nadie la usaría. Tiempo real: `ConversationChangedAsync` después de cada transición; sin tipo de
evento nuevo en el hub.

### 4.7 Harness de pruebas

`MessagingApiHarness.ActivateAutomationAsync(conversationId)`: `UPDATE` directo de `automation_status = 'Active'`,
`automation_started_at = now()` (y `version + 1`) con `WHERE status = 'Open' AND assigned_member_id IS NULL`, y
falla la prueba si no afectó una fila: así ninguna prueba fabrica `Resolved + Active` ni `Active` con asesor. Es
el sustituto del motor hasta el spec 4, y vive sólo en el harness.

**El único camino SQL que asigna sobre una fila existente es `TryAutoAssignAsync`** (los `INSERT` de la ingesta y
de `ConversationOpener` nacen en `None`). Si el cambio de §4.4 se olvida, el síntoma es un **500 por el `CHECK`**
al enviar en una conversación `Active`, no un 422: la prueba «enviar en una `Active` sin asesor» existe por eso.

## 5. Backend: horarios de atención

### 5.1 Modelo

Migración `AddBusinessHours` en Messaging:

```sql
CREATE TABLE messaging.business_hours_schedules (
  id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL,
  name varchar(60) NOT NULL,
  is_default boolean NOT NULL DEFAULT false,
  weekly jsonb NOT NULL,       -- [{"day":1,"ranges":[{"from":480,"to":1080}]}, …]  day 1=lunes … 7=domingo; minutos locales
  exceptions jsonb NOT NULL,   -- [{"date":"2026-11-02","ranges":[]}, …]  ranges vacío = no atiende
  holiday_country varchar(2) NULL,  -- D-H9: 'CO' = festivos de Colombia calculados; NULL = sin festivos automáticos
  created_at timestamptz NOT NULL,
  updated_at timestamptz NOT NULL,
  version bigint NOT NULL
);
ALTER TABLE messaging.business_hours_schedules
  ADD CONSTRAINT "CK_business_hours_holiday_country" CHECK (holiday_country IS NULL OR holiday_country IN ('CO'));
CREATE UNIQUE INDEX "IX_business_hours_tenant_name" ON messaging.business_hours_schedules (tenant_id, lower(name));
CREATE UNIQUE INDEX "IX_business_hours_tenant_default" ON messaging.business_hours_schedules (tenant_id) WHERE is_default;

CREATE TABLE messaging.connection_business_hours (
  connection_id uuid PRIMARY KEY,           -- integrations.connections.id, referencia blanda (sin FK, como conversations.connection_id)
  tenant_id uuid NOT NULL,
  schedule_id uuid NOT NULL REFERENCES messaging.business_hours_schedules (id) ON DELETE RESTRICT,
  assigned_at timestamptz NOT NULL
);
CREATE INDEX "IX_connection_business_hours_schedule" ON messaging.connection_business_hours (schedule_id);
```

`IX_business_hours_tenant_name` se traduce **por nombre** en `MessagingUnitOfWork` a 409
`messaging.business_hours.name_taken`; `IX_business_hours_tenant_default` a 409 `default_conflict` (carrera entre
dos «predeterminado»; el handler lo evita leyendo antes, el índice es la red). El `RESTRICT` es la red de
`in_use`.

### 5.2 Dominio

`BusinessHoursSchedule` (agregado, `Modules.Messaging.Domain`):

- `Create(id, tenantId, name, weekly, exceptions, holidayCountry, now)`; `Rename`, `ReplaceWeekly`,
  `ReplaceExceptions`, `SetHolidayCountry`, `MakeDefault`, `ClearDefault`, `Duplicate(newId, now)` → nuevo
  agregado «{name} (copia)» recortado a 60, con el mismo `HolidayCountry`.
- Reglas (`MessagingDomainException`, 422): nombre 1..60 recortado (`name_invalid`); por día 0..3 franjas con
  `0 ≤ from < to ≤ 1440`, sin solaparse, ordenadas (`ranges_invalid`); excepciones hasta 100, fechas únicas
  (`exceptions_invalid`); `HolidayCountry` es `null` o `"CO"` (`holiday_country_invalid`); los siete días siempre
  presentes en `weekly` (colección fija).
- **Puro y testeable sin base:** `IsOpenAt(DateTimeOffset instant, TimeZoneInfo tz)` y
  `NextChangeAt(DateTimeOffset instant, TimeZoneInfo tz)` (el próximo instante en que cambia abierto/cerrado,
  buscando hasta 14 días; `null` si no cambia). Convierte el instante con `TimeZoneInfo.ConvertTime` y resuelve
  la fecha local en este orden: **excepción** de esa fecha → **festivo** del país (cerrado todo el día) → día de
  la semana. Una franja `[from, to)` en minutos desde las 00:00 locales; `to = 1440` es hasta medianoche. Un huso
  con horario de verano se respeta porque la conversión la hace `TimeZoneInfo`; Colombia no lo tiene, pero el
  tipo no lo supone (como `TenantCalendar`).
- **`ColombianHolidays.For(int year)`** (`Modules.Messaging.Domain`, estático y puro, `IReadOnlySet<DateOnly>` de
  18 fechas) implementa la Ley 51 de 1983 y se escribe acá para que el constructor no la invente:
  - Fijos, sin traslado: 1 ene, 1 may, 20 jul, 7 ago, 8 dic, 25 dic.
  - Trasladados al **lunes siguiente** si no caen en lunes (Ley Emiliani): 6 ene, 19 mar, 29 jun, 15 ago, 12 oct,
    1 nov, 11 nov.
  - Según la Pascua (`P`, cómputo gregoriano de Meeus/Jones/Butcher): Jueves Santo `P−3` y Viernes Santo `P−2` sin
    traslado; Ascensión `P+39`, Corpus Christi `P+60` y Sagrado Corazón `P+68`, los tres trasladados al lunes
    siguiente (`P+43`, `P+64`, `P+71`).
  - `UpcomingHolidays(DateOnly fromLocal, int count)` devuelve las próximas `count` fechas (la lista usa 12 meses):
    es lo que la página muestra, para que el frontend **no** tenga la regla.

`ConnectionBusinessHours` es una fila sin comportamiento (registro EF), escrita por el handler de asignación.

### 5.3 Endpoints

Grupo `/api/v1/tenants/{tenantId:guid}/messaging/business-hours`. Lectura con `messaging.conversation.read`;
escritura con `messaging.business_hours.manage`. `If-Match` donde hay versión (`RequireVersion`); 412 por
`EnsureVersion`, 428 si falta.

| Método y ruta | Política | Cuerpo / respuesta |
| --- | --- | --- |
| `GET /` | read | `BusinessHoursListDto(Items: BusinessHoursDto[], TimeZone, Connections: BusinessHoursConnectionOptionDto[])` — `Connections` son **todas** las del tenant, en cualquier estado, con `(ConnectionId, Name, Channel, Status, ScheduleId?, ScheduleName?)`: es lo que el editor lista (D-H10) |
| `POST /` | manage | `SaveBusinessHoursRequest(Name, Weekly, Exceptions, HolidayCountry, ConnectionIds)` → 201 `BusinessHoursDto` |
| `PUT /{id}` | manage, If-Match | `SaveBusinessHoursRequest` → 200 |
| `DELETE /{id}` | manage, If-Match | 204; 422 `in_use`, `is_default` |
| `POST /{id}/duplicate` | manage | 201 `BusinessHoursDto` «{nombre} (copia N)» (D-H14); sin If-Match: no modifica el origen |
| `POST /{id}/default` | manage, If-Match | 200 `BusinessHoursDto`; quita el predeterminado anterior en la misma transacción y sube la versión de los dos (D-H11) |

`SaveBusinessHoursRequest(Name, Weekly, Exceptions, HolidayCountry, ConnectionIds)`: `HolidayCountry` es `null` o
`"CO"` (D-H9). `ConnectionIds` **reemplaza el conjunto**
de conexiones del horario: las que no vienen y lo tenían quedan sin asignación; las que vienen y tenían otro,
cambian. Cada id tiene que ser una conexión del tenant según `ListAllAsync` (D-H10), si no 422
`validation.failed` con `ConnectionIds`. Las filas huérfanas del horario se borran en la misma transacción.

```csharp
public sealed record BusinessHoursDto(Guid Id, string Name, bool IsDefault,
    IReadOnlyList<WeekdayHoursDto> Weekly,            // siempre 7, lunes a domingo
    IReadOnlyList<ExceptionHoursDto> Exceptions,
    string? HolidayCountry,                           // "CO" | null (D-H9)
    IReadOnlyList<DateOnly> UpcomingHolidays,         // próximos 12 meses según HolidayCountry; vacía con null. La página los muestra en lectura: la regla vive en el backend
    IReadOnlyList<BusinessHoursConnectionDto> Connections,  // (ConnectionId, Name, Channel)
    bool OpenNow, DateTimeOffset? NextChangeAt,
    DateTimeOffset UpdatedAt, long Version);
public sealed record WeekdayHoursDto(int Day, IReadOnlyList<TimeRangeDto> Ranges);
public sealed record TimeRangeDto(int From, int To);          // minutos locales
public sealed record ExceptionHoursDto(DateOnly Date, IReadOnlyList<TimeRangeDto> Ranges);
```

Validador FluentValidation para `SaveBusinessHoursRequest` (campo + código, 422 `validation.failed` con `errors`):
`Name`, `Weekly` (7 elementos, días 1..7 sin repetir), `Weekly[i].Ranges[j]`, `Exceptions[i].Date`,
`HolidayCountry` (`null` o `CO`). El dominio revalida y da el código.

**Conexiones:** `MessagingConnectionDto` (de `GET /messaging/connections`) suma `Guid? BusinessHoursScheduleId`,
`string? BusinessHoursName`, `bool OpenNow`. Con `null` y sin predeterminado, `OpenNow = true` (D-H4). El DTO lo
construye el adaptador de Bootstrapper (`MessagingConnectionDirectory.ListActiveAsync`), que no ve los horarios:
los tres campos los **completa el handler** `ListMessagingConnectionsHandler` después, con `IBusinessHours`, en
una sola consulta por lote. El adaptador no cambia. Esa lista también alimenta «conversación nueva» y campañas:
los campos nuevos les son indiferentes.

### 5.4 Puerto para consumidores internos

```csharp
// Modules.Messaging.Application
public interface IBusinessHours
{
    // Resuelve: asignación de la conexión → predeterminado del tenant → siempre abierto.
    Task<BusinessHoursStatus> GetStatusAsync(Guid tenantId, Guid connectionId, CancellationToken ct);
    // Lo mismo para un lote, en una consulta: lo usa GET /messaging/connections.
    Task<IReadOnlyDictionary<Guid, BusinessHoursStatus>> GetStatusesAsync(Guid tenantId, IReadOnlyCollection<Guid> connectionIds, CancellationToken ct);
}
public sealed record BusinessHoursStatus(bool IsOpen, DateTimeOffset? NextChangeAt, Guid? ScheduleId, string? ScheduleName);
```

Implementación en Infrastructure sobre `MessagingDbContext` + `ITenantClock` (Tenancy.Application está permitido
por `MessagingLayerTests`). Lo usan `GET /messaging/connections` y la lista de horarios hoy; el bloque «Horario de
atención» y el mensaje fuera de horario mañana.

### 5.5 Permiso

`MessagingPermissions.BusinessHoursManage = "messaging.business_hours.manage"`. Las dos mitades en
`QepServiceCollectionExtensions`: `PermissionDefinition` («Administrar horarios de atención», categoría
Messaging, riesgo `low`, `RequiredModules: []`) y `AddPolicy`. En el rol `admin`; no en `advisor`, `billing` ni
`cashier`. Sin la política el síntoma es 500, no 403.

### 5.6 Auditoría

`IMessagingAuditRecorder.RecordBusinessHours(tenantId, actorId, action, scheduleId, at)` con acciones
`messaging.business_hours.{created,updated,deleted,duplicated,default_set,connections_assigned}`, resource type
`business_hours_schedule`.

## 6. Frontend

Punto de partida en `qep-frontend` (verificar la rama antes de leer). Convenciones que aplican: container en la
ruta y vista sin hooks; una `*.api.ts` por feature; `If-Match` con la versión **congelada** al abrir; `retry:
false` en mutaciones y `invalidateQueries` en `onSettled`; errores con `describe…Failure`, nunca el `detail`;
toda pantalla cubre carga, vacío, error y éxito.

### 6.1 Tipos y API (`features/messaging/`)

- `types/messaging.ts`: `ConversationAutomation { status: 'Active'|'Paused'; startedAt; pausedAt: string|null; pausedBy: MessageActor|null; pauseReason: AutomationPauseReason|null }`;
  `ConversationSummary.automation: ConversationAutomation | null`; `ConversationCounts.automated: number`;
  `ConversationsQuery.automation?: 'active'|'paused'|'any'|'none'`; `CONVERSATION_EVENT_TYPES` suma
  `'AutomationPaused' | 'AutomationResumed'`; `ConversationEvent.pauseReason: AutomationPauseReason | null`;
  `AUTOMATION_PAUSE_REASON_LABELS`.
- `services/messaging.api.ts`: `parseConversation` lee `automation` (ausente → `null`, por compatibilidad con un
  backend anterior); `fetchConversations` manda `automation=` **sólo cuando el usuario eligió un filtro** (el
  valor por defecto es sin filtro, y `any` es una opción más, no el default); `pauseAutomation(t, id, version)` y
  `resumeAutomation(t, id, version)` → `POST …/automation/{pause|resume}` con `If-Match`;
  `describeAutomationFailure` (412 «Alguien más cambió esta conversación. Revisa cómo quedó.», 422
  `not_available` «Esta conversación no la atiende un asistente.», `already_paused`, `already_active`, 404 y 428
  → recargar).
- `conversationsQueryKey` incluye `automation`; `useInboxStore` suma `automation` a los filtros **y a
  `NO_FILTERS`**, es decir, se comporta como `unreadOnly` (lo limpia «Limpiar»), no como `channel` (que hoy no
  está en `NO_FILTERS` y no se limpia por ese camino).

### 6.2 Bandeja

- **Fila** (`conversation-list-item.tsx`): tercera línea, antes de las etiquetas, un `AutomationChip` «Asistente»
  (índigo) o «Asistente pausado» (naranja) cuando `automation` no es `null`. Sin chip en `None`.
- **Filtros** (`conversation-list.tsx`, `FilterPopover`): un `Segmented` «Asistente: Todas / Con asistente /
  Pausado / Sin asistente» (`any` sólo como opción, el valor inicial es sin filtro). Chip removible «Quitar filtro
  Asistente». `activeFiltersOf`, `hasActiveFilters`, `InboxFilters` suman el campo. El contador `counts.automated`
  se muestra junto a la opción «Con asistente». **Todo el bloque se dibuja sólo si `counts.automated > 0` o el
  filtro ya tiene valor** (D-A10): un tenant sin asistentes ve el popover exactamente como hoy. Si el filtro
  estaba puesto y el contador bajó a cero, el chip removible sigue visible para poder quitarlo.
- **Encabezado del hilo** (`thread-header.tsx`): junto a los chips de ventana y de resuelta, el `AutomationChip`
  con texto «La atiende el asistente» / «Asistente pausado por {nombre}» (o «por el sistema»). Con `canManage`:
  botón «Pausar asistente» (si `Active`) o «Devolver al asistente» (si `Paused` y `Open`). Props nuevas en
  `ThreadHeaderProps`: `onPauseAutomation`, `onResumeAutomation`, `isAutomating`. Reanudar con asesor asignado
  pide confirmación en un `Dialog`: «La conversación dejará de estar asignada a {nombre}».
- **Hook** `hooks/use-conversation-automation.ts`: `useConversationAutomation(conversation, { onStart })` →
  `{ pause, resume, isAutomating, failure, dismissFailure }`, con `patchConversationCaches` e invalidación de
  `conversationsKey` y `messagesQueryKey` (para que aparezca el evento). En el container `$conversationId.tsx`
  limpia las fallas de los otros dos hooks, y la falla se muestra en el mismo banner `actionFailure`.
- **Eventos** (`utils/conversation-event.ts`): `AutomationPaused` → «{actor} pausó el asistente.» con sufijo por
  motivo («al tomar la conversación», «al responder», «al transferir», sin sufijo en `Manual`; «El asistente pasó
  la conversación a un asesor.» en `Handoff` sin actor). `AutomationResumed` → «{actor} devolvió la conversación
  al asistente.».

### 6.3 Horarios de atención (`features/messaging/` + ruta nueva)

Vive **entera en `features/messaging/`**, como motivos de resolución y respuestas rápidas (precedente: un permiso de
núcleo sobre algo del módulo messaging, en Configuración). Así la página usa `messaging.api.ts` y
`useMessagingConnections` sin cruzar features.

- **Ruta** `src/routes/_authenticated/settings/business-hours.tsx`: `<ModuleGate modules={['messaging']}>` +
  `BusinessHoursAccessGate` (`messaging.business_hours.manage`; sin él, denegado, como
  `resolution-reasons-access-gate.tsx`; D-H13) + container `BusinessHoursRoute` con `useBusinessHours` y
  `useBusinessHoursMutations` (las conexiones vienen en la lista, D-H10).
- **Sección** en `use-settings-sections.ts`: `can(MESSAGING_PERMISSIONS.businessHoursManage) && moduleOn('messaging') && { label: 'Horarios de atención', to: '/settings/business-hours' }`, justo después de «General».
- **Vista** `pages/business-hours-page.tsx` (dos columnas; en móvil, lista arriba y editor abajo):
  - *Lista*: tarjeta por horario con nombre, «PREDETERMINADO», resumen de la semana en una línea, conexiones que
    lo usan, «Abierto ahora · cierra a las 6:00 p. m.» / «Cerrado · abre mañana a las 8:00 a. m.» (de `openNow` y
    `nextChangeAt`, formateados con `useRegionalFormat().timeZone`). Vacío: tarjeta punteada «Crear el primero».
  - *Editor* (`components/business-hours-editor.tsx`): nombre; tabla de siete días con interruptor «Atiende» y
    hasta tres franjas con dos `<input type="time">` cada una (sin time picker propio: el nativo cumple), «+ franja»,
    «Copiar a todos los días»; excepciones con `Calendar` (existe, react-day-picker) para elegir la fecha y las
    mismas franjas; `Switch` **«Festivos de Colombia»** (`holidayCountry: 'CO' | null`) con, debajo y en lectura,
    los próximos festivos de `upcomingHolidays` como fechas formateadas («lun 2 nov»; el backend no manda el nombre
    del festivo, §8) y el texto «Un festivo cierra todo el día. Si ese día sí atiendes, agrégalo como excepción con
    su horario.»; conexiones del tenant como lista de `Switch` (**las `connections` del `GET /business-hours`**, todas,
    con su estado; las de otro horario muestran «hoy usa X»); acciones Duplicar, Predeterminado, Eliminar (deshabilitado con tooltip si está en uso o
    es predeterminado); Descartar y **un solo Guardar** (`PUT /{id}` con `ConnectionIds`, D-H11). Formulario con
    react-hook-form + `zodResolver(businessHoursFormSchema)` en `types/business-hours-form.schema.ts` (espejo de
    las reglas de dominio; `from < to` con el mensaje «Una franja no puede pasar de medianoche: divídela en dos
    días», D-H12). Después de cualquier mutación la página relee la lista; nunca manda dos acciones seguidas con
    la misma versión.
  - La zona horaria del tenant en una línea de lectura con enlace a `/settings`.
- **API** en `services/messaging.api.ts`: `fetchBusinessHours`, `createBusinessHours`, `updateBusinessHours`
  (If-Match), `deleteBusinessHours` (If-Match), `duplicateBusinessHours`, `setDefaultBusinessHours` (If-Match),
  `describeBusinessHoursFailure` (409 `name_taken` → campo `name`; 422 `in_use`, `is_default`; 412 → `stale`).
  La lista de conexiones del editor viene en `connections` de `GET /business-hours` (D-H10).
- **Estados** de la página: carga, vacío, error con «Reintentar», éxito; mutación: deshabilita el formulario,
  412 cierra el editor y muestra el aviso `role="alert"`, 409 marca `name`.

## 7. Pruebas (TDD, RED antes que GREEN)

**Backend, unitarias** (`Modules.Messaging.UnitTests`):
- `Conversation`: `StartAutomation` exige `Open`, sin asesor y `None`; `Take` sobre `Active` devuelve `Taken` +
  `AutomationPaused(AdvisorTook)`; `TransferTo` igual con `AdvisorTransferred`; `Release` deja `Paused`;
  `Resolve` deja `None`; `ResumeAutomation` con asesor devuelve `Released` + `AutomationResumed`; `PauseAutomation`
  en `Paused` → `already_paused`; en `None` → `not_available`.
- `ConversationEventJson`: ida y vuelta con `pauseReason`.
- `BusinessHoursSchedule`: franjas inválidas, solapadas, más de tres; excepciones repetidas; `IsOpenAt` en
  `America/Bogota` dentro y fuera de franja, en excepción cerrada, en excepción con franja propia, a medianoche
  (`to = 1440`); `NextChangeAt` desde abierto (cierre de hoy), desde cerrado (apertura del próximo día que
  atiende), con un domingo y un festivo en medio, y `null` cuando nada cambia en 14 días; un huso con horario de
  verano (`America/New_York`) alrededor del cambio.
- `ColombianHolidays`: `For(2026)` y `For(2027)` dan exactamente las 18 fechas oficiales de cada año (tabla
  literal en la prueba, no recalculada); un fijo que cae en fin de semana no se traslada (25 dic 2027, sábado); un
  Emiliani que cae en lunes se queda (6 ene 2025); Pascua de un año conocido (2026-04-05) y sus cinco derivados;
  `UpcomingHolidays` desde una fecha a mitad de año cruza al año siguiente. `IsOpenAt` con `HolidayCountry = CO`:
  festivo cerrado todo el día aunque el día de la semana atienda; festivo con excepción con franjas → abierto en la
  franja; con `HolidayCountry = null` el mismo día abre; `NextChangeAt` salta el festivo.

**Backend, integración** (`Modules.Messaging.IntegrationTests`, contra el esquema migrado; acotadas al módulo):
- `ConversationAutomationApiTests`: pausar y reanudar con `If-Match` (200, 412, 428); `take` pausa y escribe el
  evento; enviar un mensaje en una `Active` sin asesor la autoasigna y la pausa (`AdvisorReplied`) con evento y
  auditoría; `resolve` deja `automation = null` en el resumen; reanudar con asesor libera; `GET /conversations?automation=paused`
  y `counts.automated`; cada `CK_*` con un `UPDATE` adrede inválido.
- `BusinessHoursApiTests`: CRUD con los 7 días; `name_taken` 409; duplicar dos veces («(copia)», «(copia 2)»);
  predeterminado único (segundo `default` mueve el anterior y sube las dos versiones); borrar en uso 422 y
  predeterminado 422; `ConnectionIds` en el `PUT` (reemplazo, conexión de otro tenant 422, conexión pausada
  aceptada); fila huérfana no cuenta para `in_use` y se borra; excepciones pasadas podadas al guardar; `openNow` y
  `nextChangeAt` con el reloj del harness fijado; `holidayCountry = CO` con el reloj en un festivo → `openNow =
  false` y `upcomingHolidays` no vacía, `holidayCountry = "XX"` → 422 `validation.failed` con `HolidayCountry`,
  `CK_business_hours_holiday_country` con un `UPDATE` adrede inválido; `GET /messaging/connections` con
  `businessHoursName` y `openNow`; sin predeterminado → `openNow = true`.
- Permiso: `business_hours.manage` ausente → 403 en escrituras y 200 en `GET`; política registrada (si no, 500).
- `ArchitectureTests`: sin cambios de reglas; `MessagingLayerTests` sigue verde (sólo Tenancy.Application nuevo
  en uso, ya permitido).

**Frontend** (vitest + testing-library, `fetch` stub):
- `conversation-list-item.test.tsx`: chip «Asistente» / «Asistente pausado» / ninguno.
- `conversation-list-filters.test.tsx`: con `counts.automated = 0` y sin filtro, el popover **no** muestra
  «Asistente»; con `automated > 0`, elegir «Pausado» llama `onAutomationChange('paused')`; chip removible visible
  aunque el contador sea cero mientras el filtro esté puesto.
- `thread-header.test.tsx`: con `automation: null` no hay chip ni botones de asistente.
- `messaging-filters.test.tsx`: la URL lleva `automation=paused`.
- `$conversationId.test.tsx`: «Pausar asistente» manda `POST …/automation/pause` con `If-Match: "2"`; reanudar con
  asesor muestra el diálogo y luego manda `resume`; 412 muestra el banner y relee; evento en el hilo con el texto.
- `conversation-event.test.ts`: los dos tipos y los cinco motivos.
- `settings/business-hours.test.tsx`: carga, vacío, error, éxito; sin permiso no pide nada; módulo apagado muestra
  `MODULE_NOT_IN_PLAN_MESSAGE`; crear manda los 7 días; 409 marca `name`; 412 cierra y avisa; duplicar;
  predeterminado; eliminar deshabilitado en uso; asignar conexiones.
- `business-hours-page.test.tsx`: `it.each` de estados; «Copiar a todos»; tercera franja máximo; «Abierto ahora»;
  el `Switch` «Festivos de Colombia» manda `holidayCountry: 'CO'` y apagado manda `null`; con `upcomingHolidays`
  lista las fechas y con vacía no dibuja la lista.
- `use-settings-sections.test.ts`: la entrada aparece con permiso y módulo.

## 8. Despliegue y fuera de alcance

**Despliegue.** Backend primero: `automation` ausente en el resumen lo lee el frontend actual como `null` (parser
defensivo), y los dos eventos nuevos llegan con `type: null` → «La conversación tuvo un cambio.» hasta que salga
el frontend. Dos migraciones de Messaging, sin datos que migrar: las columnas nacen en `None` y las tablas vacías.
Frontend después, con el permiso ya en `admin`.

**Fuera de alcance:**
- El motor del bot, los asistentes como entidad, `automation_assistant_id`, el «paso n de m» (spec 4).
- Interactivos salientes (spec 2). Envío proactivo y claves de API (spec 3).
- Autoría «asistente» en los mensajes (`sent_by` para un no miembro): va con el motor.
- Un evento de tiempo real distinto para «lo tiene el asistente» (`notify` sigue igual).
- Festivos de **otros países** y festivos regionales (D-H9 cubre sólo Colombia, nacional). Nombres de los festivos
  en la pantalla. Varios horarios por conexión. Horario por asesor.
- Asignar el horario desde Integraciones (D-H5).
