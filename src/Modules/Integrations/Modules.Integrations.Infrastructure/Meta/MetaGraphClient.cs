using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Una respuesta de Graph ya leída: status, cuerpo (sólo para parsear, nunca para el log) y el
/// error si lo hay. <c>UnreachableReason</c> = timeout, red o 5xx.</summary>
internal sealed record MetaGraphResponse(HttpStatusCode Status, string Body, MetaGraphError? Error, string? UnreachableReason)
{
    public bool IsSuccess => UnreachableReason is null && (int)Status is >= 200 and < 300;
}

/// <summary>
/// Spec 2026-10-09, decisión 3: el cliente de Graph de Integrations (<c>integrations.meta-graph</c>), con el
/// patrón de <c>ZenviaConnectionTester</c>: sin redirecciones (el token viaja en <c>Authorization</c>),
/// 10 s, sin loggers de headers. Registra status, <c>error.code</c>, <c>error_subcode</c> y <c>fbtrace_id</c>;
/// nunca el token, el <c>code</c>, el PIN ni el <c>client_secret</c>.
/// </summary>
internal sealed partial class MetaGraphClient(
    IHttpClientFactory httpClientFactory,
    IOptions<MetaAppOptions> options,
    ILogger<MetaGraphClient> logger)
{
    public const string HttpClientName = "integrations.meta-graph";

    public const string BaseUrl = "https://graph.facebook.com/";

    [LoggerMessage(Level = LogLevel.Information, Message = "Graph {Operation} answered HTTP {StatusCode} (code {Code}, subcode {Subcode}, fbtrace {FbTraceId})")]
    private static partial void LogAnswered(ILogger logger, string operation, int statusCode, int? code, int? subcode, string? fbTraceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graph {Operation} could not be reached: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    /// <summary>Handler primario sin redirecciones automáticas: un 3xx no lleva el token a otro host.</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>Spec: 10 s por llamada y <c>User-Agent: qep-integrations</c>.</summary>
    internal static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-integrations");
    }

    public string Version => string.IsNullOrWhiteSpace(options.Value.GraphApiVersion)
        ? MetaAppOptions.DefaultGraphApiVersion
        : options.Value.GraphApiVersion.Trim();

    /// <summary>Ruta relativa a la versión: <c>Path("111?fields=…")</c> → <c>v24.0/111?fields=…</c>.</summary>
    public string Path(string relative) => $"{Version}/{relative.TrimStart('/')}";

    public async Task<MetaGraphResponse> SendAsync(
        string operation, HttpRequestMessage request, string? accessToken, CancellationToken cancellationToken)
    {
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var error = response.IsSuccessStatusCode ? null : MetaGraphError.TryParse(body);
            LogAnswered(logger, operation, (int)response.StatusCode, error?.Code, error?.Subcode, error?.FbTraceId);
            return (int)response.StatusCode >= 500
                ? new MetaGraphResponse(response.StatusCode, body, error, $"http_{(int)response.StatusCode}")
                : new MetaGraphResponse(response.StatusCode, body, error, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El timeout del HttpClient, no una cancelación de quien llama.
            LogUnreachable(logger, operation, "timeout");
            return new MetaGraphResponse(HttpStatusCode.RequestTimeout, string.Empty, null, "timeout");
        }
        catch (HttpRequestException)
        {
            LogUnreachable(logger, operation, "network");
            return new MetaGraphResponse(HttpStatusCode.ServiceUnavailable, string.Empty, null, "network");
        }
    }
}
