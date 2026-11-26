using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.OpenAi;

/// <summary>
/// Spec 2026-10-29 §9.1, O-10 (P-6 para OpenAI): <c>GET https://api.openai.com/v1/models</c> con la clave como <c>Bearer</c>, que no
/// gasta tokens del tenant. 2xx Ok; 401 y 403 rechazada; 429, 5xx, un 3xx, timeout o red → «no pude verificar». No verifica el saldo ni
/// el acceso a los modelos de la lista: Integrations no la ve, y los dos llegan en la primera llamada real del agente
/// (<c>QuotaExhausted</c>, <c>ModelUnavailable</c>). Una clave restringida necesita leer modelos (<c>api.model.read</c>) para pasar.
/// Registra sólo el status: nunca la clave, los headers ni el cuerpo (no lo lee), y el cliente no tiene loggers.
/// </summary>
internal sealed partial class OpenAiConnectionTester(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiConnectionTester> logger) : IProviderConnectionTester
{
    public const string HttpClientName = "integrations.openai";

    private static readonly Uri ModelsUri = new("https://api.openai.com/v1/models");

    public string ProviderKey => IntegrationProviders.OpenAi.Key;

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenAI credential test answered HTTP {StatusCode}: {Outcome}")]
    private static partial void LogAnswered(ILogger logger, int statusCode, ConnectionTestOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenAI credential test could not reach the provider: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    /// <summary>Sin redirecciones: la clave no viaja a otro host (un 3xx es «no pude verificar»).</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>Como Anthropic y Zenvia: 10 s por prueba y <c>User-Agent: qep-integrations</c>.</summary>
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets[OpenAiFieldKeys.ApiKey]);

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

        // 401: clave inválida o IP fuera de la lista; 403: sin permiso (guía de errores de OpenAI). Las dos piden otra clave.
        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ConnectionTestResult.CredentialsRejected
            : ConnectionTestResult.Unreachable($"http_{code}");
    }
}
