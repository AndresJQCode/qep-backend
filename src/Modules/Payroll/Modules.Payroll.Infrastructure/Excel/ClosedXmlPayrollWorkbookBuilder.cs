using System.Globalization;
using ClosedXML.Excel;
using Modules.Payroll.Application;

namespace Modules.Payroll.Infrastructure.Excel;

/// <summary>
/// El «Resumen de nómina» con ClosedXML. No calcula: el encabezado de la empresa (filas 1 a 5), los títulos del diseño (fila 7), una
/// fila por empleado, la fila <c>Totales</c> (los totales del backend como valores, nunca fórmulas) y la hoja <c>No liquidados</c>.
/// Qué columnas hay y cómo se leen es del <see cref="PayrollColumnLayout"/>.
/// </summary>
internal sealed class ClosedXmlPayrollWorkbookBuilder : IPayrollWorkbookBuilder
{
    public const string ExcludedSheetName = "No liquidados";
    public const string TotalsLabel = "Totales";

    private const int TitleRow = 7;
    private const string MoneyFormat = "#,##0";
    private const double MinimumColumnWidth = 12;

    public byte[] Build(PayrollWorkbook workbook, PayrollColumnLayout layout)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(layout);

        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add(layout.SheetName);
        WriteHeader(sheet, workbook);
        WriteTable(sheet, workbook, layout);
        WriteExcluded(book, workbook);

        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteHeader(IXLWorksheet sheet, PayrollWorkbook workbook)
    {
        sheet.Cell(1, 1).Value = "Empresa";
        sheet.Cell(1, 2).Value = workbook.CompanyName;
        sheet.Cell(1, 3).Value = "NIT";
        sheet.Cell(1, 4).SetValue(workbook.CompanyTaxId);
        sheet.Cell(2, 1).Value = "Período";
        sheet.Cell(2, 2).SetValue(
            $"{workbook.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} a {workbook.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        sheet.Cell(3, 1).Value = "Quincena";
        sheet.Cell(3, 2).Value = workbook.HalfLabel;
        sheet.Cell(4, 1).Value = "Estado";
        sheet.Cell(4, 2).Value = workbook.StatusLabel;
        sheet.Cell(5, 1).Value = "Generado";
        sheet.Cell(5, 2).SetValue(workbook.GeneratedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sheet.Range(1, 1, 5, 1).Style.Font.Bold = true;
        sheet.Cell(1, 3).Style.Font.Bold = true;
    }

    private static void WriteTable(IXLWorksheet sheet, PayrollWorkbook workbook, PayrollColumnLayout layout)
    {
        for (var column = 0; column < layout.Columns.Count; column++)
        {
            var title = sheet.Cell(TitleRow, column + 1);
            title.Value = layout.Columns[column].Header;
            title.Style.Font.Bold = true;
        }

        var rowNumber = TitleRow + 1;
        foreach (var row in workbook.Rows)
        {
            for (var column = 0; column < layout.Columns.Count; column++)
            {
                Write(sheet.Cell(rowNumber, column + 1), layout.Columns[column], layout.Columns[column].Value(row));
            }

            rowNumber++;
        }

        // The totals row: what the backend summed, as values.
        sheet.Cell(rowNumber, 1).Value = TotalsLabel;
        for (var column = 0; column < layout.Columns.Count; column++)
        {
            if (layout.Columns[column].Total is { } total)
            {
                Write(sheet.Cell(rowNumber, column + 1), layout.Columns[column], total(workbook.Totals));
            }
        }

        sheet.Range(rowNumber, 1, rowNumber, layout.Columns.Count).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(TitleRow);

        var columns = sheet.Columns(1, layout.Columns.Count);
        columns.AdjustToContents(TitleRow, TitleRow);
        foreach (var column in columns.Where(column => column.Width < MinimumColumnWidth))
        {
            column.Width = MinimumColumnWidth;
        }
    }

    private static void Write(IXLCell cell, PayrollColumn column, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case decimal money:
                cell.SetValue(money);
                break;
            case int whole:
                cell.SetValue(whole);
                break;
            default:
                // Text, documents included: a leading zero is not lost.
                cell.Style.NumberFormat.Format = "@";
                cell.SetValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                return;
        }

        if (column.Format == PayrollColumnFormat.Money)
        {
            cell.Style.NumberFormat.Format = MoneyFormat;
        }
    }

    private static void WriteExcluded(XLWorkbook book, PayrollWorkbook workbook)
    {
        var sheet = book.Worksheets.Add(ExcludedSheetName);
        string[] titles = ["Tipo de documento", "Número de documento", "Nombre", "Motivo"];
        for (var column = 0; column < titles.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = titles[column];
        }

        sheet.Row(1).Style.Font.Bold = true;
        for (var index = 0; index < workbook.Excluded.Count; index++)
        {
            var entry = workbook.Excluded[index];
            sheet.Cell(index + 2, 1).SetValue(entry.DocumentType);
            sheet.Cell(index + 2, 2).SetValue(entry.DocumentNumber);
            sheet.Cell(index + 2, 3).SetValue(entry.FullName);
            sheet.Cell(index + 2, 4).SetValue(entry.Reason);
        }

        var columns = sheet.Columns(1, titles.Length);
        columns.AdjustToContents();
        foreach (var column in columns.Where(column => column.Width < MinimumColumnWidth))
        {
            column.Width = MinimumColumnWidth;
        }
    }
}
