using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

// BFF (CLAUDE.md): copia literal del contrato del frontend (spec 2026-10-09 §5.3). Enums por nombre
// (Inbound, Image, Read, Open). customerWindowExpiresAt viaja calculado para que la pantalla no sepa
// de las 24 h; connectionName y customer vienen resueltos por página para que la lista no pida nada
// aparte; MessageHit lleva conversationId, contact, customer y connectionName por lo mismo.

/// <summary>Spec 2026-10-10 §5.1: <c>userId</c> es <c>null</c> sólo en una conversación vieja sin entrante con BSUID;
/// <c>waId</c> es <c>null</c> cuando Meta no mandó el teléfono. Al menos uno viene (CK_conversations_identity).</summary>
public sealed record ContactDto(string? UserId, string? WaId, string? Username, string? ProfileName);

/// <summary>Spec 2026-10-10 §5.1 (D-A8): <c>isComplete</c> viaja para que el encabezado del hilo ofrezca «Completar
/// ficha» sin otra llamada.</summary>
public sealed record CustomerRefDto(Guid Id, string Name, bool IsComplete)
{
    /// <summary>Spec qep-frontend 2026-10-15 (BFF): los chips de la fila de la bandeja. Siempre viaja, vacía si no tiene: la
    /// pantalla no sabe qué etiquetas tiene cada cliente sin otra llamada por fila.</summary>
    public IReadOnlyList<MessagingTagRef> Tags { get; init; } = [];
}

public sealed record LastMessageDto(string Direction, string Kind, string? Preview, string Status, DateTimeOffset At);

/// <summary><c>connectionName</c> llega resuelto (o «Conexión eliminada», D-M17) porque la fila de la
/// lista lo dibuja y la pantalla no tiene cómo pedirlo sin una llamada por conversación.
/// <c>customerWindowExpiresAt</c> = último entrante + 24 h, calculado acá: la regla de las 24 h es de
/// Meta y del backend, no de la pantalla.</summary>
public sealed record ConversationSummary(
    Guid Id,
    Guid ConnectionId,
    string ConnectionName,
    ContactDto Contact,
    CustomerRefDto? Customer,
    AssignedToDto? AssignedTo,
    string Status,
    int UnreadCount,
    LastMessageDto? LastMessage,
    DateTimeOffset? CustomerWindowExpiresAt,
    DateTimeOffset UpdatedAt,
    long Version,
    /// <summary>Spec 2026-10-14 §5.2 (BFF): viaja aunque se deduzca de <c>connectionId</c>, porque la fila dibuja el ícono del canal
    /// y el compositor decide qué ofrecer, y ninguno de los dos tiene la conexión a mano.</summary>
    string Channel = nameof(ConversationChannel.WhatsApp),
    /// <summary>Spec 2026-10-15 D-R7 (BFF): el motivo de la resolución vigente, sólo con <c>status = Resolved</c>. Viaja en el resumen
    /// aunque esté en el historial porque el encabezado del hilo dibuja «Resuelta · Venta cerrada» y la fila de resueltas puede mostrarlo
    /// sin pedir los mensajes. <c>null</c> si está abierta o se resolvió sin motivo.</summary>
    ResolutionReasonRefDto? ResolutionReason = null,
    /// <summary>Spec 2026-10-21 D-A10 (BFF): <c>null</c> cuando la conversación no tiene asistente (<c>None</c>): la bandeja no dibuja nada,
    /// y así la fila sin asistente no obliga a la pantalla a conocer el enum para saber que no hay qué mostrar.</summary>
    ConversationAutomationDto? Automation = null);

/// <summary>Spec 2026-10-21 §4.5: el asistente de la conversación. <c>Status</c> ∈ Active|Paused, por nombre. <c>PausedAt</c>,
/// <c>PausedBy</c> y <c>PauseReason</c> sólo vienen pausada; <c>PausedBy</c> es <c>null</c> si lo pausó el sistema, y si la membresía
/// ya no está sale como «Miembro eliminado», como <c>AssignedTo</c>.</summary>
public sealed record ConversationAutomationDto(string Status, DateTimeOffset StartedAt, DateTimeOffset? PausedAt, MemberRefDto? PausedBy, string? PauseReason);

/// <summary><c>Open</c> = conversaciones abiertas del tenant; <c>Unread</c> = la suma de
/// <c>unread_count</c> del tenant (§5.4: la pantalla lo dibuja como «N sin leer»). Ninguno respeta la
/// búsqueda: son los números de las pestañas, no de la página. <c>Mine</c> y <c>Unassigned</c> cuentan sólo
/// abiertas (D-A10): son las pestañas de la cola de trabajo. <c>Automated</c> = abiertas con asistente (activo o pausado); es una
/// colección de tamaño fijo del BFF: viaja siempre, también en cero, para que la pestaña dibuje «0» sin lógica propia.</summary>
public sealed record ConversationCountsDto(int Open, int Unread, int Mine, int Unassigned, int Automated);

/// <summary><c>Total</c> es exacto: la consulta infinita del frontend se detiene en
/// <c>page * pageSize &gt;= total</c>.</summary>
/// <summary>La bandeja se recorre con scroll infinito, no por número de página: <c>nextCursor</c> es la posición de la última
/// fila (o <c>null</c> si no hay más) y el cliente lo devuelve tal cual en <c>cursor</c>. Con OFFSET, una conversación que sube
/// al tope mientras se recorre la lista corre todas una posición y la del borde sale repetida en la página siguiente. No hay
/// <c>total</c>: la pantalla no lo dibuja (los números de las pestañas son <c>counts</c>) y sólo servía para saber si seguir.</summary>
public sealed record ConversationPageDto(IReadOnlyList<ConversationSummary> Items, string? NextCursor, ConversationCountsDto Counts);

public sealed record MediaDto(string Url, string MimeType, string? FileName, string? Caption);

public sealed record LocationDto(double Latitude, double Longitude, string? Name, string? Address);

/// <summary>Una membresía con nombre: <c>sentBy</c> y los <c>actor</c>/<c>target</c>/<c>previous</c> de un evento (P11).
/// <c>DisplayName</c> nunca es <c>null</c>: la que ya no está viaja como «Miembro eliminado» (D-M20).</summary>
public sealed record MemberRefDto(Guid MemberId, string DisplayName);

/// <summary>Spec 2026-10-10 §5.1 y D-A13. BFF: <c>isMe</c> lo calcula el servidor porque la SPA no conoce su
/// <c>memberId</c> (<c>/auth/me</c> y <c>/authorization/me</c> sólo dan el <c>userId</c>); sin él, la pantalla no sabe
/// si mostrar el compositor o «La tiene X — Tomar».</summary>
public sealed record AssignedToDto(Guid MemberId, string DisplayName, bool IsMe);

/// <summary>Spec 2026-10-15 §3.5: un motivo de resolución con su nombre ya resuelto, incluido el de uno archivado (D-R5).</summary>
public sealed record ResolutionReasonRefDto(Guid Id, string Name);

/// <summary>Spec 2026-10-10 §5.1: lo que la burbuja dibuja del citado. Sin nombre de quien lo envió: la pantalla dice
/// «Cliente» o «Equipo» por <c>direction</c> (P21).</summary>
public sealed record ReplyToDto(Guid Id, string Direction, string Kind, string? Preview);

/// <summary>§5.1 y §6.1.5: <c>actor</c> es null cuando lo hizo el sistema.</summary>
public sealed record MessageEventDto(string Type, MemberRefDto? Actor, MemberRefDto? Target, MemberRefDto? Previous, ResolutionReasonRefDto? Reason = null, string? PauseReason = null);

/// <param name="UnsupportedReason">Sólo en un <c>Unsupported</c> cuyo error de Meta tiene texto (<c>131060</c>, <c>131051</c>);
/// si no, <c>null</c> y la burbuja dibuja la etiqueta del kind. BFF: viaja en español, como <c>failureReason</c>, porque
/// «Mensaje no compatible» no le dice al asesor que el cliente sí escribió y que hay que pedirle que reenvíe; el
/// frontend no tendría con qué saberlo sin conocer los códigos de Meta.</param>
public sealed record MessageDto(
    Guid Id,
    string Direction,
    string Kind,
    string? Text,
    MediaDto? Media,
    LocationDto? Location,
    string Status,
    string? FailureReason,
    DateTimeOffset At,
    MemberRefDto? SentBy,
    Guid? ClientId,
    ReplyToDto? ReplyTo,
    MessageEventDto? Event,
    MessageTemplateRefDto? Template = null,
    string? UnsupportedReason = null,
    /// <summary>Spec 2026-10-14 §5.2: la persona borró el mensaje en Instagram (<c>is_deleted</c>). El texto se conserva; la burbuja
    /// lo tacha.</summary>
    bool Deleted = false);

/// <summary>Spec 2026-10-11 §5.2: con qué plantilla salió un mensaje <c>Template</c>. El texto ya trae las variables
/// sustituidas; esto sólo deja a la burbuja rotularlo («Plantilla · nombre»).</summary>
public sealed record MessageTemplateRefDto(string Name, string Language);

public sealed record MessagePageDto(IReadOnlyList<MessageDto> Items, bool HasMore);

/// <summary>Un <c>Message</c> (§8.7) más su conversación, contacto, cliente y conexión (§8.8). BFF: la lista
/// de resultados dibuja de quién es cada mensaje y abre su conversación sin pedir nada aparte; por eso estos
/// cuatro viajan resueltos por página y no como un <c>conversationId</c> pelado que obligaría a una llamada
/// por resultado.</summary>
public sealed record MessageHitDto(
    Guid Id,
    string Direction,
    string Kind,
    string? Text,
    MediaDto? Media,
    LocationDto? Location,
    string Status,
    string? FailureReason,
    DateTimeOffset At,
    MemberRefDto? SentBy,
    Guid? ClientId,
    Guid ConversationId,
    ContactDto Contact,
    CustomerRefDto? Customer,
    string ConnectionName,
    ReplyToDto? ReplyTo,
    MessageEventDto? Event);

public sealed record SearchPageDto(IReadOnlyList<MessageHitDto> Items, bool HasMore);

/// <param name="Range">Spec 2026-10-20 D10: el tramo servido cuando el request trajo un <c>Range</c> que se entendió; <c>null</c> = el
/// archivo entero (200).</param>
public sealed record MediaStreamDto(Stream Content, string ContentType, long Length, string? FileName, MediaRangeDto? Range = null);
