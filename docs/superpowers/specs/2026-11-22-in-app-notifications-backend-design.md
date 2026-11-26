# Notificaciones en la app (campana) y alertas en vivo — backend

Fecha: 2026-11-22 · Repo: `qep-backend` · Rama: `feature/in-app-notifications` (worktree `qep-backend-worktrees/in-app-notifications`) ·
Estado: **aprobado por el owner e implementado en `feature/in-app-notifications`** (decisiones del owner registradas en D-12, D-14, §8.6 y §14). El spec del frontend es
`qep-frontend/docs/superpowers/specs/2026-11-22-in-app-notifications-frontend-design.md` y **lee su contrato de este documento**
(§8 y §9): este spec manda.

## 1. Intención

Hoy un miembro se entera de lo que le toca sólo si está mirando la pantalla correcta: una conversación que le transfieren suena
sólo si tiene `/messaging` abierto, una exportación lista llega sólo por correo, un ticket que el cliente contestó o un pedido de
distribuidor por aprobar no avisan a nadie. El owner pidió dos cosas:

1. **Una campana por miembro**: bandeja de avisos con no leídos, historial, «marcar como leído» y «marcar todo como leído».
2. **Una alerta en vivo global** (sonido + notificación del sistema operativo) cuando entra un mensaje de Mensajería, en
   **cualquier** ruta del SPA y con la pestaña en segundo plano. Esa alerta **no** se guarda en la campana: Mensajería ya tiene
   sus no leídos.

El owner rechazó el atajo de «subir el hub de Mensajería a `_authenticated`»: se construye la solución final sobre el hub global
de la app (`AppHub`, spec 2026-11-15) y el módulo `Notifications`.

## 2. Metas

- Tabla de avisos en `Notifications`, por **miembro y tenant**, con estado leído/no leído y retención de 90 días.
- Cinco productores de primera entrega (§6), cada uno publicando un evento en el outbox de **su** módulo; `Notifications` los
  consume con el mismo mecanismo de reclamo que los correos.
- Cuatro rutas HTTP (lista, conteo de no leídos, marcar uno, marcar todos), sólo para las entradas propias.
- Entrega personal por el `AppHub` que **nadie puede falsificar**: el miembro sale de la conexión, nunca del cliente.
- Alerta de mensaje entrante por el `AppHub`, con la misma regla que el `notify` de hoy (asignado → ese miembro; sin asignar →
  todo el tenant con lectura de Mensajería).
- Aviso nuevo de Soporte: ticket sin asignar por N horas hábiles, con su configuración por tenant.

## 3. No metas y antimetas

- **No hay Web Push** (pestaña cerrada). Fuera de alcance.
- **No se migra el `MessagingHub`**: sigue con sus invalidaciones, su presencia y su `notify` (§10.3).
- **No hay preferencias por tipo de aviso** («no quiero avisos de exportaciones»). Si hacen falta, es otro trabajo (§14).
- **No se guarda texto renderizado.** La fila guarda ids, números inmutables y un `kind`; el texto lo arma la pantalla.
- **No se crea un rol ni un permiso nuevo.** Leer la campana propia sólo exige sesión y membresía activa.
- Antimeta: que el **texto de un mensaje entrante** salga del servidor por un aviso. Nunca viaja: ni en la alerta en vivo, ni en
  la campana.
- Antimeta: que un módulo llame a `Notifications` directo, o que un módulo cree **su propio hub**.

## 4. Restricciones

- Capas `Domain → Application → Infrastructure → Api`, verificadas por `tests/ArchitectureTests/` (un archivo por módulo).
  `Notifications` hoy **no tiene proyecto Api**; este trabajo lo crea (`Modules.Notifications.Api`) y `NotificationsLayerTests`
  suma las reglas de Api.
- Aislamiento de tenant en doble capa: `{tenantId:guid}` en la ruta y revalidación en el handler; **403, nunca 404**, por tenant
  ajeno. Dentro del tenant, una entrada de otro miembro responde **404** (no confirma nada que el que llama no pueda ver).
- Toda migración es **expand-only** (`.claude/rules/build-y-despliegue.md`). Tablas y columnas nuevas, con default.
- Un hosted service nuevo entra a `MultiReplicaInventory` en el mismo commit, con su evidencia.
- Un consumidor nuevo de `platform.outbox_messages` escucha `DoorbellQueues.Outbox`. El `QueueDoorbellInterceptor` ya se agrega solo a
  todo `DbContext` de `Modules.*` (`QueueDoorbellRegistration.AddQueueDoorbellInterceptor`) y, como es un `DbCommandInterceptor`, timbra
  también los `INSERT` crudos: mapear el outbox en Soporte o Cobros no pide registrar nada.
- TDD con evidencia literal de RED y GREEN; presupuesto de pruebas del workspace (plan, §Restricciones globales).

## 4b. Enfoques

- **A. Llamada directa**: cada módulo llama un `INotificationsInbox` al hacer el cambio. Rápido, pero acopla cada módulo a
  `Notifications` (`ArchitectureTests` lo prohíbe entre módulos de negocio), el aviso queda fuera de la transacción del cambio o
  la alarga, y una falla de la campana rompe la escritura. Descartado.
- **B. Eventos en el outbox del productor, consumidos por `Notifications` (elegido, [owner]).** El patrón de los correos
  (`OutboxDeliveryWorker`): el evento commitea con el cambio o no existe, el consumidor reclama con lease y es idempotente, y
  `Notifications` publica en el hub después de su propio commit.
- **C. Híbrido**: outbox para lo que se guarda, llamada directa al hub para lo vivo. Es lo que queda para la alerta de entrante, que
  no se guarda y ya tiene su canal (`IMessagingNotifier` → `NOTIFY` → sinks por pod): ahí no hay outbox que agregar (§9.2).

## 5. Revisión crítica del encargo contra el código

Lo verificado leyendo el código de `develop` (`c9282c9af`), y lo que cambia del encargo:

1. **El SPA no conoce su `memberId`** (D-F1, `qep-frontend/CLAUDE.md`, `assignedTo.isMe`). Un tópico `notifications:{memberId}`
   que el cliente suscribe exigiría exponerlo. Y el grupo del `AppHub` es `{tenant}:{topic}` (`RealtimeTopics.GroupOf`), así que
   un tópico fijo como `notifications:me` juntaría a todos los miembros en un grupo. **Cambio:** entrega personal por un **grupo
   del miembro** que el hub une en `OnConnectedAsync`, como `MessagingHub.MemberGroup` (decisión D-3). Sin `Subscribe`.
2. **`IRealtimePublisher` sólo publica por tópico.** Se suma un puerto aparte, `IRealtimeMemberPublisher` (no un método más:
   precedente de `ISupportMemberPermissions`, ningún doble existente cambia), y el sobre `RealtimeEnvelope` gana `MemberId`.
3. **Publicar la alerta de entrante con `IRealtimePublisher` la multiplicaría por pod**: el `notify` de hoy nace en
   `MessagingEventsListener`, que corre en **cada** pod y reparte a los `IMessagingEventSink`. Si un sink republicara por
   `pg_notify`, cada pod lo volvería a mandar. **Cambio:** un segundo `IMessagingEventSink` en Bootstrapper que escribe directo en
   `IHubContext<AppHub>` del pod, igual que `MessagingHubEventSink` escribe en el suyo (D-9).
4. **Soporte no tiene horas hábiles propias.** `ISupportBusinessCalendar` sólo responde `IsBusinessDay(DateOnly)` y sirve a los
   términos legales («no es el horario de atención»). El tenant **ya tiene horarios de atención** en `/settings/business-hours`
   (Mensajería): varios `BusinessHoursSchedule` por tenant, uno `IsDefault`, franjas semanales, excepciones y festivos
   (`HolidayCountry`). `BusinessHoursClock.BusinessSeconds(BusinessHoursSchedule? schedule, DateTimeOffset from, DateTimeOffset to,
   TimeZoneInfo timeZone) : long` (`Modules.Messaging.Domain`) ya cuenta segundos hábiles, y con `schedule = null` cuenta de corrido.
   La zona sale de `ITenantClock.GetAsync(tenantId)` → `TenantCalendar.TimeZone` (así la obtiene `ListBusinessHoursHandler`, que la
   devuelve como `BusinessHoursListDto.TimeZone`). Leer los horarios por `GET /messaging/business-hours` exige el módulo `messaging` y
   `messaging.conversation.read`. **[owner]** El aviso cuenta con ese horario, a través de un contrato compartido en
   `BuildingBlocks.Application` que implementa Mensajería (`IBusinessHoursProvider`); Soporte no referencia Mensajería (D-14).
5. **No existe «desde cuándo está sin asignar».** `SupportTicket.Assign` sólo guarda `AssigneeMemberId` y un evento
   `AssigneeChanged`. **Cambio:** columna `unassigned_since` en el ticket, que mantiene el dominio (D-15).
6. **`Support` y `ServiceContracts` no tienen outbox.** Los correos de Soporte y el digest de cobros salen por llamada directa a
   adaptadores de Bootstrapper (`SupportReplyEmailSender`, `IServiceContractsStaffEmail`). **Cambio:** cada uno mapea la proyección
   de `platform.outbox_messages` en su `DbContext`, como Catalog, Quotations o Messaging (`TemplateHeaderImageReleasePublisher`).
7. **Messaging sí escribe en el outbox** (`MessagingOutboxRecord`, `MessagingDbContext.OutboxMessages`), pero **no tiene evento
   de asignación**. Las asignaciones salen por dos caminos: `IConversationEvents.Record` (EF, handlers y traspasos de
   `AutomationConversations`/`AiConversations`) y `ConversationEventRows.InsertAsync` (SQL, ingesta y `ConversationOpener`).
   **Cambio:** el aviso se deriva del `ConversationEvent` en esos dos puntos, no en cada handler (D-10).
8. **Los payloads de exportación no traen id de exportación** y no existe un `GET` que vuelva a firmar el enlace en ningún módulo
   (`POST /products/export`, `/customers/export`, `/suppliers/export`, `/quotations/export`, `/orders/export`; ninguna ruta de
   descarga). El enlace **no se puede re-derivar**: se guarda **[owner]** (D-12).
9. **`quotations.order.created.v1` ya tiene consumidor** (`Pipeline.QuotationEventsProcessor`), pero no para avisos al equipo.
   Cada consumidor tiene su inbox, así que sumar el de `Notifications` no toca a Pipeline.
10. **`ServiceContractsMemberRef.CanManage`** es «membresía activa y roles con `billing.service.manage`»
    (`IServiceContractsMemberDirectory.cs:3`). Verificado.
11. **`RealtimeTopics.ForbiddenCode` es el código de Soporte** con la nota «hasta que un segundo módulo use el hub; entonces pasa a
    `realtime.topic_forbidden`». Este trabajo suma a Mensajería al hub: el código del hub cambia (D-8). El frontend sólo lo loguea.
12. **El historial del outbox ya existe**: un consumidor nuevo vería todo `quotations.order.created.v1` histórico como pendiente.
    **Cambio:** los consumidores de la campana ignoran eventos con más de 24 h (D-6).
13. **El `advisorMemberId` de un pedido mayorista, verificado:** `PurchasesWholesaleOrderIntake` usa el asesor de la relación si está
    activo y, si no, `FindOwnerMembershipIdAsync` del mayorista; eso viaja en `draft.AdvisorMembershipId` → `order.AdvisorId`
    (`IWholesaleOrderGateway.cs:177`) → `advisorMemberId` de `quotations.order.created.v1` (`QuotationLifecycleEventPublisher.PublishOrderCreated`).
    Coincide con el encargo.
14. **`ReopenIfWaiting` tiene tres llamadores**: dos en `SupportThreadFollower` (sistema) y `AssignUnclassified` (un asesor clasifica a
    mano un entrante). En el tercero quien actúa puede ser el mismo responsable: no se le avisa a sí mismo (§6).
15. **`CancelOrder` publica `wholesale-cancelled` también para un pedido mayorista ya aprobado.** La entrada resuelta como «aprobado» no se
    pisa (D-13).
16. **Los eventos de la ingesta llevan la hora de Meta** (menos 1 ms en `InboundIngestion`). Con la regla de 24 h (D-6), una reentrega
    tardía de Meta se perdería: el `occurred_at` del outbox es la hora de escritura y la del hecho va en el payload (§12).
17. **El grupo personal no revisa módulo ni permiso** como `MessagingHub` al conectar (módulo `messaging` + `messaging.conversation.read`).
    La alerta personal de entrante los revisa antes de mandar (§9.2).
18. **Un horario sin ninguna franja nunca abre**: `BusinessHoursClock.BusinessSeconds` da 0 y el aviso no saldría nunca. Las opciones de
    horario dicen si tienen franjas (§8.6) para que la pantalla lo advierta.

## 6. Catálogo de la primera entrega

| `kind` | Origen | Destinatario | Se guarda |
| --- | --- | --- | --- |
| `ConversationAssigned` | `messaging.conversation.assigned.v1` (nuevo) | la membresía destino | sí |
| `ExportReady` | `catalog.product-export-ready.v1`, `customers.export-ready.v1`, `suppliers.export-ready.v1`, `quotations.export-ready.v1`, `wms.export-ready.v1` (saldos de Bodegas) | quien pidió (usuario → membresía) | sí |
| `ExportFailed` | `quotations.export-failed.v1` | quien pidió | sí |
| `SupportCustomerReplied` | `support.ticket.customer-replied.v1` (nuevo) | `SupportTicket.AssigneeMemberId` | sí |
| `SupportTicketUnassigned` | `support.ticket.unassigned-alert.v1` (nuevo) | los miembros elegidos por el tenant | sí |
| `WholesaleOrderPendingApproval` | `quotations.order.created.v1` con `source == "Wholesale"`; se resuelve con `quotations.order.wholesale-approved.v1` y `…wholesale-cancelled.v1` | `advisorMemberId` del pedido | sí |
| `ServiceChargesOverdue` | `billing.charges.overdue-digest.v1` (nuevo) | cada miembro que recibe el digest del día | sí |
| `ServicePriceReviewsDue` | `billing.price-reviews.digest.v1` (nuevo, spec reajuste §6.6) | cada miembro que recibe el digest del día | sí |
| `HrMedicalExamsDue` | `hr.medical-exams.due-digest.v1` (nuevo, spec 2026-11-23 §9) | cada miembro con `hr.medical.manage` | sí |
| `HrExitExamRequired` | `hr.medical-exams.exit-required.v1` (nuevo, spec 2026-11-23 §9) | cada miembro con `hr.medical.manage` | sí |
| — (`inboundMessageAlert`) | `IMessagingNotifier.InboundMessageAsync` | asignado, o el tenant con lectura de Mensajería | **no** (sólo en vivo) |

Reglas por tipo:

- **`ConversationAssigned`**: `Transferred` con destino distinto del actor (`TransferConversationHandler`, `Conversation.TransferTo`),
  `Inherited` (`InboundIngestion.cs:218-222`), y `AutomationPaused` con `PauseReason = Handoff` y `Handoff.AssignedMemberId`
  (`Conversation.HandOffAutomation`, llamado desde `AutomationConversations.cs:110` y `AiConversations.cs:89`). **No** `Taken` ni
  `AutoTaken` (quien recibe es quien actúa), ni el `Taken` de `ConversationOpener`. **[owner]**
- **Exportaciones**: productos, clientes y proveedores sólo tienen «lista»; sus fallas son errores HTTP síncronos y no dejan
  entrada. Cotizaciones y pedidos tienen las dos. **[owner]** El export de saldos de Bodegas (`exportKind: "Stock"`) también
  sólo tiene «lista»: se arma dentro del request.
- **`SupportCustomerReplied`**: sólo cuando un entrante vinculado pasa el ticket de `WaitingOnCustomer` a `Open`
  (`SupportThreadRules.ReopenIfWaiting`, único punto de ese cambio, con tres llamadores) **y** el ticket tiene responsable, **y** quien
  causó el cambio no es ese responsable (en `AssignUnclassified` lo causa un asesor; en el seguidor del hilo, el sistema). Sin responsable no hay aviso
  inmediato: lo cubre `SupportTicketUnassigned`. `SupportReplyEmailTemplate` es el correo asesor→cliente y no tiene que ver. **[owner]**
- **`SupportTicketUnassigned`**: §7. **[owner, con el ajuste de §5.4]**
- **`WholesaleOrderPendingApproval`**: sólo el asesor del pedido, no todos los que tienen `quotations.order.manage`, para que la
  aprobación de uno no deje avisos viejos en otros. **[owner]** Al aprobarse o cancelarse, la entrada queda **resuelta y leída**
  (D-13).
- **`ServiceChargesOverdue`**: una entrada por miembro y día, «Tienes N cobros vencidos», sólo si `N > 0`, con los mismos
  destinatarios y el mismo reclamo del digest (`ServiceContractsReminderProcessor.SendDigestsAsync`, `DailyDigestStore.TryClaimAsync`).
  **[owner]**
- **`ServicePriceReviewsDue`** (spec reajuste §6.6): una entrada por miembro y día, sólo si su digest trae revisiones de precio por
  decidir o cobros retenidos (`dueCount + heldCount > 0`), con los mismos destinatarios y el mismo reclamo del digest. La llave es el
  día (`billing-price-review:{runDate}`): una reentrega del mismo día no duplica.

- **`HrMedicalExamsDue`** (spec 2026-11-23 §9): una entrada por miembro y día, «Tienes N exámenes médicos pendientes», sólo si `N > 0`
  (`N = ExitPending + NoExams + Overdue + DueSoon`), para quienes tienen `hr.medical.manage`. La llave es el día
  (`hr-medical-exams:{runDate}`): una reentrega del mismo día no duplica.
- **`HrExitExamRequired`** (spec 2026-11-23 §9): una entrada por miembro y terminación, «Salió {empleado}: egreso pendiente», que nace
  con `TerminateContract` y apunta al empleado (`resource_id`). La llave es `hr-exit-exam:{employeeId}:{dueDate}`. Ningún payload ni
  entrada lleva texto clínico ni el tipo de examen.

## 7. Ticket sin asignar (aviso nuevo de Soporte)

Configuración por tenant en `SupportTenantSettings` (`/settings/support`), bajo `support.settings.manage`:

| Campo | Default | Regla |
| --- | --- | --- |
| `unassignedAlert.enabled` | `false` | **[owner]** apagado por defecto |
| `unassignedAlert.thresholdHours` | `4` | entero 1–72, horas hábiles |
| `unassignedAlert.scheduleId` | `null` | un horario de atención del tenant; `null` = el predeterminado del tenant |
| `unassignedAlert.recipientIds` | `[]` | hasta 20 membresías; con `enabled = true`, al menos una |

- Los destinatarios («miembros líderes») los elige el tenant **entre quienes pueden gestionar tickets**
  (`ISupportMemberDirectory.ListManagersAsync`, `support.ticket.manage`): el aviso pide asignar, y asignar exige gestionar.
  Sin rol ni permiso nuevo. **[owner, 2026-11-22]**
- Al guardar, un id que no está en esa lista responde 422 `support.settings.alert_recipient_invalid`.
- Al avisar, un destinatario inactivo o que perdió `manage` se salta. Si no queda ninguno, no se avisa, y el `GET` lo dice
  (`activeRecipientCount = 0`, `recipients[].canReceive = false`) para que la pantalla lo muestre.
- **Horas hábiles [owner]**: las del horario de atención (`/settings/business-hours`), con sus franjas, excepciones y festivos, en la
  zona del tenant, contadas con `BusinessHoursClock.BusinessSeconds`. Horario efectivo: el elegido si sigue existiendo; si no se eligió
  o se borró, el **predeterminado** del tenant; si el tenant no tiene ninguno, **horas corridas** (la semántica «sin horario = siempre
  abierto» de Mensajería). El `GET` dice cuál rige y por qué (§8.6) para que la pantalla lo muestre. **[owner]**
- Un horario editado cuenta con su forma **actual** también para el tramo ya transcurrido; no se guarda una foto. **decisión de diseño**
- Borrar un horario no se bloquea desde Soporte (no hay referencia entre módulos): el aviso cae al predeterminado y la pantalla lo dice.
  El `scheduleId` guardado no se limpia solo: se lee como «borrado» hasta que alguien guarde otro.
- Estados que cuentan: `New`, `Open`, `WaitingInternal`. `WaitingOnCustomer`, `Resolved` y `Closed` no cuentan (nadie tiene que
  actuar todavía, o ya terminó). **decisión de diseño (abierta en §14)**
- **El reloj mide tiempo continuo en un estado que cuenta y sin responsable** (D-15): `unassigned_since` se pone cada vez que el ticket
  **entra** en esa situación (alta sin responsable; `Assign(null)` estando en un estado que cuenta; un cambio de estado hacia `New`,
  `Open` o `WaitingInternal` sin responsable, incluido `ReopenIfWaiting`) y se borra al asignarse o al salir a un estado que no cuenta.
  Un ticket que esperó días al cliente y vuelve a `Open` empieza de cero, no avisa en el acto. **decisión del coordinador, 2026-11-22**
- El reloj arranca en `max(unassigned_since, unassigned_alert_enabled_at)`: prender el aviso no dispara de golpe por tickets
  viejos.
- **Un aviso por ticket y por tramo**: la clave es `(ticket_id, unassigned_since)`. Asignado antes del umbral, no hay aviso. Cada tramo
  nuevo (otra vez sin asignar, o de vuelta a un estado que cuenta) tiene otro `unassigned_since` y cuenta de nuevo. **[owner]**
- Texto: «Ticket SOP-000127 lleva 4 h sin asignar»; abre el ticket.

## 8. Contrato HTTP

Grupo `/api/v1/tenants/{tenantId:guid}/notifications`, `RequireAuthorization()` (sólo sesión: no hay permiso; cada miembro lee lo
suyo). Cada handler revalida: tenant de la ruta = tenant activo (si no, 403 `authorization.denied`) y membresía activa de quien
llama (`IMembershipDirectory.FindActiveMembershipIdAsync`; si no hay, 403 `authorization.denied`). **Todo repositorio filtra por
`tenant_id` y `member_id` de quien llama.** Sin módulo: la campana es del núcleo.

### 8.1 `GET …/notifications?cursor=&limit=&unreadOnly=`

- `limit`: 1–50, default 20. `unreadOnly`: `true|false`, default `false`. `cursor`: el `nextCursor` anterior, opaco.
- Orden: `createdAt` descendente, desempate por `id` descendente. Paginación por cursor (scroll), como la bandeja de Mensajería y
  las sesiones de agentes: los avisos llegan mientras se pagina y un offset repetiría o saltaría filas.
- 200:

```json
{
  "items": [ /* InAppNotificationDto */ ],
  "nextCursor": "string | null",
  "unreadCount": 3
}
```

  `unreadCount` viaja en la lista porque la pantalla pinta la insignia con lo mismo que acaba de leer (BFF).
- 422 `validation.failed` con `errors.limit` o `errors.cursor` (cursor ilegible).

### 8.2 `GET …/notifications/unread-count`

200 `{ "count": 3 }`. Lo que la insignia pide al cargar y al recibir un aviso. Cuenta exacta; el «99+» es de la pantalla.

### 8.3 `POST …/notifications/{notificationId:guid}/read`

Idempotente: una entrada ya leída responde igual, sin cambiar `readAt`. 200:

```json
{ "notification": { /* InAppNotificationDto */ }, "unreadCount": 2 }
```

404 `notifications.notification.not_found` si el id no es del miembro (de otro miembro del mismo tenant, o inexistente). Sin
`If-Match`: es una marca personal, idempotente, sin carrera que perder (dos pestañas que marcan lo mismo terminan igual).

### 8.4 `POST …/notifications/read-all`

Cuerpo opcional `{ "upTo": "2026-11-22T15:04:05.123Z" }`: marca las no leídas con `createdAt <= upTo`. Sin cuerpo (o `upTo`
nulo), todas. `upTo` existe para no marcar como leído un aviso que llegó **después** de que la persona abrió la lista: la pantalla
manda el `createdAt` de la más nueva que mostró, **tal cual lo recibió** (el texto, sin pasar por `Date`). 200 `{ "unreadCount": 0 }`.
Sin `If-Match`, por la misma razón.

- **Binding y errores (verificado contra el patrón del repo):** `ApiExceptionHandler` no traduce `BadHttpRequestException`, así que un campo
  tipado como fecha que no parsea lo rechaza el binder con **400**. Por eso, como `SaveBusinessHoursRequest` («opcional campo a campo para que
  un cuerpo incompleto salga 422 con el mapa `errors`, no 400 del binder»), el request es `MarkAllReadRequest(string? UpTo)` y el validador
  lo parsea: un `upTo` que no es ISO 8601 con zona responde **422 `validation.failed` con `errors.upTo`**. Un cuerpo que no es JSON sigue
  siendo 400 del binder.
- **Precisión:** `created_at` es `timestamptz` (microsegundos). El DTO serializa el valor **leído de la base** en formato ISO 8601 completo
  (`"O"`), así que el texto que vuelve en `upTo` es exactamente el guardado y `created_at <= upTo` incluye a esa entrada. El frontend nunca
  redondea ni reformatea.

Ambas escrituras publican `notificationsChanged` al miembro (§9.1) **después** del commit, para que sus otras pestañas
actualicen la insignia.

### 8.5 `InAppNotificationDto`

```json
{
  "id": "uuid",
  "kind": "ConversationAssigned | ExportReady | ExportFailed | SupportCustomerReplied | SupportTicketUnassigned | WholesaleOrderPendingApproval | ServiceChargesOverdue | ServicePriceReviewsDue | SupportTaskAssigned | SupportTicketAssigned | HrMedicalExamsDue | HrExitExamRequired",
  "createdAt": "ISO 8601",
  "readAt": "ISO 8601 | null",
  "resolvedAt": "ISO 8601 | null",
  "data": { }
}
```

`kind` viaja por nombre; el diccionario de textos es del frontend. Un `kind` que el frontend no conozca (backend más nuevo) se pinta
genérico. `data` según `kind` (los nombres son contrato; `null` es explícito, nunca se omite la clave):

| `kind` | `data` |
| --- | --- |
| `ConversationAssigned` | `{ conversationId, reason: "Transferred" \| "Inherited" \| "Handoff", actorName: string \| null, contactName: string \| null }` |
| `ExportReady` | `{ exportKind: "Products" \| "Customers" \| "Suppliers" \| "Quotations" \| "Orders" \| "Stock", fileName, rowCount, expiresAt, downloadUrl: string \| null }` |
| `ExportFailed` | `{ exportKind: "Quotations" \| "Orders" }` |
| `SupportCustomerReplied` | `{ ticketId, ticketNumber }` |
| `SupportTicketUnassigned` | `{ ticketId, ticketNumber, thresholdHours }` |
| `WholesaleOrderPendingApproval` | `{ orderId, orderNumber, resolution: "Approved" \| "Cancelled" \| null }` |
| `ServiceChargesOverdue` | `{ overdueCount, runDate: "yyyy-MM-dd" }` |
| `ServicePriceReviewsDue` | `{ dueCount, heldCount, runDate: "yyyy-MM-dd" }` |
| `HrMedicalExamsDue` | `{ pendingCount, runDate: "yyyy-MM-dd" }` |
| `HrExitExamRequired` | `{ employeeId, employeeName, dueDate: "yyyy-MM-dd" }` |

- `actorName` y `contactName` se resuelven **al leer** (`IInAppNotificationNames`, adaptador en Bootstrapper): un nombre que cambia
  se ve actualizado. `actorName` es `null` en `Inherited` y `Handoff` (lo hizo el sistema o un asistente) y para un miembro que ya
  no aparece. `contactName` es el nombre del cliente vinculado o, si no hay, el nombre de perfil del contacto; sin nombre real,
  `null` (la pantalla dice «un contacto»). El adaptador **reusa la regla de Mensajería**; si Mensajería no expone un lector por ids,
  se agrega `IMessagingConversationLabels` en `Modules.Messaging.Application` (plan, Tarea 6).
- `downloadUrl` es `null` desde `expiresAt` (la pantalla muestra «El enlace venció») y la retención lo borra de la fila (§11).
- `ticketNumber` es el radicado ya formateado (`SOP-000127`, `SupportTicketNumber.Format`), `orderNumber` el número visible del
  pedido. Los dos son inmutables y viajan en el evento.
- `resolvedAt`/`resolution` sólo los usa `WholesaleOrderPendingApproval` por ahora.

### 8.6 Ajustes de Soporte (cambio al contrato existente)

`GET /api/v1/tenants/{tenantId}/support/settings` suma a `SupportSettingsDto`:

```json
"unassignedAlert": {
  "enabled": false,
  "thresholdHours": 4,
  "minThresholdHours": 1,
  "maxThresholdHours": 72,
  "scheduleId": "uuid | null",
  "schedule": { "source": "Chosen | Default | ChosenMissing | None", "id": "uuid | null", "name": "string | null" },
  "scheduleOptions": [ { "id": "uuid", "name": "string", "isDefault": true, "hasWeeklyHours": true } ],
  "timeZone": "America/Bogota",
  "maxRecipients": 20,
  "recipients": [ { "memberId": "uuid", "name": "string", "canReceive": true } ],
  "recipientOptions": [ { "memberId": "uuid", "name": "string" } ],
  "activeRecipientCount": 1
}
```

- `recipientOptions`: quienes pueden ser destinatarios hoy (`ISupportMemberDirectory.ListManagersAsync`), por nombre. Viaja acá porque
  `GET /support/assignees` exige `SupportPolicies.TicketView` y quien sólo tiene `support.settings.manage` no podría elegir. **[owner, 2026-11-22]**

- `scheduleId` es lo guardado (`null` = predeterminado). `schedule` es lo que **rige hoy**: `Chosen` (el guardado existe),
  `Default` (no se eligió: rige el predeterminado), `ChosenMissing` (el guardado se borró: rige el predeterminado, o nada si no hay;
  `id`/`name` son los del que rige), `None` (el tenant no tiene horarios: horas corridas; `id`/`name` nulos). La pantalla lo explica
  sin cruzar datos. **decisión de diseño**
- `scheduleOptions`: los horarios del tenant por nombre (`IBusinessHoursProvider.ListSchedulesAsync`, D-14), sólo `id`, `name`, `isDefault` y
  `hasWeeklyHours` (`false` si ningún día de la semana tiene franjas: con ese horario el aviso no saldría nunca, §5.18; las excepciones de
  fecha no se miran, la pantalla sólo advierte).
  Los sirve **Soporte**, bajo su propia política (`SupportPolicies.SettingsView`), sin exigir `messaging.conversation.read`: quien configura
  Soporte necesita elegir un horario aunque no lea Mensajería, y nombres de horario no son datos sensibles. Las franjas no viajan acá: para
  verlas o editarlas está `/settings/business-hours`, con sus permisos. **decisión de diseño; se mantiene, el owner no la objetó (2026-11-22)**
- `timeZone`: id IANA de `TenantCalendar.TimeZone`, para que la pantalla diga de qué reloj habla, como `BusinessHoursListDto.TimeZone`.
- `recipients` viaja **en el orden guardado** y con nombre (BFF: la pantalla no cruza con `/assignees`). `canReceive` es «activa y con
  `support.ticket.manage` hoy».

`PUT …/support/settings` (con `If-Match`, como hoy) acepta `unassignedAlert` **opcional**: ausente o `null` conserva lo guardado
(el frontend publicado no lo manda; mismo criterio que `surveyEnabled`). Con valor, viaja entero:
`{ enabled, thresholdHours, scheduleId, recipientIds }`. Errores: 422 `validation.failed` con `errors` en
`UnassignedAlert.ThresholdHours`, `UnassignedAlert.RecipientIds` (rutas de propiedad en PascalCase, la convención del repo; el frontend baja la primera letra de cada segmento y las lee como `unassignedAlert.thresholdHours`; más de 20, repetidos, o vacío con `enabled`); 422
`support.settings.alert_recipient_invalid` si un id no puede gestionar tickets; 422 `support.settings.alert_schedule_invalid` si
`scheduleId` no es un horario del tenant (`errors` no aplica: es un id elegido de una lista). La auditoría nombra `unassignedAlert` en
`changedFields`. Un solo `PUT`, una sola versión.

## 9. Tiempo real

### 9.1 Entrega personal: grupo del miembro

- `AppHub.OnConnectedAsync` une la conexión al grupo `RealtimeTopics.MemberGroupOf(tenantId, memberId)` =
  `"{tenant:D}:@member:{member:D}"`, con el miembro de la membresía activa (ya resuelto ahí). El cliente no manda nada.
- **Infalsificable**: ningún autorizador puede declarar un prefijo que empiece por `@` (prueba nueva), así que `Subscribe("@member:…")`
  siempre se rechaza, y el grupo sólo se forma desde las claims de la conexión.
- `IRealtimeMemberPublisher.PublishToMemberAsync(tenantId, memberId, kind, payload, ct)` (BuildingBlocks), implementado por
  `PgRealtimePublisher`: mismo `pg_notify('qep_realtime', …)`, con `RealtimeEnvelope.MemberId` y `Topic = "@member"`. El
  dispatcher entrega a `MemberGroupOf`. Nunca lanza (mismo contrato que `IRealtimePublisher`).
- Compatibilidad durante el rollout: un pod viejo lee el sobre sin `MemberId` y manda al grupo `{tenant}:@member`, que no tiene a
  nadie. Inofensivo.

Evento de la campana, al grupo del miembro:

| Evento | Payload | Cuándo |
| --- | --- | --- |
| `notificationsChanged` | `{ "notificationId": "uuid" \| null }` | entrada nueva, resuelta, leída o «todo leído» (`null`) |

### 9.2 Alerta de mensaje entrante

| Evento | Payload | A quién |
| --- | --- | --- |
| `inboundMessageAlert` | `{ "conversationId": "uuid", "messageId": "uuid" }` | con asignado: su grupo de miembro; sin asignado: tópico `messaging` |

- Tópico **`messaging`** del `AppHub`, autorizado por `MessagingRealtimeAuthorizer` (prefijo `messaging`): sólo la forma exacta
  `messaging`, módulo `messaging` prendido y `messaging.conversation.read` del miembro, leído de la base
  (`IMessagingMemberPermissions`, copia de `ISupportMemberPermissions`). Es la misma regla del endpoint del `MessagingHub`
  (`RequireAuthorization(MessagingPermissions.ConversationRead)`). Presencia: siempre rechazada.
- Lo entrega `AppHubMessagingAlertSink` (Bootstrapper), un `IMessagingEventSink` más: cada pod lo recibe del listener y escribe
  en **sus** conexiones. Misma decisión de destino que `MessagingHubEventSink` (`AssignedMemberId`).
- **Con asignado, antes de mandar al grupo del miembro**, el sink revisa en un scope propio lo que `MessagingHub` exige al conectar: módulo
  `messaging` prendido (`ITenantModules`; tenant sin fila no se niega) y `messaging.conversation.read` del miembro (`IMessagingMemberPermissions`).
  Si no, no manda. El tópico `messaging` ya lo filtra su autorizador al suscribir. **decisión del coordinador, 2026-11-22**
- **Sin texto, sin nombre**: el frontend pide la conversación por HTTP con su propia autorización si quiere el nombre.

### 9.3 Códigos

`RealtimeTopics.ForbiddenCode` pasa a `realtime.topic_forbidden` (tópico sin autorizador, mal formado o tope). Cada autorizador
conserva el suyo: Soporte `support.realtime.topic_forbidden`, Mensajería `messaging.realtime.topic_forbidden` (nuevo en
`MessagingErrorCodes`).

## 10. Decisiones

**D-1 [owner]** Enfoque B: productores → outbox de su módulo → consumidores en `Notifications` → `AppHub`.

**D-2 [owner]** Un socket por pestaña: el `AppHub`. Ningún módulo crea su hub. El hub anuncia ids; la pantalla relee por HTTP.

**D-3 (decisión de diseño)** Entrega personal por grupo del miembro unido en `OnConnectedAsync`, no por tópico autorizado. Motivos:
el SPA no conoce su `memberId` (D-F1); no hay `Subscribe` que pueda fallar y quedar sordo hasta el próximo reconnect; un reconnect
vuelve a unir el grupo solo; es infalsificable por construcción. Costo: toda conexión recibe los avisos de su miembro aunque esa
pestaña no tenga campana; son ids, y la campana se monta en todas.

**D-4 (decisión de diseño)** Tabla nueva `notifications.in_app_notifications`, **no** reusar `Notification`/`Internal`. `Notification`
es el registro de un **envío** (estado `Pending/Sent/Failed`, `RecipientAddress`, `TemplateRef`, anonimización para borrar
usuarios), con `RecipientId` = usuario y tenant opcional; no tiene payload ni estado de lectura. Mezclarlos obliga a cada consulta de
correo a filtrar canal y le da a la campana invariantes que no son suyos (anonimizar, fallar). `NotificationChannel.Internal` queda
como está (no se usa; quitarlo es otro cambio).

**D-5 (decisión de diseño)** Destinatario siempre **membresía**. Las exportaciones traen `subjectId` (usuario): el consumidor lo
traduce con `IMembershipDirectory.FindActiveMembershipIdAsync(subjectId, tenantId)`; sin membresía activa, el mensaje se procesa sin
entrada.

**D-6 (decisión de diseño)** Los consumidores de la campana sólo toman eventos con `occurred_at >= now − 24 h` (`InAppOutboxConsumer.MaxEventAge`).
Sin esto, el primer despliegue llenaría la campana con el histórico del outbox (todos los pedidos creados). Lo más viejo nunca se
reclama: no cuesta ni una fila de inbox.

**D-7 (decisión de diseño)** Idempotencia en dos capas: el reclamo por consumidor de siempre (`InboxClaims`, PK
`(consumer, message_id)`), y un índice único `(tenant_id, member_id, dedupe_key)` en la tabla. `dedupe_key` es el id del mensaje del
outbox salvo `billing-overdue:{runDate}` y `support-unassigned:{ticketId}:{unassignedSince:O}`, donde el productor puede repetir el
evento (re-reclamo del digest; reintento del worker). Las entradas se escriben con `INSERT … ON CONFLICT (tenant_id, member_id,
dedupe_key) DO NOTHING`, **una sentencia por entrada**, y después se marca `processed_at`, todo en la transacción del mensaje. No con
`SaveChanges` de EF: un 23505 en un lote revertiría todas las entradas del mensaje (un aviso de ticket sin asignar trae una por destinatario)
y el mensaje se reintentaría hasta `MaxAttempts` sin escribir nada. Sólo se publica `notificationsChanged` por las filas que **sí** se
insertaron (`RETURNING id`).

**D-8 (decisión de diseño)** `RealtimeTopics.ForbiddenCode` pasa a `realtime.topic_forbidden` (la nota del código lo anticipaba).

**D-9 (decisión de diseño)** La alerta de entrante la entrega un sink por pod (`AppHubMessagingAlertSink`), no un republish. Durante
la transición el `MessagingHub` **sigue mandando `notify`**: el frontend nuevo deja de sonar por su cuenta y manda ese `notify` al
mismo manejador global, que desduplica por conversación (spec del frontend). Quitar `notify` es un paso aparte, cuando el frontend
nuevo esté en producción (§13). Así un frontend viejo sigue sonando igual que hoy.

**D-10 (decisión de diseño)** `messaging.conversation.assigned.v1` se deriva del `ConversationEvent` en los dos puntos donde se escribe
la fila de evento (`ConversationEvents.Record` y `ConversationEventRows.InsertAsync`), con una función pura
`ConversationAssignmentNotice.From(ConversationEvent)`. Así cubre transferencia, herencia y traspaso, y cualquier camino futuro que
escriba esos eventos, en la misma transacción que el cambio. El traspaso **además** suena (hoy `HandoffAsync` llama
`InboundMessageAsync` al asignado, D-M5): campana y alerta conviven a propósito.

**D-11 (decisión de diseño)** Retención 90 días (`Notifications:InApp:RetentionDays`, ajustable). Borrado en lotes de 1.000 por un worker
horario, idempotente.

**D-12 [owner, 2026-11-22]** El enlace de descarga **se guarda** en `data` porque no se puede re-derivar (§5.8). Se sirve sólo al
miembro dueño, sólo antes de `expiresAt`, y la retención lo **borra de la fila** después de vencer (no espera los 90 días); desde ahí la
entrada dice «El enlace venció». Alternativa descartada: un `GET …/exports/{id}/download` por módulo que vuelva a firmar, que además
exige meter la clave del objeto en cuatro payloads.

**D-13 (decisión de diseño)** Un pedido mayorista aprobado o cancelado **resuelve** su entrada (`resolved_at`, `resolution`) y la marca
leída. Los tres eventos mayoristas los procesa **un solo consumidor** en orden de `occurred_at`, para que la aprobación no llegue antes
que la creación en el caso normal. Sólo se resuelve una entrada **todavía sin resolver**: `CancelOrder` publica `wholesale-cancelled`
también para un pedido ya aprobado, y eso no pisa «aprobado». Residual: si la creación falla y se reintenta después de la aprobación,
queda una entrada sin resolver.

**D-14 [owner, 2026-11-22: usar el horario de atención; el contrato compartido es decisión de diseño]** Las horas del aviso son las del
horario de atención del tenant, expuesto como **capacidad compartida** con un contrato en `BuildingBlocks.Application`, el mismo patrón de
`IRealtimePublisher`/`IRealtimeTopicAuthorizer` (`BuildingBlocks.Application.Realtime`) y de `IUserReferenceProbe` (contrato en
BuildingBlocks, implementado y registrado por cada módulo en su `…InfrastructureExtensions`, p. ej. Catalog):

```csharp
namespace BuildingBlocks.Application.BusinessHours;

/// <param name="HasWeeklyHours">false si ningún día de la semana tiene franjas (ese horario nunca abre por su semana).</param>
public sealed record BusinessHoursScheduleRef(Guid Id, string Name, bool IsDefault, bool HasWeeklyHours);

/// <param name="EffectiveScheduleId">El horario con que se contó; null = el tenant no tiene horarios y se contó de corrido.</param>
public sealed record BusinessHoursSpan(long BusinessSeconds, Guid? EffectiveScheduleId);

public interface IBusinessHoursProvider
{
    /// <summary>Los horarios del tenant por nombre.</summary>
    Task<IReadOnlyList<BusinessHoursScheduleRef>> ListSchedulesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>scheduleId null o inexistente → el predeterminado; sin horarios → siempre abierto.</summary>
    Task<bool> IsOpenAsync(Guid tenantId, Guid? scheduleId, DateTimeOffset instant, CancellationToken cancellationToken);

    /// <summary>Misma resolución; sin horarios, segundos corridos. Los extremos se llaman <c>fromInstant</c>/<c>toInstant</c> porque CA1716 reserva <c>to</c> como nombre de parámetro de una interfaz.</summary>
    Task<BusinessHoursSpan> ElapsedBusinessSecondsAsync(
        Guid tenantId, Guid? scheduleId, DateTimeOffset fromInstant, DateTimeOffset toInstant, CancellationToken cancellationToken);
}
```

- **Lo implementa Mensajería**, dueña de los horarios: `Modules.Messaging.Infrastructure/Persistence/BusinessHoursProvider.cs` sobre
  `IBusinessHoursRepository` (`FindAsync`, `FindDefaultAsync`, `ListAsync`), `ITenantClock` (la zona, `TenantCalendar.TimeZone`) y
  `BusinessHoursClock.BusinessSeconds`; se registra en `MessagingInfrastructureExtensions` (como `BusinessHoursRepository` ya implementa
  `IBusinessHours` ahí). Mensajería ya referencia `BuildingBlocks.Application`. La regla de «qué horario rige» vive **una vez**, en el provider.
- **Soporte depende sólo de BuildingBlocks**: `SupportLayerTests.ApplicationOnlyReferencesTenancyAmongTheBusinessModules` prohíbe sólo
  ensamblados `Modules.*` distintos de Support y Tenancy; `BuildingBlocks.Application` ya lo referencia (`ICommand`, `IExecutionContext`). Sin
  adaptador en Bootstrapper y sin puerto propio de Soporte.
- **Desvío deliberado de la convención de Soporte**: el comentario de esa prueba dice que lo de afuera entra por puertos `ISupport*` con
  adaptadores en Bootstrapper. Acá no, a propósito: el horario de atención es una capacidad que varios módulos van a usar (Automatizaciones e IA
  hoy tienen cada uno su adaptador), y un contrato compartido evita un adaptador por consumidor. La prueba sigue en verde; se actualiza su
  comentario para nombrar esta excepción.
- La zona para mostrarla en los ajustes la lee Soporte de `ITenantClock` (Tenancy), que ya usa (`UpdateSupportSettingsHandler`).
- El provider no mira si el módulo `messaging` está prendido: los horarios son datos del tenant y siguen ahí aunque el módulo se apague.
- Fuera de alcance: migrar `AutomationBusinessHours`/`AiBusinessHours` (Bootstrapper) a este contrato; pueden hacerlo después sin cambiar nada acá.
- **Proyecto futuro posible:** sacar los horarios de Mensajería a un módulo propio (`/settings/business-hours` ya es una pantalla de
  Configuración, no de Mensajería). No cambiaría a ningún consumidor: el contrato de BuildingBlocks se queda y sólo se mueve quién lo implementa.
- `ISupportBusinessCalendar` no cambia: sigue siendo de los términos legales.

**D-15 (decisión de diseño; regla de pausa: decisión del coordinador, 2026-11-22)** `support.tickets.unassigned_since` (nullable)
mide tiempo continuo **sin responsable y en un estado que cuenta** (`New`, `Open`, `WaitingInternal`). El dominio (`SupportTicket`) lo
pone al **entrar** en esa situación (alta sin responsable; `Assign(null)` en un estado que cuenta; `ChangeStatus` hacia un estado que cuenta
sin responsable, lo que incluye `ReopenIfWaiting`) y lo borra al asignarse o al pasar a `WaitingOnCustomer`, `Resolved` o `Closed`. Un
cambio entre dos estados que cuentan no lo toca. Migración: tickets sin responsable en un estado que cuenta arrancan en la hora de la
migración (sin avalancha); los demás quedan en `null`. El worker además **sana** tickets sin responsable, en un estado que cuenta y con
`unassigned_since` nulo (escritos por un pod viejo durante el rollout) con un `UPDATE` que no toca `version`; y **limpia** `unassigned_since` en los tickets que SÍ tienen responsable (un pod viejo asignó sin borrarlo), para que una desasignación posterior de un pod viejo no herede un tramo ajeno (un aviso inmediato, o uno que nunca llega).

**D-16 (decisión de diseño)** `support.unassigned_alerts (ticket_id, unassigned_since)` como reclamo del aviso: `INSERT … ON CONFLICT DO
NOTHING` y el outbox en la **misma** transacción. No se toca la fila del ticket, así que el aviso nunca le da un 412 a quien lo edita.
Una fila con `recipient_count = 0` registra que no había a quién avisar (no se reintenta).

**D-17 (decisión de diseño)** El digest de cobros publica su evento **después** de `DailyDigestStore.TryClaimAsync` y **antes** de mirar el
correo: un miembro sin correo igual ve la campana. Si el proceso muere entre el reclamo y el `SaveChanges` del outbox, la fila del digest
queda `Sending` y, al vencer su lease (5 min), `TryClaimAsync` la vuelve a reclamar (`ON CONFLICT … WHERE status = 'Sending' AND claimed_until
< now`) en la corrida siguiente del mismo día: el evento se publica entonces, y si ya se había publicado, la `dedupe_key` del día lo absorbe.
Residual real: sólo si el día termina sin otra corrida (`FailStaleAsync` la cierra como fallida).

**D-18 (decisión de diseño)** Nombre del evento de cobros: `billing.charges.overdue-digest.v1`, con el prefijo de la clave de módulo y de
los permisos (`billing`), que no cambiaron con el renombre a «Contratos de servicio».

## 11. Modelo de datos y migraciones

### `notifications.in_app_notifications` (nueva, `NotificationsDbContext`)

| Columna | Tipo | Nota |
| --- | --- | --- |
| `id` | uuid PK | v7 |
| `tenant_id` | uuid not null | |
| `member_id` | uuid not null | membresía destinataria |
| `kind` | varchar(64) not null | enum por nombre |
| `resource_id` | uuid null | conversación, ticket o pedido (para resolver y desduplicar) |
| `data` | jsonb not null | §8.5 sin los nombres resueltos al leer (guarda `actorMemberId` en vez de `actorName`) |
| `dedupe_key` | varchar(200) not null | D-7 |
| `source_message_id` | uuid null | id del mensaje del outbox, para soporte |
| `created_at` | timestamptz not null | |
| `read_at` | timestamptz null | |
| `resolved_at` | timestamptz null | |

Índices: único `IX_in_app_notifications_member_dedupe (tenant_id, member_id, dedupe_key)`; lista
`IX_in_app_notifications_member_created (tenant_id, member_id, created_at DESC, id DESC)`; parcial
`IX_in_app_notifications_member_unread (tenant_id, member_id) WHERE read_at IS NULL`; `IX_in_app_notifications_resource
(tenant_id, kind, resource_id)`; `IX_in_app_notifications_created (created_at)` para la retención. Migración
`AddInAppNotifications`, expand-only.

### Soporte (`SupportDbContext`, dos migraciones expand-only, una por commit para que cada commit deje el modelo al día)

- `AddUnassignedAlertSettings` (plan, Tarea 11b): `support.tenant_settings` gana `unassigned_alert_enabled bool default false`,
  `unassigned_alert_hours int default 4`, `unassigned_alert_recipient_ids uuid[] default '{}'`, `unassigned_alert_schedule_id uuid null`
  (sin FK: el horario es de otro módulo), `unassigned_alert_enabled_at timestamptz null`. (El nombre real de la tabla de ajustes se toma del
  `SupportDbContext` al implementar.)
- `AddUnassignedAlerts` (plan, Tarea 12): `support.tickets.unassigned_since timestamptz null` + backfill (D-15: sólo sin responsable y en
  `New`/`Open`/`WaitingInternal`) + índice parcial `(tenant_id) WHERE assignee_member_id IS NULL`; y
  `support.unassigned_alerts (ticket_id uuid, unassigned_since timestamptz, tenant_id uuid, recipient_count int, alerted_at timestamptz,
  PK (ticket_id, unassigned_since))`.

### Sin migración

Messaging, ServiceContracts y Hr (spec 2026-11-23: sus tablas `hr.tenant_runs` y `hr.daily_digests` van en la migración de Hr, no en la de Notifications; `kind` es texto sin CHECK, así que los dos kinds nuevos no piden migración) sólo **escriben** en `platform.outbox_messages` (proyección `ExcludeFromMigrations`, columnas
`correlation_id` y `attempts` NOT NULL como `CatalogOutboxMessage`).

## 12. Eventos nuevos (contrato entre módulos)

Payload camelCase, leído por nombre con `JsonDocument` (convención de los workers de `Notifications`). En los cuatro, el `occurred_at` de la
**fila del outbox** es la hora en que se escribe (el reloj del productor), y el `occurredAt` del **payload** es la hora del hecho (en la
ingesta, la de Meta). Así la regla de 24 h (D-6) mira cuándo se publicó, y una reentrega tardía de Meta no se pierde.

| Evento | Payload | Correlación |
| --- | --- | --- |
| `messaging.conversation.assigned.v1` | `{ tenantId, conversationId, memberId, reason, actorMemberId, occurredAt }` | conversación |
| `support.ticket.customer-replied.v1` | `{ tenantId, ticketId, ticketNumber, assigneeMemberId, occurredAt }` | ticket |
| `support.ticket.unassigned-alert.v1` | `{ tenantId, ticketId, ticketNumber, recipientMemberIds, thresholdHours, unassignedSince, occurredAt }` | ticket |
| `billing.charges.overdue-digest.v1` | `{ tenantId, memberId, runDate, overdueCount, occurredAt }` | `{tenantId}:{memberId}:{runDate}` |
| `billing.price-reviews.digest.v1` | `{ tenantId, memberId, runDate, dueCount, heldCount, occurredAt }` | `{tenantId}:{memberId}:{runDate}` |
| `hr.medical-exams.due-digest.v1` | `{ tenantId, memberId, runDate, pendingCount, occurredAt }` | `{tenantId}:{memberId}:{runDate}` |
| `hr.medical-exams.exit-required.v1` | `{ tenantId, memberId, employeeId, employeeName, dueDate, occurredAt }` | `{tenantId:N}:{memberId:N}:{contractId:N}` (los tres en 32 hex: con el formato `D` miden 110 y `correlation_id` es `varchar(100)`) |

Consumidores (todos en `Notifications.Infrastructure`, drenados por `NotificationsDeliveryWorker`):

| Consumidor | Eventos |
| --- | --- |
| `notifications.in-app.conversations` | `messaging.conversation.assigned.v1` |
| `notifications.in-app.exports` | los seis de exportación |
| `notifications.in-app.support` | `support.ticket.customer-replied.v1`, `support.ticket.unassigned-alert.v1` |
| `notifications.in-app.wholesale` | `quotations.order.created.v1`, `quotations.order.wholesale-approved.v1`, `quotations.order.wholesale-cancelled.v1` |
| `notifications.in-app.billing` | `billing.charges.overdue-digest.v1`, `billing.price-reviews.digest.v1` |
| `notifications.in-app.hr` | `hr.medical-exams.due-digest.v1`, `hr.medical-exams.exit-required.v1` |

## 13. Fallas, observabilidad, seguridad y despliegue

**Fallas.** Un mensaje que falla queda reclamado y se reintenta al vencer el lease; pasado `MaxAttempts` (3) se procesa sin entrada y
se loguea `Warning` con el id del mensaje y el consumidor (como los correos). La publicación en vivo va después del commit y nunca
lanza: si falla, la insignia se pone al día con el sondeo de respaldo del frontend. El worker de Soporte aísla por tenant y por
ticket (un error se loguea y sigue). Un aviso es secundario: nunca deshace el cambio que lo causó.

**Observabilidad.** `LoggerMessage` con ids (consumidor, mensaje, tenant, ticket), nunca payload. Logs nuevos: entrada creada
(`Debug`), destinatario sin membresía (`Information`), aviso sin destinatarios (`Information`), retención (filas borradas,
`Information`). Sin métricas nuevas en esta entrega.

**Seguridad.** Cada lectura y escritura filtra por `tenant_id` **y** `member_id` de quien llama. El grupo personal sale de las claims.
Los payloads en vivo son ids. El texto del mensaje entrante nunca sale. `downloadUrl` sólo al dueño y sólo vigente. `ToString()` de los
DTO y entidades no imprime `data`.

**Despliegue: backend primero, aditivo.** Orden: (1) migraciones expand-only de `Notifications` y `Support`; (2) el código. Un frontend
viejo no llama las rutas nuevas, no escucha `notificationsChanged` ni `inboundMessageAlert`, y el `notify` del `MessagingHub` sigue.
**Paso posterior (otro cambio, después de que el frontend nuevo esté en producción):** quitar `notify` del `MessagingHub` y del
frontend.

## 14. Decisiones abiertas para el owner

**Lo único que sigue abierto para el owner** son estos cuatro puntos:

1. **Retención**: 90 días (propuesta). ¿Otro valor?
2. **Estados que cuentan para «sin asignar»**: `New`, `Open`, `WaitingInternal` (propuesta). ¿Incluir `WaitingOnCustomer`?
3. **Preferencias por tipo de aviso**: fuera de esta entrega; ¿se quieren después?
4. **Cuándo quitar `notify` del `MessagingHub`** (propuesta: el despliegue siguiente al del frontend nuevo).

Ya cerrado, para la trazabilidad: destinatarios y nombres de horario bajo permisos de Soporte (`recipientOptions`, `scheduleOptions`) y
destinatarios sólo entre quienes gestionan tickets [owner, 2026-11-22]; enlaces de descarga guardados (D-12) [owner]; horario de atención del
tenant (D-14) [owner]; origen del `advisorMemberId` mayorista, verificado en el código (§5.13).
