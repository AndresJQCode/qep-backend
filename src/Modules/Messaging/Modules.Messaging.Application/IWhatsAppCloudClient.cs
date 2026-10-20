using System.Text.Json.Nodes;

namespace Modules.Messaging.Application;

public enum SendOutcome
{
    Sent,
    GraphError,
    Unconfirmed,

    /// <summary>La conexión no llegó a establecerse (rechazada, sin DNS): el request nunca salió y reintentar no puede duplicar nada.
    /// Quien no distingue este caso lo trata como <see cref="Unconfirmed"/>.</summary>
    NotSent,
}

/// <summary>§8.3: 2xx con <c>messages[0].id</c>; un error de Graph con su <c>code</c> y <c>title</c>; o nada
/// confirmado (timeout, 5xx, red).</summary>
public sealed record SendResult(SendOutcome Outcome, string? Wamid, int? Code, string? Title);

/// <summary>Copia chica de <c>GraphFailure</c> de Integrations: Messaging no lo referencia. Spec 2026-10-14 §4.8: <c>UserTitle</c> y
/// <c>UserMessage</c> son <c>error_user_title</c> y <c>error_user_msg</c> de Graph, lo único que distingue «nombre ya existe» del
/// resto de los 400 ahora que <c>error_subcode</c> no se devuelve. Sólo las operaciones de plantillas los llenan.</summary>
public sealed record MessagingGraphFailure(int HttpStatus, int? Code, string Reason, string? UserTitle = null, string? UserMessage = null);

/// <summary>Spec 2026-10-14 §4.4: lo que <c>POST /{waba}/message_templates</c> devuelve. La categoría puede diferir de la pedida.</summary>
public sealed record CreatedTemplate(string Id, string Status, string Category);

/// <summary>§4.4: <c>AlreadyGone</c> cuando Meta respondió que la plantilla ya no existe; el espejo la elimina igual (D-T13).</summary>
public sealed record DeletedTemplate(bool AlreadyGone);

public sealed record MessagingGraphResult<T>(T? Value, MessagingGraphFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>Revisión final F4: lo que trajo <c>GET /{waba_id}/message_templates</c>. <c>Complete = false</c> cuando el cliente cortó antes de
/// la última página (el tope de páginas o un <c>paging.next</c> que no sigue): lo que vino es cierto, lo que falta no dice nada.</summary>
public sealed record TemplateListing(IReadOnlyList<System.Text.Json.JsonElement> Templates, bool Complete);

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

    /// <summary>Messaging-media-reply: la misma subida, por stream y con el largo conocido de antes (un stream de R2 no lo dice y,
    /// sin él, el multipart saldría en chunked). <paramref name="fileName"/> es el del multipart: genérico y ASCII; el que ve la
    /// persona viaja en <see cref="SendDocumentAsync"/>. Va por su propio cliente, con 5 minutos.</summary>
    Task<MessagingGraphResult<string>> UploadMediaAsync(
        MessagingSender sender, Stream content, long contentLength, string mimeType, string fileName, CancellationToken cancellationToken);

    /// <summary>Messaging-media-reply R4: <c>type: "image"</c> con el id de la subida; <paramref name="caption"/> y
    /// <paramref name="contextWamid"/> sólo viajan si vienen. Mismo <see cref="SendResult"/> que el texto.</summary>
    Task<SendResult> SendImageAsync(
        MessagingSender sender, SendTarget target, string mediaId, string? caption, string callbackData, string? contextWamid, CancellationToken cancellationToken);

    /// <summary>Messaging-media-reply R4: <c>type: "document"</c>; <paramref name="fileName"/> es el nombre real que WhatsApp
    /// muestra (<c>document.filename</c>).</summary>
    Task<SendResult> SendDocumentAsync(
        MessagingSender sender, SendTarget target, string mediaId, string fileName, string? caption, string callbackData, string? contextWamid,
        CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-20 D5: <c>type: "video"</c>, como la imagen; <paramref name="caption"/> y <paramref name="contextWamid"/>
    /// sólo viajan si vienen.</summary>
    Task<SendResult> SendVideoAsync(
        MessagingSender sender, SendTarget target, string mediaId, string? caption, string callbackData, string? contextWamid, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §6.4: <c>type: "audio"</c>. Con <paramref name="voice"/> viaja <c>voice: true</c> y llega como nota de
    /// voz; sin él (un audio adjunto como archivo, spec 2026-10-20 D5) la clave no viaja y WhatsApp lo muestra como archivo. Sin
    /// <c>caption</c>: WhatsApp no lo admite en audio. Devuelve el mismo <see cref="SendResult"/> que el texto.</summary>
    Task<SendResult> SendAudioAsync(
        MessagingSender sender, SendTarget target, string mediaId, bool voice, string callbackData, string? contextWamid, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §5.1: <c>GET /{waba_id}/message_templates</c> con los campos que lee el normalizador, de a 100 y
    /// siguiendo <c>paging.next</c> hasta 10 páginas. Devuelve cada plantilla tal cual la mandó Meta (ya clonada, sin ligar al
    /// documento), para que <see cref="TemplateCatalog"/> la normalice. Una falla en cualquier página es la falla de todo; un corte por el
    /// tope de páginas o por un <c>paging.next</c> que no se sigue es éxito con <see cref="TemplateListing.Complete"/> en <c>false</c>.</summary>
    Task<MessagingGraphResult<TemplateListing>> ListTemplatesAsync(
        MessagingTemplateSource source, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §5.1: <c>type: "template"</c> por el mismo <c>MessagePayload</c> del texto. Devuelve el mismo
    /// <see cref="SendResult"/>. Sin cita: una plantilla no responde a nada.</summary>
    Task<SendResult> SendTemplateAsync(
        MessagingSender sender, SendTarget target, TemplateSend send, string callbackData, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-14 §4.4: <c>POST /{wabaId}/message_templates</c> con el payload completo (nombre, idioma, categoría,
    /// <c>parameter_format</c> y <c>components</c>). Una falla trae <c>UserTitle</c>/<c>UserMessage</c> para <c>TemplateGraphErrors</c>.</summary>
    Task<MessagingGraphResult<CreatedTemplate>> CreateTemplateAsync(MessagingTemplateSource source, JsonObject payload, CancellationToken cancellationToken);

    /// <summary>§4.4 (D-T11): <c>DELETE /{wabaId}/message_templates?hsm_id=&amp;name=</c>, sólo ese idioma. «No existe» cuenta como éxito con
    /// <c>AlreadyGone</c>.</summary>
    Task<MessagingGraphResult<DeletedTemplate>> DeleteTemplateAsync(MessagingTemplateSource source, string metaTemplateId, string name, CancellationToken cancellationToken);

    /// <summary>§4.5: la Resumable Upload del PDF de ejemplo con el <c>AppId</c> de <c>Meta:App</c>; devuelve el handle para
    /// <c>header_handle</c>. Sin <c>AppId</c> configurado falla con <c>no_app_id</c> sin llamar a Meta.</summary>
    Task<MessagingGraphResult<string>> UploadTemplateExampleAsync(
        MessagingTemplateSource source, ReadOnlyMemory<byte> content, string mimeType, string fileName, CancellationToken cancellationToken);

    /// <summary>§8.4: best effort, 5 s. <c>true</c> si Meta respondió 2xx.</summary>
    Task<bool> MarkReadAsync(MessagingSender sender, string wamid, CancellationToken cancellationToken);

    /// <summary>§8.6, paso 2: GET /{mediaId} → url (vence a los 5 min), mime_type, sha256, file_size.</summary>
    Task<MessagingGraphResult<MediaInfo>> GetMediaAsync(MessagingSender sender, string mediaId, CancellationToken cancellationToken);

    /// <summary>§8.6, paso 3: GET url con Authorization: Bearer, en stream.</summary>
    Task<Stream> OpenMediaAsync(MessagingSender sender, Uri url, CancellationToken cancellationToken);
}
