namespace Modules.Messaging.Application;

public enum SendOutcome
{
    Sent,
    GraphError,
    Unconfirmed,
}

/// <summary>§8.3: 2xx con <c>messages[0].id</c>; un error de Graph con su <c>code</c> y <c>title</c>; o nada
/// confirmado (timeout, 5xx, red).</summary>
public sealed record SendTextResult(SendOutcome Outcome, string? Wamid, int? Code, string? Title);

/// <summary>Copia chica de <c>GraphFailure</c> de Integrations: Messaging no lo referencia.</summary>
public sealed record MessagingGraphFailure(int HttpStatus, int? Code, string Reason);

public sealed record MessagingGraphResult<T>(T? Value, MessagingGraphFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public sealed record MediaInfo(Uri Url, string MimeType, string? Sha256, long FileSize);

/// <summary>Decisión 3: el cliente de Graph de Messaging (<c>messaging.meta-graph</c>). Nunca registra el token ni la URL firmada.</summary>
public interface IWhatsAppCloudClient
{
    /// <summary>§8.3 y spec 2026-10-10 §6.1.4: <paramref name="contextWamid"/> = el <c>wamid</c> citado, o <c>null</c>.</summary>
    Task<SendTextResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken);

    /// <summary>§8.4: best effort, 5 s. <c>true</c> si Meta respondió 2xx.</summary>
    Task<bool> MarkReadAsync(MessagingSender sender, string wamid, CancellationToken cancellationToken);

    /// <summary>§8.6, paso 2: GET /{mediaId} → url (vence a los 5 min), mime_type, sha256, file_size.</summary>
    Task<MessagingGraphResult<MediaInfo>> GetMediaAsync(MessagingSender sender, string mediaId, CancellationToken cancellationToken);

    /// <summary>§8.6, paso 3: GET url con Authorization: Bearer, en stream.</summary>
    Task<Stream> OpenMediaAsync(MessagingSender sender, Uri url, CancellationToken cancellationToken);
}
