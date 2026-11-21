using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// Diagnóstico: con <c>Siigo:DebugCurl=true</c> escribe, <b>sólo de las llamadas que fallan</b> (respuesta no 2xx o excepción de red), el
/// <c>curl</c> reproducible y la respuesta de Siigo (status y cuerpo). Una llamada exitosa no deja nada: su respuesta trae el token de
/// <c>/auth</c>. El token y la <c>access_key</c> salen enmascarados; para repetir la llamada se pone un token nuevo (<c>POST /auth</c>).
/// <b>El cuerpo de una factura o de un cliente lleva datos personales</b>: el interruptor nace apagado, se prende en el ConfigMap sólo
/// mientras se diagnostica y se vuelve a apagar. El log va en su propia categoría, distinta de la del <c>HttpClient</c> que
/// <c>RemoveAllLoggers</c> quita.
/// </summary>
internal sealed partial class SiigoCurlLogHandler(ILogger<SiigoCurlLogHandler> logger, bool enabled) : DelegatingHandler
{
    private const int MaxLoggedBody = 8000;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            LogFailure(logger, BuildCurl(request, body), exception.GetType().Name, string.Empty);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var reply = await response.Content.ReadAsStringAsync(cancellationToken);
            LogFailure(
                logger,
                BuildCurl(request, body),
                ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                reply.Length > MaxLoggedBody ? reply[..MaxLoggedBody] + "…" : reply);
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Siigo call failed. Request: {Curl}\nResponse: {Status}\n{Body}")]
    private static partial void LogFailure(ILogger logger, string curl, string status, string body);

    private static string BuildCurl(HttpRequestMessage request, string? body)
    {
        var curl = new StringBuilder("curl.exe -X ").Append(request.Method.Method).Append(" \"").Append(request.RequestUri).Append('"');
        foreach (var header in request.Headers)
        {
            var value = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? "Bearer <TOKEN>"
                : string.Join(", ", header.Value);
            curl.Append(" -H \"").Append(header.Key).Append(": ").Append(value).Append('"');
        }

        if (body is not null)
        {
            curl.Append(" -H \"Content-Type: application/json\" -d '").Append(AccessKey().Replace(body, "\"access_key\":\"<ACCESS_KEY>\"")).Append('\'');
        }

        return curl.ToString();
    }

    [GeneratedRegex("\"access_key\"\\s*:\\s*\"[^\"]*\"")]
    private static partial Regex AccessKey();
}
