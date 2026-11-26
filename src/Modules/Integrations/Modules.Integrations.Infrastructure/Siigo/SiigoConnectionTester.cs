using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Siigo;

/// <summary>
/// Spec 2026-11-18 (integración Siigo): <c>POST {Siigo:BaseUrl}/auth</c> con el usuario, la clave de acceso y el <c>Partner-Id</c>, y
/// con el token <c>GET /v1/document-types?type=FV</c>, una lectura barata que no escribe nada. 2xx en las dos es Ok; 401 o 403 es
/// credencial rechazada; 3xx, 429, 5xx, timeout o red, «no pude verificar». <c>Siigo:BaseUrl</c> y <c>Siigo:PartnerId</c> se leen de la
/// configuración (<see cref="SiigoTesterOptions"/>) porque Integrations no ve al módulo Siigo; sin <c>Partner-Id</c> no se llama a Siigo. Registra sólo el
/// status: nunca la clave, el token, los headers ni el cuerpo, y el cliente no tiene loggers (<c>RemoveAllLoggers</c>).
/// </summary>
internal sealed partial class SiigoConnectionTester(
    IHttpClientFactory httpClientFactory,
    IOptions<SiigoTesterOptions> options,
    ILogger<SiigoConnectionTester> logger) : IProviderConnectionTester
{
    public const string HttpClientName = "integrations.siigo";

    public const string DefaultBaseUrl = "https://api.siigo.com";

    /// <summary>Lo mismo que el timeout del cliente (<see cref="ConfigureClient"/>), para la lectura del cuerpo de <c>/auth</c>.</summary>
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(15);

    public string ProviderKey => IntegrationProviders.Siigo.Key;

    [LoggerMessage(Level = LogLevel.Information, Message = "Siigo credential test step {Step} answered HTTP {StatusCode}: {Outcome}")]
    private static partial void LogAnswered(ILogger logger, string step, int statusCode, ConnectionTestOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Siigo credential test could not reach the provider: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    /// <summary>Sin redirecciones: la clave y el token no viajan a otro host (un 3xx es «no pude verificar»).</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>15 s por paso y <c>User-Agent: qep-integrations</c>.</summary>
    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-integrations");
    }

    public async Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        var partnerId = options.Value.PartnerId;
        if (string.IsNullOrWhiteSpace(partnerId))
        {
            LogUnreachable(logger, "partner_id_missing");
            return ConnectionTestResult.Unreachable("partner_id_missing");
        }

        var baseUrl = options.Value.BaseUrl;
        var baseUri = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/') + "/");
        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var auth = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "auth"))
            {
                Content = JsonContent.Create(new AuthRequest(fields[SiigoFieldKeys.Username], secrets[SiigoFieldKeys.AccessKey])),
            };
            auth.Headers.TryAddWithoutValidation("Partner-Id", partnerId);
            using var authResponse = await client.SendAsync(auth, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var authResult = Classify(authResponse.StatusCode);
            LogAnswered(logger, "auth", (int)authResponse.StatusCode, authResult.Outcome);
            if (authResult.Outcome != ConnectionTestOutcome.Ok)
            {
                return authResult;
            }

            // El cliente manda con ResponseHeadersRead, así que el timeout del HttpClient no cubre el cuerpo: se acota aparte, con el
            // mismo tiempo, o un servidor que manda los headers y se queda callado dejaría la prueba colgada.
            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bodyTimeout.CancelAfter(BodyTimeout);
            var token = await ReadTokenAsync(authResponse, bodyTimeout.Token);
            if (token is null)
            {
                LogUnreachable(logger, "auth_without_token");
                return ConnectionTestResult.Unreachable("auth_without_token");
            }

            using var documentTypes = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "v1/document-types?type=FV"));
            documentTypes.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            documentTypes.Headers.TryAddWithoutValidation("Partner-Id", partnerId);
            using var documentTypesResponse = await client.SendAsync(
                documentTypes, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var result = Classify(documentTypesResponse.StatusCode);
            LogAnswered(logger, "document-types", (int)documentTypesResponse.StatusCode, result.Outcome);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El timeout del HttpClient o el de la lectura del cuerpo, no una cancelación de quien llama.
            LogUnreachable(logger, "timeout");
            return ConnectionTestResult.Unreachable("timeout");
        }
        // Una conexión que se cae a mitad del cuerpo de /auth es HttpIOException, que hereda de IOException y no de HttpRequestException.
        catch (Exception exception) when (exception is HttpRequestException or IOException)
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

    // Un 200 con un cuerpo que no es JSON o sin access_token no deja seguir: no hay con qué leer los tipos de documento.
    private static async Task<string?> ReadTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String
                && token.GetString() is { Length: > 0 } value
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record AuthRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("username")] string Username,
        [property: System.Text.Json.Serialization.JsonPropertyName("access_key")] string AccessKey);
}

/// <summary>Las dos claves de la sección <c>Siigo</c> que el probador necesita. La sección es del módulo Siigo, que valida que existan
/// (Tarea 5); Integrations no lo ve, así que las lee por su cuenta y sin validar: sin <c>PartnerId</c> el probador responde
/// <c>partner_id_missing</c>.</summary>
internal sealed class SiigoTesterOptions
{
    public const string SectionName = "Siigo";

    public string? BaseUrl { get; set; }

    public string? PartnerId { get; set; }
}
