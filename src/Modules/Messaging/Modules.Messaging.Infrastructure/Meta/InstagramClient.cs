using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.Meta;

/// <summary>
/// Spec 2026-10-14 §6.2: la Messaging API de Instagram con Instagram Login, en <c>graph.instagram.com</c> (no <c>graph.facebook.com</c>).
/// Mismo patrón que <see cref="WhatsAppCloudClient"/>: registra status, <c>error.code</c>, <c>error_subcode</c> y <c>fbtrace_id</c>;
/// nunca el token, el cuerpo, el texto ni las URLs (la del adjunto entrante está firmada; la del saliente es una URL prefirmada de R2).
/// </summary>
internal sealed partial class InstagramClient(
    IHttpClientFactory httpClientFactory,
    IOptions<MessagingInstagramOptions> options,
    ILogger<InstagramClient> logger) : IInstagramClient
{
    public const string HttpClientName = "messaging.instagram-graph";

    /// <summary>La descarga de un adjunto entrante desde el CDN de Meta (<c>lookaside.fbsbx.com</c>): hasta 25 MB, con redirecciones,
    /// y con el mismo tope que la copia de WhatsApp.</summary>
    public const string DownloadHttpClientName = "messaging.instagram-download";

    public const string BaseUrl = "https://graph.instagram.com/";
    public const string DefaultGraphApiVersion = "v24.0";
    public const string ProfileFields = "name,username,profile_pic";
    public static readonly TimeSpan SeenTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instagram Graph {Operation} answered HTTP {StatusCode} (code {Code}, subcode {Subcode}, fbtrace {FbTraceId})")]
    private static partial void LogAnswered(ILogger logger, string operation, int statusCode, int? code, int? subcode, string? fbTraceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instagram Graph {Operation} could not be reached: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>El CDN redirige; se siguen hasta 5 saltos, sin credenciales (no viaja ningún header de autorización).</summary>
    internal static SocketsHttpHandler CreateDownloadHandler() => new() { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };

    internal static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-messaging");
    }

    internal static void ConfigureDownloadClient(HttpClient client)
    {
        client.Timeout = DownloadTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-messaging");
    }

    private string Version => string.IsNullOrWhiteSpace(options.Value.GraphApiVersion) ? DefaultGraphApiVersion : options.Value.GraphApiVersion.Trim();

    public Task<SendResult> SendTextAsync(InstagramSender sender, string recipientId, string text, CancellationToken cancellationToken) =>
        PostMessageAsync(sender, new JsonObject
        {
            ["recipient"] = new JsonObject { ["id"] = recipientId },
            ["message"] = new JsonObject { ["text"] = text },
        }, cancellationToken);

    public Task<SendResult> SendAttachmentAsync(InstagramSender sender, string recipientId, string attachmentType, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        return PostMessageAsync(sender, new JsonObject
        {
            ["recipient"] = new JsonObject { ["id"] = recipientId },
            ["message"] = new JsonObject
            {
                ["attachment"] = new JsonObject
                {
                    ["type"] = attachmentType,
                    ["payload"] = new JsonObject { ["url"] = url.AbsoluteUri },
                },
            },
        }, cancellationToken);
    }

    public async Task<bool> MarkSeenAsync(InstagramSender sender, string recipientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var payload = new JsonObject { ["recipient"] = new JsonObject { ["id"] = recipientId }, ["sender_action"] = "mark_seen" };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{Uri.EscapeDataString(sender.IgUserId)}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var answer = await SendAsync("seen", request, sender.AccessToken, SeenTimeout, cancellationToken);
        using var response = answer.Response;
        return response?.IsSuccessStatusCode == true;
    }

    public async Task<MessagingGraphResult<InstagramProfile>> GetProfileAsync(InstagramSender sender, string igsid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Version}/{Uri.EscapeDataString(igsid)}?fields={ProfileFields}");
        var answer = await SendAsync("profile", request, sender.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new MessagingGraphResult<InstagramProfile>(null, new MessagingGraphFailure(0, null, answer.UnreachableReason ?? "unreachable"));
        }

        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<InstagramProfile>(null, new MessagingGraphFailure(status, answer.Error?.Code, $"http_{status}"));
        }

        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new MessagingGraphResult<InstagramProfile>(null, new MessagingGraphFailure(status, null, "unreadable"));
            }

            return new MessagingGraphResult<InstagramProfile>(
                new InstagramProfile(ReadString(root, "name"), ReadString(root, "username"), ReadString(root, "profile_pic")), null);
        }
        catch (JsonException)
        {
            return new MessagingGraphResult<InstagramProfile>(null, new MessagingGraphFailure(status, null, "unreadable"));
        }
    }

    public async Task<InstagramDownload?> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(DownloadHttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            LogUnreachable(logger, "download", exception.HttpRequestError.ToString());
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            LogAnswered(logger, "download", (int)response.StatusCode, null, null, null);
            response.Dispose();
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new InstagramDownload(new ResponseOwningStream(stream, response), response.Content.Headers.ContentType?.ToString(), response.Content.Headers.ContentLength);
    }

    private async Task<SendResult> PostMessageAsync(InstagramSender sender, JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{Uri.EscapeDataString(sender.IgUserId)}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var answer = await SendAsync("send", request, sender.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new SendResult(answer.UnreachableReason == "not-sent" ? SendOutcome.NotSent : SendOutcome.Unconfirmed, null, null, null);
        }

        if ((int)response.StatusCode >= 500)
        {
            return new SendResult(SendOutcome.Unconfirmed, null, null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SendResult(SendOutcome.GraphError, null, answer.Error?.Code, answer.Error?.Title);
        }

        var mid = ReadMessageId(answer.Body);
        return mid is null ? new SendResult(SendOutcome.Unconfirmed, null, null, null) : new SendResult(SendOutcome.Sent, mid, null, null);
    }

    /// <summary>§3: la respuesta es <c>{ "recipient_id", "message_id" }</c>.</summary>
    private static string? ReadMessageId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "message_id");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<GraphAnswer> SendAsync(string operation, HttpRequestMessage request, string accessToken, TimeSpan? timeout, CancellationToken cancellationToken)
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
        catch (HttpRequestException exception)
        {
            response?.Dispose();
            var neverLeft = exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError;
            LogUnreachable(logger, operation, neverLeft ? "not-sent" : "network");
            return new GraphAnswer(null, string.Empty, null, neverLeft ? "not-sent" : "network");
        }
    }

    private sealed record GraphAnswer(HttpResponseMessage? Response, string Body, GraphError? Error, string? UnreachableReason);

    /// <summary>Código, subcódigo, título y fbtrace; nunca <c>message</c>. Spec 2026-10-14 §6.2: «fuera de la ventana» es el código 10
    /// con el subcódigo 2534022; el código queda en 10, que es lo que la tabla de motivos traduce.</summary>
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
                    error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var parsedCode) ? parsedCode : null,
                    error.TryGetProperty("error_subcode", out var subcode) && subcode.ValueKind == JsonValueKind.Number && subcode.TryGetInt32(out var parsedSubcode) ? parsedSubcode : null,
                    ReadString(error, "error_user_title") ?? ReadString(error, "type"),
                    ReadString(error, "fbtrace_id"));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>El stream de la descarga, que al cerrarse libera también la respuesta.</summary>
    private sealed class ResponseOwningStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
