namespace Modules.Notifications.Application;

public sealed record QuotationsExportKindNames(string Singular, string Plural);

/// <summary>
/// Cómo se nombra en el correo cada kind de <c>quotations.export-*.v1</c>. El kind viaja como el
/// nombre del enum de Quotations; Notifications no referencia ese módulo, así que la traducción
/// vive acá. Un kind desconocido cae a "registros": que el texto llegue después que el evento no
/// puede dejar sin correo a quien pidió la exportación.
/// </summary>
public static class QuotationsExportKindText
{
    public static QuotationsExportKindNames Of(string kind) => kind switch
    {
        "Quotations" => new("cotización", "cotizaciones"),
        "Sales" => new("venta", "ventas"),
        _ => new("registro", "registros"),
    };
}
