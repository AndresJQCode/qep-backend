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
public sealed record CustomerRefDto(Guid Id, string Name, bool IsComplete);

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
    long Version);

/// <summary><c>Open</c> = conversaciones abiertas del tenant; <c>Unread</c> = la suma de
/// <c>unread_count</c> del tenant (§5.4: la pantalla lo dibuja como «N sin leer»). Ninguno respeta la
/// búsqueda: son los números de las pestañas, no de la página. <c>Mine</c> y <c>Unassigned</c> cuentan sólo
/// abiertas (D-A10): son las pestañas de la cola de trabajo.</summary>
public sealed record ConversationCountsDto(int Open, int Unread, int Mine, int Unassigned);

/// <summary><c>Total</c> es exacto: la consulta infinita del frontend se detiene en
/// <c>page * pageSize &gt;= total</c>.</summary>
public sealed record ConversationPageDto(IReadOnlyList<ConversationSummary> Items, int Total, int Page, int PageSize, ConversationCountsDto Counts);

public sealed record MediaDto(string Url, string MimeType, string? FileName, string? Caption);

public sealed record LocationDto(double Latitude, double Longitude, string? Name, string? Address);

/// <summary>Una membresía con nombre: <c>sentBy</c> y los <c>actor</c>/<c>target</c>/<c>previous</c> de un evento (P11).
/// <c>DisplayName</c> nunca es <c>null</c>: la que ya no está viaja como «Miembro eliminado» (D-M20).</summary>
public sealed record MemberRefDto(Guid MemberId, string DisplayName);

/// <summary>Spec 2026-10-10 §5.1 y D-A13. BFF: <c>isMe</c> lo calcula el servidor porque la SPA no conoce su
/// <c>memberId</c> (<c>/auth/me</c> y <c>/authorization/me</c> sólo dan el <c>userId</c>); sin él, la pantalla no sabe
/// si mostrar el compositor o «La tiene X — Tomar».</summary>
public sealed record AssignedToDto(Guid MemberId, string DisplayName, bool IsMe);

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
    Guid? ClientId);

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
    string ConnectionName);

public sealed record SearchPageDto(IReadOnlyList<MessageHitDto> Items, bool HasMore);

public sealed record MediaStreamDto(Stream Content, string ContentType, long Length, string? FileName);
