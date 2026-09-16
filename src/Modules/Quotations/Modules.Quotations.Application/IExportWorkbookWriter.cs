namespace Modules.Quotations.Application;

/// <summary>
/// Escribe el Excel de una exportación fila por fila, sin tenerlo entero en memoria (D8). Puerto
/// y no la librería directa por el mismo motivo que el builder de Catalog: el SDK de OpenXML es
/// una decisión de infraestructura y Application no compila contra él.
/// </summary>
public interface IExportWorkbookWriter
{
    IExportWorkbook Create(string sheetName, IReadOnlyList<ExportColumn> columns);
}

/// <summary>Un archivo en construcción. La cabecera ya está escrita al crearlo.</summary>
public interface IExportWorkbook : IDisposable
{
    /// <summary>Una fila, con una celda por columna y en el orden de las columnas.</summary>
    void AppendRow(IReadOnlyList<ExportCell> cells);

    /// <summary>Cierra el archivo y devuelve la ruta del temporal. Vale hasta <c>Dispose</c>,
    /// que lo borra: quien lo sube lo tiene que hacer antes.</summary>
    string Complete();
}

/// <summary>Una columna con su encabezado (sin tildes, como Reporting) y su ancho fijo.</summary>
public sealed record ExportColumn(string Header, double Width);

/// <summary>
/// Una celda: texto, número o enlace, nunca dos a la vez. Las fechas viajan como texto ISO a
/// propósito; los importes como número, porque quien abre el archivo los suma y filtra. El enlace
/// (spec 2026-09-15, E5) lo usa el Excel de pedidos para los comprobantes de pago: <c>Url</c> es el
/// destino y <c>Text</c> lo que se ve.
/// </summary>
public readonly record struct ExportCell(string? Text, decimal? Number, string? Url)
{
    public static ExportCell OfText(string? value) => new(value ?? string.Empty, null, null);

    public static ExportCell OfNumber(decimal value) => new(null, value, null);

    public static ExportCell OfLink(string url, string text) => new(text, null, url);
}
