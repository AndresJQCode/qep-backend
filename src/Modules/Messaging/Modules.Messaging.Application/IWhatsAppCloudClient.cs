namespace Modules.Messaging.Application;

public enum SendOutcome
{
    Sent,
    GraphError,
    Unconfirmed,
}

/// <summary>§8.3: 2xx con <c>messages[0].id</c>; un error de Graph con su <c>code</c> y <c>title</c>; o nada
/// confirmado (timeout, 5xx, red).</summary>
public sealed record SendResult(SendOutcome Outcome, string? Wamid, int? Code, string? Title);

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
    Task<SendResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §6.4: <c>POST /{phone_number_id}/media</c> en multipart; devuelve el <c>id</c> del
    /// medio en Meta. Una falla lleva el status (0 = no hubo respuesta) y el <c>error.code</c> si vino.</summary>
    Task<MessagingGraphResult<string>> UploadMediaAsync(
        MessagingSender sender, ReadOnlyMemory<byte> content, string mimeType, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §6.4: <c>type: "audio"</c> con <c>voice: true</c>, para que llegue como nota de voz.
    /// Devuelve el mismo <see cref="SendResult"/> que el texto (D-V4: el nombre no se cambia en este trabajo).</summary>
    Task<SendResult> SendAudioAsync(
        MessagingSender sender, SendTarget target, string mediaId, string callbackData, string? contextWamid, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §5.1: <c>GET /{waba_id}/message_templates</c> con los campos que lee el normalizador, de a 100 y
    /// siguiendo <c>paging.next</c> hasta 10 páginas. Devuelve cada plantilla tal cual la mandó Meta (ya clonada, sin ligar al
    /// documento), para que <see cref="TemplateCatalog"/> la normalice. Una falla en cualquier página es la falla de todo.</summary>
    Task<MessagingGraphResult<IReadOnlyList<System.Text.Json.JsonElement>>> ListTemplatesAsync(
        MessagingTemplateSource source, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §5.1: <c>type: "template"</c> por el mismo <c>MessagePayload</c> del texto. Devuelve el mismo
    /// <see cref="SendResult"/>. Sin cita: una plantilla no responde a nada.</summary>
    Task<SendResult> SendTemplateAsync(
        MessagingSender sender, SendTarget target, TemplateSend send, string callbackData, CancellationToken cancellationToken);

    /// <summary>§8.4: best effort, 5 s. <c>true</c> si Meta respondió 2xx.</summary>
    Task<bool> MarkReadAsync(MessagingSender sender, string wamid, CancellationToken cancellationToken);

    /// <summary>§8.6, paso 2: GET /{mediaId} → url (vence a los 5 min), mime_type, sha256, file_size.</summary>
    Task<MessagingGraphResult<MediaInfo>> GetMediaAsync(MessagingSender sender, string mediaId, CancellationToken cancellationToken);

    /// <summary>§8.6, paso 3: GET url con Authorization: Bearer, en stream.</summary>
    Task<Stream> OpenMediaAsync(MessagingSender sender, Uri url, CancellationToken cancellationToken);
}
