using System.Text.RegularExpressions;

namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-09 §7.2: una persona (<c>wa_id</c>) hablando con un número de la organización
/// (<c>connection_id</c>). La ingesta de entrantes <b>no</b> pasa por acá (SQL atómico, §7.5), y marcar
/// leído tampoco (un UPDATE atómico, §8.4: con el token de concurrencia, un entrante en medio daría 412):
/// este agregado sólo resuelve y reabre, con <see cref="Version"/> como token de concurrencia.
/// La «foto» del último mensaje la escribe la ingesta; acá es de sólo lectura.
/// </summary>
public sealed partial class Conversation
{
    public const int WaIdMaxLength = 20;
    public const int ProfileNameMaxLength = 256;
    public const int PreviewMaxLength = 200;

    /// <summary>Spec 2026-10-10 §6.1.1: el BSUID mide hasta 131 (2 + 1 + 128); 150 deja margen.</summary>
    public const int UserIdMaxLength = 150;

    /// <summary>Meta: hasta 35; la columna deja margen sin costo.</summary>
    public const int UsernameMaxLength = 64;

    /// <summary>§8.3: 24 h desde el último mensaje de la persona.</summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(24);

    /// <summary>§8.4: Meta sólo marca leído dentro de 30 días desde la recepción.</summary>
    public static readonly TimeSpan ReadReceiptWindow = TimeSpan.FromDays(30);

    /// <summary>§6.1.1: código ISO 3166 alfa-2 + «.» + 1 a 128 alfanuméricos.</summary>
    public static bool IsValidUserId(string? value) => value is not null && UserIdShape().IsMatch(value);

    [GeneratedRegex("^[A-Z]{2}\\.[A-Za-z0-9]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdShape();

    private Conversation()
    {
        WaId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>Dígitos, sin «+», como lo manda Meta.</summary>
    public string WaId { get; private set; }

    public string? ProfileName { get; private set; }

    public ConversationStatus Status { get; private set; }

    public int UnreadCount { get; private set; }

    public DateTimeOffset? LastInboundAt { get; private set; }

    public string? LastInboundWamid { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public Guid? LastMessageId { get; private set; }

    public MessageDirection? LastMessageDirection { get; private set; }

    public MessageKind? LastMessageKind { get; private set; }

    public string? LastMessagePreview { get; private set; }

    public MessageStatus? LastMessageStatus { get; private set; }

    public DateTimeOffset? LastMessageAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset? CustomerWindowExpiresAt => LastInboundAt?.Add(WindowLength);

    public bool IsWindowOpen(DateTimeOffset now) => CustomerWindowExpiresAt is { } expiresAt && expiresAt > now;

    /// <summary>Para Meta: hay un último entrante y tiene menos de 30 días (§8.4). Estático porque marcar
    /// leído no carga el agregado: recibe lo que devuelve el UPDATE atómico.</summary>
    public static bool CanAcknowledgeReading(string? lastInboundWamid, DateTimeOffset? lastInboundAt, DateTimeOffset now) =>
        lastInboundWamid is not null && lastInboundAt is { } at && now - at < ReadReceiptWindow;

    /// <summary>Una conversación nueva, abierta y sin mensajes. En producción la crea la ingesta por SQL;
    /// esto lo usan las pruebas y el harness.</summary>
    public static Conversation Start(Guid id, Guid tenantId, Guid connectionId, string waId, string? profileName, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(waId) || waId.Length > WaIdMaxLength || !waId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A wa_id is 1 to 20 digits.", nameof(waId));
        }

        return new Conversation
        {
            Id = id,
            TenantId = tenantId,
            ConnectionId = connectionId,
            WaId = waId,
            ProfileName = Truncate(profileName, ProfileNameMaxLength),
            Status = ConversationStatus.Open,
            UnreadCount = 0,
            LastActivityAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Sólo pruebas: una conversación con contadores y ventana ya puestos, como los dejaría la ingesta.
    /// Internal (visible sólo para las pruebas): ningún código de producción puede saltarse las reglas.</summary>
    internal static Conversation ForTests(Conversation source, int unreadCount, DateTimeOffset? lastInboundAt, string? lastInboundWamid = "wamid.test")
    {
        source.UnreadCount = unreadCount;
        source.LastInboundAt = lastInboundAt;
        source.LastInboundWamid = lastInboundAt is null ? null : lastInboundWamid;
        return source;
    }

    public void Resolve(DateTimeOffset now)
    {
        if (Status == ConversationStatus.Resolved)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AlreadyResolved, "The conversation is already resolved.");
        }

        Status = ConversationStatus.Resolved;
        Touch(now);
    }

    public void Reopen(DateTimeOffset now)
    {
        if (Status == ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AlreadyOpen, "The conversation is already open.");
        }

        Status = ConversationStatus.Open;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
