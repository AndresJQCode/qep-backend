using Modules.Support.Domain;

namespace Modules.Support.Application;

// Contrato para las pantallas de qep-frontend (plan, «Contrato para las pantallas»). Los enums viajan por nombre: el diccionario
// de textos lo tiene el frontend.

/// <summary>Un tipo de ticket como lo dibujan el selector del alta y la tabla de ajustes. <c>Version</c> va en el <c>If-Match</c> del
/// <c>PUT</c>; <c>Position</c> es el orden en que la pantalla los lista. <c>InUse</c> (fase 4a, plan G6) dice si algún ticket, en
/// cualquier estado, usa el tipo: la tabla de ajustes ofrece «Eliminar» sólo si es falso, y si no, sólo «Desactivar», sin probar el
/// <c>DELETE</c> para enterarse del 422. <c>FormalNextStep</c> (fase 4b, H6) es el «siguiente paso» propio del tipo, que pisa el del
/// tenant en el PDF de la respuesta formal; null es «usa el del tenant».</summary>
public sealed record SupportTicketTypeDto(
    Guid Id,
    string Name,
    int? TermBusinessDays,
    bool TermIsLegal,
    bool RequiresFormalResponse,
    bool IsPublished,
    bool IsActive,
    int Position,
    long Version,
    bool InUse,
    string? FormalNextStep,
    bool IsInternal = false);

/// <summary>El umbral de «por vencer» del tenant [owner 2026-11-14]. Viaja con su rango (<c>Min</c> y <c>Max</c>) para que el
/// formulario de ajustes no lo repita; 0 significa «apagado». Un tenant que nunca guardó ve 3. <c>FormalNextStep</c> (fase 4b, H6) es
/// el «siguiente paso» del tenant para el PDF de la respuesta formal, null sin texto. <c>SurveyEnabled</c> y <c>AutoCloseBusinessDays</c>
/// (fase 4b, H14) viajan con el rango del cierre automático, por la misma razón que el umbral.</summary>
public sealed record SupportSettingsDto(
    int DueSoonBusinessDays,
    int MinDueSoonBusinessDays,
    int MaxDueSoonBusinessDays,
    long Version,
    string? FormalNextStep,
    bool SurveyEnabled,
    int AutoCloseBusinessDays,
    int MinAutoCloseBusinessDays,
    int MaxAutoCloseBusinessDays,
    SupportUnassignedAlertDto UnassignedAlert,
    SupportTaskNoticeDto TaskNotice);

/// <summary>Task-notices spec 2026-11-22 §5.4: the WhatsApp connection chosen for task notices (null = none, notices go by email), the ones
/// to choose from with the state of the template on each, and the global wait before escalating. The chosen id is shown even when it is no
/// longer among <c>Connections</c>.</summary>
public sealed record SupportTaskNoticeDto(
    Guid? WhatsAppConnectionId, IReadOnlyList<SupportTaskNoticeConnectionDto> Connections, int EscalationAfterMinutes);

public sealed record SupportTaskNoticeConnectionDto(Guid ConnectionId, string Name, string? PhoneNumber, string TemplateStatus);

/// <summary>
/// Spec 2026-11-22 §8.6: the «ticket sin asignar» alert, with everything the settings screen needs to draw it without crossing data
/// (BFF). <c>ScheduleId</c> is what is stored (null = the default) and <c>Schedule</c> is what rules today and why; the options of schedule
/// and recipient travel here, under Support's own settings permission, so whoever configures Support can choose without reading
/// Messaging or the assignees list. <c>HasWeeklyHours</c> false warns that a schedule never opens through its week (the alert would
/// never go out); date exceptions are not looked at.
/// </summary>
public sealed record SupportUnassignedAlertDto(
    bool Enabled,
    int ThresholdHours,
    int MinThresholdHours,
    int MaxThresholdHours,
    Guid? ScheduleId,
    SupportAlertScheduleDto Schedule,
    IReadOnlyList<SupportAlertScheduleOptionDto> ScheduleOptions,
    string TimeZone,
    int MaxRecipients,
    IReadOnlyList<SupportAlertRecipientDto> Recipients,
    IReadOnlyList<SupportAlertRecipientOptionDto> RecipientOptions,
    int ActiveRecipientCount);

/// <summary><c>Source</c>: <c>Chosen</c> (the stored one exists), <c>Default</c> (none chosen: the default rules), <c>ChosenMissing</c>
/// (the stored one was deleted: the default rules, or nothing if there is none; <c>Id</c> and <c>Name</c> are the ones that rule) or
/// <c>None</c> (no schedule rules: around the clock).</summary>
public sealed record SupportAlertScheduleDto(string Source, Guid? Id, string? Name);

public sealed record SupportAlertScheduleOptionDto(Guid Id, string Name, bool IsDefault, bool HasWeeklyHours);

/// <summary><c>CanReceive</c>: active and with <c>support.ticket.manage</c> today. In the order they were saved.</summary>
public sealed record SupportAlertRecipientDto(Guid MemberId, string Name, bool CanReceive);

public sealed record SupportAlertRecipientOptionDto(Guid MemberId, string Name);

/// <summary>
/// La configuración vigente del formulario público (fase 3, plan F14). <c>Version</c> es la de la última fila (0 sin filas: el
/// formulario por defecto, sin publicar) y va en el <c>If-Match</c> del <c>PUT</c>. <c>PublishBlockers</c> dice por qué no se puede
/// publicar (<c>privacy_notice_required</c>, <c>anonymous_notice_required</c>, <c>terms_required</c>, <c>organization_required</c>,
/// <c>organization_inactive</c>, <c>no_published_types</c>), así la pantalla lo explica sin adivinar; vacía si se puede.
/// <c>PublicUrl</c> es el enlace que el tenant comparte. <c>AllowAttachments</c> dice si la página ofrece adjuntos (spec §9.3; apagado
/// por defecto). <c>Organization</c> es la empresa elegida (null = ninguna; <c>IsActive</c> false si se desactivó después) y
/// <c>OrganizationOptions</c> las activas del tenant, por nombre: el selector las usa en vez de <c>GET …/companies</c>, que exige
/// <c>companies.company.read</c> (spec 2026-11-15 §9.3, plan L32).
/// </summary>
public sealed record SupportFormSettingsDto(
    int Version,
    bool IsPublished,
    bool ShowDocument,
    bool ShowAddress,
    bool ShowLandline,
    bool BusinessNameRequired,
    bool AllowAttachments,
    string? AccentColor,
    string? IntroText,
    string? SuccessMessage,
    string PublicUrl,
    IReadOnlyList<string> PublishBlockers,
    SupportFormOrganizationDto? Organization,
    IReadOnlyList<SupportFormOrganizationDto> OrganizationOptions);

/// <summary>A tenant company in the public form settings. <c>DocumentTypeCode</c> ∈ <c>NIT</c>, <c>CC</c>, <c>CE</c>,
/// <c>PASAPORTE</c>, <c>NIF</c>, <c>NIE</c>, <c>TAX_ID</c>; <c>DocumentNumber</c> is the company's tax id. <c>IsActive</c> false can only
/// come in <c>Organization</c> (one chosen and deactivated later), never in <c>OrganizationOptions</c>.</summary>
public sealed record SupportFormOrganizationDto(Guid Id, string Name, string DocumentTypeCode, string DocumentNumber, bool IsActive);

/// <summary>The current version of each legal document, or its example (spec 2026-11-15 §7). <c>Variables</c> es la lista que el
/// editor ofrece para insertar, en el orden del contrato (spec 2026-11-16 §3); va al final para no mover los campos que ya existían.</summary>
public sealed record SupportLegalDocumentsDto(
    SupportLegalDocumentDto PrivacyNotice,
    SupportLegalDocumentDto AnonymousNotice,
    SupportLegalDocumentDto Terms,
    IReadOnlyList<SupportLegalVariableDto> Variables);

/// <summary>Una variable de los documentos legales: se escribe <c>{{Name}}</c>; <c>Label</c> es lo que muestra el menú y
/// <c>Example</c> el valor de la vista previa (spec 2026-11-16, V-1).</summary>
public sealed record SupportLegalVariableDto(string Name, string Label, string Example);

/// <summary>
/// One version of a legal document: the current one, an old one or the example (<c>Version</c> 0, <c>IsExample</c>, without
/// <c>CreatedAt</c> nor <c>CreatedBy</c>). <c>ContentHtml</c> is always sanitized. <c>AuthorizationQuestion</c>, <c>PolicyUrl</c> and
/// <c>DefaultAuthorizationQuestion</c> are null except on <c>PrivacyNotice</c>; <c>DefaultAuthorizationQuestion</c> is the question
/// the portal shows when <c>AuthorizationQuestion</c> is null, so the editor offers it without building it in the browser.
/// </summary>
public sealed record SupportLegalDocumentDto(
    string Kind,
    int Version,
    bool IsExample,
    string ContentHtml,
    string? AuthorizationQuestion,
    string? PolicyUrl,
    string? DefaultAuthorizationQuestion,
    DateTimeOffset? CreatedAt,
    SupportMemberDto? CreatedBy);

/// <summary>The published versions of a kind, newest first, without content; empty if none was published.</summary>
public sealed record SupportLegalDocumentHistoryDto(string Kind, IReadOnlyList<SupportLegalDocumentVersionItemDto> Versions);

public sealed record SupportLegalDocumentVersionItemDto(int Version, DateTimeOffset CreatedAt, SupportMemberDto CreatedBy);

/// <summary>Una página de la bandeja. <c>Summary</c> es del tenant entero, no de la página ni del filtro: las pestañas cuentan siempre
/// lo mismo, filtres lo que filtres.</summary>
public sealed record SupportTicketPageDto(
    IReadOnlyList<SupportTicketListItemDto> Items, int Total, int Page, int PageSize, SupportInboxSummaryDto Summary);

/// <summary><c>ByStatus</c> trae los seis estados, en el orden de la máquina, también en cero: las pestañas no conocen el enum.</summary>
public sealed record SupportInboxSummaryDto(IReadOnlyList<SupportStatusCountDto> ByStatus, int Overdue, int DueSoon);

public sealed record SupportStatusCountDto(string Status, int Count);

/// <summary>
/// Fase 4b (H24, H25): el reporte de Soporte de los tickets radicados entre <see cref="From"/> y <see cref="To"/> (fechas locales del
/// tenant). Las colecciones de tamaño fijo viajan completas, también en cero: <see cref="ByStatus"/> trae los seis estados,
/// <see cref="ByType"/> todos los tipos del tenant (activos o no, en su orden) y cada lista de calificaciones las tres, así la pantalla no
/// conoce los enums del backend para dibujar la que falta. <see cref="OverdueNow"/> son los de la población vencidos hoy, con la misma
/// regla que la bandeja.
/// </summary>
public sealed record SupportReportDto(
    DateOnly From,
    DateOnly To,
    int Received,
    int Resolved,
    int OverdueNow,
    IReadOnlyList<SupportStatusCountDto> ByStatus,
    IReadOnlyList<SupportReportTypeRowDto> ByType,
    SupportResolutionTimeDto ResolutionTime,
    SupportSatisfactionDto Satisfaction);

/// <summary>Una fila por tipo. <see cref="IsActive"/> viaja para que la pantalla marque un tipo desactivado que todavía tiene tickets en el
/// rango; <see cref="AverageResolutionBusinessDays"/> es null sin resueltos.</summary>
public sealed record SupportReportTypeRowDto(
    Guid TypeId,
    string Name,
    bool IsActive,
    int Received,
    int Resolved,
    int OverdueNow,
    decimal? AverageResolutionBusinessDays,
    IReadOnlyList<SupportRatingCountDto> Ratings);

/// <summary>El tiempo de resolución en días hábiles después de la radicación hasta la última resolución, en bruto (sin descontar pausas),
/// con un decimal. <c>OnTime + Late + WithoutTerm = Resolved</c>: <see cref="WithoutTerm"/> existe para que la suma cierre con los tickets
/// de un tipo sin término.</summary>
public sealed record SupportResolutionTimeDto(
    int Resolved,
    decimal? AverageBusinessDays,
    decimal? MedianBusinessDays,
    int OnTime,
    int Late,
    int WithoutTerm);

/// <summary>Las encuestas de los tickets de la población, de cualquier ciclo. <c>Pending + Sent + WithoutSurvey + Failed + Disabled =
/// Requested</c>: <see cref="Disabled"/> existe para que la suma cierre cuando el tenant tenía la encuesta apagada, y
/// <see cref="WithoutSurvey"/> son las demás «sin encuesta» (sin plantilla, contacto bloqueado, reabierto…). <see cref="Answered"/> y
/// <see cref="Ratings"/> cuentan las respondidas.</summary>
public sealed record SupportSatisfactionDto(
    int Requested,
    int Pending,
    int Sent,
    int WithoutSurvey,
    int Failed,
    int Disabled,
    int Answered,
    IReadOnlyList<SupportRatingCountDto> Ratings);

/// <summary>Una calificación por nombre (<c>Good</c>, <c>Regular</c>, <c>Bad</c>) y cuántas.</summary>
public sealed record SupportRatingCountDto(string Rating, int Count);

/// <summary>Una fila de la bandeja. <c>RequesterDisplayName</c> lo resuelve el backend (cliente vinculado; si no, nombres y
/// apellidos; si no, razón social; si no, «NIT número») para que la fila no repita la regla. <c>DueState</c> y
/// <c>BusinessDaysLeft</c> ya vienen evaluados con el calendario y el umbral del tenant. <c>Priority</c> ∈ <c>Low</c>, <c>Medium</c>,
/// <c>High</c>, <c>Urgent</c>.</summary>
public sealed record SupportTicketListItemDto(
    Guid Id,
    string Number,
    string Subject,
    string Status,
    string Priority,
    string TypeName,
    string RequesterDisplayName,
    SupportMemberDto? Assignee,
    DateTimeOffset ReceivedAt,
    DateOnly? DueOn,
    string DueState,
    int? BusinessDaysLeft,
    bool IsInternal = false,
    SupportMemberDto? CreatedBy = null);

/// <summary>El detalle del ticket. <c>AllowedTransitions</c> sale de la máquina de estados del dominio: el selector de estado no la
/// repite. <c>Version</c> va en el <c>If-Match</c> de las acciones. <c>IssuedPin</c> (fase 4a, plan G3) trae el PIN de consulta sólo
/// en el <c>201</c> del alta manual anónima, para que el asesor se lo dicte a quien llamó o vino en persona: ese ticket no tiene a dónde
/// recibirlo, y en el <c>200</c> de <c>POST …/tickets/{id}/pin</c> (spec 2026-11-16), el PIN nuevo de un anónimo. <c>ConsultUrl</c>
/// (spec 2026-11-16, P-6) viaja sólo junto con <c>IssuedPin</c>: el enlace de la página de consulta de ese radicado, para el mismo
/// diálogo de entrega. En toda otra respuesta los dos son <c>null</c>. <c>Surveys</c> (fase 4b, H15) son las encuestas de satisfacción,
/// una por ciclo de resolución, en orden.</summary>
public sealed record SupportTicketDetailDto(
    Guid Id,
    string Number,
    string Subject,
    string Description,
    string Status,
    string Priority,
    IReadOnlyList<string> AllowedTransitions,
    SupportTicketTypeSnapshotDto Type,
    SupportClockDto Clock,
    string Intake,
    string? ManualMedium,
    string? ReplyChannel,
    SupportCustomerRefDto? Customer,
    SupportRequesterDto? Requester,
    SupportMemberDto? Assignee,
    int ResolutionCount,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<SupportTicketEventDto> Events,
    long Version,
    Guid? ConversationId,
    string? ConversationChannel,
    bool FollowsThread,
    DateTimeOffset? FollowOffAt,
    int UnclassifiedCount,
    IReadOnlyList<SupportReplyDto> Replies,
    bool IsAnonymous,
    IReadOnlyList<SupportCustomerCandidateDto> CustomerCandidates,
    SupportConsultAccessDto? ConsultAccess,
    IReadOnlyList<SupportAttachmentDto> Attachments,
    IReadOnlyList<SupportLegalAcceptanceDto> LegalAcceptances,
    string? IssuedPin,
    IReadOnlyList<SupportSurveyDto> Surveys,
    string? ConsultUrl = null,
    bool IsInternal = false,
    SupportMemberDto? CreatedBy = null);

/// <summary>
/// One legal acceptance of a public filing (spec 2026-11-15 §7, plan L19), in the order <c>PrivacyNotice</c>, <c>AnonymousNotice</c>,
/// <c>Terms</c>; an empty list if the ticket has none (a manual ticket), never <c>null</c>. No content: it opens with
/// <c>GET …/tickets/{id}/legal-acceptances/{kind}</c>. On <c>AnonymousNotice</c>, <c>AcceptedAt</c> means «continued after reading it».
/// <c>VisitorIp</c> is null on both rows of an anonymous ticket.
/// </summary>
public sealed record SupportLegalAcceptanceDto(string Kind, int Version, DateTimeOffset AcceptedAt, string? VisitorIp);

/// <summary>
/// What the requester was shown and accepted (spec 2026-11-15 §7, plan L20): the sanitized HTML as it was served, the question shown,
/// the policy URL and the characterization notice (the last three only on <c>PrivacyNotice</c>). Never today's document.
/// </summary>
public sealed record SupportLegalAcceptanceSnapshotDto(
    string Kind,
    int Version,
    DateTimeOffset AcceptedAt,
    string? VisitorIp,
    string ContentHtml,
    string? AuthorizationQuestion,
    string? PolicyUrl,
    string? CharacterizationText);

/// <summary>Un adjunto del formulario público (plan F37), en el orden en que se adjuntó. El archivo se baja con
/// <c>GET …/tickets/{id}/attachments/{attachmentId}/download</c>: la pantalla no necesita <c>storage.file.read</c>. Una lista vacía
/// si no tiene, nunca <c>null</c>.</summary>
public sealed record SupportAttachmentDto(Guid Id, string FileName, string MimeType, long SizeBytes);

/// <summary>La URL firmada de un adjunto: vive 5 minutos y baja el archivo con su nombre.</summary>
public sealed record SupportAttachmentDownloadDto(string Url);

/// <summary>Fase 4b (H13): la URL firmada del PDF de una respuesta formal, con el nombre del archivo y cuándo vence, para que la pantalla
/// la abra enseguida y la vuelva a pedir si se demoró.</summary>
public sealed record SupportDocumentLinkDto(Uri Url, string FileName, DateTimeOffset ExpiresAt);

// Fase 3 (formulario público): IsAnonymous apaga en la pantalla todo lo que pide un solicitante (sólo se responde por la página), y el
// backend rechaza vincularle cliente (support.ticket.anonymous_has_no_customer);
// CustomerCandidates son los clientes con el documento de la foto cuando el ticket no tiene cliente (cero o varios al radicar, plan
// F21), para vincular uno sin buscarlo, y siempre vacía en un anónimo; ConsultAccess dice si la información con el PIN salió, por dónde y por qué no.

/// <summary>Un cliente con el mismo documento que el solicitante. <c>IsActive</c> como en <see cref="SupportCustomerRefDto"/>.</summary>
public sealed record SupportCustomerCandidateDto(Guid Id, string Name, bool IsActive);

/// <summary>El último envío de la información con el PIN. <c>Channel</c> ∈ <c>Email</c>, <c>Thread</c>, <c>None</c>;
/// <c>FailureCode</c> si no salió (<c>send_failed</c>, <c>window_closed</c>…). Nunca trae el PIN.</summary>
public sealed record SupportConsultAccessDto(DateTimeOffset? LastSentAt, string? Channel, string? FailureCode);

// Fase 2 (Mensajería): el detalle suma la conversación del ticket. FollowOffAt dibuja la franja «Este ticket no sigue el hilo desde …»
// sin que la pantalla lea la traza; UnclassifiedCount es el aviso «hay mensajes sin clasificar» de esa conversación; Replies trae las
// respuestas con su estado, porque una fallida se muestra igual que en el hilo.

/// <summary>
/// Fase 4b (H15 a H21): la encuesta de un ciclo de resolución, como la dibuja el detalle. <c>Status</c> ∈ <c>Pending</c>, <c>Sent</c>,
/// <c>Skipped</c> («sin encuesta», con <c>ReasonCode</c>: <c>survey_disabled</c>, <c>template_not_approved</c>…), <c>Failed</c>;
/// <c>Channel</c> y <c>AnsweredVia</c> ∈ <c>WhatsApp</c>, <c>Email</c>, <c>Portal</c>; <c>Rating</c> ∈ <c>Good</c>, <c>Regular</c>,
/// <c>Bad</c>. <c>CommentPromptSent</c> dice si salió el mensaje que pide el comentario por WhatsApp: la pantalla no lee los mensajes.
/// </summary>
public sealed record SupportSurveyDto(
    int ResolutionNumber,
    string Status,
    string? Channel,
    string? ReasonCode,
    DateTimeOffset? SentAt,
    string? Rating,
    string? Comment,
    DateTimeOffset? AnsweredAt,
    string? AnsweredVia,
    bool CommentPromptSent);

/// <summary>Una respuesta del ticket (plan E16). <c>Channel</c> ∈ <c>Thread</c>, <c>WhatsAppTemplate</c>, <c>Email</c>, <c>Portal</c>
/// (fase 3, F12: publicada en la página de consulta); <c>Delivery</c> ∈ <c>Text</c>, <c>Template</c>, <c>Email</c>, <c>Portal</c> (la pantalla dice «salió como plantilla» sin conocer la ventana);
/// <c>Status</c> ∈ <c>Pending</c>, <c>Sent</c>, <c>Failed</c>, con <c>FailureCode</c> si falló. <c>ClientId</c> es el que mandó la
/// pantalla: con él retoma una respuesta que quedó <c>Pending</c> (revisión final, I3). <c>MessageId</c> también viene en una fallida si
/// Mensajería registró el mensaje. <c>HasAttachment</c> dice si se mandó (o se intentó) con archivo, salido del adjunto que guarda la
/// fila: «Reintentar» de una fallida no la reenvía como texto solo (owner, 2026-11-15). El PDF de una respuesta formal (fase 4b) no
/// cuenta como adjunto: viaja en <c>Document</c>, porque el sistema lo genera al enviar y «Reintentar» no lo manda como archivo del
/// asesor; si <c>HasAttachment</c> lo incluyera, la pantalla ofrecería reenviar un archivo que nadie subió.</summary>
public sealed record SupportReplyDto(
    Guid Id,
    Guid ClientId,
    string Channel,
    string Delivery,
    string Text,
    string Status,
    string? FailureCode,
    Guid? MessageId,
    SupportMemberDto? Author,
    DateTimeOffset CreatedAt,
    bool HasAttachment,
    SupportReplyDocumentDto? Document = null);

/// <summary>Fase 4b (H3, H9): el PDF de una respuesta formal. <c>Signer</c> es el responsable que firmó, con el nombre como salió en el
/// PDF (una foto: si después cambia su nombre visible, la carta firmada no cambia); la pantalla no lee la fila del documento. El archivo
/// se baja por <c>GET …/replies/{replyId}/document</c>.</summary>
public sealed record SupportReplyDocumentDto(string FileName, long SizeBytes, SupportMemberDto Signer, string SignerTitle);

/// <summary>La pestaña de Soporte de la ficha de una conversación: los tickets de esa conversación y los del mismo cliente por otros
/// canales (<c>IsThisConversation</c> los distingue). <c>CanCreate</c> y su motivo (<c>messaging_disabled</c>, <c>no_permission</c>)
/// deciden si la pantalla muestra «Abrir ticket», sin que conozca módulos ni permisos. <c>UnclassifiedCount</c> enciende el aviso de
/// mensajes sin clasificar.</summary>
public sealed record SupportConversationTicketsDto(
    Guid ConversationId,
    bool CanCreate,
    string? CannotCreateReason,
    IReadOnlyList<SupportConversationTicketDto> Items,
    int UnclassifiedCount);

/// <summary>Una fila de la pestaña: la fila de la bandeja más si es de esta conversación y si la sigue (el ícono «siguiendo»).</summary>
public sealed record SupportConversationTicketDto(SupportTicketListItemDto Ticket, bool IsThisConversation, bool FollowsThread);

/// <summary>La foto del tipo que guardó el ticket: editar el tipo después no la cambia.</summary>
public sealed record SupportTicketTypeSnapshotDto(Guid Id, string Name, int? TermBusinessDays, bool TermIsLegal, bool RequiresFormalResponse);

/// <summary><c>DueState</c> ∈ <c>NoTerm</c>, <c>OnTrack</c>, <c>DueSoon</c>, <c>Overdue</c>, <c>Paused</c>, <c>Finished</c>.
/// <c>TermIsLegal</c> decide si la pantalla dice «vence el plazo legal» o «vence el objetivo». Fase 4a (plan G9 a G13):
/// <c>Extension</c> es la prórroga ya hecha (null sin prórroga). <c>MaxExtensionBusinessDays</c> decide si la pantalla ofrece
/// «Prorrogar» y su tope: es el término del ticket si hoy se puede prorrogar y null si no (ya prorrogado, sin término, esperando al
/// cliente, vencido, resuelto o cerrado), así la pantalla no repite las reglas. El aviso al solicitante lo manda el asesor como
/// respuesta (plan G13); <c>Extension</c> sirve para prellenarlo. Fase 4b (H22): <c>AutoCloseOn</c> es el día hábil después del cual el
/// sistema cierra el ticket si el cliente no responde; sólo mientras está Resuelto, recalculado en cada lectura (una respuesta del cliente
/// lo corre, soltarla lo devuelve), así la pantalla no conoce la regla.</summary>
public sealed record SupportClockDto(
    DateOnly? DueOn,
    string DueState,
    int? BusinessDaysLeft,
    int? TermBusinessDays,
    bool TermIsLegal,
    int PausedBusinessDays,
    SupportExtensionDto? Extension,
    int? MaxExtensionBusinessDays,
    DateOnly? AutoCloseOn = null);

/// <summary>La prórroga del ticket. Sin autor: quién la hizo está en la traza, en el evento <c>Extended</c>.</summary>
public sealed record SupportExtensionDto(int BusinessDays, string Reason, DateTimeOffset ExtendedAt);

/// <summary>Una entrada de la traza, con campos planos y nulos según <c>Kind</c> y los nombres ya resueltos: la pantalla no lee
/// JSON ni busca miembros. Un miembro o cliente que ya no se encuentra sale como «Sin nombre».</summary>
public sealed record SupportTicketEventDto(
    int Ordinal,
    string Kind,
    DateTimeOffset OccurredAt,
    SupportMemberDto? Actor,
    string? FromStatus,
    string? ToStatus,
    SupportMemberDto? Assignee,
    string? FromTypeName,
    string? ToTypeName,
    SupportCustomerRefDto? Customer,
    Guid? MessageId,
    bool? Follow,
    string? ReplyChannel,
    int? ExtensionBusinessDays,
    DateOnly? FromDueOn,
    DateOnly? ToDueOn,
    string? Reason,
    string? FromPriority,
    string? ToPriority,
    bool? Formal = null);

// Fase 2: MessageId es el mensaje de MessagePinned, MessageReleased, MessageClassified, MessageDismissed y ReplySent; Follow, el nuevo
// valor de FollowThreadChanged; ReplyChannel, el canal de ReplySent. Planos por la misma razón que los de la fase 1.
// Fase 4a (plan G11): ExtensionBusinessDays, FromDueOn, ToDueOn y Reason sólo vienen llenos en Extended.
// Prioridad (spec 2026-11-15 §5.2): FromPriority y ToPriority son los nombres de PriorityChanged, nulos en los demás.
// Fase 4b: Formal sólo en ReplySent, verdadero si la respuesta llevó el PDF formal (la traza dice «envió la respuesta formal» sin leer las
// respuestas).

/// <summary><c>IsMe</c> (fase 4a, plan G8): si el miembro es quien hace la petición. El SPA no conoce su <c>memberId</c>, y sin este
/// campo no puede decir «tú» ni ocultar «Asignármelo» en un ticket que ya es suyo. Fuera de una sesión siempre es falso.</summary>
public sealed record SupportMemberDto(Guid Id, string DisplayName, bool IsMe);

/// <summary><c>IsActive</c> falso: el cliente se desactivó o se fusionó; la pantalla lo marca.</summary>
public sealed record SupportCustomerRefDto(Guid Id, string Name, bool IsActive);

/// <summary>La foto del solicitante tomada al radicar (plan D7).</summary>
public sealed record SupportRequesterDto(
    string? FirstNames,
    string? LastNames,
    string? BusinessName,
    string? DocumentType,
    string? DocumentNumber,
    string? Email,
    string? MobilePhone,
    string? LandlinePhone,
    string CountryCode,
    string? Address);

/// <summary>Los candidatos a responsable: exactamente los que el backend acepta al asignar.</summary>
public sealed record SupportAssigneesDto(IReadOnlyList<SupportMemberDto> Items);

/// <summary>Un mensaje del hilo como lo dibuja el expediente del ticket, sin que la pantalla tenga permisos de Mensajería (plan E20):
/// el texto viaja acá. <c>Direction</c> ∈ <c>Inbound</c>, <c>Outbound</c>; <c>Kind</c> es el tipo de Mensajería por nombre;
/// <c>AuthorName</c>, el miembro o la clave de API que lo mandó (nulo en un entrante o del asistente). El medio se baja por la ruta de
/// Mensajería: <c>HasMedia</c> y el tipo y nombre del archivo le dicen a la pantalla qué dibujar antes de pedirlo.</summary>
public sealed record SupportThreadMessageDto(
    Guid Id,
    string Direction,
    string Kind,
    string? Text,
    string? Caption,
    DateTimeOffset OccurredAt,
    string? AuthorName,
    bool HasMedia,
    string? MediaMimeType,
    string? MediaFileName);

/// <summary>Una página del expediente, del más nuevo al más viejo. <c>Next</c> es nulo en la última: la pantalla no calcula si hay
/// más.</summary>
public sealed record SupportTicketMessagesDto(IReadOnlyList<SupportTicketMessageDto> Items, SupportMessageCursorDto? Next);

/// <summary><c>LinkState</c> ∈ <c>Linked</c> (lo vinculó el seguimiento: se dibuja plegado) o <c>Pinned</c> (lo fijó un asesor: en
/// línea). Los soltados no salen.</summary>
public sealed record SupportTicketMessageDto(SupportThreadMessageDto Message, string LinkState);

/// <summary><c>Before</c> es opaco: la pantalla lo devuelve tal cual en <c>?before=</c> para pedir la página siguiente.</summary>
public sealed record SupportMessageCursorDto(string Before);

/// <summary>Los entrantes de una conversación que cayeron en dos o más tickets y nadie clasificó: la pregunta «¿De qué ticket es?».</summary>
public sealed record SupportUnclassifiedDto(IReadOnlyList<SupportUnclassifiedMessageDto> Items);

/// <summary>El mensaje y sus opciones: los tickets abiertos que lo cubren, con la versión que asignar manda en el cuerpo. La pantalla
/// no calcula los candidatos.</summary>
public sealed record SupportUnclassifiedMessageDto(SupportThreadMessageDto Message, IReadOnlyList<SupportTicketRefDto> Candidates);

public sealed record SupportTicketRefDto(Guid Id, string Number, string Subject, long Version);

/// <summary>Qué canal puede usar el asesor para responder y qué va a pasar (plan E17b): la pantalla no conoce la ventana de 24 h ni la
/// aprobación de plantillas. <c>DefaultChannel</c> es el canal de respuesta del ticket si está disponible; si no, el primero que lo
/// esté (hilo, plantilla, correo).</summary>
/// <remarks>Fase 3 (plan F12): <c>Portal</c> es la página de consulta, disponible sólo para un ticket anónimo, que no tiene ningún otro
/// canal; los demás vienen no disponibles con <c>portal_only</c>, y en uno con solicitante <c>Portal</c> viene con
/// <c>portal_unavailable</c>.</remarks>
public sealed record SupportReplyOptionsDto(
    SupportThreadOptionDto Thread,
    SupportChannelOptionDto WhatsAppTemplate,
    SupportChannelOptionDto Email,
    string DefaultChannel,
    SupportChannelOptionDto Portal,
    SupportFormalOptionsDto Formal);

/// <summary>
/// Fase 4b (H1, H3, H5, H10): lo que la pantalla necesita para ofrecer «Respuesta formal» sin conocer tipos de plantilla ni reglas de
/// firma. <c>Suggested</c> es el «exige respuesta formal» del tipo del ticket: la pantalla marca la casilla de entrada. <c>BlockedReason</c>
/// (<c>signer_required</c>: sin responsable que firme; <c>organization_required</c>: sin empresa para el membrete) apaga la respuesta formal
/// en todo canal. <c>ThreadReason</c> existe porque fuera de la ventana el hilo sale por otra plantilla («Respuesta formal»): puede
/// estar aprobada la de «Aviso de respuesta» y no ésta, y la pantalla no conoce los tipos (null: el hilo puede llevarla ahora).
/// <c>TemplateConnections</c> es el selector de la plantilla formal sin conversación: las conexiones con ella aprobada.
/// </summary>
public sealed record SupportFormalOptionsDto(
    bool Suggested,
    string? BlockedReason,
    string? ThreadReason,
    IReadOnlyList<SupportConnectionOptionDto> TemplateConnections);

/// <summary>El hilo de la conversación del ticket. <c>Delivery</c> ∈ <c>Text</c> (dentro de la ventana) o <c>Template</c> (fuera, en
/// WhatsApp: sale «Aviso de respuesta»), para que la pantalla avise antes de enviar. <c>Reason</c> cuando no está disponible:
/// <c>no_conversation</c>, <c>messaging_disabled</c>, <c>contact_blocked</c>, <c>window_closed</c> (Instagram) o
/// <c>template_not_approved</c>. <c>CanAttach</c> enciende el adjunto: sólo con entrega <c>Text</c> y si quien llama puede gestionar
/// tickets y abrir la sesión de subida de Mensajería (<c>messaging.conversation.manage</c>); la pantalla no conoce esos permisos.</summary>
public sealed record SupportThreadOptionDto(
    bool Available, string? Reason, string? Delivery, DateTimeOffset? WindowClosesAt, bool CanAttach);

/// <summary>La plantilla o el correo. <c>Reason</c>: <c>messaging_disabled</c>, <c>no_mobile</c>, <c>template_not_approved</c> (no hay
/// conexión con la plantilla aprobada) o <c>no_email</c>, y los del hilo cuando la plantilla va a la conversación del ticket.
/// <c>Connections</c> sólo trae algo para la plantilla sin conversación: el selector de conexión.</summary>
public sealed record SupportChannelOptionDto(bool Available, string? Reason, IReadOnlyList<SupportConnectionOptionDto> Connections);

public sealed record SupportConnectionOptionDto(Guid Id, string Name);

/// <summary>La respuesta recién creada (o la que ya existía con ese <c>clientId</c>) y el detalle del ticket con su traza y su estado
/// nuevos: la pantalla repinta las dos cosas con una sola llamada.</summary>
public sealed record SupportReplyResultDto(SupportReplyDto Reply, SupportTicketDetailDto Ticket);

/// <summary>Spec 2026-11-15 §5.4: an internal note. <c>Files</c> in the order they were sent; <c>Author</c> stays named after the
/// member is removed («Sin nombre» without a visible name).</summary>
public sealed record SupportTicketNoteDto(
    Guid Id, string Text, SupportMemberDto Author, DateTimeOffset CreatedAt, IReadOnlyList<SupportNoteFileDto> Files);

public sealed record SupportNoteFileDto(Guid FileId, string Name, string MimeType, long SizeBytes);

/// <summary>A page of notes, newest first; <c>Next</c> is null on the last one (same cursor shape as the ticket's file).</summary>
public sealed record SupportTicketNotesDto(IReadOnlyList<SupportTicketNoteDto> Items, SupportMessageCursorDto? Next);

internal static class SupportDtoMapping
{
    public static SupportTicketTypeDto ToDto(SupportTicketType type, bool inUse) => new(
        type.Id.Value,
        type.Name,
        type.TermBusinessDays,
        type.TermIsLegal,
        type.RequiresFormalResponse,
        type.IsPublished,
        type.IsActive,
        type.Position,
        type.Version,
        inUse,
        type.FormalNextStep,
        type.IsInternal);

    public static SupportSettingsDto ToDto(
        SupportTenantSettings settings, SupportUnassignedAlertDto unassignedAlert, SupportTaskNoticeDto taskNotice) => new(
        settings.DueSoonBusinessDays,
        SupportTenantSettings.MinDueSoonBusinessDays,
        SupportTenantSettings.MaxDueSoonBusinessDays,
        settings.Version,
        settings.FormalNextStep,
        settings.SurveyEnabled,
        settings.AutoCloseBusinessDays,
        SupportTenantSettings.MinAutoCloseBusinessDays,
        SupportTenantSettings.MaxAutoCloseBusinessDays,
        unassignedAlert,
        taskNotice);
}
