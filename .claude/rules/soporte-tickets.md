---
paths:
  - "src/Modules/Support/**"
  - "tests/Modules/Support/**"
  - "src/Bootstrapper/**/Support*.cs"
---

# Soporte: tickets, respuestas, plazos y vínculos al hilo

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **`support.ticket.view` es una política, no un permiso: exige `support.ticket.read` o `support.ticket.manage`** (plan de soporte
  fase 1, D5; el owner fijó que quien tenga cualquiera de los dos ve todos los tickets del tenant). Vive en `SupportPolicies.TicketView`,
  fuera de `SupportPermissions`, para que no se cuente ni se asigne como permiso, y se registra con `AddAnyPermissionRequirement`
  (`QepServiceCollectionExtensions`); `SupportPolicies.SettingsView` (`support.settings.view`) hace lo mismo con `read`, `manage` o
  `support.settings.manage`. Una ruta de lectura de Soporte con `RequireAuthorization(SupportPermissions.Read)` deja afuera a un rol con
  sólo `manage`, y el handler tiene que revalidar con la misma regla (`SupportAuthorization.EnsureCanViewAsync` /
  `EnsureCanViewSettingsAsync`), no con `EnsureAsync(Read)`. Es el primer módulo con este patrón.
- **Una columna con id de membresía en el esquema `support` entra en `SupportUserReferenceProbe`** (`src/Bootstrapper/`). Hoy revisa
  `tickets.assignee_member_id`, `ticket_events.actor_member_id`, `ticket_replies.author_member_id`,
  `unclassified_messages.resolved_by_member_id`, `form_versions.created_by`, `legal_text_versions.created_by`,
  `legal_document_versions.created_by` y `ticket_notes.author_member_id`; una columna nueva que no se sume ahí deja borrar al usuario que
  referencia, sin error. Los ids de membresía dentro de `ticket_events.details` (responsable anterior y nuevo de un
  `AssigneeChanged`) **no** se revisan, a propósito: si el usuario se borra, la traza los muestra como miembro removido («Sin
  nombre»). No los «arregles» sumando un escaneo del JSON: retendría a todo usuario que alguna vez fue responsable de un ticket.
- **El calendario de Soporte no es el de la bandeja** (owner, 2026-11-15, spec §13.4, revierte la decisión del 2026-11-14). Es lunes a
  viernes, menos los festivos de Colombia si el país del tenant es `CO` (`SupportBusinessCalendar`, en Bootstrapper por
  `ColombianHolidays`); no lee el horario de atención ni sus excepciones. No lo «unifiques» con `BusinessHoursSchedule`: un horario
  que abre los sábados correría los términos legales. Lo amarra
  `SupportBusinessCalendarTests.TheCalendarIsMondayToFridayLessColombianHolidaysOnEveryDayOfAYear`.
- **Un ticket `Resolved` o `Closed` no cambia de responsable, tipo ni cliente: `422 support.ticket.not_editable`** (R9, owner
  2026-11-15). La guarda está en el agregado (`SupportTicket.EnsureEditable`), no en los handlers, para que la cumpla todo camino
  nuevo; rechaza aunque el valor sea el mismo. La fusión de clientes (`CustomerMergeReferences`, SQL crudo) sigue moviendo tickets en
  cualquier estado, a propósito: es el sistema, no un asesor. **Extendida (owner, 2026-11-15):** fijar o soltar un mensaje y «Seguir el
  hilo» se permiten `Resolved` y dan el mismo `422 support.ticket.not_editable` en `Closed`, también apagar un hilo ya apagado
  (`SupportTicket.EnsureNotClosed`, desde `RecordMessageEvent` para `MessagePinned`/`MessageReleased` y desde `SetFollowThread`).
  Reemplazó a `support.ticket.follow_closed`, que ya no existe. Responder se permite en cualquier estado. Los caminos del sistema no pasan
  por esa guarda: el worker del hilo vincula entrantes (`SupportThreadFollower`), la fusión y el borrado de clientes son SQL crudo, y
  clasificar desde «sin clasificar» o «De ninguno» (`MessageClassified`, `MessageDismissed`) tampoco la toman.
- **Un tipo de ticket sin tickets sí se borra, pero la regla la cierra la FK, no la consulta previa** (fase 4a, plan G5).
  `FK_tickets_ticket_types_ticket_type_id` es `ON DELETE RESTRICT` y `SupportUnitOfWork` la traduce **por nombre** y SQLSTATE: `23001`
  (borrar un tipo usado) → `422 support.ticket_type.in_use`, `23503` (radicar o cambiar a un tipo recién borrado) →
  `support.ticket_type.unavailable`. La pregunta de `DeleteTicketTypeHandler` (`AnyOfTypeAsync`) es sólo la respuesta amable: sola, un
  `DELETE` y un alta a la vez dejan un ticket con `ticket_type_id` huérfano. No la cambies por la consulta. Lo amarran
  `TheForeignKeyTurnsADeleteOfAUsedTypeInto422InUse` y `TheForeignKeyTurnsATicketOnADeletedTypeInto422Unavailable`.
- **La prórroga se guarda en días hábiles y entra en `SupportTicket.DueOnFor`, el único cálculo del vencimiento** (fase 4a, plan G9).
  `DueOn` = radicación + término + `ExtensionBusinessDays` + `PausedBusinessDays`; así una pausa posterior y un cambio de tipo la siguen
  contando (G12: cambiar de tipo conserva los días). Un camino nuevo que recalcule el vencimiento pasa por `DueOnFor` o pierde la
  prórroga sin error. Las guardas de `Extend` van en un orden fijo (`not_editable`, `extension_already_used`, `extension_no_term`,
  `extension_clock_stopped`, `extension_overdue`, `extension_too_long`) y `MaxExtensionBusinessDays` usa las mismas cinco primeras: el
  DTO no las repite. La auditoría `support.ticket.extended` lleva nombres de campo, **nunca el motivo**. `Extended` es el último valor
  de `SupportTicketEventKind` y un pod viejo no lo lee (EF hace `Enum.Parse`): el detalle de un ticket prorrogado da 500 ahí durante el
  rollout, y al reanudar o cambiar el tipo el código viejo reescribe `due_on` sin la prórroga. Nadie prorroga hasta que no quede un
  pod viejo, y un rollback después de la primera prórroga no se hace (README, «Gestión y plazos de Soporte»). El doble del término se
  mide contra el término vigente al prorrogar: un cambio de tipo a uno más corto conserva la prórroga (P-5), no la recortes.
- **`IssuedPin` y `ConsultUrl` sólo viajan en el `201` del alta manual anónima y en el `200` de `POST …/tickets/{id}/pin`** (fase 4a,
  plan G3; spec 2026-11-16, P-6). `CreateTicketHandler` y `ReissueTicketPinHandler` los ponen con `with` sobre el detalle compuesto; el
  composer siempre los deja en `null`, así que el `GET`, las demás acciones y la auditoría nunca los llevan. Un camino nuevo que componga
  el detalle no los copia: es la única vez que el asesor puede dictarle el PIN al solicitante. Lo amarran
  `AnAnonymousManualTicketReturnsItsPinOnlyOnCreation` y `SupportPinReissueApiTests`. El PIN nuevo de la consola **desbloquea**
  (`SupportTicketAccess.ReissueByAgent`); `Reissue`, el del reenvío público, no: no los fundas.
- **El teléfono del cliente vinculado va al fijo de la foto del solicitante, nunca al celular** (owner, 2026-11-15, P3;
  `TicketRequesterNormalizer.WithCustomerContact`). `customers.phone_e164` puede ser fijo, y una plantilla de WhatsApp a un fijo la
  rechaza Meta: la respuesta queda `Failed` sin que el alta lo advierta. El celular de la foto sale sólo del solicitante o del `wa_id`
  de la conversación; sin él, `reply-options` dice `no_mobile`. No lo «arregles» copiando el teléfono al celular para habilitar la
  plantilla.
- **Las pruebas de plazos de Soporte comparten un host por clase con el reloj movible: `ClockedSupportHost`** (`SupportTicketsApiTests`,
  `SupportTicketActionsApiTests`, `SupportInboxApiTests`). Rompe a propósito la regla de «una prueba que toca el reloj levanta su host»:
  el `ITenantClock` del host es un `MutableTenantClock` y lo comparten sólo las pruebas de esa clase. Hoy es seguro porque xUnit corre
  las pruebas de una clase una tras otra, `StartTestAsync()` devuelve el reloj a `ClockedSupportHost.Reception` (viernes
  2026-11-13 10:00 en Bogotá) antes de cada una, y cada prueba siembra su propio tenant y sólo afirma sobre él. Se rompe, con fallas
  que dependen del orden, si una prueba nueva usa el host sin llamar a `StartTestAsync()` (hereda el reloj que dejó la anterior) o si se
  activa el paralelismo dentro de la clase (dos pruebas moverían el mismo reloj). Una prueba que necesite otra configuración del host
  levanta el suyo.
- **`ConversationEventType` y `MessageTemplateKind` ganaron valores al final** (`TicketOpened`, `TicketClosed`; `SupportReply`), y los
  dos se guardan por nombre (`details->>'type'`, `message_templates.kind`). No los reordenes ni los insertes en el medio. Escribir en el
  hilo desde Soporte pasa por `IConversationEventAppender`, enrolado en la transacción de Soporte por `SupportConversationEvents`: nunca
  un `INSERT` de Soporte en `messaging.*`.
- **Responder desde Soporte no toma la conversación** (plan E12): `ISupportReplyOutbound` no pasa por `OutboundGuards`, no la asigna, no
  cambia su estado y liquida con `seen: 0`. Lo autoriza Soporte con `support.ticket.manage`. La liquidación sí es la de siempre
  (`OutboundSettlement`), que es la que apaga `history_managed`: un camino nuevo de envío desde Soporte que se salte `IOutboundMessages`
  deja que la siguiente parte del historial pise la conversación. Lo amarra `SupportReplyOutboundTests.ASupportReplyTurnsOffHistoryManaged`.
- **Un vínculo `Linked` del worker no sube la versión del ticket ni escribe evento** (plan E7). Si lo hiciera, cada mensaje del cliente
  le daría un 412 al asesor que tiene el ticket abierto. Sólo sube cuando cambia el estado (`WaitingOnCustomer` → `Open`); pasar sola
  una fila de «sin clasificar» (`SupportThreadFollower.ReassignAsync`) tampoco escribe evento. El único `MessageClassified` lo escribe
  el asesor al asignar. La idempotencia del worker es por índices (`IX_ticket_messages_ticket_message`, `PK_unclassified_messages`,
  traducidos por nombre), no por candados: no la cambies por una consulta previa.
- **El retomado de una respuesta `Pending` sale por su fila, nunca por el reintento** (`SendTicketReply`, revisión del bloque C y
  revisión final). Una `Pending` de más de 60 s se retoma con el canal, la entrega, la conexión, el `target_wa_id`, el adjunto y el texto
  guardados, sin mirar `If-Match`. La plantilla al celular retomada no vuelve a revisar el celular de la foto, la conexión ni la
  aprobación de la plantilla: Mensajería, idempotente por `clientId`, devuelve primero lo que ya salió, y esas revisiones la marcarían
  `Failed` aunque el mensaje sí haya salido. Si el canal la rechaza o ya no tiene ruta, queda `Failed` con el motivo y nunca se borra
  (sólo una respuesta nueva rechazada antes de enviar se borra). El correo no tiene `clientId` del otro lado: retomar una `Pending` de
  correo lo manda otra vez, y eso está aceptado (README).
- **El adjunto de una respuesta de Soporte se guarda en `ticket_replies` (`upload_id`, `file_name`) y viaja hasta
  `ISupportReplyOutbound`**, que arma la clave del staging con la membresía de quien responde: la sesión de subida tiene que abrirla el
  mismo asesor, para esa conversación. Un retomado manda el adjunto guardado, nunca el del reintento. Las reglas que Soporte copia de
  Mensajería (nombre 256, pie 1024, permiso de la sesión) viven en `SupportReplyAttachmentRules` y las amarra `SupportReplyParityTests`.
- **El feed del seguimiento mira sólo las últimas `Support:Workers:FeedLookbackHours` (24) por `created_at`** (`SupportThreadFeed`,
  revisión final). Sin el límite recorría cada conversación seguida desde `follow_since` en cada tick, para siempre. Un worker caído más
  que el lookback deja sin vincular lo que llegó mientras tanto: no lo «arregles» quitando el límite, sube la configuración.
- **`CustomerDeletionReferences` desvincula la conversación de los tickets que la usaban** (plan E18): `conversation_id` en `NULL`,
  seguimiento apagado, `ticket_messages` y `unclassified_messages` de esa conversación borrados y `ticket_replies.message_id` en `NULL`,
  subiendo `version`. El ticket sobrevive (lo bloquea su propio cliente, no la conversación). Una tabla nueva de Soporte con un id de
  conversación o de mensaje entra ahí y en `CustomerDeletionAcrossModulesTests`.
- **Las pruebas de integración corren con `SupportThreadFollowWorker` prendido salvo que lo saquen** (`Support:Workers:Enabled`,
  verdadero si falta). Cada 5 s lee toda la base sin tenant: una prueba que siembra entrantes y espera verlos sin clasificar o sin
  vincular pierde la carrera contra el worker. `SupportApiHarness.OpenOwnHostAsync` lo apaga y además lo saca del contenedor por
  `ImplementationType` (si la bandera dejara de leerse, la prueba sigue sin worker); `SupportHostWithoutWorkers` hace lo mismo por clase.
- **El cuerpo de la plantilla `SupportReply` no termina ni empieza con una variable** (Meta rechaza esos cuerpos). El texto base cierra
  con «Si tienes dudas, responde este mensaje.» y `TheBaseBodyNeitherStartsNorEndsWithAVariable` lo amarra. Un texto propio de un tenant
  (cuerpo de `POST …/template-management/support-reply`) tiene la misma restricción en Meta; la aprobación es por conexión.
- **Un código de falla nuevo de Soporte va a `SupportChannelReasons` y, si sale de Mensajería, también a su copia en
  `SupportReplyOutbound`; `SupportFailureCodeParityTests` los amarra. El frontend traduce por esta lista** (Tarea 8a, owner 2026-11-15).
  Es el único vocabulario del `failureCode` de una respuesta, del `ConsultAccess.FailureCode`, del `reason` de `reply-options` y del
  `cannotCreateReason` de la pestaña de Soporte de una conversación (`no_permission`, `messaging_disabled`): un
  literal suelto en `Modules.Support.*` es un código que la pantalla no conoce.
