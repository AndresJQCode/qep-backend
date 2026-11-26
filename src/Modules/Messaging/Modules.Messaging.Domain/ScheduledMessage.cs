namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-11-09: ciclo de vida de un mensaje programado. Se guarda por nombre.</summary>
public enum ScheduledMessageStatus
{
    Scheduled,
    Sending,
    Sent,
    Failed,
    Cancelled,
}

/// <summary>El archivo ya subido a almacenamiento que acompaña a un mensaje programado.</summary>
public sealed record ScheduledMedia(string StorageKey, string MimeType, string? FileName, long SizeBytes, string? Sha256Hex);

/// <summary>Qué campos cambió realmente una edición, ya comparados contra lo guardado y normalizados.</summary>
public sealed record ScheduledEditResult(IReadOnlyList<string> ChangedFields);

/// <summary><c>{saludo}</c> en un mensaje programado: se resuelve al enviar, así que los topes miden con el saludo más largo.</summary>
public static class ScheduledText
{
    public const string GreetingToken = "{saludo}";

    public const int LongestGreetingLength = 13;

    public static int MeasuredLength(string text)
    {
        var tokens = CountTokens(text);
        return text.Length - (tokens * GreetingToken.Length) + (tokens * LongestGreetingLength);
    }

    public static string ResolveGreeting(string text, string greeting) => text.Replace(GreetingToken, greeting, StringComparison.Ordinal);

    private static int CountTokens(string text)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(GreetingToken, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += GreetingToken.Length;
        }

        return count;
    }
}

/// <summary>
/// Spec 2026-11-09: un mensaje de una conversación que sale solo a la hora elegida, mientras la ventana de 24 h siga abierta.
/// <see cref="Version"/> es el token de concurrencia. Las transiciones del usuario (<c>Edit</c>, <c>Cancel</c>, <c>SendNow</c>,
/// <c>Retry</c>, <c>Discard</c>) viven aquí; los guardas lanzan <see cref="InvalidOperationException"/> porque el handler consulta
/// <see cref="CanEdit"/> / <see cref="CanRetry"/> antes y responde el 409. Las de la máquina de envío (<c>MarkSending</c>,
/// <c>MarkSent</c>, <c>MarkFailed</c>) son internas.
/// </summary>
public sealed class ScheduledMessage
{
    /// <summary>Lo mínimo que debe faltar para la hora programada.</summary>
    public static readonly TimeSpan MinimumLead = TimeSpan.FromMinutes(1);

    /// <summary>Cuánto antes de que Meta cierre la ventana de 24 h deja de ofrecerse enviar.</summary>
    public static readonly TimeSpan WindowSafetyMargin = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan CustomerServiceWindow = TimeSpan.FromHours(24);

    private ScheduledMessage()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid ClientId { get; private set; }

    public MessageKind Kind { get; private set; }

    public bool IsVoiceNote { get; private set; }

    public string? Text { get; private set; }

    public string? Caption { get; private set; }

    public string? MediaStorageKey { get; private set; }

    public string? MediaMimeType { get; private set; }

    public string? MediaFileName { get; private set; }

    public long? MediaSizeBytes { get; private set; }

    public string? MediaSha256 { get; private set; }

    public DateTimeOffset ScheduledAt { get; private set; }

    public ScheduledMessageStatus Status { get; private set; }

    public Guid AuthorMemberId { get; private set; }

    public Guid CreatedByMemberId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? UpdatedByMemberId { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? CancelledByMemberId { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? SentMessageId { get; private set; }

    public string? FailureCode { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public long Version { get; private set; }

    public bool CanEdit => Status == ScheduledMessageStatus.Scheduled;

    public bool CanCancel => CanEdit;

    public bool CanSendNow => CanEdit;

    public bool CanRetry => Status == ScheduledMessageStatus.Failed;

    public bool CanDiscard => CanRetry;

    /// <summary>Ocupa el cupo de uno activo por conversación.</summary>
    public bool IsActive => Status is ScheduledMessageStatus.Scheduled or ScheduledMessageStatus.Sending or ScheduledMessageStatus.Failed;

    public static ScheduledMessage CreateText(
        Guid id, Guid tenantId, Guid conversationId, Guid clientId, string text, DateTimeOffset scheduledAt, Guid memberId, DateTimeOffset now)
    {
        var message = New(id, tenantId, conversationId, clientId, MessageKind.Text, scheduledAt, memberId, now);
        message.Text = text.Trim();
        return message;
    }

    public static ScheduledMessage CreateMedia(
        Guid id,
        Guid tenantId,
        Guid conversationId,
        Guid clientId,
        MessageKind kind,
        bool isVoiceNote,
        string? caption,
        ScheduledMedia media,
        DateTimeOffset scheduledAt,
        Guid memberId,
        DateTimeOffset now)
    {
        var message = New(id, tenantId, conversationId, clientId, kind, scheduledAt, memberId, now);
        message.IsVoiceNote = isVoiceNote;
        message.Caption = NormalizeCaption(caption);
        message.MediaStorageKey = media.StorageKey;
        message.MediaMimeType = media.MimeType;
        message.MediaFileName = media.FileName;
        message.MediaSizeBytes = media.SizeBytes;
        message.MediaSha256 = media.Sha256Hex;
        return message;
    }

    /// <summary>La hora programada va al menos <see cref="MinimumLead"/> adelante y no más allá de
    /// <c>lastInboundAt + 24 h − <see cref="WindowSafetyMargin"/></c>; sin mensaje entrante no hay ventana.</summary>
    public static void EnsureSchedulable(DateTimeOffset scheduledAt, DateTimeOffset? lastInboundAt, DateTimeOffset now)
    {
        if (scheduledAt < now + MinimumLead)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ScheduledTooSoon, "The scheduled time must be at least one minute ahead.");
        }

        if (lastInboundAt is not { } inbound || scheduledAt > inbound + CustomerServiceWindow - WindowSafetyMargin)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.ScheduledOutsideWindow, "The scheduled time is outside the 24-hour customer service window.");
        }
    }

    /// <summary>Si al enviar sigue valiendo la ventana, con el mismo margen con que se aceptó la hora.</summary>
    public static bool IsWindowOpenForSending(DateTimeOffset? lastInboundAt, DateTimeOffset now) =>
        lastInboundAt is { } inbound && inbound + CustomerServiceWindow - WindowSafetyMargin > now;

    /// <summary>Un campo omitido o <c>null</c> conserva lo guardado; el pie vacío o de espacios lo borra. Sólo cuenta como cambio lo que
    /// difiere después de normalizar. El autor cambia únicamente si cambió el texto o el pie.</summary>
    public ScheduledEditResult Edit(
        DateTimeOffset? scheduledAt,
        string? text,
        bool textProvided,
        string? caption,
        bool captionProvided,
        Guid memberId,
        DateTimeOffset now)
    {
        RequireStatus(CanEdit);
        var changed = new List<string>(3);

        if (scheduledAt is { } at && at != ScheduledAt)
        {
            ScheduledAt = at;
            changed.Add("scheduledAt");
        }

        if (textProvided && text is not null && text.Trim() is var newText && newText != Text)
        {
            Text = newText;
            changed.Add("text");
        }

        if (captionProvided && caption is not null && NormalizeCaption(caption) is var newCaption && newCaption != Caption)
        {
            Caption = newCaption;
            changed.Add("caption");
        }

        if (changed.Count == 0)
        {
            return new ScheduledEditResult(changed);
        }

        if (changed.Contains("text") || changed.Contains("caption"))
        {
            AuthorMemberId = memberId;
        }

        UpdatedByMemberId = memberId;
        UpdatedAt = now;
        Version++;
        return new ScheduledEditResult(changed);
    }

    public void Cancel(Guid memberId, DateTimeOffset now)
    {
        RequireStatus(CanCancel);
        Close(memberId, now);
    }

    /// <summary>«Enviar ya»: adelanta la hora a ahora para que el próximo ciclo lo tome.</summary>
    public void SendNow(DateTimeOffset now)
    {
        RequireStatus(CanSendNow);
        ScheduledAt = now;
        Version++;
    }

    /// <summary>Un fallido vuelve a la cola para salir ya.</summary>
    public void Retry(DateTimeOffset now)
    {
        RequireStatus(CanRetry);
        ScheduledAt = now;
        FailureCode = null;
        Status = ScheduledMessageStatus.Scheduled;
        Version++;
    }

    public void Discard(Guid memberId, DateTimeOffset now)
    {
        RequireStatus(CanDiscard);
        Close(memberId, now);
    }

    internal void MarkSending(DateTimeOffset now)
    {
        RequireStatus(Status == ScheduledMessageStatus.Scheduled);
        Status = ScheduledMessageStatus.Sending;
        Attempts++;
        LockedUntil = now;
        Version++;
    }

    internal void MarkSent(Guid sentMessageId, DateTimeOffset now)
    {
        RequireStatus(Status == ScheduledMessageStatus.Sending);
        Status = ScheduledMessageStatus.Sent;
        SentMessageId = sentMessageId;
        LockedUntil = null;
        UpdatedAt = now;
        Version++;
    }

    internal void MarkFailed(string failureCode, DateTimeOffset now)
    {
        RequireStatus(Status is ScheduledMessageStatus.Scheduled or ScheduledMessageStatus.Sending);
        Status = ScheduledMessageStatus.Failed;
        FailureCode = failureCode;
        LockedUntil = null;
        UpdatedAt = now;
        Version++;
    }

    private static ScheduledMessage New(
        Guid id, Guid tenantId, Guid conversationId, Guid clientId, MessageKind kind, DateTimeOffset scheduledAt, Guid memberId, DateTimeOffset now) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            ConversationId = conversationId,
            ClientId = clientId,
            Kind = kind,
            ScheduledAt = scheduledAt,
            Status = ScheduledMessageStatus.Scheduled,
            AuthorMemberId = memberId,
            CreatedByMemberId = memberId,
            CreatedAt = now,
            Version = 1,
        };

    private static string? NormalizeCaption(string? caption) => string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();

    private void Close(Guid memberId, DateTimeOffset now)
    {
        Status = ScheduledMessageStatus.Cancelled;
        CancelledByMemberId = memberId;
        CancelledAt = now;
        Version++;
    }

    private static void RequireStatus(bool allowed)
    {
        if (!allowed)
        {
            throw new InvalidOperationException("The scheduled message is not in a state that allows this change; the handler checks first.");
        }
    }
}
