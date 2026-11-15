using System.Net;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Anthropic;

/// <summary>
/// Plan 2026-11-12, C-2: <c>GET https://api.anthropic.com/v1/models</c> con la clave, que no gasta tokens del tenant. 2xx Ok; 401 y 403
/// rechazada; 429 (siempre tasa, nunca cuota), 5xx, 529, un 3xx, timeout o red → «no pude verificar». El saldo no se verifica aquí: sin
/// gastar no hay forma, y el 402 llega en la primera llamada real del agente. <c>HttpClient</c> directo y no el SDK de Anthropic: son
/// dos headers, y el SDK vive sólo en el módulo Ai (AiLayerTests). Registra sólo el status: nunca la clave, los headers ni el cuerpo
/// (no lo lee), y el cliente no tiene loggers (<c>RemoveAllLoggers</c>).
/// </summary>
internal sealed partial class AnthropicConnectionTester(
    IHttpClientFactory httpClientFactory,
    ILogger<AnthropicConnectionTester> logger) : IProviderConnectionTester
{
    public const string HttpClientName = "integrations.anthropic";

    private static readonly Uri ModelsUri = new("https://api.anthropic.com/v1/models");

    /// <summary>La versión de la API que manda el SDK actual; en <c>/v1/models</c> se comporta igual que la anterior.</summary>
    private const string ApiVersion = "2026-10-21";

    public string ProviderKey => IntegrationProviders.Anthropic.Key;

    [LoggerMessage(Level = LogLevel.Information, Message = "Anthropic credential test answered HTTP {StatusCode}: {Outcome}")]
    private static partial void LogAnswered(ILogger logger, int statusCode, ConnectionTestOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic credential test could not reach the provider: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    /// <summary>Sin redirecciones: la clave no viaja a otro host (un 3xx es «no pude verificar»), como Zenvia.</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>Como Zenvia: 10 s por prueba y <c>User-Agent: qep-integrations</c>.</summary>
    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-integrations");
    }

    public async Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUri);
        request.Headers.TryAddWithoutValidation("x-api-key", secrets[AnthropicFieldKeys.ApiKey]);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

        try
        {
            using var response = await httpClientFactory
                .CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var result = Classify(response.StatusCode);
            LogAnswered(logger, (int)response.StatusCode, result.Outcome);
            return result;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El timeout del HttpClient, no una cancelación de quien llama.
            LogUnreachable(logger, "timeout");
            return ConnectionTestResult.Unreachable("timeout");
        }
        catch (HttpRequestException)
        {
            LogUnreachable(logger, "network");
            return ConnectionTestResult.Unreachable("network");
        }
    }

    internal static ConnectionTestResult Classify(HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            return ConnectionTestResult.Ok;
        }

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ConnectionTestResult.CredentialsRejected
            : ConnectionTestResult.Unreachable($"http_{code}");
    }
}
