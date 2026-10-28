using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Automations.IntegrationTests;

/// <summary>Una parte de un cuerpo <c>multipart/form-data</c> como la recibió la API: nombre del campo, nombre del archivo (sólo en la
/// parte del archivo), tipo y bytes.</summary>
internal sealed record ExternalApiPart(string Name, string? FileName, string? ContentType, byte[] Bytes)
{
    public string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>Lo que llegó a la API externa: método, URI completa (con la query), el header <c>x-api-key</c>, el cuerpo como texto (JSON) o
/// sus partes (multipart).</summary>
internal sealed record ExternalApiRequest(HttpMethod Method, Uri Uri, string? ApiKey, string? Body, IReadOnlyList<ExternalApiPart> Parts);

/// <summary>
/// La API de Clasic de mentira (spec 2026-10-21 Anexo C), como <see cref="FakeMetaGraphHandler"/>: es el handler primario del cliente
/// nombrado <c>automations.external-api</c> en el host de pruebas, así que ninguna prueba sale a la red ni consulta el DNS (el handler
/// anti-SSRF de producción queda reemplazado sólo acá; <c>CompositionRootTests</c> fija el de producción). Responde por patrón de ruta
/// (regex sobre el path y la query); a diferencia del de Graph, gana la regla registrada <b>última</b>, para que una prueba pise la respuesta
/// por defecto de su fixture. Lo que no coincide responde 404. Una regla puede tardar: la espera respeta la cancelación, como un socket.
/// </summary>
internal sealed class FakeExternalApiHandler : HttpMessageHandler
{
    private readonly ConcurrentStack<(Regex Pattern, TimeSpan Delay, HttpStatusCode Status, string Body)> _rules = new();

    public ConcurrentQueue<ExternalApiRequest> Requests { get; } = new();

    public void Respond(string pathPattern, HttpStatusCode status, string body) => RespondAfter(pathPattern, TimeSpan.Zero, status, body);

    public void RespondAfter(string pathPattern, TimeSpan delay, HttpStatusCode status, string body) =>
        _rules.Push((new Regex(pathPattern, RegexOptions.CultureInvariant), delay, status, body));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        var parts = new List<ExternalApiPart>();
        if (request.Content is MultipartFormDataContent form)
        {
            foreach (var part in form)
            {
                var disposition = part.Headers.ContentDisposition;
                parts.Add(new ExternalApiPart(
                    disposition?.Name?.Trim('"') ?? string.Empty, disposition?.FileName?.Trim('"'), part.Headers.ContentType?.MediaType,
                    await part.ReadAsByteArrayAsync(cancellationToken)));
            }
        }
        else if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var apiKey = request.Headers.TryGetValues("x-api-key", out var values) ? string.Join(',', values) : null;
        Requests.Enqueue(new ExternalApiRequest(request.Method, request.RequestUri!, apiKey, body, parts));

        var target = request.RequestUri!.PathAndQuery;
        foreach (var (pattern, delay, status, responseBody) in _rules)
        {
            if (pattern.IsMatch(target))
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    }
}
