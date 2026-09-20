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

    // Base64 y no la ruta del archivo: `qcode-pdf` no ve el disco del backend, sólo lo que viaja
    // en el request. La plantilla lo decodifica ella misma (no hay decodificador nativo en
    // Typst) porque el servicio sólo acepta `source` y `data`, sin un tercer canal para binarios.
    private static readonly string LogoBase64 = ReadLogoBase64();

    private readonly PdfOptions settings = options.Value.Pdf;

    public async Task<byte[]> RenderAsync(
        QuotationPdfDocument document, CancellationToken cancellationToken)
    {
        // El logo no es parte de `QuotationPdfDocument`: es configuración de despliegue, no dato
        // de la cotización (mismo criterio que `VITE_TENANT_BRAND_LOGO` en el frontend), así que
        // se agrega acá, en el borde de infraestructura, y no ensucia el documento de dominio.
        var data = JsonSerializer.SerializeToNode(document, DataFormat)!.AsObject();
        data["logo"] = LogoBase64;

        var payload = new
        {
            source = Template,
            data,
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

    // Mismo criterio que la plantilla: embebido, no configuración, para que no se pierda al
    // publicar la imagen.
    private static string ReadLogoBase64()
    {
        var assembly = typeof(QCodePdfRenderer).Assembly;
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            resource => resource.EndsWith("tenant-logo.webp", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The logo 'tenant-logo.webp' is not embedded in the assembly.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Convert.ToBase64String(buffer.ToArray());
    }
}
