---
paths:
  - "src/Modules/Support/**"
  - "tests/Modules/Support/**"
  - "src/Bootstrapper/**/Support*.cs"
  - "src/Bootstrapper/Realtime/**"
---

# Soporte: hub en tiempo real, notas, respuesta formal y encuestas

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **El hub global `/api/v1/hubs/app` vive en Bootstrapper y no es de ningún módulo** (`src/Bootstrapper/Realtime/`, spec 2026-11-15).
  Un módulo se suma registrando su `IRealtimeTopicAuthorizer` (contrato en `BuildingBlocks.Application.Realtime`) para su prefijo de
  tópico, como `SupportRealtimeAuthorizer`; el hub no conoce a nadie. Mensajería sigue en su `MessagingHub`. El tópico de un ticket se
  acepta sólo en la forma exacta que escribe `SupportRealtimeTopics.Ticket` (Guid `D` en minúsculas): otra forma del mismo id sería otro
  grupo y otra sala de presencia.
- **El aviso de «ticket sin asignar» mide tiempo continuo, en horas hábiles del tenant, y no toca el ticket** (spec 2026-11-22 §7, D-14 a D-16).
  `SupportTicket.UnassignedSince` lo mantiene el dominio: se pone al **entrar** en «sin responsable y en `New`/`Open`/`WaitingInternal`» (alta sin
  responsable, `Assign(null)`, un `ChangeStatus` hacia un estado que cuenta, incluido el que hace `ReopenIfWaiting`) y se borra al asignarse o al salir;
  entre dos estados que cuentan no cambia. Por eso un ticket que esperó días al cliente y vuelve a `Open` empieza de cero y no avisa en el acto. Un
  `UPDATE` o un alta por SQL crudo que no pase por el dominio deja `unassigned_since` en null: el worker (`SupportUnassignedAlertWorker`) lo sana con
  un `UPDATE` que no toca `version`. El reloj arranca en `max(unassigned_since, unassigned_alert_enabled_at)`, así que prender el aviso no dispara
  por tickets viejos; las horas las cuenta `IBusinessHoursProvider` (contrato de BuildingBlocks que implementa Mensajería, no un puerto `ISupport*`:
  desvío deliberado, D-14). El reclamo es `INSERT … ON CONFLICT DO NOTHING` sobre `support.unassigned_alerts (ticket_id, unassigned_since)` en la misma
  transacción que el evento: un aviso por ticket y por tramo, y una fila con `recipient_count = 0` es «no había a quién avisar» y no se reintenta.
  Un destinatario que ya no gestiona tickets se salta al avisar; el `GET` de los ajustes lo dice (`canReceive`).
- **Los avisos de Soporte por el hub se llaman a mano, después del commit** (plan N13): cada handler de escritura y
  `SupportThreadFollower` llaman `ISupportRealtime.TicketChangedAsync` con `CancellationToken.None`, no el unit of work, porque varias
  escrituras confirman con `ISupportTransaction`. Una escritura nueva que no lo llame deja la pantalla sin avisar, sin error. La fusión y
  el borrado de clientes (SQL crudo en Bootstrapper) **no** avisan, a propósito (owner O5).
- **La presencia «está respondiendo» es por conexión, no por miembro** (`{memberId}:{connectionId}`): cerrar una pestaña no borra la
  otra. Una entrada que nadie renueva (pod caído) la quita `RealtimePresenceSweeper` a los ≤ 50 s. `presenceChanged` lleva el estado
  completo, por eso `RealtimeEventsListener` entrega los avisos en orden con un solo consumidor: dos en desorden dejan un fantasma.
- **Una nota interna no pasa por el agregado del ticket** (spec 2026-11-15 §5.4): no sube `version`, no escribe `ticket_events` y nada
  de ella sale por `public/support`. Sus tablas son insert-only. Su `id` es el `clientId` de la pantalla (`PK_ticket_notes`).
- **Un archivo de nota ya está reclamado**: `ticket_note_files` entra en `SupportAttachmentReferenceProbe` (o el barrido de staging lo
  borra). Su dueño en Storage es `SupportNoteUploads.OwnerKey(ticket, miembro)`, determinista: cambiar la derivación deja huérfanas
  las subidas abiertas que todavía no están en una nota. **La clave es un UUID v8** y la del formulario público
  (`SupportPublicUploads.NewKey()`) un v4: `SupportAttachmentStorage.CountPendingAsync` cuenta sólo
  `(get_byte(uuid_send(owner_id), 6) >> 4) = 4`, así que ninguna subida de nota, reclamada o abandonada, cuenta en el tope
  `attachments_busy` del formulario (revisión del Bloque B). No uses `uuid_extract_version`: exige Postgres 17+ y la versión de
  producción no está probada en el repo. Completar o radicar con un `uploadKey` que no es v4 es `support.public.attachment_invalid`
  (`SupportPublicUploads.IsKey`). Si la clave pierde el v8, un miembro que abre subidas sin adjuntarlas deja al formulario sin adjuntos.
- **La respuesta formal se genera una sola vez** (fase 4b, H7). El PDF se guarda antes de la fila `Pending` y una respuesta retomada
  manda el **guardado** (`ticket_reply_documents` y su `DocumentClientId`), nunca vuelve a llamar al generador. `ticket_reply_documents`
  es prueba legal: sólo se inserta (su único borrado es la cascada de una respuesta que nunca salió) y el `Down` de
  `AddSupportReplyDocuments` no se corre. Nada bajo `support/` en R2 lleva regla de ciclo de vida. Un error después del commit no borra
  el PDF: `SendTicketReply` relee la fila por `clientId` antes de descartarlo.
- **`Support:Pdf:ApiKey` es una clave propia de `qcode-pdf`, nunca `Quotations:Pdf:ApiKey`**, y es obligatoria en `Production` (sin
  ella el pod no arranca). `qcode-pdf` identifica al cliente por el hash de la clave.
- **Firma la respuesta formal el responsable del ticket, no quien envía** (owner 2026-11-16, Q-7). Sin responsable, `422
support.reply.formal_signer_required`. `ticket_reply_documents.signer_member_id` está en `SupportUserReferenceProbe`: un usuario que
  firmó una respuesta formal no se borra.
- **Toda transición a Resuelto pide la encuesta en el dominio** (`SupportTicket.ChangeStatus`, H15): un camino nuevo a Resuelto la
  recibe gratis; uno que se salte `ChangeStatus` (SQL crudo) deja esa resolución sin encuesta. Un cambio de la encuesta **nunca sube la
  versión del ticket** (un 412 al asesor que lo tiene abierto), pero sí avisa por el hub.
- **El reclamo de una encuesta cubre un envío, no el lote** (revisión del bloque C). `SupportSurveySender.ProcessAsync` renueva
  `locked_until` justo antes de mandar y sólo si sigue siendo el valor que puso su lote (`RenewClaimAsync`, comparado a microsegundos);
  si otro worker la volvió a reclamar, la deja. Una excepción inesperada pasa la encuesta a su reintento (`send_failed`, 1/5/15 minutos y
  `Failed`); sin eso volvería a lanzar cada vez que vence el reclamo. No subas el lote (`SurveyBatchSize`, 5) para «ir más rápido».
- **No hay encuesta por Instagram** (owner 2026-11-16, Q-2): un ticket de Instagram recibe el enlace por correo si tiene correo, si no
  sólo la página de consulta. Un camino nuevo que mande la encuesta por el hilo de Instagram contradice la decisión.
- **Los botones de la encuesta son fijos (`Buena`, `Regular`, `Mala`) y viven en dos copias** (`SupportSurveyRatings` y
  `SupportSurveyTemplateDefinition.ButtonLabels`), amarradas por `SupportSurveyParityTests`. Un toque cuenta sólo para la encuesta que
  cita (`reply_to_message_id` = su `message_id`) y sólo mientras el ticket sigue en esa resolución.
- **El mensaje que pide el comentario sale una sola vez por encuesta** (owner 2026-11-16, Q-11), con un `ClientId` fijo
  (`SupportSurveyPrompt.ClientIdFor`) por el envío de respuestas de Soporte: no es una respuesta del asesor, no toma ni reabre la
  conversación, y un tick repetido recibe de Mensajería el mismo mensaje. Sin reintento ni recordatorio: un fallo queda en
  `prompt_failure_code`. No lo «arregles» reintentando.
- **El token del enlace de calificación viaja en el cuerpo, nunca en una ruta**, y sólo se guarda su SHA-256 (`UX_ticket_surveys_token`).
  Lo amarra `TheSurveyTokenNeverReachesLogsAuditOrEvents` (host en `Trace`).
- **El cierre automático cuenta sólo las respuestas del cliente cuyo vínculo es `Linked` o `Pinned` ahora** (owner 2026-11-16, Q-4).
  Soltar una devuelve la fecha; el toque y el comentario de **cualquier** encuesta del tenant nunca cuentan, no sólo los de la del ticket:
  el seguimiento del hilo vincula cada entrante a todos los tickets abiertos de la conversación. `clock.autoCloseOn` se recalcula en cada lectura y
  el worker (`Noisy`) relee el ticket y cierra con su versión: la réplica que pierde salta el ticket.
