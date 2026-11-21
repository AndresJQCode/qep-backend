using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Coordinadora;

/// <summary>
/// Spec 2026-11-21 (integración Coordinadora) §5.1: el probador comprueba que el AGS de Coordinadora responde, con
/// <c>Cotizador.departamentos</c> por JSON-RPC 2.0 (el mismo camino con el que el README regenera el snapshot de ciudades). <b>No manda
/// ninguna credencial</b>: ninguna operación de lectura barata del AGS ni del servidor de guías verifica las claves sin cotizar o
/// generar algo con una cuenta y un producto que viven en la configuración del módulo <c>coordinadora</c>, no en la conexión. «Probar»
/// verifica alcance; las credenciales se confirman con la primera cotización (apikey y clave) y la primera guía (usuario y clave de
/// guías). 2xx con <c>result</c> es Ok; 2xx con <c>error</c> de JSON-RPC, 3xx, 4xx, 5xx, timeout o red, «no pude verificar». Lee
/// <c>Coordinadora:AgsUrl</c> de la configuración (<see cref="CoordinadoraTesterOptions"/>) porque Integrations no ve al módulo.
/// Registra sólo el status: nunca el cuerpo ni los campos.
/// </summary>
internal sealed partial class CoordinadoraConnectionTester(
    IHttpClientFactory httpClientFactory,
    IOptions<CoordinadoraTesterOptions> options,
    ILogger<CoordinadoraConnectionTester> logger) : IProviderConnectionTester
{
    public const string HttpClientName = "integrations.coordinadora";

    public const string DefaultAgsUrl = "https://ws.coordinadora.com/ags/1.5/server.php";

    /// <summary>El cuerpo JSON-RPC, constante: sin credenciales ni datos del tenant.</summary>
    internal const string DepartmentsRequest = """{"jsonrpc":"2.0","method":"Cotizador.departamentos","params":{},"id":1}""";

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(15);

    public string ProviderKey => IntegrationProviders.Coordinadora.Key;

    [LoggerMessage(Level = LogLevel.Information, Message = "Coordinadora reachability test answered HTTP {StatusCode}: {Outcome}")]
    private static partial void LogAnswered(ILogger logger, int statusCode, ConnectionTestOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Coordinadora reachability test could not reach the provider: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    /// <summary>Sin redirecciones: aunque este probador no mande claves, el cliente del módulo las manda en el cuerpo, y los dos
    /// clientes se configuran igual.</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>15 s y <c>User-Agent: qep-integrations</c>.</summary>
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
        var agsUrl = options.Value.AgsUrl;
        var uri = new Uri(string.IsNullOrWhiteSpace(agsUrl) ? DefaultAgsUrl : agsUrl.Trim());
        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(DepartmentsRequest, Encoding.UTF8, "application/json"),
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var result = Classify(response.StatusCode);
            if (result.Outcome != ConnectionTestOutcome.Ok)
            {
                LogAnswered(logger, (int)response.StatusCode, result.Outcome);
                return result;
            }

            // Con ResponseHeadersRead el timeout del HttpClient no cubre el cuerpo: se acota aparte, con el mismo tiempo.
            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bodyTimeout.CancelAfter(BodyTimeout);
            var answered = await HasResultAsync(response, bodyTimeout.Token);
            result = answered ? ConnectionTestResult.Ok : ConnectionTestResult.Unreachable("ags_without_result");
            LogAnswered(logger, (int)response.StatusCode, result.Outcome);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, "timeout");
            return ConnectionTestResult.Unreachable("timeout");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            LogUnreachable(logger, "network");
            return ConnectionTestResult.Unreachable("network");
        }
    }

    /// <summary>Un 2xx hay que leerlo (el AGS contesta 200 también con un <c>error</c> de JSON-RPC); todo lo demás es «no pude
    /// verificar»: el probador no mandó credenciales, así que ningún status es «credencial rechazada».</summary>
    internal static ConnectionTestResult Classify(HttpStatusCode status)
    {
        var code = (int)status;
        return code is >= 200 and < 300 ? ConnectionTestResult.Ok : ConnectionTestResult.Unreachable($"http_{code}");
    }

    // Un 200 con un cuerpo que no es JSON, o sin result, no dice que el AGS atienda: es «no pude verificar».
    private static async Task<bool> HasResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("result", out var result)
                && result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>La URL del AGS, de la sección <c>Coordinadora</c>. La sección es del módulo Coordinadora, que la valida; Integrations
/// no lo ve, así que la lee por su cuenta y sin validar: vacía vale la de producción.</summary>
internal sealed class CoordinadoraTesterOptions
{
    public const string SectionName = "Coordinadora";

    public string? AgsUrl { get; set; }
}
