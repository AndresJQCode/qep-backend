using System.Net;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.ExternalApi;

/// <summary>
/// Spec 2026-10-21 D-H3: <c>GET {baseUrl}{testPath}</c> (<c>/</c> sin ruta de prueba) con <c>{authHeader}: {apiKey}</c>
/// (<c>x-api-key</c> sin header). 2xx o 3xx → Ok (sin seguir la redirección: una API que manda a su login ya respondió);
/// 401/403 → rechazada; otro status, timeout o red → «no pude verificar»; una URL que la política de D-H2 no acepta, al
/// guardar o al resolver, marca <c>baseUrl</c>. Registra sólo el status y el resultado: nunca headers, cuerpo (no lo lee),
/// la URL ni la clave, y el <c>HttpClient</c> no tiene loggers (<c>RemoveAllLoggers</c>, D-H5).
/// </summary>
internal sealed partial class ExternalApiConnectionTester(
    IHttpClientFactory httpClientFactory,
    ILogger<ExternalApiConnectionTester> logger) : IProviderConnectionTester
{
    public const string HttpClientName = "integrations.external-api";

    private const string DefaultAuthHeader = "x-api-key";

    private const string DefaultTestPath = "/";

    public string ProviderKey => IntegrationProviders.ExternalApi.Key;

    [LoggerMessage(Level = LogLevel.Information, Message = "External API credential test answered HTTP {StatusCode}: {Outcome}")]
    private static partial void LogAnswered(ILogger logger, int statusCode, ConnectionTestOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "External API credential test could not reach the provider: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "External API connection settings were rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

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
        var baseUrl = fields.GetValueOrDefault(ExternalApiFieldKeys.BaseUrl) ?? string.Empty;
        // El patrón del catálogo ya exige https y un host; una URL que ni siquiera se arma no tiene host que juzgar.
        var rejected = Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            ? ExternalApiUrlPolicy.Reject(baseUri)
            : "host_missing";
        if (rejected is not null)
        {
            LogRejected(logger, rejected);
            return ConnectionTestResult.Invalid(ExternalApiFieldKeys.BaseUrl, rejected);
        }

        // testPath empieza por «/» (patrón del catálogo): se concatena a la base sin su «/» final, así /v1 + /health es
        // /v1/health, no /health como resolvería new Uri(base, relativa).
        var testPath = NonBlank(fields, ExternalApiFieldKeys.TestPath) ?? DefaultTestPath;
        var authHeader = NonBlank(fields, ExternalApiFieldKeys.AuthHeader) ?? DefaultAuthHeader;
        // P2-R30: el catálogo ya lo rechaza al guardar; una conexión guardada antes no puede mandar la clave en Host.
        if (ExternalApiUrlPolicy.IsReservedHeader(authHeader))
        {
            LogRejected(logger, "reserved_header");
            return ConnectionTestResult.Invalid(ExternalApiFieldKeys.AuthHeader, "reserved_header");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl.TrimEnd('/') + testPath, UriKind.Absolute));
        request.Headers.TryAddWithoutValidation(authHeader, secrets[ExternalApiFieldKeys.ApiKey]);

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
        catch (HttpRequestException exception) when (RejectionIn(exception) is { } rejection)
        {
            // ConnectCallback (D-H2): el nombre resolvió a un rango interno. Es la URL, no la red.
            LogRejected(logger, rejection.Reason);
            return ConnectionTestResult.Invalid(ExternalApiFieldKeys.BaseUrl, rejection.Reason);
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
        if (code is >= 200 and < 400)
        {
            return ConnectionTestResult.Ok;
        }

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ConnectionTestResult.CredentialsRejected
            : ConnectionTestResult.Unreachable($"http_{code}");
    }

    private static string? NonBlank(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.GetValueOrDefault(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static ExternalApiUrlRejectedException? RejectionIn(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is ExternalApiUrlRejectedException rejection)
            {
                return rejection;
            }
        }

        return null;
    }
}
