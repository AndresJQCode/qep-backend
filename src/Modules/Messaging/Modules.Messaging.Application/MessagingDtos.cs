namespace Modules.Messaging.Application;

// BFF (CLAUDE.md): copia literal del contrato del frontend (spec 2026-10-09 §5.3). Enums por nombre
// (Inbound, Image, Read, Open). customerWindowExpiresAt viaja calculado para que la pantalla no sepa
// de las 24 h; connectionName y customer vienen resueltos por página para que la lista no pida nada
// aparte; MessageHit lleva conversationId, contact, customer y connectionName por lo mismo.

public sealed record ContactDto(string WaId, string? ProfileName);

public sealed record CustomerRefDto(Guid Id, string Name);

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
    string Status,
    int UnreadCount,
    LastMessageDto? LastMessage,
    DateTimeOffset? CustomerWindowExpiresAt,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary><c>Open</c> = conversaciones abiertas del tenant; <c>Unread</c> = la suma de
/// <c>unread_count</c> del tenant (§5.4: la pantalla lo dibuja como «N sin leer»). Ninguno respeta la
/// búsqueda: son los números de las pestañas, no de la página.</summary>
public sealed record ConversationCountsDto(int Open, int Unread);

/// <summary><c>Total</c> es exacto: la consulta infinita del frontend se detiene en
/// <c>page * pageSize &gt;= total</c>.</summary>
public sealed record ConversationPageDto(IReadOnlyList<ConversationSummary> Items, int Total, int Page, int PageSize, ConversationCountsDto Counts);

public sealed record MediaDto(string Url, string MimeType, string? FileName, string? Caption);

public sealed record LocationDto(double Latitude, double Longitude, string? Name, string? Address);

public sealed record SentByDto(Guid MemberId, string? DisplayName);

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
    SentByDto? SentBy,
    Guid? ClientId);

public sealed record MessagePageDto(IReadOnlyList<MessageDto> Items, bool HasMore);

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
    SentByDto? SentBy,
    Guid? ClientId,
    Guid ConversationId,
    ContactDto Contact,
    CustomerRefDto? Customer,
    string ConnectionName);

public sealed record SearchPageDto(IReadOnlyList<MessageHitDto> Items, bool HasMore);

public sealed record MediaStreamDto(Stream Content, string ContentType, long Length, string? FileName);
