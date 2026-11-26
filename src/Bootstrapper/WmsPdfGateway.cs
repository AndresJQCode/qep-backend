using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Modules.Quotations.Infrastructure;
using Modules.Wms.Application;
using Modules.Wms.Domain;

namespace Bootstrapper;

/// <summary>
/// La salida de Bodegas hacia `qcode-pdf` (`POST /pdf`, Typst). Reutiliza la configuración de <c>Quotations:Pdf</c> (P-9): es el mismo
/// servicio y la misma clave, sin un secreto nuevo en k8s. Vive acá porque Wms no puede referenciar Quotations (WmsLayerTests) y
/// Bootstrapper es el único que ve los dos. Copia de <c>QCodePdfRenderer.RenderAsync</c> sin <c>assets</c>: una etiqueta no lleva logo.
/// </summary>
internal sealed class WmsPdfGateway(HttpClient httpClient, IOptions<QuotationsOptions> options) : IWmsPdfGateway
{
    // camelCase a propósito: es el contrato con la plantilla, que lee `item.warehouseCode`.
    private static readonly JsonSerializerOptions DataFormat = new(JsonSerializerDefaults.Web);

    private readonly PdfOptions settings = options.Value.Pdf;

    public async Task<byte[]> RenderAsync(string source, object data, string fileName, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["source"] = source,
            ["data"] = JsonSerializer.SerializeToElement(data, DataFormat),
            ["filename"] = fileName,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/pdf")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);

        // Red caída o timeout de 30 s: sin esto escapan HttpRequestException/TaskCanceledException y la API responde 500, no lo que
        // promete IWmsPdfGateway. Una cancelación del llamador (token vivo = falso) no se traga: sigue siendo cancelación.
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException exception)
        {
            throw new WmsDomainException(WmsErrorCodes.LabelRenderFailed, $"qcode-pdf was unreachable: {exception.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WmsDomainException(WmsErrorCodes.LabelRenderFailed, "qcode-pdf did not answer before the timeout.");
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            // El 400 trae `{ error, details }` con el mensaje del compilador de Typst: si `@preview/tiaoma` no resuelve en el
            // servicio, el síntoma es éste y no una etiqueta en blanco. La clave nunca viaja en el mensaje.
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new WmsDomainException(
                WmsErrorCodes.LabelRenderFailed,
                $"qcode-pdf responded {(int)response.StatusCode}: {body}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
