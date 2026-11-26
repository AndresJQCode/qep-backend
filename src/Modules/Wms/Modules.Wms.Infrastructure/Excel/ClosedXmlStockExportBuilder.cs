using System.Globalization;
using ClosedXML.Excel;
using Modules.Tenancy.Application;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Excel;

/// <summary>
/// Arma el Excel de saldos con ClosedXML (RF-030). Las cantidades van como número y no como texto: el archivo se usa para sumar y
/// cruzar. «Para picking» (S10) viaja junto a «Disponible» porque un bin de reserva lleno no tiene nada que tomar aunque tenga
/// disponible.
/// </summary>
internal sealed class ClosedXmlStockExportBuilder : IStockExportBuilder
{
    private const string DataSheetName = "Saldos";

    // `AdjustToContents()` ajusta al ancho exacto del texto y deja la cabecera pegada a la celda siguiente; mismo piso que los demás exports.
    private const double MinimumColumnWidth = 14;

    private static readonly string[] Columns =
    [
        "Bodega",
        "Ubicación",
        "Propietario",
        "Producto",
        "Código",
        "Código de barras",
        "Lote",
        "Vence",
        "Estado",
        "Disponible",
        "Para picking",
        "Reservado",
        "En mano",
        "Unidad",
    ];

    public StockExportFile Build(IReadOnlyList<StockRowDto> rows, TenantCalendar calendar, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(calendar);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DataSheetName);

        for (var column = 0; column < Columns.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = Columns[column];
        }

        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRow(sheet, index + 2, rows[index]);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Row(1).Style.Font.Bold = true;
        var columns = sheet.Columns(1, Columns.Length);
        columns.AdjustToContents();
        foreach (var column in columns)
        {
            if (column.Width < MinimumColumnWidth)
            {
                column.Width = MinimumColumnWidth;
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        // En la hora del tenant: es la que la persona ve en su reloj.
        var generatedAtLocal = calendar.ToLocal(calendar.UtcNow);
        return new StockExportFile(
            stream.ToArray(), $"saldos-{generatedAtLocal.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.xlsx");
    }

    private static void WriteRow(IXLWorksheet sheet, int row, StockRowDto item)
    {
        sheet.Cell(row, 1).Value = item.WarehouseCode;
        sheet.Cell(row, 2).Value = item.LocationPath;
        sheet.Cell(row, 3).Value = item.OwnerName;
        sheet.Cell(row, 4).Value = item.ProductName;
        sheet.Cell(row, 5).Value = item.VariantCode;
        sheet.Cell(row, 6).Value = item.Barcode ?? string.Empty;
        sheet.Cell(row, 7).Value = item.LotCode ?? string.Empty;
        if (item.ExpirationDate is { } expiration)
        {
            sheet.Cell(row, 8).Value = expiration.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 8).Style.DateFormat.Format = "yyyy-mm-dd";
        }

        sheet.Cell(row, 9).Value = item.Status;
        sheet.Cell(row, 10).Value = item.Available;
        sheet.Cell(row, 11).Value = item.Pickable;
        sheet.Cell(row, 12).Value = item.Reserved;
        sheet.Cell(row, 13).Value = item.OnHand;
        sheet.Cell(row, 14).Value = item.BaseUom;
    }
}
