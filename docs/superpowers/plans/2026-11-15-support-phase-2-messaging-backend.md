# Soporte (PQRSF) — Fase 2 «Mensajería», backend · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que un asesor abra un ticket desde una conversación de Mensajería, que el ticket vincule solo los entrantes del hilo (con fijar, soltar, «sin clasificar», «De ninguno» y «Seguir el hilo»), que el hilo muestre cuándo se abrió y cerró un ticket, y que el asesor responda desde el ticket por el hilo, por la plantilla «Aviso de respuesta» fuera de la ventana de 24 h o por correo.

**Architecture:** Support sigue sin referenciar Messaging, Customers ni Notifications: todo entra por puertos de `Modules.Support.Application` con adaptadores en `src/Bootstrapper/`. Los entrantes no se empujan desde la ingesta de Mensajería: un worker de Support (`SupportThreadFollowWorker`) los lee cada pocos segundos por un puerto cuyo adaptador hace SQL crudo sobre `support.tickets` y `messaging.messages`, y decide vincular, dejar sin clasificar o reabrir. Mensajería gana tres cosas que Support consume por adaptador: dos tipos de evento del hilo (`TicketOpened`, `TicketClosed`), un emisor de respuestas de Soporte (`ISupportReplyOutbound`: texto en el hilo o plantilla) y la plantilla del sistema `SupportReply`, calcada de la de pedido (`028aa490`).

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, FluentValidation, xUnit v3, Testcontainers.PostgreSql.

**Spec:** [`docs/superpowers/specs/2026-11-14-support-tickets-pqrs-design.md`](../specs/2026-11-14-support-tickets-pqrs-design.md), §8.1 a §8.3, §8.5 y §12 fase 2. Plan anterior: [`2026-11-14-support-phase-1-core-backend.md`](2026-11-14-support-phase-1-core-backend.md) («Lo que esta fase deja preparado»). Las pantallas son de `qep-frontend`; la sección «Contrato para las pantallas» dice qué necesitan.

---

## Antes de empezar

- Worktree: `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\support-messaging`, rama `feature/support-messaging`, sale de `develop` en `5904b975` (la fase 1 ya está mergeada). Usa rutas absolutas: un script con `cwd` relativo escribe en el checkout de `develop`.
- Antes de **cada** commit: `git branch --show-current`. Si dice `main` o `develop`, frena.
- Commits en conventional commits, en inglés, `git commit -m "…" -- <rutas>`, **sin `Co-Authored-By` ni ninguna atribución de IA**, aunque el harness la sugiera.
- Comandos en PowerShell, sin `&&`. Si `Api.exe` o `dotnet Api.dll` está corriendo, el build falla por archivo bloqueado: avisa, no lo detengas.
- **No se corre ninguna prueba en esta rama** (owner, 2026-11-15): ni unitarias ni de integración, ni filtradas. El owner las corre cuando termine otra sesión. TDD sigue en el orden: la prueba se escribe primero; la evidencia de RED es la salida de `dotnet build` del proyecto de pruebas fallando por compilación (`CS0246`, `CS1061`…), y la de GREEN es `dotnet build` de la solución sin errores ni avisos. Una prueba que compila desde el principio (comportamiento sobre tipos existentes) se marca en el reporte como «RED sin evidencia: no se corre». Guarda las dos salidas en el reporte del bloque.
- Atajos: `$W` = la ruta del worktree; `$SU` = `$W\tests\Modules\Support\Modules.Support.UnitTests`; `$SI` = `…\Modules.Support.IntegrationTests`; `$MU`/`$MI` = los de `Modules\Messaging`; `$NU` = `…\Modules\Notifications\Modules.Notifications.UnitTests`; `$AT` = `$W\tests\ArchitectureTests\ArchitectureTests`; `$BU` = `$W\tests\Bootstrapper\Bootstrapper.UnitTests`; `$CI` = `…\Modules\Customers\Modules.Customers.IntegrationTests`.
- Migraciones con el factory de diseño, desde `$W`:
  ```powershell
  dotnet ef migrations add AddSupportMessaging --project src/Modules/Support/Modules.Support.Infrastructure --context SupportDbContext -o Persistence/Migrations
  dotnet ef migrations add AddSupportReplyTemplateKind --project src/Modules/Messaging/Modules.Messaging.Infrastructure --context MessagingDbContext -o Persistence/Migrations
  ```
- `docs/estado.md` **no se toca** en esta rama.

## Global Constraints

- Support no referencia `Modules.Messaging.*`, `Modules.Customers.*` ni `Modules.Notifications.*`; `SupportLayerTests` no se afloja. Messaging no referencia Support (`MessagingLayerTests`).
- Ninguna escritura de Support toca tablas de `messaging` por SQL: escribir en Mensajería (mensaje, evento del hilo, conversación nueva) pasa por un puerto de `Modules.Messaging.Application`. Leer sí puede ser SQL crudo en Bootstrapper, con su prueba de esquema.
- La conversación se identifica por su `id` (`messaging.conversations.id`), nunca por teléfono. `wa_id` puede ser `null` y en Instagram lo es siempre.
- Un entrante es `messaging.messages` con `direction = 1` y `NOT imported`. Ecos (`direction = 2`), historial importado y mensajes de otro tenant nunca se vinculan solos.
- Estados del vínculo, por nombre: `Linked` (vinculado solo, plegado), `Pinned` (fijado, en línea), `Released` (soltado: fuera del expediente y nunca se vuelve a vincular solo).
- «Ticket abierto» en esta fase = `status <> 'Closed'`. Un ticket `Closed` deja de seguir el hilo al cerrarse (`follow_thread = false`, `follow_off_at = closed_at`).
- Plantilla: `MessageTemplateKind.SupportReply`, nombre `qep_soporte_respuesta` (versiones `qep_soporte_respuesta_v2`…, prefijo reservado `qep_soporte_respuesta_`), idioma `es`, categoría `UTILITY`, variables fijas más `radicado` y `respuesta`, sin encabezado (el PDF formal es de la fase 4). Texto base, tuteando: `Hola {{nombre_contacto}}, te escribimos sobre tu caso {{radicado}}: {{respuesta}}`.
- El texto de una respuesta: 1 a 4 096 caracteres recortado, sin caracteres de control salvo `\n`, `\r` y `\t` (la regla de `SendMessageValidator`). Por plantilla, además, las reglas de `TemplateParameterRules` (1 024, sin saltos de línea ni más de cuatro espacios seguidos): si no las cumple, `422 support.reply.template_text_invalid`, antes de tocar nada.
- Aislamiento: rutas bajo `/api/v1/tenants/{tenantId:guid}/support`, política más revalidación en el handler (`SupportAuthorization`), 403 nunca 404 por tenant ajeno. Un ticket o una conversación que no es del tenant de la ruta es 404 (pre-flight R12 de la fase 1).
- Toda acción que cambie el ticket exige `If-Match` con su `version` (428 sin él, 412 vieja). El worker nunca exige versión y **sólo sube la versión cuando cambia el estado o escribe un evento**: un vínculo `Linked` no sube la versión, para no darle un 412 al asesor por cada mensaje del cliente.
- Mensajes legibles en español tuteando; códigos, rutas, enums y permisos en inglés.

## Decisiones del plan

Mías (planner) sobre lo que el spec deja abierto o no cubre. El owner las puede revertir.

| # | Decisión | Por qué |
| --- | --- | --- |
| E1 | **Alcance de la respuesta** [owner, 2026-11-15]: hilo (WhatsApp e Instagram), plantilla «Aviso de respuesta» fuera de la ventana y correo. La plantilla «Apertura de Soporte» (radicado, PIN y enlace de consulta) pasa a la fase 3; la encuesta, a la 4. Por eso un ticket creado desde una conversación **no** manda todavía el mensaje con la información del ticket (§9.4). | El PIN y la página de consulta nacen en la fase 3. |
| E2 | Los entrantes llegan por **sondeo**, no por evento: `SupportThreadFollowWorker` corre cada `Support:Workers:FollowIntervalSeconds` (5 por defecto) y lee por `ISupportThreadFeed`. | La ingesta de Mensajería son tres sentencias en una transacción con su propio contrato (`InboundIngestion`); un evento de outbox por cada entrante de todos los tenants llenaría `platform.outbox_messages` aunque el tenant no use Soporte. El costo es una latencia de segundos. |
| E3 | El worker es idempotente por índices, no por candados: `IX_ticket_messages_ticket_message` único y la PK de `unclassified_messages` (`message_id`). Dos réplicas a la vez: la segunda choca, se traduce por nombre de índice y se descarta. El reabrir por un entrante usa la versión optimista; si choca, el siguiente tick lo reintenta. | Precedente: el triage de Pipeline (`IX_triage_decisions_conversation_inbound`). |
| E4 | Ventana de seguimiento por ticket: `follow_since` (al crear o al prender de nuevo) y `follow_off_at` (al apagar o al cerrar). Un entrante cuenta para un ticket si `follow_since <= occurred_at` y (`follow_off_at IS NULL` o `occurred_at < follow_off_at`). Lo que llegó con el interruptor apagado no se vincula al prenderlo. | §8.2 sólo fija el apagado; es la lectura que no mete mensajes viejos sin que el asesor lo decida (anti-objetivo «basura que se cuela»). |
| E5 | Candidatos de un entrante: los tickets abiertos («ticket abierto» de Global Constraints) de su conversación cuya ventana lo cubre. Uno: `Linked`. Dos o más: fila en `unclassified_messages`. Cero: nada. Una fila sin clasificar pendiente pasa sola al ticket cuando queda exactamente un candidato (lo revisa el mismo worker). | §8.2. |
| E6 | Un entrante vinculado (solo o al asignarlo desde «sin clasificar») a un ticket en `WaitingOnCustomer` lo pasa a `Open` con `ChangeStatus(Open, actor: null)`. En `New`, `Open`, `WaitingInternal` y `Resolved` **no** cambia el estado. | §8.2 sólo nombra `espera_cliente`. Que un `Resolved` se reabra con la respuesta del cliente es **decisión pendiente P4** del owner (§8.4 la deja a la fase del cierre automático). |
| E7 | Eventos nuevos del ticket: `MessagePinned`, `MessageReleased`, `MessageClassified`, `MessageDismissed`, `FollowThreadChanged`, `ReplySent`, `ConversationLinked`. Un vínculo automático (`Linked`) **no** escribe evento: el registro es la fila del vínculo. | Un evento por mensaje inundaría la traza y subiría la versión en cada entrante. |
| E8 | «De ninguno» marca la fila `Dismissed` y escribe `MessageDismissed` en **cada** ticket candidato abierto. Asignar desde «sin clasificar» la marca `Assigned` y deja el mensaje `Pinned` en el elegido, con `MessageClassified`. | §8.2: «lo fija» y «deja una nota en los tickets abiertos». |
| E9 | Fijar acepta cualquier mensaje de la conversación del ticket (entrante o saliente, también anterior al ticket). Soltar sólo un mensaje que tiene fila en ese ticket; queda `Released`. | El asesor decide qué entra al expediente; el worker nunca revive un `Released`. |
| E10 | Crear desde una conversación: `POST /support/conversations/{conversationId}/tickets`. Exige el módulo `messaging` prendido. `Intake = Conversation`, `ReplyChannel = Thread`, `ManualMedium = null`, sigue el hilo desde ya. Cliente = el de la conversación si `customers` está prendido y el cliente está activo (con `CustomerReferenceLock` compartido); si no, sin cliente. Foto del solicitante: el contacto del cliente (regla F1) y, en WhatsApp con `wa_id`, celular `+{wa_id}` si el solicitante no tiene; sin cliente, `FirstNames = profile_name` o `username`. Escribe `TicketOpened` en el hilo en la misma transacción. | §8.1. La transacción compartida sigue el precedente de `CatalogProductDeletionCleanup` (`SetDbConnection` + `UseTransactionAsync`). |
| E11 | El evento del hilo se escribe dentro de la transacción de Support: el adaptador de Bootstrapper enrola `MessagingDbContext` en la conexión y la transacción de `SupportDbContext` y llama a `IConversationEventAppender` de Mensajería. `TicketOpened` al crear desde la conversación o al vincular una conversación nueva por plantilla; `TicketClosed` al pasar a `Closed`. | §8.2: «la conversación recibe eventos nuevos». En la misma transacción no queda un ticket cerrado sin su evento ni al revés. |
| E12 | Responder no toma la conversación, no cambia su estado ni su asignación y no resta no leídos (`seen: 0`), igual que `BillingTemplateOutbound`: lo autoriza Support con `support.ticket.manage`, no la bandeja. El reclamo y la liquidación sí pasan por `IOutboundMessages`/`OutboundSettlement`, que apagan `history_managed` (gotcha del repo); se amarra con prueba. | Precedente de las plantillas del sistema; la bandeja no le pide al asesor de Soporte tomar la conversación para contestar un caso. |
| E13 | Canal `Thread`: dentro de la ventana, texto (o un adjunto, Tarea 11); fuera, en WhatsApp sale la plantilla `SupportReply` a la misma conversación (por `user_id`/`wa_id`, sin `OpenByPhoneAsync`); en Instagram es `422 support.reply.window_closed`. Contacto bloqueado: `422 support.reply.contact_blocked`. | §8.3 y §8.5. |
| E14 | Canal `WhatsAppTemplate` en un ticket sin conversación: plantilla al celular de la foto (`MobilePhone` sin `+`) por la conexión que elige el asesor (`connectionId` del cuerpo), con `IConversationOpener.OpenByPhoneAsync`. El ticket guarda esa conversación (`ConversationLinked`, `TicketOpened`) y empieza a seguir el hilo desde el envío. Con conversación, se comporta como `Thread`. | §8.3: «crea una al enviar». |
| E15 | Canal `Email`: a `Requester.Email` por `ISupportReplyEmail`, adaptador sobre `IEmailChannel` con la plantilla nueva `SupportReplyEmailTemplate` de Notifications. Asunto `Respuesta a tu caso SOP-000127`. Cualquier excepción del canal es `Failed` con `send_failed` (precedente `ServiceContractsEmailSender`). | §8.3. |
| E16 | Cada respuesta es una fila de `support.ticket_replies` (canal, texto, estado `Pending`/`Sent`/`Failed`, código de falla, `message_id` si salió por Mensajería, autor). Idempotencia por `client_id` (único por ticket): la fila `Pending` se confirma **antes** de llamar afuera y su `id` es el `clientId` que viaja a Mensajería, así un reintento no duplica en Meta. | Mensajería es idempotente por `clientId`; el correo no lo es y se acepta (riesgo anotado en Review Focus). |
| E17 | Una respuesta que Meta o el correo rechazan se guarda `Failed` y el endpoint responde **201** con la fila en `Failed`: el hilo ya muestra el mensaje fallido y la pantalla tiene que mostrar lo mismo. Lo que se rechaza **antes** de enviar (sin ventana, sin plantilla aprobada, sin contacto, texto inválido, conexión caída) es 422 sin fila. `resolve: true` sólo resuelve si salió (`Sent`). | §8.3 «Enviar y marcar Resuelto». |
| E17b | `GET /tickets/{id}/reply-options` le dice a la pantalla qué canal puede usar y qué va a pasar (texto o plantilla), con motivo cuando no puede. | BFF: la pantalla no conoce la ventana de 24 h ni la aprobación de plantillas. |
| E18 | Borrar clientes: si se borra una conversación con tickets (el ticket cambió de cliente después), `CustomerDeletionReferences` pone `conversation_id = NULL`, `follow_thread = false`, `follow_off_at = now` en esos tickets, borra sus `ticket_messages` y `unclassified_messages`, y deja `ticket_replies.message_id = NULL`. La fusión no cambia ids de conversación: no se toca. | El mensaje desaparece con la conversación; el ticket sigue (lo bloquea su propio cliente, D4). |
| E19 | Los adjuntos (Tarea 11) reusan la sesión de subida de Mensajería (`…/messages/media/upload-session`); la respuesta lleva `uploadId`. Un adjunto por respuesta, sólo canal `Thread` dentro de la ventana. Quien no tenga permiso de Mensajería no puede abrir la sesión de subida: la pantalla oculta el adjunto (`ReplyOptions.Thread.CanAttach`). | Evita una segunda ruta de subida a R2. |
| E20 | Ver el contenido de los mensajes vinculados no exige permisos de Mensajería: el texto viaja en el DTO de Soporte. El medio (imagen, PDF) sí se baja por `GET …/messaging/media/{messageId}`, que exige los de Mensajería. | Mostrar el expediente es de Soporte; la descarga de medios ya tiene su control. Reabrir esto es del owner. |

## Review Focus

1. **Un mensaje del cliente que llega fuera de orden** (Meta reintenta horas después) con `occurred_at` anterior a la creación del ticket no debe vincularse, aunque su `created_at` sea posterior. Tarea 8: `AnInboundOlderThanTheTicketIsNotLinked`.
2. **Dos réplicas o dos ticks sobre el mismo entrante** no deben dejar dos vínculos, dos filas sin clasificar ni un 500. Tarea 8: `ProcessingTheSameInboundTwiceLinksItOnce` (dos `ProcessBatchAsync` seguidos y uno concurrente).
3. **Un reintento del mismo `clientId`** después de que Meta aceptó pero antes de guardar no debe mandar dos mensajes al cliente. Tarea 10: `RetryingAReplyWithTheSameClientIdSendsOnce`. El correo no tiene esa garantía: queda anotado en el README.
4. **Responder desde Soporte una conversación con historial de coexistencia** debe apagar `history_managed`, o la siguiente parte del historial pisa el estado. Tarea 5: `ASupportReplyTurnsOffHistoryManaged`.
5. **Un ticket cerrado** no debe seguir capturando mensajes ni contar como candidato (si contara, todo entrante de esa conversación quedaría «sin clasificar» para siempre junto al ticket nuevo). Tarea 8: `AClosedTicketStopsFollowingAndLeavesASingleCandidate`.

---

## Contrato para las pantallas (BFF)

Rutas bajo `/api/v1/tenants/{tenantId:guid}/support`.

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /conversations/{conversationId}/tickets` | `TicketView` | `SupportConversationTicketsDto` |
| `POST /conversations/{conversationId}/tickets` | `Manage` | `201 SupportTicketDetailDto` |
| `GET /conversations/{conversationId}/unclassified` | `TicketView` | `SupportUnclassifiedDto` |
| `POST /conversations/{conversationId}/unclassified/{messageId}/assign` (`{ ticketId }`) | `Manage` | `SupportUnclassifiedDto` |
| `POST /conversations/{conversationId}/unclassified/{messageId}/dismiss` | `Manage` | `SupportUnclassifiedDto` |
| `GET /tickets/{ticketId}/messages?before=&take=` | `TicketView` | `SupportTicketMessagesDto` |
| `POST /tickets/{ticketId}/messages/{messageId}/pin` (`If-Match`) | `Manage` | `SupportTicketDetailDto` |
| `POST /tickets/{ticketId}/messages/{messageId}/release` (`If-Match`) | `Manage` | `SupportTicketDetailDto` |
| `PUT /tickets/{ticketId}/follow-thread` (`{ follow }`, `If-Match`) | `Manage` | `SupportTicketDetailDto` |
| `GET /tickets/{ticketId}/reply-options` | `TicketView` | `SupportReplyOptionsDto` |
| `POST /tickets/{ticketId}/replies` (`If-Match`) | `Manage` | `201 SupportReplyResultDto` |

DTO nuevos (en `SupportDtos.cs`, cada uno con el motivo de pantalla en su comentario):

- `SupportTicketDetailDto` suma `Guid? ConversationId`, `string? ConversationChannel` (`WhatsApp`/`Instagram`), `bool FollowsThread`, `DateTimeOffset? FollowOffAt` (la franja «Este ticket no sigue el hilo (WhatsApp) desde …»), `int UnclassifiedCount` y `IReadOnlyList<SupportReplyDto> Replies`. `SupportTicketEventDto` suma `Guid? MessageId`, `bool? Follow` y `string? ReplyChannel`.
- `SupportConversationTicketsDto(Guid ConversationId, bool CanCreate, string? CannotCreateReason, IReadOnlyList<SupportConversationTicketDto> Items, int UnclassifiedCount)`. `CannotCreateReason` ∈ `messaging_disabled`, `no_permission`. `SupportConversationTicketDto(SupportTicketListItemDto Ticket, bool IsThisConversation, bool FollowsThread)`: la pestaña de la ficha lista los tickets de la conversación y los del mismo cliente por otros canales, marcados.
- `SupportUnclassifiedDto(IReadOnlyList<SupportUnclassifiedMessageDto> Items)`; `SupportUnclassifiedMessageDto(SupportThreadMessageDto Message, IReadOnlyList<SupportTicketRefDto> Candidates)`; `SupportTicketRefDto(Guid Id, string Number, string Subject, long Version)`. La pregunta «¿De qué ticket es?» trae sus opciones: la pantalla no las calcula.
- `SupportThreadMessageDto(Guid Id, string Direction, string Kind, string? Text, string? Caption, DateTimeOffset OccurredAt, string? AuthorName, bool HasMedia, string? MediaMimeType, string? MediaFileName)`.
- `SupportTicketMessagesDto(IReadOnlyList<SupportTicketMessageDto> Items, SupportMessageCursorDto? Next)`; `SupportTicketMessageDto(SupportThreadMessageDto Message, string LinkState)` con `LinkState` ∈ `Linked`, `Pinned`; los `Released` no salen. Del más nuevo al más viejo, 50 por defecto, máximo 100.
- `SupportReplyOptionsDto(SupportThreadOptionDto Thread, SupportChannelOptionDto WhatsAppTemplate, SupportChannelOptionDto Email, string DefaultChannel)`. `SupportThreadOptionDto(bool Available, string? Reason, string? Delivery, DateTimeOffset? WindowClosesAt, bool CanAttach)` con `Delivery` ∈ `Text`, `Template`. `SupportChannelOptionDto(bool Available, string? Reason, IReadOnlyList<SupportConnectionOptionDto> Connections)`. Motivos: `no_conversation`, `messaging_disabled`, `window_closed`, `contact_blocked`, `template_not_approved`, `no_mobile`, `no_email`, `connection_unavailable`. `Connections` sólo trae algo para `WhatsAppTemplate` sin conversación: las conexiones de WhatsApp activas con la plantilla aprobada.
- `SupportReplyRequest(Guid? ClientId, string? Channel, string? Text, Guid? ConnectionId, Guid? UploadId, bool Resolve)`.
- `SupportReplyResultDto(SupportReplyDto Reply, SupportTicketDetailDto Ticket)`; `SupportReplyDto(Guid Id, string Channel, string Delivery, string Text, string Status, string? FailureCode, Guid? MessageId, SupportMemberDto? Author, DateTimeOffset CreatedAt)`.

En Mensajería, el DTO del evento del hilo suma `Guid? TicketId` y `string? TicketNumber` (resuelto al leer, como los nombres de miembros).

## Estructura de archivos

```
src/Modules/Support/
  Domain/        SupportTicket (+ CreateFromConversation, LinkConversation, SetFollowThread, eventos nuevos),
                 SupportTicketMessage, SupportTicketMessageState, SupportUnclassifiedMessage, SupportUnclassifiedResolution,
                 SupportTicketReply, SupportReplyStatus, SupportReplyDelivery, SupportErrorCodes (+)
  Application/   ISupportConversationDirectory, ISupportThreadFeed, ISupportConversationEvents, ISupportReplyChannels,
                 ISupportReplyEmail, ISupportTicketMessageRepository, ISupportUnclassifiedRepository, ISupportReplyRepository,
                 Conversations/*.cs, Thread/*.cs, Replies/*.cs, SupportThreadFollower
  Infrastructure/ Persistence/* (+ migración AddSupportMessaging), SupportThreadFollowWorker, SupportWorkerOptions
  Api/           SupportEndpoints (+ rutas nuevas)
src/Modules/Messaging/
  Domain/        ConversationEventType (+ TicketOpened, TicketClosed), ConversationEvent (+ TicketId), MessageTemplateKind (+ SupportReply),
                 MessageTemplateName (+ prefijo)
  Application/   IConversationEventAppender, IMessagingTicketNames, SupportReplyTemplateProvisioning, ISupportReplyOutbound/SupportReplyOutbound,
                 SystemTemplateVariables (+ SupportReply)
  Infrastructure/ ConversationEventAppender, SupportReplyTemplateReadiness, migración AddSupportReplyTemplateKind
src/Modules/Notifications/Application/SupportReplyEmailTemplate.cs
src/Bootstrapper/  SupportConversationDirectory, SupportThreadFeed, SupportConversationEvents, SupportReplyChannels,
                   SupportReplyEmailSender, MessagingTicketNames, CustomerDeletionReferences (+)
```

---

## Bloque A — dominio y persistencia de Support (Tareas 1–2, un implementador)

### Task 1: el ticket conoce su conversación, el seguimiento, los vínculos y las respuestas

**Files:**
- Modify: `src/Modules/Support/Modules.Support.Domain/SupportTicket.cs`, `SupportTicketEventKind.cs`, `SupportErrorCodes.cs`
- Create: `Modules.Support.Domain/SupportTicketMessage.cs`, `SupportTicketMessageState.cs`, `SupportUnclassifiedMessage.cs`, `SupportUnclassifiedResolution.cs`, `SupportTicketReply.cs`, `SupportReplyStatus.cs`, `SupportReplyDelivery.cs`, `SupportConversationRef.cs`
- Test: `tests/Modules/Support/Modules.Support.UnitTests/SupportTicketConversationTests.cs`, `SupportTicketMessageTests.cs`, `SupportTicketReplyTests.cs`

**Interfaces:**
- Consumes: el agregado de la fase 1 (`SupportTicket`, `SupportClockContext`, `TicketRequester`).
- Produces:
  ```csharp
  public sealed record SupportConversationRef(Guid Id, string Channel);           // "WhatsApp" | "Instagram"
  public enum SupportTicketEventKind { Created, StatusChanged, AssigneeChanged, TypeChanged, CustomerChanged,
      MessagePinned, MessageReleased, MessageClassified, MessageDismissed, FollowThreadChanged, ReplySent, ConversationLinked }
  // SupportTicket, además de lo de la fase 1:
  public Guid? ConversationId { get; } public string? ConversationChannel { get; }
  public bool FollowThread { get; } public DateTimeOffset? FollowSince { get; } public DateTimeOffset? FollowOffAt { get; }
  public static SupportTicket CreateFromConversation(SupportTicketId id, Guid tenantId, long number, SupportTicketType type, string subject,
      string description, SupportConversationRef conversation, Guid? customerId, TicketRequester? requester, Guid? assigneeMemberId,
      Guid actorMemberId, DateTimeOffset now, SupportClockContext clock);       // Intake Conversation, ReplyChannel Thread, sigue desde now
  public void ChangeStatus(SupportTicketStatus target, Guid? actorMemberId, DateTimeOffset now, SupportClockContext clock); // null = sistema
  public void SetFollowThread(bool follow, Guid actorMemberId, DateTimeOffset now);   // igual: no-op; prender pone FollowSince = now y FollowOffAt = null
  public void LinkConversation(SupportConversationRef conversation, Guid actorMemberId, DateTimeOffset now); // sólo si no tiene; si no, InvalidOperationException
  public bool CoversInbound(DateTimeOffset occurredAt);   // abierto + ventana E4
  public void RecordMessageEvent(SupportTicketEventKind kind, Guid messageId, Guid? actorMemberId, DateTimeOffset now); // Pinned/Released/Classified/Dismissed
  public void RecordReply(SupportTicketReply reply, DateTimeOffset now);               // evento ReplySent con canal y messageId
  // Al entrar a Closed: FollowThread = false, FollowOffAt = now.
  public sealed class SupportTicketMessage
  {   // Id, TenantId, TicketId, ConversationId, MessageId, State, OccurredAt, CreatedAt, UpdatedAt
      public static SupportTicketMessage Link(Guid tenantId, SupportTicketId ticketId, Guid conversationId, Guid messageId,
          DateTimeOffset occurredAt, SupportTicketMessageState state, DateTimeOffset now);
      public void Pin(DateTimeOffset now); public void Release(DateTimeOffset now);       // Released → Pin vale (el asesor lo vuelve a fijar)
  }
  public enum SupportTicketMessageState { Linked, Pinned, Released }
  public sealed class SupportUnclassifiedMessage
  {   // TenantId, ConversationId, MessageId (PK), OccurredAt, CreatedAt, Resolution, ResolvedAt, ResolvedByMemberId, AssignedTicketId
      public static SupportUnclassifiedMessage Create(Guid tenantId, Guid conversationId, Guid messageId, DateTimeOffset occurredAt, DateTimeOffset now);
      public void Assign(SupportTicketId ticketId, Guid? actorMemberId, DateTimeOffset now);   // ya resuelta: InvalidOperationException
      public void Dismiss(Guid actorMemberId, DateTimeOffset now);
  }
  public enum SupportUnclassifiedResolution { Assigned, Dismissed, Reassigned }   // Reassigned = pasó sola al único candidato (E5)
  public enum SupportReplyStatus { Pending, Sent, Failed }
  public enum SupportReplyDelivery { Text, Template, Email }
  public sealed class SupportTicketReply
  {   // Id, TenantId, TicketId, ClientId, Channel (SupportReplyChannel), Delivery, Text, Status, FailureCode, MessageId, ConnectionId,
      // AuthorMemberId, CreatedAt, UpdatedAt
      public const int TextMaxLength = 4096;
      public static SupportTicketReply Start(Guid tenantId, SupportTicketId ticketId, Guid clientId, SupportReplyChannel channel,
          SupportReplyDelivery delivery, string text, Guid? connectionId, Guid authorMemberId, DateTimeOffset now);
      public void MarkSent(Guid? messageId, DateTimeOffset now); public void MarkFailed(string code, DateTimeOffset now);  // sólo desde Pending
  }
  ```
  Códigos nuevos en `SupportErrorCodes`: `ConversationNotFound = "support.conversation.not_found"`, `MessageNotInConversation = "support.message.not_in_conversation"`, `MessageNotLinked = "support.message.not_linked"`, `UnclassifiedNotPending = "support.unclassified.not_pending"`, `TicketNotCandidate = "support.unclassified.ticket_not_candidate"`, `NoConversation = "support.ticket.no_conversation"`, `ReplyWindowClosed = "support.reply.window_closed"`, `ReplyContactBlocked = "support.reply.contact_blocked"`, `ReplyTemplateNotApproved = "support.reply.template_not_approved"`, `ReplyTemplateTextInvalid = "support.reply.template_text_invalid"`, `ReplyConnectionUnavailable = "support.reply.connection_unavailable"`, `ReplyConnectionRequired = "support.reply.connection_required"`, `ReplyAttachmentUnavailable = "support.reply.attachment_unavailable"`.

- [ ] **Paso 1: pruebas** (zona `America/Bogota`, `FakeCalendar` de la fase 1):
  ```csharp
  [Fact] public void ATicketFromAConversationFollowsItsThreadFromCreation()
  // Intake Conversation, ReplyChannel Thread, ManualMedium null, ConversationId y canal, FollowThread true, FollowSince == now, eventos [Created]
  [Fact] public void ATicketFromAConversationWithoutCustomerNeedsARequesterName()      // requester_required
  [Fact] public void TurningFollowOffStampsTheTimeAndAnEvent()                          // FollowOffAt == now, FollowThreadChanged, Version + 1
  [Fact] public void TurningFollowOnAgainStartsANewWindow()                             // FollowSince = segundo now, FollowOffAt null
  [Fact] public void SettingTheSameFollowIsANoOp()
  [Fact] public void ClosingStopsFollowing()                                            // Closed → FollowThread false, FollowOffAt == ClosedAt
  [Fact] public void CoversInboundOnlyInsideTheWindow()                                 // antes de FollowSince: false; en [since, off): true; ≥ off: false; Closed: false
  [Fact] public void ASystemStatusChangeHasNoActor()                                    // ChangeStatus(Open, null, …) → evento con ActorMemberId null
  [Fact] public void LinkingAConversationToATicketThatHasOneThrows()
  [Fact] public void ALinkedConversationWritesConversationLinkedAndStartsFollowing()
  [Fact] public void AReleasedMessageCanBePinnedAgain()                                 // SupportTicketMessage
  [Fact] public void AResolvedUnclassifiedMessageCannotBeResolvedAgain()
  [Fact] public void AReplyMovesOnlyFromPending()                                       // MarkSent tras MarkFailed → InvalidOperationException
  [Fact] public void RecordingAReplyWritesReplySentWithChannelAndMessage()
  ```
- [ ] **Paso 2:** `dotnet build $SU` → falla por compilación (`CS0117`/`CS0246`). Guarda la salida.
- [ ] **Paso 3:** implementa. `CreateFromConversation` comparte con `CreateManual` un constructor privado; `Details` de los eventos nuevos es JSON camelCase: `{ messageId }`, `{ follow, at }`, `{ replyId, channel, delivery, messageId }`, `{ conversationId, channel }`.
- [ ] **Paso 4:** `dotnet build $W\Backend.slnx` → 0 errores y 0 avisos (en adelante, «build limpio»).
- [ ] **Paso 5:** commit `feat(support): track the ticket conversation, thread follow, message links and replies`.

### Task 2: persistencia (migración `AddSupportMessaging`)

**Files:**
- Modify: `Modules.Support.Infrastructure/Persistence/SupportDbContext.cs`, `SupportUnitOfWork.cs`, `SupportTicketRepository.cs`
- Create: `Persistence/SupportTicketMessageRepository.cs`, `SupportUnclassifiedRepository.cs`, `SupportReplyRepository.cs`, `Migrations/<ts>_AddSupportMessaging.cs`; `Modules.Support.Application/ISupportTicketMessageRepository.cs`, `ISupportUnclassifiedRepository.cs`, `ISupportReplyRepository.cs`
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportMessagingPersistenceTests.cs` (host propio)

**Interfaces:**
- Consumes: Tarea 1.
- Produces:
  ```csharp
  public interface ISupportTicketMessageRepository
  {
      Task<SupportTicketMessage?> FindAsync(Guid tenantId, SupportTicketId ticketId, Guid messageId, CancellationToken ct);
      Task<IReadOnlyList<SupportTicketMessage>> ListVisibleAsync(Guid tenantId, SupportTicketId ticketId,
          (DateTimeOffset OccurredAt, Guid MessageId)? before, int take, CancellationToken ct);    // Linked y Pinned, (occurred_at, message_id) DESC
      void Add(SupportTicketMessage link);
  }
  public interface ISupportUnclassifiedRepository
  {
      Task<SupportUnclassifiedMessage?> FindAsync(Guid tenantId, Guid messageId, CancellationToken ct);
      Task<IReadOnlyList<SupportUnclassifiedMessage>> ListPendingAsync(Guid tenantId, Guid conversationId, CancellationToken ct);
      Task<int> CountPendingAsync(Guid tenantId, Guid conversationId, CancellationToken ct);
      void Add(SupportUnclassifiedMessage message);
  }
  public interface ISupportReplyRepository
  {
      Task<SupportTicketReply?> FindByClientIdAsync(Guid tenantId, SupportTicketId ticketId, Guid clientId, CancellationToken ct);
      Task<IReadOnlyList<SupportTicketReply>> ListAsync(Guid tenantId, SupportTicketId ticketId, CancellationToken ct);  // por CreatedAt
      void Add(SupportTicketReply reply);
  }
  // ISupportTicketRepository suma:
  Task<IReadOnlyList<SupportTicket>> ListOpenByConversationAsync(Guid tenantId, Guid conversationId, CancellationToken ct); // con tracking
  Task<IReadOnlyList<SupportTicket>> ListByConversationOrCustomerAsync(Guid tenantId, Guid conversationId, Guid? customerId, CancellationToken ct);
  ```
- Esquema (migración `AddSupportMessaging`):
  - `tickets` suma `conversation_id uuid NULL`, `conversation_channel varchar(16) NULL`, `follow_thread boolean NOT NULL DEFAULT false`, `follow_since timestamptz NULL`, `follow_off_at timestamptz NULL`; `CK_tickets_conversation_channel` (`NULL` o `'WhatsApp','Instagram'`), `CK_tickets_follow` (`NOT follow_thread OR (conversation_id IS NOT NULL AND follow_since IS NOT NULL)`); índice parcial `IX_tickets_tenant_conversation (tenant_id, conversation_id) WHERE conversation_id IS NOT NULL`. Los tickets existentes quedan sin conversación y sin seguir: no hay backfill.
  - Reescribe `CK_ticket_events_kind` con los doce valores (la fase 1 la creó en `AddSupportProbeIndexesAndChecks`).
  - `ticket_messages`: `id`, `tenant_id`, `ticket_id` FK → `tickets(id) ON DELETE CASCADE`, `conversation_id`, `message_id`, `state varchar(16)` con `CK_ticket_messages_state`, `occurred_at`, `created_at`, `updated_at`. Único `IX_ticket_messages_ticket_message (ticket_id, message_id)`; índice `IX_ticket_messages_ticket_visible (ticket_id, occurred_at DESC, message_id DESC) WHERE state <> 'Released'`; índice `IX_ticket_messages_conversation (tenant_id, conversation_id)`.
  - `unclassified_messages`: `message_id uuid PK`, `tenant_id`, `conversation_id`, `occurred_at`, `created_at`, `resolution varchar(16) NULL` con su CHECK, `resolved_at NULL`, `resolved_by_member_id NULL`, `assigned_ticket_id NULL`; índice parcial `IX_unclassified_pending (tenant_id, conversation_id) WHERE resolution IS NULL`.
  - `ticket_replies`: `id`, `tenant_id`, `ticket_id` FK → `tickets(id) ON DELETE CASCADE`, `client_id uuid`, `channel varchar(24)`, `delivery varchar(16)`, `text varchar(4096)`, `status varchar(16)`, `failure_code varchar(64) NULL`, `message_id uuid NULL`, `connection_id uuid NULL`, `author_member_id uuid`, `created_at`, `updated_at`; CHECKs de `channel`, `delivery` y `status`; único `IX_ticket_replies_ticket_client (ticket_id, client_id)`.
- `SupportUnitOfWork` traduce **por nombre de índice** `IX_ticket_messages_ticket_message`, `PK_unclassified_messages` e `IX_ticket_replies_ticket_client` a `ResourceConflictException` con los códigos `support.message.already_linked`, `support.unclassified.already_recorded` y `support.reply.duplicate_client_id` (constantes nuevas en `SupportErrorCodes`); el worker y el envío los atrapan.
- `SupportUserReferenceProbe` (Bootstrapper) suma `ticket_replies.author_member_id` y `unclassified_messages.resolved_by_member_id`, con índices `IX_ticket_replies_author` e `IX_unclassified_resolved_by` en la misma migración (gotcha de la sonda).

- [ ] **Paso 1: pruebas** (`SupportMessagingPersistenceTests`, host propio, no se corren):
  ```csharp
  [Fact] public async Task ATicketFromAConversationRoundTripsItsFollowWindow()
  [Fact] public async Task TheSameMessageCannotBeLinkedTwiceToATicket()       // ResourceConflictException support.message.already_linked
  [Fact] public async Task VisibleMessagesSkipReleasedAndPageByCursor()        // 3 Linked/Pinned + 1 Released, take 2 → 2 y cursor; segunda página → 1
  [Fact] public async Task TheSameUnclassifiedMessageCannotBeRecordedTwice()
  [Fact] public async Task AReplyClientIdIsUniquePerTicket()
  [Fact] public async Task OpenTicketsOfAConversationExcludeClosedOnes()
  [Fact] public async Task TheEventKindCheckAcceptsTheNewKinds()               // un MessagePinned se guarda
  ```
  `SupportUserReferenceProbeTests` suma `ARepliedMemberIsReferenced` y `AMemberWhoDismissedAMessageIsReferenced`.
- [ ] **Paso 2:** `dotnet build $SI` → falla por compilación. Guarda la salida.
- [ ] **Paso 3:** implementa y genera `AddSupportMessaging`; revisa el SQL: los nombres de CHECK e índices de arriba. Si algún `FromSql` lee `support.tickets` con columnas listadas a mano, suma las nuevas (gotcha del `FromSql` crudo).
- [ ] **Paso 4:** `dotnet build` de la solución → 0 errores.
- [ ] **Paso 5:** commit `feat(support): persist ticket conversations, message links, unclassified messages and replies`.

- [ ] **Fin del Bloque A:** build de la solución limpio; reporte con las salidas de RED y GREEN. Revisión del bloque.

---

## Bloque B — lo que Mensajería expone (Tareas 3–5, un implementador)

### Task 3: eventos `TicketOpened`/`TicketClosed` y el número del ticket en el hilo

**Files:**
- Modify: `Modules.Messaging.Domain/ConversationEvent.cs` (dos valores **al final** del enum; `Guid? TicketId = null` al final del `record`), el DTO y el mapeo del evento del hilo (donde hoy se resuelven los nombres de miembros: búscalo desde `MessagingMemberNames`), `MessagingInfrastructureExtensions.cs`
- Create: `Modules.Messaging.Application/IConversationEventAppender.cs`, `IMessagingTicketNames.cs`; `Modules.Messaging.Infrastructure/ConversationEventAppender.cs`; `src/Bootstrapper/MessagingTicketNames.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/ConversationTicketEventsTests.cs`, `tests/Bootstrapper/Bootstrapper.UnitTests/MessagingTicketNamesTests.cs` (o la integración de Support si la prueba necesita base)

**Interfaces:**
- Produces:
  ```csharp
  public interface IConversationEventAppender
  {   // Escribe con el MessagingDbContext del scope; quien llama decide la transacción (E11). Conversación de otro tenant: false, sin escribir.
      Task<bool> AppendTicketEventAsync(Guid tenantId, Guid conversationId, ConversationEventType type, Guid ticketId,
          Guid? actorMemberId, DateTimeOffset occurredAt, CancellationToken ct);   // sólo TicketOpened o TicketClosed; otro tipo: ArgumentException
  }
  public interface IMessagingTicketNames { Task<IReadOnlyDictionary<Guid, string>> FindNumbersAsync(Guid tenantId, IReadOnlyCollection<Guid> ticketIds, CancellationToken ct); }
  ```
  `MessagingTicketNames` (Bootstrapper) lee `SupportDbContext` sin tracking y devuelve `SupportTicketNumber.Format(number)`; un id que no está no aparece y el hilo muestra el evento sin número.
- [ ] **Paso 1: pruebas:** `AppendingTicketOpenedShowsInTheThreadWithTheTicketNumber`, `AppendingToAConversationOfAnotherTenantWritesNothing`, `TheEventEnumKeepsItsStoredNamesInOrder` (los valores viejos conservan su posición; los nuevos van detrás de `AutomationContinued`).
- [ ] **Paso 2:** `dotnet build $MI` → falla por compilación.
- [ ] **Paso 3:** implementa con `ConversationEventRows.InsertAsync`. El evento no toca `version`, estado, no leídos ni `history_managed` de la conversación: es una marca, no una acción del asesor sobre la conversación (decisión anotada en el resumen del `record`).
- [ ] **Paso 4:** build limpio. **Paso 5:** commit `feat(messaging): add ticket opened and closed thread events`.

### Task 4: plantilla del sistema `SupportReply`

**Files:** los de Mensajería que tocó `028aa490` para `Order`, sin los de Quotations: `MessageTemplate.cs` (`SupportReply` al final del enum; `MessageTemplateName.SupportReplyName`/`SupportReplyPrefix` y el prefijo en la lista de reservados), `SystemTemplateVariables.cs` (`SupportReply`: fijas + `radicado` «Radicado del caso» ejemplo `SOP-000127` + `respuesta` «Respuesta del asesor» ejemplo `Revisamos tu solicitud y ya quedó resuelta.`), `SystemTemplateDrafts.cs`, `TemplateManagement*.cs`, `TemplateManagementEndpoints.cs`, `MessagingDbContext.cs` (`CK_message_templates_kind` con `'SupportReply'`), `MessageTemplateRepository.cs`, `MessagingInfrastructureExtensions.cs`, `MessagingErrorCodes.cs` si hace falta
- Create: `Modules.Messaging.Application/SupportReplyTemplateProvisioning.cs` (`SupportReplyTemplateDefinition` calcada de `OrderTemplateDefinition`, con los valores de Global Constraints), `Modules.Messaging.Infrastructure/Persistence/SupportReplyTemplateReadiness.cs` (`ISupportReplyTemplateReadiness.FindApprovedAsync(tenantId, connectionId)` → `ApprovedTemplate?`), migración `AddSupportReplyTemplateKind`
- Test: los de `028aa490` para `Order`, renombrados: `SupportReplyTemplateDefinitionTests` (nombre, versiones, prefijo reservado, ejemplos sin `$ # %`), y en integración `SupportReplyTemplateProvisioningApiTests` (aprovisionar por conexión, segunda vez 409, readiness devuelve la fila aprobada).
- [ ] **Paso 1–2:** pruebas y `dotnet build $MU` / `$MI` en rojo por compilación.
- [ ] **Paso 3:** implementa copiando el camino de `Order`. Una plantilla libre no puede usar el nombre ni el prefijo.
- [ ] **Paso 4:** build limpio. **Paso 5:** commit `feat(messaging): add the support reply system template`.

### Task 5: `ISupportReplyOutbound` — responder desde Soporte

**Files:**
- Create: `Modules.Messaging.Application/SupportReplyOutbound.cs`; registro en `MessagingInfrastructureExtensions.cs`
- Test: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SupportReplyOutboundTests.cs` (host propio: usa el doble de Meta que anota llamadas)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record SupportThreadReplySend(Guid TenantId, Guid ConversationId, Guid ClientId, Guid AuthorMemberId, string Text, Guid? UploadId);
  public sealed record SupportTemplateReplySend(Guid TenantId, Guid? ConversationId, Guid? ConnectionId, string? WaId, Guid? CustomerId,
      string RecipientName, Guid ClientId, Guid AuthorMemberId, string TicketNumber, string Text);   // conversación, o conexión + WaId (E14)
  public abstract record SupportReplySendResult
  {
      public sealed record Sent(Guid MessageId, Guid ConversationId) : SupportReplySendResult;
      public sealed record Rejected(string Reason) : SupportReplySendResult;   // antes de enviar: window_closed, contact_blocked, template_not_approved,
                                                                               // template_text_invalid, connection_unavailable, conversation_not_found,
                                                                               // attachment_unavailable
      public sealed record Failed(Guid? MessageId, string Code) : SupportReplySendResult;   // Meta lo rechazó: la fila queda Failed en el hilo
  }
  public sealed record SupportThreadState(Guid ConversationId, string Channel, bool Blocked, DateTimeOffset? WindowClosesAt, Guid ConnectionId);
  public interface ISupportReplyOutbound
  {
      Task<SupportThreadState?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken ct);
      Task<SupportReplySendResult> SendThreadAsync(SupportThreadReplySend send, CancellationToken ct);      // en ventana: texto o adjunto; fuera: Rejected(window_closed)
      Task<SupportReplySendResult> SendTemplateAsync(SupportTemplateReplySend send, CancellationToken ct);
  }
  ```
  Sigue `BotOutbound` (texto) y `BillingTemplateOutbound` (plantilla): idempotente por `ClientId` (`FindByClientIdAsync` + `TakeInFlightAsync`), `SentByMemberId = AuthorMemberId`, sin `OutboundGuards.AdmitAsync` (no exige `Open` ni dueño), `seen: 0`, conexión por `ResolveSenderWithReasonAsync`. La plantilla a una conversación existente usa `SendTarget.ForTemplate(conversation.UserId, conversation.WaId)`; sin conversación, `OpenByPhoneAsync`. Valores: `radicado` = `TicketNumber`, `respuesta` = `Text` (pasa `TemplateParameterRules` o `Rejected(template_text_invalid)`), las fijas por `TemplateSenderValues`. Instagram en ventana usa el emisor de Instagram como `SendMessageHandler`.
- [ ] **Paso 1: pruebas:** `AThreadReplyInsideTheWindowIsSentAsTheAuthor`, `AThreadReplyOutsideTheWindowIsRejectedWithoutARow`, `ABlockedContactIsRejected`, `RetryingAReplyWithTheSameClientIdSendsOnce`, `ATemplateReplyToAConversationWithoutWaIdUsesItsUserId`, `ATemplateReplyWithoutConversationOpensOneByPhone`, `ATemplateReplyWithNewLinesIsRejected`, `ASupportReplyDoesNotTakeNorReopenTheConversation`, `ASupportReplyTurnsOffHistoryManaged` (Review Focus 4), `AnInstagramReplyOutsideTheWindowIsRejected`.
- [ ] **Paso 2:** `dotnet build $MI` → rojo por compilación. **Paso 3:** implementa. **Paso 4:** build limpio.
- [ ] **Paso 5:** commit `feat(messaging): send support replies through the thread or the support template`.

- [ ] **Fin del Bloque B:** build limpio, reporte, revisión del bloque.

---

## Bloque C — adaptadores, API de Support y el worker (Tareas 6–10, un implementador)

### Task 6: adaptadores de Bootstrapper

**Files:**
- Create: `src/Bootstrapper/SupportConversationDirectory.cs`, `SupportThreadFeed.cs`, `SupportConversationEvents.cs`, `SupportReplyChannels.cs`, `SupportReplyEmailSender.cs`; `src/Modules/Notifications/Modules.Notifications.Application/SupportReplyEmailTemplate.cs`; los puertos en `Modules.Support.Application/` (`ISupportConversationDirectory.cs`, `ISupportThreadFeed.cs`, `ISupportConversationEvents.cs`, `ISupportReplyChannels.cs`, `ISupportReplyEmail.cs`)
- Modify: `src/Bootstrapper/CustomerDeletionReferences.cs` (E18), `QepServiceCollectionExtensions.cs` (registro)
- Test: `tests/Modules/Support/Modules.Support.IntegrationTests/SupportBootstrapperAdaptersTests.cs` (corre los adaptadores reales contra el esquema migrado), `tests/Modules/Notifications/.../SupportReplyEmailTemplateTests.cs`, `CustomerDeletionAcrossModulesTests` (Customers) suma un caso

**Interfaces:**
- Produces (en `Modules.Support.Application`):
  ```csharp
  public sealed record SupportConversationInfo(Guid Id, string Channel, Guid? CustomerId, string? ProfileName, string? Username,
      string? WaId, bool Blocked, DateTimeOffset? LastInboundAt);
  public interface ISupportConversationDirectory
  {
      Task<SupportConversationInfo?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken ct);
      Task<IReadOnlyList<SupportThreadMessage>> FindMessagesAsync(Guid tenantId, Guid conversationId, IReadOnlyCollection<Guid> messageIds, CancellationToken ct);
      Task<bool> BelongsToConversationAsync(Guid tenantId, Guid conversationId, Guid messageId, CancellationToken ct);
  }
  public sealed record SupportThreadMessage(Guid Id, Guid ConversationId, string Direction, string Kind, string? Text, string? Caption,
      DateTimeOffset OccurredAt, Guid? SentByMemberId, Guid? SentByChatbotId, string? SentByApiKeyName, string? MediaMimeType, string? MediaFileName);
  public sealed record SupportPendingInbound(Guid TenantId, Guid ConversationId, Guid MessageId, DateTimeOffset OccurredAt);
  public interface ISupportThreadFeed
  {   // Entrantes en vivo de conversaciones con algún ticket que las sigue, sin fila en ticket_messages ni en unclassified_messages,
      // con occurred_at >= el follow_since más viejo de esa conversación. Por (occurred_at, message_id), de a `take`.
      Task<IReadOnlyList<SupportPendingInbound>> ListPendingAsync(int take, CancellationToken ct);
      Task<IReadOnlyList<(Guid TenantId, Guid ConversationId)>> ListConversationsWithPendingUnclassifiedAsync(int take, CancellationToken ct);
  }
  public interface ISupportConversationEvents   // E11: enrola el MessagingDbContext en la transacción abierta de Support
  { Task AppendAsync(Guid tenantId, Guid conversationId, bool opened, Guid ticketId, Guid? actorMemberId, DateTimeOffset at, CancellationToken ct); }
  public interface ISupportReplyChannels        // traduce ISupportReplyOutbound y la readiness de Mensajería
  {
      Task<SupportThreadStatus?> FindThreadAsync(Guid tenantId, Guid conversationId, CancellationToken ct);
      Task<IReadOnlyList<SupportConnectionOption>> ListTemplateConnectionsAsync(Guid tenantId, CancellationToken ct);  // activas con SupportReply aprobada
      Task<bool> IsTemplateApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken ct);
      Task<SupportChannelSendResult> SendThreadAsync(...);  Task<SupportChannelSendResult> SendTemplateAsync(...);   // espejo de Task 5, tipos de Support
  }
  public interface ISupportReplyEmail { Task<SupportChannelSendResult> SendAsync(SupportReplyEmail email, CancellationToken ct); }
  public sealed record SupportReplyEmail(string ToAddress, string TenantName, string TicketNumber, string TicketSubject, string Text);
  ```
  `SupportReplyEmailTemplate.Render(SupportReplyEmailInput)` → `EmailMessage` con asunto `Respuesta a tu caso {número}`, el texto escapado en HTML (saltos de línea a `<br>`) y la versión de texto plano; tutea.
- `SupportThreadFeed` es SQL crudo por nombre sobre `support.tickets`, `support.ticket_messages`, `support.unclassified_messages` y `messaging.messages` (`direction = 1 AND NOT imported`), usando `IX_messages_thread`. Gotcha nuevo del `CLAUDE.md` en la Tarea 12.
- `CustomerDeletionReferences`, en la misma transacción y antes del `DELETE FROM messaging.conversations`: E18 sobre los ids de conversación que borra.
- [ ] **Paso 1: pruebas:** `TheFeedListsOnlyLiveInboundOfFollowedConversations` (eco, importado, otro tenant y conversación sin ticket no salen), `TheFeedSkipsMessagesAlreadyLinkedOrUnclassified`, `TheFeedRespectsTheOldestFollowSince`, `TheConversationEventJoinsTheSupportTransaction` (rollback de Support → no queda el evento), `TheDirectoryReadsAnInstagramConversationWithoutWaId`, `TheEmailEscapesHtmlAndKeepsLineBreaks`, `DeletingACustomerUnlinksTheTicketsOfItsConversations` (Customers).
- [ ] **Paso 2–4:** rojo por compilación, implementación, build limpio.
- [ ] **Paso 5:** commit `feat(support): add messaging, thread feed and email adapters`.

### Task 7: crear desde la conversación y la pestaña de la ficha

**Files:** `Modules.Support.Application/Conversations/CreateTicketFromConversation.cs`, `ListConversationTickets.cs`; `SupportEndpoints.cs`; `SupportDtos.cs`; `SupportTicketDtoComposer.cs` (campos nuevos del detalle)
- Test: `SupportConversationTicketsApiTests.cs` (integración; siembra una conversación por SQL con su cliente, como hacen las pruebas de Pipeline que leen conversaciones), `CreateTicketFromConversationValidatorTests.cs` (unitaria)
- **Orden del handler:** autorización `Manage` → membresía → validador (tipo, asunto, descripción, responsable) → módulo `messaging` (403 `tenancy.module_not_enabled`) → conversación del tenant (`404 support.conversation.not_found`) → foto del solicitante (E10) → provisión → tipo activo → cliente → responsable → transacción → candado del cliente → radicado → contexto → `CreateFromConversation` → auditoría `support.ticket.created` → `ISupportConversationEvents.AppendAsync(opened: true)` → guardar → confirmar.
- [ ] **Paso 1: pruebas:** `ATicketFromAConversationLinksItsCustomerAndFollowsTheThread`, `ATicketFromAConversationWritesTicketOpenedInTheThread`, `AWhatsAppConversationGivesTheRequesterItsMobile` (`+573001234567`), `AnInstagramConversationWithoutCustomerUsesItsUsername`, `MessagingOffIs403`, `AConversationOfAnotherTenantIs404`, `TheContactTabListsThisConversationAndTheSameCustomerTickets` (marca `IsThisConversation`), `TheContactTabSaysWhyItCannotCreate` (`messaging_disabled`).
- [ ] **Paso 2–4.** **Paso 5:** commit `feat(support): open tickets from a conversation and list them in the contact sheet`.

### Task 8: el worker de seguimiento y «sin clasificar»

**Files:** `Modules.Support.Application/Thread/SupportThreadFollower.cs`; `Modules.Support.Infrastructure/SupportThreadFollowWorker.cs`, `SupportWorkerOptions.cs` (sección `Support:Workers`, `FollowIntervalSeconds` 5, `BatchSize` 200); registro del `HostedService` en `AddSupportInfrastructure`, apagable por `Support:Workers:Enabled` como los demás workers del repo (búscalo en `AddAutomationsInfrastructure`)
- Test: `SupportThreadFollowerTests.cs` (integración, host propio con el worker apagado; la prueba llama `ProcessBatchAsync` a mano)

**Interfaces:**
- Produces: `public sealed class SupportThreadFollower { public Task<int> ProcessBatchAsync(CancellationToken ct); }` — por cada `SupportPendingInbound`: carga los tickets abiertos de la conversación con tracking, filtra `CoversInbound(occurredAt)`; uno → `SupportTicketMessage.Link(…, Linked)` y, si está en `WaitingOnCustomer`, `ChangeStatus(Open, null, …)` con auditoría `support.ticket.status_changed` de actor sistema; dos o más → `SupportUnclassifiedMessage.Create`; cero → nada (puede pasar si el ticket se cerró entre la lectura del feed y la carga). Después, por cada conversación con pendientes sin clasificar: si hay exactamente un candidato para la fila, `Assign(ticket, null)` con resolución `Reassigned` y vínculo `Linked`. Cada entrante en su propio `SaveChanges`; un conflicto de índice (E3) se descarta y uno de versión se deja para el próximo tick.
- [ ] **Paso 1: pruebas:** `ASingleFollowingTicketLinksTheInbound`, `AnInboundOlderThanTheTicketIsNotLinked` (Review Focus 1), `TwoFollowingTicketsLeaveTheInboundUnclassified`, `AnInboundWhileFollowIsOffIsNotLinked`, `ALinkedInboundReopensATicketWaitingOnTheCustomer` (estado `Open`, evento sin actor, reloj reanudado), `ALinkedInboundDoesNotReopenAResolvedTicket` (E6), `ProcessingTheSameInboundTwiceLinksItOnce` (Review Focus 2), `AClosedTicketStopsFollowingAndLeavesASingleCandidate` (Review Focus 5: la fila pendiente pasa sola al que queda), `AnAutomaticLinkDoesNotBumpTheTicketVersion`, `EchoesAndImportedMessagesAreNeverLinked`.
- [ ] **Paso 2–4.** **Paso 5:** commit `feat(support): follow ticket threads and classify inbound messages`.

### Task 9: mensajes del ticket, fijar, soltar, seguir el hilo y resolver «sin clasificar»

**Files:** `Modules.Support.Application/Thread/ListTicketMessages.cs`, `PinTicketMessage.cs`, `ReleaseTicketMessage.cs`, `SetTicketFollowThread.cs`, `ListUnclassified.cs`, `AssignUnclassified.cs`, `DismissUnclassified.cs`; `ChangeTicketStatus.cs` (al cerrar, `TicketClosed` en el hilo por `ISupportConversationEvents`, E11); `SupportEndpoints.cs`; `SupportAuditActions.cs` (`support.ticket.message_pinned`, `…message_released`, `…follow_changed`, `support.unclassified.assigned`, `support.unclassified.dismissed`)
- Test: `SupportTicketThreadApiTests.cs` (integración) y validadores unitarios
- Reglas: fijar exige que el mensaje sea de la conversación del ticket (`422 support.message.not_in_conversation`; ticket sin conversación `422 support.ticket.no_conversation`); soltar exige fila (`422 support.message.not_linked`); asignar exige fila pendiente (`422 support.unclassified.not_pending`) y un ticket candidato abierto de esa conversación (`422 support.unclassified.ticket_not_candidate`), y reabre como E6; `If-Match` del ticket en fijar, soltar y seguir; asignar lleva la `version` del ticket elegido en el cuerpo (`{ ticketId, version }`) porque la ruta es de la conversación.
- [ ] **Paso 1: pruebas:** `PinningAnOlderMessageAddsItToTheFile`, `PinningAMessageOfAnotherConversationIs422`, `ReleasingKeepsTheWorkerFromLinkingItAgain`, `TurningFollowOffShowsTheBannerDate` (`FollowOffAt` en el detalle), `AssigningAnUnclassifiedPinsItAndReopensAWaitingTicket`, `DismissingWritesANoteInEveryCandidate`, `ClosingATicketWritesTicketClosedInTheThread`, `TicketMessagesPageNewestFirstWithoutReleased`, `AStaleVersionIs412`.
- [ ] **Paso 2–4.** **Paso 5:** commit `feat(support): pin, release and classify thread messages and toggle thread follow`.

### Task 10: responder desde el ticket

**Files:** `Modules.Support.Application/Replies/GetReplyOptions.cs`, `SendTicketReply.cs`; `SupportEndpoints.cs`; `SupportTicketDtoComposer.cs` (`Replies`); `SupportAuditActions.cs` (`support.ticket.reply_sent`, `…reply_failed`)
- Test: `SupportTicketRepliesApiTests.cs` (integración, host propio con el doble de Meta y el canal de correo `log` reemplazado por uno que anota), `SendTicketReplyValidatorTests.cs`
- **Orden del handler:** autorización → membresía → validador (`ClientId` no vacío, canal exacto, texto con la regla de Global Constraints, `ConnectionId` sólo con `WhatsAppTemplate`, `UploadId` sólo con `Thread`) → ticket (404) y versión (412) → `clientId` ya visto: devuelve esa fila sin enviar → disponibilidad del canal (E13–E15; 422 con el código) → fila `Pending` confirmada → envío fuera de transacción → transacción: `MarkSent`/`MarkFailed`, vínculo `Pinned` del `MessageId` si hay, `RecordReply`, conversación nueva (E14: `LinkConversation` + `TicketOpened`), `Resolved` si `Resolve` y salió, auditoría → confirmar → 201.
- [ ] **Paso 1: pruebas:** `ReplyOptionsExplainEachChannel` (cada motivo de E17b con su escenario), `AThreadReplyInsideTheWindowIsSentAndPinned`, `AThreadReplyOutsideTheWindowGoesAsTheSupportTemplate`, `WithoutAnApprovedTemplateOutsideTheWindowIs422`, `AWhatsAppTemplateReplyWithoutConversationCreatesAndFollowsIt`, `AnEmailReplyGoesToTheRequesterEmail`, `SendAndResolveResolvesOnlyWhenSent`, `AFailedSendIs201WithTheFailedReply`, `RetryingAReplyWithTheSameClientIdSendsOnce` (Review Focus 3), `ATemplateTextWithLineBreaksIs422BeforeSending`.
- [ ] **Paso 2–4.** **Paso 5:** commit `feat(support): reply to tickets through the thread, the support template or email`.

- [ ] **Fin del Bloque C:** build limpio, reporte, revisión del bloque.

---

## Bloque D — adjuntos y documentación (Tareas 11–12, un implementador)

### Task 11: un adjunto en la respuesta por el hilo

**Files:** `SupportReplyOutbound.cs` (Mensajería: `UploadId` por el camino de `SendMediaMessage`, reusando `OutboundMediaPreparer`), `SendTicketReply.cs`, `GetReplyOptions.cs` (`CanAttach`)
- Test: en `SupportReplyOutboundTests` `AThreadReplyWithAnUploadSendsTheMedia`, `AnUploadOfAnotherConversationIsRejected`; en `SupportTicketRepliesApiTests` `AnAttachmentOutsideTheWindowIs422`.
- Reglas E19. Un `UploadId` de otra conversación o vencido es `Rejected(attachment_unavailable)` → `422 support.reply.attachment_unavailable`.
- [ ] **Pasos 1–5.** Commit `feat(support): attach a file to a thread reply`.

### Task 12: documentación

**Files:** `README.md` (§ Soporte: rutas nuevas, el worker y su configuración, la plantilla `qep_soporte_respuesta` y que se aprovisiona por conexión, que el correo no es idempotente ante un reintento), `CLAUDE.md` (gotchas: `SupportThreadFeed` lee `messaging.messages` por nombre y lo amarra `SupportBootstrapperAdaptersTests`; `ConversationEventType` y `MessageTemplateKind` ganaron valores al final; responder desde Soporte no toma la conversación; un `Linked` no sube la versión; `CustomerDeletionReferences` desvincula conversaciones de tickets), `appsettings.json` (`Support:Workers`).
- [ ] **Paso 1:** escribe. **Paso 2:** commit `docs(support): document the messaging phase`.

- [ ] **Fin del Bloque D:** build limpio y revisión. Luego la revisión fuerte de toda la rama (`develop..feature/support-messaging`).

---

## Lo que esta fase deja preparado

- **Fase 3:** la plantilla «Apertura de Soporte» y el mensaje con radicado y PIN al crear desde una conversación (E1) reusan `ISupportReplyOutbound.SendTemplateAsync` y `ISupportReplyEmail` con otra definición.
- **Fase 4:** el PDF formal va como encabezado de documento de una `SupportReply` v2 (Meta no deja editar la aprobada); el cierre automático lee «respuesta del cliente» de `ticket_messages`.

## Decisiones pendientes del owner

- **R9** (fase 1): cambios en tickets `Resolved` o `Closed` siguen permitidos; en esta fase incluye fijar, soltar y responder un ticket `Resolved`. Responder un `Closed` también se permite hoy.
- **P2** (fase 1): cómo cuenta un día de pausa parcial.
- **P3:** el celular de la foto puede ser un fijo copiado de `Customer.PhoneE164` (F1 de la fase 1). Una respuesta `WhatsAppTemplate` a ese número crea la conversación y Meta la rechaza: queda `Failed` y visible. Alternativa: dejar de copiar `PhoneE164` como celular.
- **P4:** si un entrante vinculado reabre un ticket `Resolved` (E6 dice que no).
- **P5:** texto y categoría de Meta de `qep_soporte_respuesta` (bloquea despliegue, no el código).

## Spec discrepancies (spec contra código)

1. §8.2 propone los eventos del hilo «con el id del ticket en un campo nuevo y opcional del `record`»: se hace así (`TicketId`), y el número se resuelve al leer porque `ConversationEvent` sólo guarda ids (su resumen: «nunca teléfonos, BSUIDs ni textos»).
2. §8.3 pide «el asesor ve el canal antes de enviar»; no hay pantalla en el backend: lo resuelve `reply-options` (E17b).
3. §8.5 lista tres plantillas; esta fase hace una (E1).
</content>
</invoke>
