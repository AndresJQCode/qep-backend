using Modules.Support.Domain;

namespace Modules.Support.Application;

// Contrato para las pantallas de qep-frontend (plan, «Contrato para las pantallas»). Los enums viajan por nombre: el diccionario
// de textos lo tiene el frontend.

/// <summary>Un tipo de ticket como lo dibujan el selector del alta y la tabla de ajustes. <c>Version</c> va en el <c>If-Match</c> del
/// <c>PUT</c>; <c>Position</c> es el orden en que la pantalla los lista. <c>InUse</c> (fase 4a, plan G6) dice si algún ticket, en
/// cualquier estado, usa el tipo: la tabla de ajustes ofrece «Eliminar» sólo si es falso, y si no, sólo «Desactivar», sin probar el
/// <c>DELETE</c> para enterarse del 422.</summary>
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
    bool InUse);

/// <summary>El umbral de «por vencer» del tenant [owner 2026-11-14]. Viaja con su rango (<c>Min</c> y <c>Max</c>) para que el
/// formulario de ajustes no lo repita; 0 significa «apagado». Un tenant que nunca guardó ve 3.</summary>
public sealed record SupportSettingsDto(int DueSoonBusinessDays, int MinDueSoonBusinessDays, int MaxDueSoonBusinessDays, long Version);

/// <summary>
/// La configuración vigente del formulario público (fase 3, plan F14). <c>Version</c> es la de la última fila (0 sin filas: el
/// formulario por defecto, sin publicar) y va en el <c>If-Match</c> del <c>PUT</c>. <c>PublishBlockers</c> dice por qué no se puede
/// publicar (<c>legal_texts_required</c>, <c>no_published_types</c>), así la pantalla lo explica sin adivinar; vacía si se puede.
/// <c>PublicUrl</c> es el enlace que el tenant comparte. <c>AllowAttachments</c> dice si la página ofrece adjuntos (spec §9.3; apagado
/// por defecto).
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
    IReadOnlyList<string> PublishBlockers);

/// <summary>
/// La versión vigente de los textos legales (fase 3, plan F16). Sin ninguna publicada, el texto base de QEP con <c>IsExample</c> en
/// verdadero, <c>Version</c> 0 y sin autor: la pantalla lo marca como ejemplo y nada se guarda hasta publicar.
/// </summary>
public sealed record SupportLegalTextsDto(
    int Version,
    bool IsExample,
    string AuthorizationText,
    string? PolicyText,
    string? PolicyUrl,
    string? TermsText,
    DateTimeOffset? CreatedAt,
    SupportMemberDto? CreatedBy);

/// <summary>Una página de la bandeja. <c>Summary</c> es del tenant entero, no de la página ni del filtro: las pestañas cuentan siempre
/// lo mismo, filtres lo que filtres.</summary>
public sealed record SupportTicketPageDto(
    IReadOnlyList<SupportTicketListItemDto> Items, int Total, int Page, int PageSize, SupportInboxSummaryDto Summary);

/// <summary><c>ByStatus</c> trae los seis estados, en el orden de la máquina, también en cero: las pestañas no conocen el enum.</summary>
public sealed record SupportInboxSummaryDto(IReadOnlyList<SupportStatusCountDto> ByStatus, int Overdue, int DueSoon);

public sealed record SupportStatusCountDto(string Status, int Count);

/// <summary>Una fila de la bandeja. <c>RequesterDisplayName</c> lo resuelve el backend (cliente vinculado; si no, nombres y
/// apellidos; si no, razón social; si no, «NIT número») para que la fila no repita la regla. <c>DueState</c> y
/// <c>BusinessDaysLeft</c> ya vienen evaluados con el calendario y el umbral del tenant.</summary>
public sealed record SupportTicketListItemDto(
    Guid Id,
    string Number,
    string Subject,
    string Status,
    string TypeName,
    string RequesterDisplayName,
    SupportMemberDto? Assignee,
    DateTimeOffset ReceivedAt,
    DateOnly? DueOn,
    string DueState,
    int? BusinessDaysLeft);

/// <summary>El detalle del ticket. <c>AllowedTransitions</c> sale de la máquina de estados del dominio: el selector de estado no la
/// repite. <c>Version</c> va en el <c>If-Match</c> de las acciones. <c>IssuedPin</c> (fase 4a, plan G3) trae el PIN de consulta sólo
/// en el <c>201</c> del alta manual anónima, para que el asesor se lo dicte a quien llamó o vino en persona: ese ticket no tiene a dónde
/// recibirlo. En toda otra respuesta es <c>null</c>.</summary>
public sealed record SupportTicketDetailDto(
    Guid Id,
    string Number,
    string Subject,
    string Description,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    SupportTicketTypeSnapshotDto Type,
    SupportClockDto Clock,
    string Intake,
    string? ManualMedium,
    string ReplyChannel,
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
    string? IssuedPin);

/// <summary>Un adjunto del formulario público (plan F37), en el orden en que se adjuntó. El archivo se baja con
/// <c>GET …/tickets/{id}/attachments/{attachmentId}/download</c>: la pantalla no necesita <c>storage.file.read</c>. Una lista vacía
/// si no tiene, nunca <c>null</c>.</summary>
public sealed record SupportAttachmentDto(Guid Id, string FileName, string MimeType, long SizeBytes);

/// <summary>La URL firmada de un adjunto: vive 5 minutos y baja el archivo con su nombre.</summary>
public sealed record SupportAttachmentDownloadDto(string Url);

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

/// <summary>Una respuesta del ticket (plan E16). <c>Channel</c> ∈ <c>Thread</c>, <c>WhatsAppTemplate</c>, <c>Email</c>, <c>Portal</c>
/// (fase 3, F12: publicada en la página de consulta); <c>Delivery</c> ∈ <c>Text</c>, <c>Template</c>, <c>Email</c>, <c>Portal</c> (la pantalla dice «salió como plantilla» sin conocer la ventana);
/// <c>Status</c> ∈ <c>Pending</c>, <c>Sent</c>, <c>Failed</c>, con <c>FailureCode</c> si falló. <c>ClientId</c> es el que mandó la
/// pantalla: con él retoma una respuesta que quedó <c>Pending</c> (revisión final, I3). <c>MessageId</c> también viene en una fallida si
/// Mensajería registró el mensaje.</summary>
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
    DateTimeOffset CreatedAt);

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
/// <c>TermIsLegal</c> decide si la pantalla dice «vence el plazo legal» o «vence el objetivo».</summary>
public sealed record SupportClockDto(
    DateOnly? DueOn, string DueState, int? BusinessDaysLeft, int? TermBusinessDays, bool TermIsLegal, int PausedBusinessDays);

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
    string? ReplyChannel);

// Fase 2: MessageId es el mensaje de MessagePinned, MessageReleased, MessageClassified, MessageDismissed y ReplySent; Follow, el nuevo
// valor de FollowThreadChanged; ReplyChannel, el canal de ReplySent. Planos por la misma razón que los de la fase 1.

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
    SupportChannelOptionDto Portal);

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
        inUse);

    public static SupportSettingsDto ToDto(SupportTenantSettings settings) => new(
        settings.DueSoonBusinessDays,
        SupportTenantSettings.MinDueSoonBusinessDays,
        SupportTenantSettings.MaxDueSoonBusinessDays,
        settings.Version);
}
