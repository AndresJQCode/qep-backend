using System.Globalization;
using ClosedXML.Excel;
using Modules.Reporting.Application;

namespace Modules.Reporting.Infrastructure.Excel;

/// <summary>
/// Arma los cuatro <c>.xlsx</c> con ClosedXML.
///
/// Misma forma de hoja que <c>ClosedXmlCustomerExportBuilder</c>, y por las mismas razones:
/// cabecera congelada y en negrita para que sobreviva al scroll, anchos ajustados al contenido
/// con un piso para que la cabecera no quede pegada al borde, y **fechas como texto ISO-8601**
/// —una celda de fecha se muestra segun la configuracion regional de quien abre el archivo, y
/// ahi 03/04 deja de ser una fecha sola—.
///
/// Los encabezados van sin tildes: son los que fija el contrato de API, que el frontend usa
/// como referencia de las columnas.
/// </summary>
internal sealed class ClosedXmlReportExcelBuilder : IReportExcelBuilder
{
    // Mismo piso que la exportacion de clientes: `AdjustToContents()` ajusta al ancho exacto del
    // texto y deja la cabecera pegada al borde de la celda siguiente.
    private const double MinimumColumnWidth = 14;

    private static readonly IReadOnlyList<string> SalesColumns =
    [
        "Numero Venta",
        "Numero Cotizacion",
        "Fecha",
        "Asesor",
        "Cliente",
        "CUC",
        "Estado",
        "Estado Pago",
        "Subtotal",
        "Impuesto",
        "Total"
    ];

    private static readonly IReadOnlyList<string> QuotationsColumns =
    [
        "Numero",
        "Fecha",
        "Valida Hasta",
        "Asesor",
        "Cliente",
        "CUC",
        "Estado",
        "Subtotal",
        "Impuesto",
        "Total"
    ];

    private static readonly IReadOnlyList<string> PriceChangeColumns =
    [
        "Fecha",
        "Producto",
        "Codigo",
        "Campo",
        "Escala Desde",
        "Escala Hasta",
        "Valor Anterior",
        "Valor Nuevo",
        "Diferencia",
        "Usuario"
    ];

    private static readonly IReadOnlyList<string> CustomerColumns =
    [
        "CUC",
        "Nombre",
        "Tipo Identificacion",
        "Numero Identificacion",
        "Clasificacion",
        "Departamento",
        "Ciudad",
        "Activo",
        "Creado"
    ];

    public ReportFile BuildSales(
        IReadOnlyList<SalesReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken) =>
        Build(
            "Ventas",
            SalesColumns,
            rows,
            (sheet, excelRow, item) =>
            {
                sheet.Cell(excelRow, 1).Value = item.SaleNumber;
                sheet.Cell(excelRow, 2).Value = item.QuotationNumber;
                sheet.Cell(excelRow, 3).Value = item.ConvertedAt.ToString("O");
                sheet.Cell(excelRow, 4).Value = item.AdvisorName ?? string.Empty;
                sheet.Cell(excelRow, 5).Value = item.ClientName ?? string.Empty;
                sheet.Cell(excelRow, 6).Value = item.ClientCuc ?? string.Empty;
                sheet.Cell(excelRow, 7).Value = item.Status;
                sheet.Cell(excelRow, 8).Value = item.PaymentStatus;
                sheet.Cell(excelRow, 9).Value = item.Subtotal;
                sheet.Cell(excelRow, 10).Value = item.TaxAmount;
                sheet.Cell(excelRow, 11).Value = item.Total;
            },
            $"reporte-ventas-{generatedAt:yyyyMMdd-HHmmss}.xlsx",
            cancellationToken);

    public ReportFile BuildQuotations(
        IReadOnlyList<QuotationsReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken) =>
        Build(
            "Cotizaciones",
            QuotationsColumns,
            rows,
            (sheet, excelRow, item) =>
            {
                sheet.Cell(excelRow, 1).Value = item.QuotationNumber;
                sheet.Cell(excelRow, 2).Value = item.CreatedAt.ToString("O");
                // Sin hora: ValidUntil es una fecha calendaria, no un instante.
                sheet.Cell(excelRow, 3).Value =
                    item.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    ?? string.Empty;
                sheet.Cell(excelRow, 4).Value = item.AdvisorName ?? string.Empty;
                sheet.Cell(excelRow, 5).Value = item.ClientName ?? string.Empty;
                sheet.Cell(excelRow, 6).Value = item.ClientCuc ?? string.Empty;
                sheet.Cell(excelRow, 7).Value = item.Status;
                sheet.Cell(excelRow, 8).Value = item.Subtotal;
                sheet.Cell(excelRow, 9).Value = item.TaxAmount;
                sheet.Cell(excelRow, 10).Value = item.Total;
            },
            $"reporte-cotizaciones-{generatedAt:yyyyMMdd-HHmmss}.xlsx",
            cancellationToken);

    public ReportFile BuildPriceChanges(
        IReadOnlyList<PriceChangeReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken) =>
        Build(
            "Cambios de precio",
            PriceChangeColumns,
            rows,
            (sheet, excelRow, item) =>
            {
                sheet.Cell(excelRow, 1).Value = item.ChangedAt.ToString("O");
                sheet.Cell(excelRow, 2).Value = item.ProductName;
                sheet.Cell(excelRow, 3).Value = item.ProductCode;
                sheet.Cell(excelRow, 4).Value = item.Field;
                WriteOptionalNumber(sheet.Cell(excelRow, 5), item.ScaleFromUnit);
                WriteOptionalNumber(sheet.Cell(excelRow, 6), item.ScaleToUnit);
                WriteOptionalNumber(sheet.Cell(excelRow, 7), item.PreviousValue);
                WriteOptionalNumber(sheet.Cell(excelRow, 8), item.NewValue);
                sheet.Cell(excelRow, 9).Value = item.Difference;
                sheet.Cell(excelRow, 10).Value = item.ChangedByName ?? string.Empty;
            },
            $"reporte-cambios-precio-{generatedAt:yyyyMMdd-HHmmss}.xlsx",
            cancellationToken);

    public ReportFile BuildCustomers(
        IReadOnlyList<CustomerReportItemDto> rows,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken) =>
        Build(
            "Clientes",
            CustomerColumns,
            rows,
            (sheet, excelRow, item) =>
            {
                sheet.Cell(excelRow, 1).Value = item.Cuc;
                sheet.Cell(excelRow, 2).Value = item.Name;
                sheet.Cell(excelRow, 3).Value = item.IdentificationType;
                sheet.Cell(excelRow, 4).Value = item.IdentificationNumber;
                sheet.Cell(excelRow, 5).Value = item.ClassificationName ?? string.Empty;
                sheet.Cell(excelRow, 6).Value = item.DepartmentName ?? string.Empty;
                sheet.Cell(excelRow, 7).Value = item.CityName ?? string.Empty;
                // "Si"/"No" y no true/false: es el vocabulario de quien abre el archivo, y el
                // mismo que ya usa la exportacion del padron.
                sheet.Cell(excelRow, 8).Value = item.IsActive ? "Si" : "No";
                sheet.Cell(excelRow, 9).Value = item.CreatedAt.ToString("O");
            },
            $"reporte-clientes-{generatedAt:yyyyMMdd-HHmmss}.xlsx",
            cancellationToken);

    private static ReportFile Build<TItem>(
        string sheetName,
        IReadOnlyList<string> columns,
        IReadOnlyList<TItem> rows,
        Action<IXLWorksheet, int, TItem> writeRow,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName);

        for (var column = 0; column < columns.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = columns[column];
        }

        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writeRow(sheet, index + 2, rows[index]);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Row(1).Style.Font.Bold = true;
        var sheetColumns = sheet.Columns(1, columns.Count);
        sheetColumns.AdjustToContents();
        ApplyMinimumWidth(sheetColumns);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return new ReportFile(stream.ToArray(), fileName);
    }

    // Celda vacia y no un cero: en el historico de precios, "no habia valor anterior" y "el valor
    // anterior era 0" son cosas distintas, y un 0 escrito donde no habia nada le miente a quien
    // lee el archivo.
    private static void WriteOptionalNumber(IXLCell cell, decimal? value)
    {
        if (value is null)
        {
            cell.Value = string.Empty;
            return;
        }

        cell.Value = value.Value;
    }

    private static void WriteOptionalNumber(IXLCell cell, int? value)
    {
        if (value is null)
        {
            cell.Value = string.Empty;
            return;
        }

        cell.Value = value.Value;
    }

    private static void ApplyMinimumWidth(IXLColumns columns)
    {
        foreach (var column in columns)
        {
            if (column.Width < MinimumColumnWidth)
            {
                column.Width = MinimumColumnWidth;
            }
        }
    }
}
