using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Pdf;

/// <summary>
/// Arma las etiquetas de ubicación (RF-004, P-9): una por página de 60×30 mm, con el código en Code 128. La plantilla Typst viaja
/// adentro del ensamblado y los datos van aparte, como en el PDF de cotizaciones; <c>qcode-pdf</c> es genérico y no guarda nada.
/// </summary>
internal sealed class LocationLabelRenderer(IWmsPdfGateway gateway) : IWmsLabelRenderer
{
    private static readonly string Template = ReadTemplate();

    public Task<byte[]> RenderLocationLabelsAsync(IReadOnlyList<LocationLabel> labels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(labels);

        // Los nombres en minúscula inicial son el contrato con la plantilla, que lee item.code / item.path: renombrar una propiedad
        // compila y deja la etiqueta en blanco sin que nada falle.
        var data = new
        {
            labels = labels.Select(label => new { code = label.Code, path = label.Path, warehouseCode = label.WarehouseCode }).ToArray(),
        };

        // Todas las etiquetas de un pedido son de la misma bodega (el handler lo garantiza), así que el archivo se nombra por la primera.
        var warehouseCode = labels.Count > 0 ? labels[0].WarehouseCode : "bodega";

        return gateway.RenderAsync(Template, data, $"etiquetas-{warehouseCode}.pdf", ct);
    }

    // La plantilla es parte del contrato con el servicio, no configuración: un archivo suelto en disco se pierde al publicar la imagen.
    private static string ReadTemplate()
    {
        var assembly = typeof(LocationLabelRenderer).Assembly;
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            resource => resource.EndsWith("location-label.typ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The Typst template 'location-label.typ' is not embedded in the assembly.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
