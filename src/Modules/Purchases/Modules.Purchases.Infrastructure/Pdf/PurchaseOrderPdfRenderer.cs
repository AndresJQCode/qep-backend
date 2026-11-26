using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.Infrastructure.Pdf;

/// <summary>
/// Genera el PDF de la orden de compra contra <c>qcode-pdf</c> (<c>POST /pdf</c>, Typst), calco de <c>QCodePdfRenderer</c> y
/// <c>SupportPdfRenderer</c> con su propia sección y clave (<see cref="PurchasesPdfOptions"/>). El servicio es genérico: la
/// plantilla viaja entera en cada request y los datos van aparte en <c>data</c> (la plantilla los lee con
/// <c>json(sys.inputs.data)</c>).
/// </summary>
/// <remarks>
/// Un fallo (respuesta no 2xx, red caída, tiempo agotado, cuerpo vacío) es <c>purchases.order.pdf_render_failed</c> (502). Ni el log ni
/// la excepción llevan el cuerpo de la respuesta: el error del compilador de Typst puede repetir textos de la orden. Sólo el status.
/// </remarks>
internal sealed partial class PurchaseOrderPdfRenderer(
    HttpClient httpClient, IOptions<PurchasesPdfOptions> options, ILogger<PurchaseOrderPdfRenderer> logger)
    : IPurchaseOrderPdfRenderer
{
    // camelCase a propósito: es el contrato con la plantilla, que lee `data.number`, `data.lines`…
    private static readonly JsonSerializerOptions DataFormat = new(JsonSerializerDefaults.Web);

    /// <summary>La plantilla embebida.</summary>
    internal static readonly string Template = ReadTemplate();

    private readonly PurchasesPdfOptions settings = options.Value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "qcode-pdf rejected the purchase order document with status {StatusCode}.")]
    private static partial void LogRejected(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "qcode-pdf could not be reached to render the purchase order document.")]
    private static partial void LogUnreachable(ILogger logger, Exception exception);

    public async Task<byte[]> RenderAsync(PurchaseOrderPdfDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var payload = new
        {
            source = Template,
            data = JsonSerializer.SerializeToElement(document, DataFormat),
            filename = $"{document.Number}.pdf",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.Trim().TrimEnd('/')}/pdf")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnreachable(logger, exception);
            throw RenderFailed("qcode-pdf could not be reached to render the purchase order.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                LogRejected(logger, status);
                throw RenderFailed($"qcode-pdf responded {status}.");
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (content.Length == 0)
            {
                LogRejected(logger, (int)response.StatusCode);
                throw RenderFailed("qcode-pdf returned an empty document.");
            }

            return content;
        }
    }

    private static UpstreamServiceException RenderFailed(string message) =>
        new(PurchasesErrorCodes.PdfRenderFailed, message);

    // La plantilla viaja dentro del ensamblado: es parte del contrato con el servicio, y un archivo suelto se pierde al publicar.
    private static string ReadTemplate()
    {
        var assembly = typeof(PurchaseOrderPdfRenderer).Assembly;
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            resource => resource.EndsWith("purchase-order.typ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The Typst template 'purchase-order.typ' is not embedded in the assembly.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
