using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Pdf;

/// <summary>
/// Genera el PDF de la cotización contra `qcode-pdf` (`POST /pdf`, Typst). Ese servicio es
/// genérico y compartido con el resto de las aplicaciones de QCode: no conoce la cotización ni
/// guarda plantillas, así que el markup viaja entero en cada request y los datos van aparte,
/// en `data`, que el servicio deja como `data.json` y la plantilla lee con
/// `json(sys.inputs.data)`.
///
/// Mismo criterio de implementación que <c>ZenviaWhatsAppSender</c>: un <c>HttpClient</c>
/// sencillo, sin <c>IHttpClientFactory</c>, que este módulo no usa en ningún otro lado.
/// </summary>
internal sealed class QCodePdfRenderer(
    HttpClient httpClient, IOptions<QuotationsOptions> options)
    : IQuotationPdfRenderer
{
    // camelCase a propósito: es el contrato con la plantilla, que lee `data.quotationNumber`.
    // Cambiarlo acá rompe el `.typ` sin que falle la compilación de C#.
    private static readonly JsonSerializerOptions DataFormat = new(JsonSerializerDefaults.Web);

    private static readonly string Template = ReadTemplate();

    private readonly PdfOptions settings = options.Value.Pdf;

    public async Task<byte[]> RenderAsync(
        QuotationPdfDocument document, CancellationToken cancellationToken)
    {
        var payload = new
        {
            source = Template,
            data = JsonSerializer.SerializeToElement(document, DataFormat),
            filename = $"Cotizacion-{document.QuotationNumber}.pdf",
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/pdf")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // El 400 trae `{ error, details }` con el mensaje del compilador de Typst. Sin
            // esto el caso de uso seguiría con un cuerpo que no es un PDF y el cliente
            // recibiría un adjunto roto, que es un fallo mucho más caro de diagnosticar.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new QuotationsDomainException(
                "quotation.pdf.render_failed",
                $"qcode-pdf responded {(int)response.StatusCode}: {body}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // La plantilla viaja adentro del ensamblado: es parte del contrato con el servicio, no
    // configuración, y un archivo suelto en disco se pierde al publicar la imagen.
    private static string ReadTemplate()
    {
        var assembly = typeof(QCodePdfRenderer).Assembly;
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            resource => resource.EndsWith("quotation.typ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The Typst template 'quotation.typ' is not embedded in the assembly.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
