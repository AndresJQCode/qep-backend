using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Media;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.Meta;

/// <summary>Decisión 3: <c>messaging.meta-graph</c> con el patrón de <c>ZenviaConnectionTester</c>. Registra
/// status, <c>error.code</c>, <c>error_subcode</c> y <c>fbtrace_id</c>; nunca el token, el cuerpo, el texto del
/// mensaje ni la URL firmada.</summary>
internal sealed partial class WhatsAppCloudClient(
    IHttpClientFactory httpClientFactory,
    IOptions<MessagingMetaOptions> options,
    ILogger<WhatsAppCloudClient> logger) : IWhatsAppCloudClient
{
    public const string HttpClientName = "messaging.meta-graph";
    public const string BaseUrl = "https://graph.facebook.com/";
    public const string DefaultGraphApiVersion = "v24.0";
    public static readonly TimeSpan ReadReceiptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>La subida de una nota de voz (hasta 16 MB) va por su propio cliente: en los 10 s del compartido, una red
    /// lenta la cortaría y la nota quedaría Failed por Unconfirmed sin que Meta haya contestado. El texto, el leído y la
    /// descarga de medios siguen en <see cref="HttpClientName"/>.</summary>
    public const string UploadHttpClientName = "messaging.meta-graph-upload";

    public static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Messaging-media-reply: imágenes y documentos de hasta 100 MB, de R2 a Meta dentro del request. Ni los 10 s del
    /// compartido ni los 30 s de la nota de voz alcanzan para un documento grande por una red lenta.</summary>
    public const string MediaHttpClientName = "messaging.meta-graph-media";

    public static readonly TimeSpan MediaUploadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Spec 2026-10-11 §6.4: el nombre del archivo en la subida. Meta no lo muestra en una nota de voz.</summary>
    public const string VoiceFileName = "voice.ogg";

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph {Operation} answered HTTP {StatusCode} (code {Code}, subcode {Subcode}, fbtrace {FbTraceId})")]
    private static partial void LogAnswered(ILogger logger, string operation, int statusCode, int? code, int? subcode, string? fbTraceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graph {Operation} could not be reached: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    internal static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-messaging");
    }

    internal static void ConfigureUploadClient(HttpClient client)
    {
        ConfigureClient(client);
        client.Timeout = UploadTimeout;
    }

    internal static void ConfigureMediaClient(HttpClient client)
    {
        ConfigureClient(client);
        client.Timeout = MediaUploadTimeout;
    }

    private string Version => string.IsNullOrWhiteSpace(options.Value.GraphApiVersion) ? DefaultGraphApiVersion : options.Value.GraphApiVersion.Trim();

    public Task<SendResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken) =>
        PostMessageAsync(sender, MessagePayload(target, "text", new JsonObject { ["body"] = body }, callbackData, contextWamid), cancellationToken);

    public Task<SendResult> SendInteractiveAsync(
        MessagingSender sender, SendTarget target, InteractiveSend send, string callbackData, string? contextWamid, CancellationToken cancellationToken) =>
        PostMessageAsync(sender, MessagePayload(target, "interactive", InteractiveSendJson.ToMetaObject(send), callbackData, contextWamid), cancellationToken);

    public Task<SendResult> SendAudioAsync(
        MessagingSender sender, SendTarget target, string mediaId, bool voice, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        var content = new JsonObject { ["id"] = mediaId };
        if (voice)
        {
            content["voice"] = true;
        }

        return PostMessageAsync(sender, MessagePayload(target, "audio", content, callbackData, contextWamid), cancellationToken);
    }

    /// <summary>Spec 2026-10-11 §5.1: tope de páginas de <c>message_templates</c>. Con 100 por página son 1000 plantillas, el
    /// máximo que Meta deja tener por cuenta.</summary>
    public const int MaxTemplatePages = 10;

    /// <summary>Spec 2026-10-14 §4.4: <c>id</c> y <c>rejected_reason</c> alimentan el espejo.</summary>
    public const string TemplateFields = "id,name,language,status,category,components,parameter_format,rejected_reason";

    public async Task<MessagingGraphResult<TemplateListing>> ListTemplatesAsync(MessagingTemplateSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var templates = new List<JsonElement>();
        // Revisión final F4: sólo es completo si la última página leída no traía un paging.next (ni uno que se descartó).
        var complete = false;
        var next = new Uri(
            $"{Version}/{Uri.EscapeDataString(source.WabaId)}/message_templates?fields={TemplateFields}&limit=100", UriKind.Relative);
        for (var page = 0; page < MaxTemplatePages && next is not null; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            var answer = await SendAsync("templates", request, source.AccessToken, null, cancellationToken);
            using var response = answer.Response;
            if (response is null)
            {
                return new MessagingGraphResult<TemplateListing>(null, new MessagingGraphFailure(0, null, answer.UnreachableReason ?? "unreachable"));
            }

            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new MessagingGraphResult<TemplateListing>(null, new MessagingGraphFailure(status, answer.Error?.Code, $"http_{status}"));
            }

            try
            {
                using var document = JsonDocument.Parse(answer.Body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    return new MessagingGraphResult<TemplateListing>(null, new MessagingGraphFailure(status, null, "unreadable"));
                }

                templates.AddRange(data.EnumerateArray().Select(template => template.Clone()));
                next = NextPage(root, out var declined);
                complete = next is null && !declined;
            }
            catch (JsonException)
            {
                return new MessagingGraphResult<TemplateListing>(null, new MessagingGraphFailure(status, null, "unreadable"));
            }
        }

        return new MessagingGraphResult<TemplateListing>(new TemplateListing(templates, complete), null);
    }

    /// <summary><c>paging.next</c> es una URL absoluta y firmada de Graph. Sólo se sigue si es https y del mismo host que el
    /// cliente: el token viaja en cada request y no debe salir de graph.facebook.com por lo que diga el cuerpo. <paramref name="declined"/>
    /// es <c>true</c> si había un <c>paging.next</c> y no se va a seguir: el listado queda incompleto (revisión final F4).</summary>
    private static Uri? NextPage(JsonElement root, out bool declined)
    {
        declined = false;
        if (!root.TryGetProperty("paging", out var paging) || paging.ValueKind != JsonValueKind.Object
            || !paging.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(next.GetString()))
        {
            return null;
        }

        if (Uri.TryCreate(next.GetString(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, new Uri(BaseUrl).Host, StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        declined = true;
        return null;
    }

    public Task<SendResult> SendTemplateAsync(
        MessagingSender sender, SendTarget target, TemplateSend send, string callbackData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var content = new JsonObject
        {
            ["name"] = send.Name,
            ["language"] = new JsonObject { ["code"] = send.Language },
        };
        var components = new JsonArray();
        if (send.HeaderDocument is { } header)
        {
            // El encabezado va primero: Meta lo ata al de la plantilla aprobada por el orden de los componentes.
            components.Add(new JsonObject
            {
                ["type"] = "header",
                ["parameters"] = new JsonArray(new JsonObject
                {
                    ["type"] = "document",
                    ["document"] = new JsonObject { ["id"] = header.MediaId, ["filename"] = header.FileName },
                }),
            });
        }

        if (send.Parameters.Count > 0)
        {
            var parameters = new JsonArray();
            foreach (var parameter in send.Parameters)
            {
                var item = new JsonObject { ["type"] = "text", ["text"] = parameter.Value };
                if (send.Named)
                {
                    item["parameter_name"] = parameter.Key;
                }

                parameters.Add(item);
            }

            components.Add(new JsonObject { ["type"] = "body", ["parameters"] = parameters });
        }

        // Meta pide el índice como texto y sólo el sufijo del enlace: el resto ya está en la plantilla aprobada.
        foreach (var button in send.UrlButtons ?? [])
        {
            components.Add(new JsonObject
            {
                ["type"] = "button",
                ["sub_type"] = "url",
                ["index"] = button.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["parameters"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = button.Value }),
            });
        }

        if (components.Count > 0)
        {
            content["components"] = components;
        }

        return PostMessageAsync(sender, MessagePayload(target, "template", content, callbackData, null), cancellationToken);
    }

    public Task<SendResult> SendImageAsync(
        MessagingSender sender, SendTarget target, string mediaId, string? caption, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        var content = new JsonObject { ["id"] = mediaId };
        if (caption is not null)
        {
            content["caption"] = caption;
        }

        return PostMessageAsync(sender, MessagePayload(target, "image", content, callbackData, contextWamid), cancellationToken);
    }

    public Task<SendResult> SendVideoAsync(
        MessagingSender sender, SendTarget target, string mediaId, string? caption, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        var content = new JsonObject { ["id"] = mediaId };
        if (caption is not null)
        {
            content["caption"] = caption;
        }

        return PostMessageAsync(sender, MessagePayload(target, "video", content, callbackData, contextWamid), cancellationToken);
    }

    public Task<SendResult> SendDocumentAsync(
        MessagingSender sender, SendTarget target, string mediaId, string fileName, string? caption, string callbackData, string? contextWamid,
        CancellationToken cancellationToken)
    {
        var content = new JsonObject { ["id"] = mediaId, ["filename"] = fileName };
        if (caption is not null)
        {
            content["caption"] = caption;
        }

        return PostMessageAsync(sender, MessagePayload(target, "document", content, callbackData, contextWamid), cancellationToken);
    }

    public Task<MessagingGraphResult<string>> UploadMediaAsync(
        MessagingSender sender, ReadOnlyMemory<byte> content, string mimeType, CancellationToken cancellationToken) =>
        UploadAsync(sender, new ReadOnlyMemoryContent(content), mimeType, VoiceFileName, UploadHttpClientName, cancellationToken);

    public Task<MessagingGraphResult<string>> UploadMediaAsync(
        MessagingSender sender, Stream content, long contentLength, string mimeType, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return UploadAsync(sender, new SizedStreamContent(content, contentLength), mimeType, fileName, MediaHttpClientName, cancellationToken);
    }

    private async Task<MessagingGraphResult<string>> UploadAsync(
        MessagingSender sender, HttpContent file, string mimeType, string fileName, string clientName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("whatsapp"), "messaging_product" },
            { new StringContent(mimeType), "type" },
            { file, "file", fileName },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/media") { Content = form };
        var answer = await SendAsync("upload", request, sender.AccessToken, null, cancellationToken, clientName);
        using var response = answer.Response;
        if (response is null)
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(0, null, answer.UnreachableReason ?? "unreachable"));
        }

        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(status, answer.Error?.Code, $"http_{status}"));
        }

        var id = ReadId(answer.Body);
        if (string.IsNullOrEmpty(id))
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(status, null, "no_id"));
        }

        // Review Focus 2: meta_media_id es varchar(64). Un id que no cabe haría fallar el commit después de enviar.
        return MediaTransfer.IsValidMetaMediaId(id)
            ? new MessagingGraphResult<string>(id, null)
            : new MessagingGraphResult<string>(null, new MessagingGraphFailure(status, null, "invalid_id"));
    }

    /// <summary>Spec 2026-10-10 §6.1.4: con <c>JsonObject</c> la clave ausente no viaja (ni <c>to</c> con BSUID ni
    /// <c>recipient</c> sin él). El orden de los campos es el de siempre: el texto no cambia por compartir esto.</summary>
    private static JsonObject MessagePayload(SendTarget target, string type, JsonObject content, string callbackData, string? contextWamid)
    {
        ArgumentNullException.ThrowIfNull(target);
        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["type"] = type,
            [type] = content,
            ["biz_opaque_callback_data"] = callbackData,
        };
        switch (target)
        {
            case SendToUserId byUserId:
                payload["recipient"] = byUserId.UserId;
                break;
            case SendToPhone byPhone:
                payload["to"] = byPhone.WaId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown send target.");
        }

        if (contextWamid is not null)
        {
            payload["context"] = new JsonObject { ["message_id"] = contextWamid };
        }

        return payload;
    }

    private async Task<SendResult> PostMessageAsync(MessagingSender sender, JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var answer = await SendAsync("send", request, sender.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new SendResult(answer.UnreachableReason == "not-sent" ? SendOutcome.NotSent : SendOutcome.Unconfirmed, null, null, null);
        }

        // §8.3: un 5xx no confirma nada (Meta pudo haberlo aceptado); un 4xx es un rechazo con su código.
        if ((int)response.StatusCode >= 500)
        {
            return new SendResult(SendOutcome.Unconfirmed, null, null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SendResult(SendOutcome.GraphError, null, answer.Error?.Code, answer.Error?.Title);
        }

        var wamid = ReadWamid(answer.Body);
        return wamid is null
            ? new SendResult(SendOutcome.Unconfirmed, null, null, null)
            : new SendResult(SendOutcome.Sent, wamid, null, null);
    }

    public async Task<bool> MarkReadAsync(MessagingSender sender, string wamid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(new { messaging_product = "whatsapp", status = "read", message_id = wamid }),
        };
        var answer = await SendAsync("read", request, sender.AccessToken, ReadReceiptTimeout, cancellationToken);
        using var response = answer.Response;
        return response?.IsSuccessStatusCode == true;
    }

    public async Task<MessagingGraphResult<MediaInfo>> GetMediaAsync(MessagingSender sender, string mediaId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Version}/{mediaId}");
        var answer = await SendAsync("media", request, sender.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new MessagingGraphResult<MediaInfo>(null, new MessagingGraphFailure(0, null, answer.UnreachableReason ?? "unreachable"));
        }

        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<MediaInfo>(null, new MessagingGraphFailure(status, answer.Error?.Code, $"http_{status}"));
        }

        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var root = document.RootElement;
            var url = root.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() : null;
            if (url is null)
            {
                return new MessagingGraphResult<MediaInfo>(null, new MessagingGraphFailure(status, null, "no_url"));
            }

            return new MessagingGraphResult<MediaInfo>(
                new MediaInfo(
                    new Uri(url, UriKind.Absolute),
                    root.TryGetProperty("mime_type", out var mime) && mime.ValueKind == JsonValueKind.String ? mime.GetString() ?? "application/octet-stream" : "application/octet-stream",
                    root.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null,
                    root.TryGetProperty("file_size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) ? bytes : 0),
                null);
        }
        catch (Exception exception) when (exception is JsonException or UriFormatException)
        {
            return new MessagingGraphResult<MediaInfo>(null, new MessagingGraphFailure(status, null, "unreadable"));
        }
    }

    public async Task<Stream> OpenMediaAsync(MessagingSender sender, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        // La URL ya es absoluta y firmada (vence a los 5 minutos); mismo cliente, sin redirecciones. No se registra.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sender.AccessToken);
        var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Media download answered HTTP {status}.");
        }

        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    /// <summary>Manda el request y lee el cuerpo una vez. <c>Response</c> en <c>null</c> = timeout o red; el
    /// llamador desecha la respuesta.</summary>
    private async Task<GraphAnswer> SendAsync(
        string operation, HttpRequestMessage request, string accessToken, TimeSpan? timeout, CancellationToken cancellationToken,
        string clientName = HttpClientName, string scheme = "Bearer")
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, accessToken);
        var client = httpClientFactory.CreateClient(clientName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            cts.CancelAfter(limit);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var error = response.IsSuccessStatusCode ? null : GraphError.TryParse(body);
            LogAnswered(logger, operation, (int)response.StatusCode, error?.Code, error?.Subcode, error?.FbTraceId);
            return new GraphAnswer(response, body, error, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            LogUnreachable(logger, operation, "timeout");
            return new GraphAnswer(null, string.Empty, null, "timeout");
        }
        catch (HttpRequestException exception)
        {
            response?.Dispose();
            // Sin conexión establecida el request nunca salió; cualquier otra falla pudo ocurrir con el request ya enviado.
            var neverLeft = exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError;
            LogUnreachable(logger, operation, neverLeft ? "not-sent" : "network");
            return new GraphAnswer(null, string.Empty, null, neverLeft ? "not-sent" : "network");
        }
    }

    public async Task<MessagingGraphResult<CreatedTemplate>> CreateTemplateAsync(MessagingTemplateSource source, JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{Uri.EscapeDataString(source.WabaId)}/message_templates")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var answer = await SendAsync("template-create", request, source.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new MessagingGraphResult<CreatedTemplate>(null, Unreachable(answer));
        }

        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<CreatedTemplate>(null, FailureOf(status, answer));
        }

        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var root = document.RootElement;
            var id = ReadId(answer.Body);
            if (string.IsNullOrEmpty(id) || id.Length > 64)
            {
                return new MessagingGraphResult<CreatedTemplate>(null, new MessagingGraphFailure(status, null, "no_id"));
            }

            var category = Text(root, "category") ?? payload["category"]?.GetValue<string>() ?? string.Empty;
            return new MessagingGraphResult<CreatedTemplate>(new CreatedTemplate(id, Text(root, "status") ?? "PENDING", category), null);
        }
        catch (JsonException)
        {
            return new MessagingGraphResult<CreatedTemplate>(null, new MessagingGraphFailure(status, null, "unreadable"));
        }
    }

    public async Task<MessagingGraphResult<DeletedTemplate>> DeleteTemplateAsync(
        MessagingTemplateSource source, string metaTemplateId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{Version}/{Uri.EscapeDataString(source.WabaId)}/message_templates?hsm_id={Uri.EscapeDataString(metaTemplateId)}&name={Uri.EscapeDataString(name)}");
        var answer = await SendAsync("template-delete", request, source.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new MessagingGraphResult<DeletedTemplate>(null, Unreachable(answer));
        }

        if (response.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<DeletedTemplate>(new DeletedTemplate(false), null);
        }

        var status = (int)response.StatusCode;
        // D-T13: lo que Meta ya no tiene, tampoco lo tiene el espejo. Se reconoce por el texto: Graph no da un código propio para esto.
        if (status == 400 && Mentions(answer.Error, "does not exist"))
        {
            return new MessagingGraphResult<DeletedTemplate>(new DeletedTemplate(true), null);
        }

        return new MessagingGraphResult<DeletedTemplate>(null, FailureOf(status, answer));
    }

    public async Task<MessagingGraphResult<string>> UploadTemplateExampleAsync(
        MessagingTemplateSource source, ReadOnlyMemory<byte> content, string mimeType, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var appId = options.Value.AppId?.Trim();
        if (string.IsNullOrEmpty(appId))
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(0, null, "no_app_id"));
        }

        using var start = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Version}/{Uri.EscapeDataString(appId)}/uploads?file_length={content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&file_type={Uri.EscapeDataString(mimeType)}&file_name={Uri.EscapeDataString(fileName)}");
        var started = await SendAsync("template-upload-start", start, source.AccessToken, null, cancellationToken, UploadHttpClientName);
        using var startResponse = started.Response;
        if (startResponse is null)
        {
            return new MessagingGraphResult<string>(null, Unreachable(started));
        }

        var startStatus = (int)startResponse.StatusCode;
        if (!startResponse.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<string>(null, FailureOf(startStatus, started));
        }

        var uploadId = ReadId(started.Body);
        if (string.IsNullOrEmpty(uploadId))
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(startStatus, null, "no_id"));
        }

        // El id viaja en la ruta sin escapar (Graph lo documenta con los dos puntos literales) y, si trae «?sig=…», esa firma va como
        // query del URI, no escapada dentro de la ruta. Sólo se sigue si tiene la forma esperada.
        if (!UploadIdPattern().IsMatch(uploadId))
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(startStatus, null, "invalid_upload_id"));
        }

        using var data = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Version}/{uploadId}", UriKind.Relative)) { Content = new ReadOnlyMemoryContent(content) };
        data.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        data.Headers.TryAddWithoutValidation("file_offset", "0");
        // La Resumable Upload documenta «Authorization: OAuth {token}» para la subida de bytes, no «Bearer».
        var uploaded = await SendAsync("template-upload-data", data, source.AccessToken, null, cancellationToken, UploadHttpClientName, "OAuth");
        using var dataResponse = uploaded.Response;
        if (dataResponse is null)
        {
            return new MessagingGraphResult<string>(null, Unreachable(uploaded));
        }

        var dataStatus = (int)dataResponse.StatusCode;
        if (!dataResponse.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<string>(null, FailureOf(dataStatus, uploaded));
        }

        try
        {
            using var document = JsonDocument.Parse(uploaded.Body);
            var handle = Text(document.RootElement, "h");
            return string.IsNullOrEmpty(handle)
                ? new MessagingGraphResult<string>(null, new MessagingGraphFailure(dataStatus, null, "no_handle"))
                : new MessagingGraphResult<string>(handle, null);
        }
        catch (JsonException)
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(dataStatus, null, "unreadable"));
        }
    }

    private static MessagingGraphFailure Unreachable(GraphAnswer answer) => new(0, null, answer.UnreachableReason ?? "unreachable");

    private static MessagingGraphFailure FailureOf(int status, GraphAnswer answer) =>
        new(status, answer.Error?.Code, $"http_{status}", answer.Error?.UserTitle, answer.Error?.UserMessage);

    private static bool Mentions(GraphError? error, string text) =>
        error is not null
        && ((error.UserTitle ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase)
            || (error.UserMessage ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"^upload:[A-Za-z0-9_=:-]{1,512}(\?sig=[A-Za-z0-9_=-]{1,512})?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UploadIdPattern();

    private static string? ReadId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadWamid(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].ValueKind == JsonValueKind.Object
                && messages[0].TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record GraphAnswer(HttpResponseMessage? Response, string Body, GraphError? Error, string? UnreachableReason);

    /// <summary>Copia chica de <c>MetaGraphError</c> de Integrations (no se referencia): código, subcódigo, título y fbtrace; nunca
    /// <c>message</c>, que puede repetir lo que se mandó. Spec 2026-10-14 §4.8: <c>UserTitle</c> y <c>UserMessage</c> son
    /// <c>error_user_title</c> y <c>error_user_msg</c>, el texto que Meta escribe para la persona; sólo los leen las plantillas.</summary>
    private sealed record GraphError(int? Code, int? Subcode, string? Title, string? FbTraceId, string? UserTitle, string? UserMessage)
    {
        public static GraphError? TryParse(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("error", out var error)
                    || error.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var userTitle = error.TryGetProperty("error_user_title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null;
                return new GraphError(
                    error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var c) ? c : null,
                    error.TryGetProperty("error_subcode", out var sub) && sub.ValueKind == JsonValueKind.Number && sub.TryGetInt32(out var s) ? s : null,
                    userTitle ?? (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null),
                    error.TryGetProperty("fbtrace_id", out var trace) && trace.ValueKind == JsonValueKind.String ? trace.GetString() : null,
                    userTitle,
                    error.TryGetProperty("error_user_msg", out var userMessage) && userMessage.ValueKind == JsonValueKind.String ? userMessage.GetString() : null);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
