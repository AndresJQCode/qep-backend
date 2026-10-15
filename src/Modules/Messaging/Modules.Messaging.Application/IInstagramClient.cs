namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-14 §6.2: lo que el <i>User Profile API</i> de Instagram da de quien escribe (V-7: <c>username</c> puede no venir).</summary>
public sealed record InstagramProfile(string? Name, string? Username, string? ProfilePictureUrl);

/// <summary>Un adjunto entrante de Instagram abierto desde la URL pública que manda el webhook. El stream es de quien llama.</summary>
public sealed record InstagramDownload(Stream Content, string? ContentType, long? Length);

/// <summary>
/// Spec 2026-10-14 §6.2: el cliente de la Messaging API de Instagram (<c>graph.instagram.com</c>), con el contrato de respuesta
/// de <see cref="IWhatsAppCloudClient"/> (<see cref="SendResult"/>: 2xx con <c>message_id</c>; un error de Graph con su código;
/// o nada confirmado). No hay subida de medios: Meta descarga el adjunto desde la URL que se le da. Nunca registra el token ni
/// las URLs.
/// </summary>
public interface IInstagramClient
{
    /// <summary>§3: <c>POST /{v}/{igUserId}/messages</c> con <c>recipient.id</c> = IGSID y <c>message.text</c>.</summary>
    Task<SendResult> SendTextAsync(InstagramSender sender, string recipientId, string text, CancellationToken cancellationToken);

    /// <summary>§3: <c>message.attachment = { type, payload: { url } }</c>; <paramref name="attachmentType"/> es <c>image</c>, <c>audio</c>,
    /// <c>video</c> o <c>file</c>.</summary>
    Task<SendResult> SendAttachmentAsync(InstagramSender sender, string recipientId, string attachmentType, Uri url, CancellationToken cancellationToken);

    /// <summary>Decisión 14 (V-5): <c>sender_action: mark_seen</c>, best effort.</summary>
    Task<bool> MarkSeenAsync(InstagramSender sender, string recipientId, CancellationToken cancellationToken);

    /// <summary><c>GET /{v}/{igsid}?fields=name,username,profile_pic</c> con el token de la cuenta.</summary>
    Task<MessagingGraphResult<InstagramProfile>> GetProfileAsync(InstagramSender sender, string igsid, CancellationToken cancellationToken);

    /// <summary>§6.2: la URL pública (con vencimiento) de un adjunto entrante, sin token. <c>null</c> si no responde 2xx.</summary>
    Task<InstagramDownload?> OpenAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>Spec 2026-10-14 decisión 16: la nota de voz grabada por el asesor (OGG/Opus) pasa a WAV PCM 16 bits mono para
/// Instagram, que no acepta Opus. Adaptador en Infrastructure con un decodificador administrado.</summary>
public interface IOpusTranscoder
{
    /// <summary>Lanza <see cref="InvalidDataException"/> si el OGG no decodifica.</summary>
    byte[] ToWav(byte[] oggOpus);
}

/// <summary>Spec 2026-10-14 decisión 16 y §3: lo fijo del WAV que se le entrega a Instagram.</summary>
public static class InstagramAudio
{
    public const string WavMimeType = "audio/wav";

    public const string AttachmentType = "audio";

    /// <summary>§3: el tope de Meta para un audio en Instagram.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;
}
