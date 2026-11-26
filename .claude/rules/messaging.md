---
paths:
  - "src/Modules/Messaging/**"
  - "tests/Modules/Messaging/**"
  - "src/Bootstrapper/Messaging/**"
  - "src/Bootstrapper/**/Messaging*.cs"
---

# Mensajería (WhatsApp, Instagram, bandeja, campañas, plantillas)

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **Una plantilla de WhatsApp rechazada con "An error occurred while sending the template for
  approval" no la rechazó Meta:** es Zenvia que no logró enviarla. El contenido, el tono y la
  categoría nunca se revisaron, así que cambiarlos no sirve — mira el `name` (sólo minúsculas,
  dígitos y guiones bajos), el `examples` (sin `$`, `#`, `%`) y el `Content-Type` del PDF de
  ejemplo. Costó cuatro intentos el 2026-09-04. El procedimiento entero está en
  [README § Plantilla de WhatsApp](../../README.md#plantilla-de-whatsapp-zenvia).
- **El texto de la plantilla no se edita: se crea otra.** Meta no permite modificar una plantilla
  aprobada, así que cambiar una coma implica plantilla nueva, aprobación nueva y editar el
  `templateId` de la conexión de Zenvia de cada tenant (no hay cuenta global desde el plan
  2026-10-21). Si además cambian las variables, se toca `WhatsAppQuotationMessage` y
  `ZenviaWhatsAppSender` — y el `templateId` de las conexiones se actualiza en **el mismo
  despliegue** que ese código: una plantilla cuyas variables no coinciden con las que el backend
  manda rompe el envío en producción sin error de compilación.
- **La cotización por Meta no usa `OutboundGuards` ni `ConversationOpener.OpenAsync`** (spec 2026-10-21). `QuotationTemplateOutbound`
  busca la conversación por teléfono (`OpenByPhoneAsync`) y no por `customer_id`, para no caer en el hilo de otro número (D16b);
  pasa `seen: 0` para no tocar los no leídos (D16a); y el PDF va por `id` de media, no por URL. Un cambio en el reclamo o la
  liquidación (`IOutboundMessages`, `OutboundSettlement`) se prueba también con `QuotationTemplateOutboundTests`.
- **`Conversations` como módulo no existe; la bandeja de WhatsApp es `Messaging`** (spec 2026-10-09).
  Los veinticinco módulos construidos son ApiAccess, Audit, Authorization, Buyers, Catalog, Companies, Customers, Geography,
  Identity, Integrations, Loans, Messaging, Notifications, Pipeline, Pos, Purchases, Quotations, Reporting, Shopify, Storage, Store, Suppliers, Support, Tenancy y Wms — cada
  uno con su `<Modulo>LayerTests.cs` en `tests/ArchitectureTests/` (`ApiAccessLayerTests` el de ApiAccess). `Platform` también existe, pero sin
  `PlatformLayerTests` (H26). Messaging no referencia
  Integrations, Customers ni Storage: lo que necesita entra por `IMessagingConnectionDirectory`,
  `IMessagingCustomerDirectory` e `IMessagingMediaStore`, con adaptadores en Bootstrapper.
- **La clave de una conversación es el BSUID, no el teléfono** (spec 2026-10-10). `wa_id` puede venir
  `null` y dos conversaciones con BSUID pueden compartir número (reciclado). Un cuerpo de webhook de prueba
  sin `from_user_id` se descarta en silencio —la entrega queda procesada y no aparece nada—: es el síntoma,
  no un bug de la ingesta.
- **El historial de coexistencia no pasa por `IngestOnceAsync`; `history_managed` decide si la regla de abiertas corre**
  (spec 2026-10-29). La apagan: el entrante en vivo (`InboundIngestion`), `Resolve` y `Reopen`, la respuesta desde QEP
  (`OutboundMessages`), toda asignación (`Conversation.Assign` —tomar, transferir—, el traspaso del bot y
  `TryAutoAssignAsync`), iniciar el asistente desde el hilo (`StartAutomationByMember`), el eco (`CoexistenceIngestion`, R11) y marcar leída o no leída (`MarkReadAsync`, `MarkUnreadAsync`,
  `MarkUnreadFromAsync`). La campaña no. Una acción nueva del asesor sobre una conversación tiene que decidir qué hace con la
  bandera: si no la apaga, la siguiente parte del historial le pisa el estado o los no leídos sin error (precedente: marcar
  leída faltaba y el contador volvía). Y el resumen de la importación sólo escribe si la parte cambia algo: subir `version`
  sin cambio le da un 412 al asesor.
- **Un mensaje del historial no trae `to`: la dirección sale de comparar `from` con el id del hilo** (producción, 2026-10-30).
  Los ecos sí traen `to`, y un fixture de historial que también lo llevaba pasó en verde y escondió el bug: 795 mensajes del
  negocio entraron como del contacto. Un fixture de Meta se copia de la forma real (`messaging.webhook_deliveries`), no de otro
  campo del webhook.
- **Un eco (`smb_message_echoes`) a un número desconocido crea la conversación `Resolved`, no `Open`** (P-5, owner
  2026-10-30). Abierta y sin asignar caería en la bandeja y en el triage del embudo (`TriageRules.IsPending` sólo mira
  abierta + cliente) sin que haya nada pendiente. No lleva `history_managed`, así que la regla de abiertas no la abre; la
  reabre el entrante en vivo con su `Reopened`. No la «arregles» a `Open` para que se vea el eco: el hilo está en
  «Resueltas». Lleva `started_by_echo`, que saca su creación de las sesiones del reporte (`MessagingReportSource`): sin eso, el
  eco más el `Reopened` contarían dos sesiones por una conversación. No lo cambies por «tiene entrante»: una conversación que
  abre un asesor con plantilla no tiene entrante y sí cuenta desde su creación (S3).
- **Lo importado se marca con `imported` (conversación y mensaje), y esa marca no se apaga nunca** (owner 2026-10-30). No es
  `history_managed`, que se apaga con cualquier acción del asesor: el reporte no puede depender de una bandera que un «marcar
  leída» borra. `MessagingReportSource` la lee por nombre en SQL crudo: una conversación importada no arranca sesión al crearse
  (la arranca su primer entrante en vivo, salvo que ya la haya arrancado un `Reopened`), y un mensaje importado nunca es entrante
  de arranque ni respuesta, porque su `created_at` es la hora de la importación y una parte tardía cae _después_ del entrante en
  vivo. Para «primer entrante» sí cuenta, con `occurred_at`. Un camino nuevo que escriba historial en `messaging.messages` tiene
  que poner `imported = true`, o infla el reporte del día en que llega. Lo amarra `CoexistenceReportTests`. El `fromHistory` del
  resumen sale de `imported` y `last_inbound_at`, no de `history_managed`: tomarla, transferirla o marcarla leída apaga esa bandera
  sin abrir la ventana, y el aviso del compositor tiene que seguir mientras responder exija plantilla.
- **El webhook de Meta (`/api/webhooks/whatsapp`) no se prueba con el stub de auth.** En modo stub el
  middleware de CSRF ni se registra, así que la exención por ruta sólo se ve con
  `Authentication__UseDevelopmentStub=false`. Y su limitador `webhook` no es el `Public`: es concurrencia
  global con cola, porque un 429 hace que Meta reintente hasta 7 días. El resto está en
  [README § Mensajería](../../README.md#mensajería-whatsapp-cloud).
- **Todo camino que inserte o reclame destinatarios de campaña respeta el candado de `MarkFrozenAsync`** (spec 2026-10-24 §4.4). El
  desduplicado `duplicate_contact` corre una sola vez, al terminar de congelar la audiencia, con la campaña `FOR UPDATE`;
  `AddRecipientsAsync` inserta con `FOR SHARE` y sólo si `audience_frozen_at IS NULL`, y el worker no reclama mientras sea `NULL`. Un
  camino nuevo que escriba en `campaign_recipients` fuera de ahí deja un `Pending` que nadie desduplica y manda dos plantillas al
  mismo número; y un destinatario sembrado a mano en una prueba nunca sale si su campaña no tiene `audience_frozen_at`.
- **Un fixture de `message_templates` sin `id` deja el catálogo vacío** (spec 2026-10-14). El catálogo de envío lee
  del espejo `messaging.message_templates`, que indexa por `meta_template_id`, y el refresco salta los elementos sin
  `id`: el síntoma es `messaging.template.not_available` en una prueba que antes pasaba, no un error del espejo. Y el
  webhook `message_template_status_update` hay que suscribirlo en la app de Meta: sin él los estados sólo cambian
  con el refresco.
- **Instagram es un canal de la misma bandeja de Messaging, no un módulo** (spec 2026-10-14). La conversación lleva
  `channel` (`WhatsApp` | `Instagram`) y en Instagram `user_id` es el IGSID numérico y `wa_id` es siempre `null`: todo
  código nuevo que empareje por teléfono tiene que tratar ese `null`. Las plantillas, las campañas y `POST /conversations`
  responden `messaging.channel_unsupported`; el catálogo de plantillas de una conexión de Instagram devuelve `[]`, no 422.
- **El webhook de Instagram (`/api/webhooks/instagram`) se firma con `Meta:Instagram:AppSecret`, no con `Meta:App:AppSecret`**
  (V-1): son dos secretos distintos aunque vivan en la misma app de Meta. El verificador acepta cualquiera de los dos para
  Instagram, así que una firma inválida en producción casi siempre es que falta `META_INSTAGRAM_APP_SECRET` en el pipeline.
  El `timestamp` de Instagram viene en **milisegundos**; el de WhatsApp, en segundos. Y los echo (lo que el negocio responde
  desde la app) llegan dentro de `messages` con `is_echo`, no en un campo aparte: la ingesta los deduplica por `mid` con lo
  que QEP ya mandó.
- **Un adjunto de Instagram no tiene `meta_media_id`: tiene `message_media.source_url`**, una URL pública con vencimiento
  que el worker de medios baja sin token. `MediaTransfer` decide el camino por `SourceUrl`; `InsertMediaAsync` acepta
  `meta_media_id = ''` si hay URL. Hacia afuera no hay subida a Meta: al adjunto saliente se le da una URL prefirmada de R2
  de 10 minutos (`IMessagingMediaStore.CreateDownloadUrlAsync`), y la nota de voz se transcodifica a WAV con Concentus
  (`ConcentusOpusTranscoder`) porque Instagram no acepta Opus. V-4 y V-8 (que Meta descargue la URL firmada y acepte el
  WAV) siguen **sin verificar contra Meta real**: el handoff dice qué mirar.
- **EF ordena los `UPDATE` de un `SaveChanges` por clave primaria, no por el orden en que tocaste las entidades.**
  Con un índice único parcial que admite una sola fila (`IX_business_hours_tenant_default`, `WHERE is_default`), «quitar
  el predeterminado al anterior y ponérselo al nuevo» en un solo `SaveChanges` falla con 409 cada vez que el nuevo tiene
  un id **menor** (GUID v7 más antiguo): su `UPDATE … true` corre antes que el `… false` del anterior. La prueba que sólo
  mueve el predeterminado del más viejo al más nuevo pasa por casualidad. La solución es dos `SaveChanges` en una
  transacción explícita con el orden fijo (`BusinessHoursRepository.SwapDefaultAsync`, 2026-10-21), como
  `TryAutoAssignAsync`; y cualquier `SaveChanges` fuera de `MessagingUnitOfWork` pasa por `MessagingSaveChanges.SaveAsync`,
  que es donde viven las traducciones por nombre de índice —un `SaveChanges` directo del `DbContext` responde 500 donde
  tocaba 409 o 412. Un intercambio parecido en otro módulo (un «único activo» por tenant) tiene el mismo problema.
- **Dos eventos del mismo cambio en el hilo necesitan `occurred_at` distinto.** `MessageQueries` ordena por
  `occurred_at, id` y los GUID v7 **no** están ordenados dentro del mismo milisegundo, así que «pausó el asistente» salía
  antes que «tomó la conversación». `ConversationAssignmentSteps.CommitAsync` y `TryAutoAssignAsync` escriben el evento
  n-ésimo de un cambio en `now + n µs`; una prueba que compare los tipos sin orden esconde la regresión.
- **Los `reason` de `qep_webhook_discarded_total` son contrato con `migracion-k8s`** (spec 2026-11-16). `QepWebhookDiscarding`
  los lee por nombre (`unknown_route`, `without_user_id`): renombrar uno en `WebhookDiscardReasons` compila, pasa las pruebas
  de Messaging y deja la alerta muda. Un descarte nuevo de Meta (un `Log…Discarded` o `Log…Ignored` más en
  `WebhookDeliveryProcessor`) llama `MessagingMetrics.RecordDiscarded` al lado del log, suma su motivo en
  `WebhookDiscardReasons` y en `WebhookDiscardReasonsTests`, y decide si entra en la alerta. Si no suma, el descarte vuelve a
  ser silencioso. Shopify todavía no cuenta (sin clientes conectados); los puntos están en el spec.
- **`Messaging:Automation:Enqueue` está prendido (plan 2) y es lo único que hace que el bot conteste.** Sin el flag el webhook no
  encola y el bot queda mudo sin error. La cola se purga (filas procesadas a los 7 días, `Automations:Workers:InboxRetentionDays`) y
  antes de desplegar la migración `AddAutomationEngine` en un ambiente donde alguien puso a mano `automation_status` ≠ 'None', la
  migración normaliza esas filas a 'None'.
- **El motor sólo responde una conversación `Open`, sin asesor y con el asistente `Active`; `IBotOutbound` lo vuelve a verificar al
  enviar** (`RunProcessor.DecideAsync`): entre el claim del inbox y el envío un asesor puede haber tomado la conversación.
- **Una fila de `automation_inbox` tiene tipo** (spec 2026-11-03). `ReleaseRun` es la señal de que una persona resolvió, tomó, transfirió o
  devolvió la conversación: `RunProcessor` sólo cierra la corrida viva (`Interrupted`, `human_took_over`) si arrancó antes de la fila y
  `HumanTookOver` sigue siendo verdadero, y nunca arranca ni avanza nada. Dentro de `Engine`, una fila sin mensaje sigue siendo un arranque
  manual: todo lector nuevo usa `LEFT JOIN` y filtra por `kind`. Una acción humana nueva sobre una conversación con asistente decide si
  encola la señal, en el handler y no en el agregado (el bot también resuelve). Una fila del inbox puede existir sin mensaje —un arranque
  manual (spec 2026-10-31) o una señal—, y por eso `run_units.inbound_message_id` y `conversation_runs.last_inbound_message_id` pueden
  apuntar a una fila y no a un mensaje; el camino manual del motor es `ResumeAsync`, no el de un entrante. Un `INNER JOIN` del inbox a
  `messages` pierde esas filas en silencio: el arranque se encola, se marca procesado y el bot no contesta, sin error. La regla del
  `LEFT JOIN` cubre también a quien lea esas dos columnas: tiene que tratar el mensaje `null`.
- **`Conversation.ContinueAutomation` no escribe `AutomationResumedAt`, y esa ausencia es todo el mecanismo** (spec 2026-11-01). Sellarlo
  hace que `BotAttendance.HumanTookOver` dé verdadero, la corrida termine `Interrupted` y el flujo reinicie: justo lo que `automation/continue`
  evita. No hay columna ni bandera de «continuada». Por la misma razón `AutomationPauseReason = Manual` **no** prueba que ningún asesor
  haya hablado: tomar, transferir o responder sobre una pausa no cambia el motivo, y después de liberar queda igual. Se mide con los
  eventos del hilo desde `AutomationPausedAt` (`WasTakenSinceAsync`); una regla nueva sobre «sin asesor» tiene que hacer lo mismo. Y la
  decisión está en un solo lugar para el resumen y el POST (`ManualRunStarter.CanContinueAsync`): si el modal y el botón discrepan,
  promete lo que no hace.
- **Un entrante que llega fuera de orden no se encola para el bot** (`InboundIngestion.ShouldEnqueue`, 2026-10-23). La cola
  ordena por llegada y Meta reintenta una entrega fallida hasta horas después: el 2026-10-22, con QEP sin recibir unos diez
  minutos, un «hola» de las 21:42 llegó a las 22:01 detrás de uno de las 21:56 que el bot ya había contestado, y el bot mandó
  el mismo mensaje otra vez sin que el cliente escribiera. Por eso se compara su `occurred_at` contra el `last_inbound_at`
  **de antes** (el CTE `old`; el `SET` ya lo pisó con `GREATEST`). No lo muevas al motor: ahí sólo cubriría uno de los tres
  caminos. Una prueba que postee webhooks con timestamps decrecientes ya no ejerce el bot desde el segundo.
- **El toque de un botón de la encuesta de Soporte no se encola para el bot, y el comentario que sigue sí** (owner, 2026-11-16).
  `InboundIngestion.IsSupportSurveyTapAsync` corre después de `ShouldEnqueue` (cuya regla de orden no cambia): un `Interactive` que cita
  un saliente cuyo `details.template` (nombre e idioma) casa con la fila `SupportSurvey` de `message_templates` de la misma conexión se
  ingresa y se cita como siempre, pero no entra a `automation_inbox`. El mensaje no guarda el tipo de plantilla: si cambias la forma del
  `details` de un envío de plantilla (`TemplateDetailsJson`), el filtro deja de casar sin error y el bot vuelve a contestar los toques. El
  comentario libre no se filtra, a propósito. Lo amarra `AutomationInboxTests.ATapOnTheSurveyTemplateIsNotEnqueuedButATapOnAnotherTemplateIs`.
- **Una plantilla del sistema se envía desde su fila, no desde una definición** (spec 2026-10-31). `QuotationTemplateDefinition.BaseBody` y
  `BillingTemplateDefinition.BaseBody` son sólo el texto inicial: cada tenant lo cambia. Un dato nuevo disponible para el texto entra en
  `SystemTemplateVariables` y en los valores de `QuotationTemplateOutbound` o `ServiceContractsMetaWhatsAppSender`, con su etiqueta; si falta en
  el segundo, `SystemTemplateSend.Build` devuelve `null` y el envío falla con `message_rejected`. `POSITIONAL` sólo existe en filas viejas, y
  `LegacyPositionalKeys` da su orden: no lo cambies. Nombrada o no se decide por las claves, no por la columna, porque el refresco guarda
  `POSITIONAL` si Meta no manda el campo. Las claves son en español (`numero_cotizacion`, `valor_total`, `vigencia`…). Las cuatro primeras
  variables son las fijas de `FIXED_VARIABLE_NAMES` del frontend: si esa lista crece, `SystemTemplateVariables` también, y hay que darle valor
  en `TemplateSenderValues`; llevan guion bajo, por eso `FlowValidator.CheckName` las exime de la regla de nombres propios. `fullname`,
  `order_number`, `total` y `valid_until` salieron de la lista pero las cotizaciones aprobadas los llevan: `LegacyKeys` les da su valor en
  `SystemTemplateSend.Build` y `IsAdoptable` los acepta; no los limpies.
- **El override del webhook vive en la WABA, no en la configuración** (addendum 2026-10-30 del spec 2026-10-29). Quitar
  `Meta:App:WebhookOverrideCallbackUrl` no le dice nada a Meta: la WABA sigue mandando a esa URL hasta que alguien repita
  `POST /{wabaId}/subscribed_apps` sin cuerpo. Y al revés: **cualquier** suscripción sin cuerpo lo borra, según la documentación
  de Meta, también un signup de QEP con la clave vacía o una re-suscripción desde lulo chat, y el síntoma es que los webhooks
  vuelven a la URL de la app sin ningún error. Lo único que dice el estado real es `GET /{wabaId}/subscribed_apps`; el procedimiento
  está en [README § Historial de coexistencia](../../README.md#historial-de-coexistencia).
- **Un camino nuevo que lea autores de `messaging.messages` tiene que tratar `sent_by_kind = 4`** (clave de API; el 3 es el agente
  de IA, en su propia entrada más abajo). Ese autor no tiene `sent_by_member_id`: lleva `sent_by_api_key_id` y `sent_by_api_key_name`, así que un join al
  miembro lo pierde en silencio. Y uno que muestre «primera respuesta» debe excluirlo: un envío automático de un sistema externo no
  es una respuesta de una persona.
- **El indicador de escritura de WhatsApp marca el mensaje como leído para el contacto** (decisión del owner, 2026-10-31). Cloud API lo
  manda en la misma llamada que el leído (`status: "read"` + `typing_indicator`), así que el contacto ve el chulo azul aunque en QEP la
  conversación siga sin leer. No es un bug: `POST …/typing` no toca `unread_count`, y no hay que «arreglar» la diferencia. Instagram no
  tiene ese efecto: `typing_on` va solo.
- **El recordatorio de cita de la Agenda es una plantilla del sistema más (`AppointmentReminder`, `qep_agenda_recordatorio`, plan 2026-11-21 Tarea 12)**, y su
  envío tiene tres reglas que no se ven en la plantilla. (1) Sale por `AppointmentReminderOutbound` (el mismo `PlainTemplateOutbound` del cobro y el aviso
  de Mayorista), con cliente: abre la conversación por teléfono y la cuelga del cliente de la cita; un cliente borrado devuelve
  `messaging.customer.not_found`, no una excepción. (2) La fila de `messages` exige un autor y la elige la Agenda, no Mensajería: el miembro del recurso
  y, sin él o inactivo, quien tocó la cita por última vez; sin ninguno activo la cita se omite con `no_sender_member`. (3) El `clientId` es un `Guid`
  derivado (SHA-256) de la llave `appointment-reminder:{id}:{starts_at UTC ticks}` (`AppointmentReminderRules.KeyOf`, `SchedulingReminderSender.ClientIdOf`): si el
  mensaje salió y marcar la cita falló, el reintento devuelve la misma fila; reprogramar cambia la hora, por tanto la llave, y la cita se avisa de nuevo. Las variables son `nombre_cliente`, `servicio`, `fecha_hora` (zona del tenant, hora en 24 h, `es-CO`), `recurso` y
  `sede`, más las cuatro fijas; `nombre_cliente` repite a la fija `nombre_contacto`, y si se renombra una clave hay que cambiar plantilla, definición y
  adaptador en el mismo despliegue. El consentimiento (PD-7) no se pide: se omite con `consent_revoked` si la última fila de `customers.data_consents`
  del cliente es una revocación (también una `Medium = Other`) y con `marketing_opt_out` si la ficha lo tiene; no hay baja automática por texto libre.
- **El presupuesto de envío de campañas se queda en Postgres** (`messaging.connection_send_budgets`, decisión del owner 2026-11-13).
  D-6 del spec de Valkey no se construyó: no hay adaptador de Valkey para el cubo.
- **Un formato nuevo de `OutboundMediaPolicy` tiene cuatro dueños más: el sniffer, la tabla de Storage del chatbot, la paridad del chatbot y el cliente de Meta** (2026-11-22). `OutboundMediaFormat` +
  `OutboundMediaSniffer.Detect` (la extensión sin firma ni sniffer se rechaza) y, si el formato también lo manda el bot, `FileUploadPolicy.ChatbotMediaTypes`: sin esa fila
  `ChatbotMediaPolicyParityTests` se pone roja. El sticker (`.webp`, kind `Sticker`) es la excepción: Meta lo recibe con `type: sticker`, sin `caption`, y
  `ChatbotMediaPolicy` lo devuelve como `null` porque `ChatbotMediaKinds` no tiene ese tipo. Su tope depende de si está animado (100 KB estático, 500 KB animado), así que la tabla
  trae el mayor y `OutboundMediaPreparer` mide el estático al leer. `IsAudio` y `IsSticker` son los únicos que rechazan `caption`: no los fundas en `!AcceptsCaption`, o un sticker
  devolvería el error «Los audios se envían sin texto».
