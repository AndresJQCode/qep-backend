using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Zenvia;

/// <summary>
/// Spec 2026-10-08, «Probar la credencial»: <c>GET {BaseUrl}/v2/templates</c> con <c>X-API-TOKEN</c>
/// (respuesta 1 del owner). 2xx → Ok; 401/403 → rechazada; timeout, red y cualquier otro status →
/// «no pude verificar» (P13). El número emisor no se valida contra Zenvia. Registra sólo el status y
/// el resultado: nunca headers ni cuerpo (no lo lee), y el <c>HttpClient</c> no tiene loggers de
/// headers (<c>RemoveAllLoggers</c>).
/// </summary>
internal sealed partial class ZenviaConnectionTester(
    IHttpClientFactory httpClientFactory,
    IOptions<ZenviaOptions> options,
    ILogger<ZenviaConnectionTester> logger) : IProviderConnectionTester
{
    public const string HttpClientName = "integrations.zenvia";

    private const string TemplatesPath = "v2/templates";

    public string ProviderKey => IntegrationProviders.Zenvia.Key;

    [LoggerMessage(Level = LogLevel.Information, Message = "Zenvia credential test answered HTTP {StatusCode}: {Outcome}")]
    private static partial void LogAnswered(ILogger logger, int statusCode, ConnectionTestOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Zenvia credential test could not reach the provider: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    /// <summary>Spec: 10 s por prueba y <c>User-Agent: qep-integrations</c>.</summary>
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
        var baseUri = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, TemplatesPath));
        request.Headers.TryAddWithoutValidation("X-API-TOKEN", secrets[ZenviaFieldKeys.ApiToken]);

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
