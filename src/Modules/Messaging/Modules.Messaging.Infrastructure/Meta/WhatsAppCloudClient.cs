using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
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

    private string Version => string.IsNullOrWhiteSpace(options.Value.GraphApiVersion) ? DefaultGraphApiVersion : options.Value.GraphApiVersion.Trim();

    public async Task<SendTextResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(target);
        // Spec 2026-10-10 §6.1.4: con JsonObject la clave ausente no viaja (ni "to" con BSUID ni "recipient" sin él).
        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["type"] = "text",
            ["text"] = new JsonObject { ["body"] = body },
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

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var answer = await SendAsync("send", request, sender.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new SendTextResult(SendOutcome.Unconfirmed, null, null, null);
        }

        // §8.3: un 5xx no confirma nada (Meta pudo haberlo aceptado); un 4xx es un rechazo con su código.
        if ((int)response.StatusCode >= 500)
        {
            return new SendTextResult(SendOutcome.Unconfirmed, null, null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SendTextResult(SendOutcome.GraphError, null, answer.Error?.Code, answer.Error?.Title);
        }

        var wamid = ReadWamid(answer.Body);
        return wamid is null
            ? new SendTextResult(SendOutcome.Unconfirmed, null, null, null)
            : new SendTextResult(SendOutcome.Sent, wamid, null, null);
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
        string operation, HttpRequestMessage request, string accessToken, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var client = httpClientFactory.CreateClient(HttpClientName);
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
        catch (HttpRequestException)
        {
            response?.Dispose();
            LogUnreachable(logger, operation, "network");
            return new GraphAnswer(null, string.Empty, null, "network");
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

    /// <summary>Copia chica de <c>MetaGraphError</c> de Integrations (no se referencia): código, subcódigo, título y
    /// fbtrace; nunca <c>message</c>, que puede repetir lo que se mandó.</summary>
    private sealed record GraphError(int? Code, int? Subcode, string? Title, string? FbTraceId)
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

                return new GraphError(
                    error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var c) ? c : null,
                    error.TryGetProperty("error_subcode", out var sub) && sub.ValueKind == JsonValueKind.Number && sub.TryGetInt32(out var s) ? s : null,
                    error.TryGetProperty("error_user_title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString()
                        : error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null,
                    error.TryGetProperty("fbtrace_id", out var trace) && trace.ValueKind == JsonValueKind.String ? trace.GetString() : null);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
