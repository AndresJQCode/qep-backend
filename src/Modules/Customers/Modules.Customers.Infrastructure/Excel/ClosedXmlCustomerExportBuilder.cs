using ClosedXML.Excel;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Excel;

/// <summary>
/// Arma el Excel del padron con ClosedXML.
///
/// Las diez columnas de <see cref="CustomerImportColumns"/> van primero y en su orden exacto, y las
/// cuatro propias al final. Esa forma es deliberada: el importador ubica las columnas **por nombre
/// de cabecera** (ver <c>ClosedXmlCustomerImporter</c>), asi que un archivo exportado se puede
/// corregir y volver a importar sin editarle la estructura, y las columnas de mas no le molestan.
/// </summary>
internal sealed class ClosedXmlCustomerExportBuilder : ICustomerExportBuilder
{
    private const string DataSheetName = "Clientes";

    // Mismo piso que la plantilla de importacion: `AdjustToContents()` ajusta al ancho exacto del
    // texto y deja la cabecera pegada al borde de la celda siguiente.
    private const double MinimumColumnWidth = 14;

    private const string CucColumn = "CUC";
    private const string ActiveColumn = "Activo";
    private const string CreatedAtColumn = "Creado";
    private const string UpdatedAtColumn = "Actualizado";

    private static readonly IReadOnlyList<string> Columns =
    [
        .. CustomerImportColumns.Ordered,
        CucColumn,
        ActiveColumn,
        CreatedAtColumn,
        UpdatedAtColumn
    ];

    public CustomerExportFile Build(
        IReadOnlyList<CustomerDto> customers,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DataSheetName);

        for (var column = 0; column < Columns.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = Columns[column];
        }

        for (var index = 0; index < customers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRow(sheet, index + 2, customers[index]);
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Row(1).Style.Font.Bold = true;
        var columns = sheet.Columns(1, Columns.Count);
        columns.AdjustToContents();
        ApplyMinimumWidth(columns);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return new CustomerExportFile(
            stream.ToArray(),
            $"clientes-{generatedAt:yyyyMMdd-HHmmss}.xlsx");
    }

    // El mismo orden que `Columns`. Los textos de las columnas compartidas son los que el
    // importador espera leer: el nombre de la clasificacion y del departamento/ciudad, no sus ids.
    private static void WriteRow(IXLWorksheet sheet, int excelRow, CustomerDto customer)
    {
        sheet.Cell(excelRow, 1).Value = customer.Name;
        sheet.Cell(excelRow, 2).Value = customer.IdentificationType;
        sheet.Cell(excelRow, 3).Value = customer.IdentificationNumber;
        sheet.Cell(excelRow, 4).Value = customer.Phone ?? string.Empty;
        sheet.Cell(excelRow, 5).Value = customer.Email ?? string.Empty;
        sheet.Cell(excelRow, 6).Value = customer.Address ?? string.Empty;
        sheet.Cell(excelRow, 7).Value = customer.Department.Name;
        sheet.Cell(excelRow, 8).Value = customer.City.Name;
        sheet.Cell(excelRow, 9).Value = customer.Classification.Name;
        // "Si"/"No" y no true/false: es el vocabulario que el importador lee y el que ve la persona
        // que abre el archivo.
        sheet.Cell(excelRow, 10).Value = customer.WithRetention ? "Si" : "No";
        sheet.Cell(excelRow, 11).Value = customer.Cuc;
        sheet.Cell(excelRow, 12).Value = customer.IsActive ? "Si" : "No";
        // Como texto ISO-8601 y no como fecha de Excel: una celda de fecha se muestra segun la
        // configuracion regional de quien abre el archivo, y ahi 03/04 deja de ser una fecha sola.
        sheet.Cell(excelRow, 13).Value = customer.CreatedAt.ToString("O");
        sheet.Cell(excelRow, 14).Value = customer.UpdatedAt.ToString("O");
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
