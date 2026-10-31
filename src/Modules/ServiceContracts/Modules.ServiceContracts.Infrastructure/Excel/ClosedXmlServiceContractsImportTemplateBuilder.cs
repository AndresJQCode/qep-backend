using ClosedXML.Excel;
using Modules.ServiceContracts.Application;

namespace Modules.ServiceContracts.Infrastructure.Excel;

/// <summary>
/// La plantilla de cobros recurrentes con ClosedXML (copia de <c>ClosedXmlCustomerImportTemplateBuilder</c>): la primera hoja
/// con los trece encabezados de <see cref="ServiceContractsImportColumns"/>, una hoja de referencia con los valores válidos y desplegables
/// en tipo de documento, moneda, ciclo y canal (§3.2b), para que no se pueda tipear un valor que la importación rechace.
/// </summary>
internal sealed class ClosedXmlServiceContractsImportTemplateBuilder : IServiceContractsImportTemplateBuilder
{
    private const string DataSheetName = "Cobros recurrentes";

    private const string ReferenceSheetName = "Referencia";

    private const double MinimumColumnWidth = 14;

    // Filas con desplegable: el tope de filas de la importación (ServiceContractsImportAnalyzer.MaxRows).
    private const int ValidationRows = ServiceContractsImportAnalyzer.MaxRows;

    private static int ColumnOf(string name) => ServiceContractsImportColumns.Ordered.ToList().IndexOf(name) + 1;

    public byte[] Build(
        IReadOnlyCollection<string> identificationTypes,
        IReadOnlyCollection<string> currencies,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        var data = workbook.Worksheets.Add(DataSheetName);
        for (var column = 0; column < ServiceContractsImportColumns.Ordered.Count; column++)
        {
            data.Cell(1, column + 1).Value = ServiceContractsImportColumns.Ordered[column];
        }

        data.SheetView.FreezeRows(1);
        data.Row(1).Style.Font.Bold = true;
        // Fecha como fecha: así Excel ofrece el selector y el lector la recibe en ISO, sin depender del formato regional.
        data.Range(2, ColumnOf(ServiceContractsImportColumns.DueDate), ValidationRows + 1, ColumnOf(ServiceContractsImportColumns.DueDate))
            .Style.DateFormat.Format = "yyyy-mm-dd";
        var dataColumns = data.Columns(1, ServiceContractsImportColumns.Ordered.Count);
        dataColumns.AdjustToContents();
        ApplyMinimumWidth(dataColumns);

        cancellationToken.ThrowIfCancellationRequested();

        string[][] lists =
        [
            identificationTypes.Order(StringComparer.Ordinal).ToArray(),
            currencies.ToArray(),
            ServiceContractsImportColumns.Cycles.ToArray(),
            ServiceContractsImportColumns.Channels.ToArray(),
        ];
        string[] titles = ["Tipos de documento", "Monedas", "Ciclos", "Canales de aviso"];
        string[] columns =
        [
            ServiceContractsImportColumns.IdentificationType,
            ServiceContractsImportColumns.Currency,
            ServiceContractsImportColumns.Cycle,
            ServiceContractsImportColumns.Channel,
        ];

        var reference = workbook.Worksheets.Add(ReferenceSheetName);
        for (var list = 0; list < lists.Length; list++)
        {
            var referenceColumn = list + 1;
            reference.Cell(1, referenceColumn).Value = titles[list];
            for (var index = 0; index < lists[list].Length; index++)
            {
                reference.Cell(index + 2, referenceColumn).Value = lists[list][index];
            }

            if (lists[list].Length > 0)
            {
                var letter = reference.Cell(1, referenceColumn).Address.ColumnLetter;
                var validation = data.Range(2, ColumnOf(columns[list]), ValidationRows + 1, ColumnOf(columns[list])).CreateDataValidation();
                validation.List($"{ReferenceSheetName}!${letter}$2:${letter}${lists[list].Length + 1}", true);
                validation.IgnoreBlanks = true;
            }
        }

        reference.Row(1).Style.Font.Bold = true;
        var referenceColumns = reference.Columns(1, lists.Length);
        referenceColumns.AdjustToContents();
        ApplyMinimumWidth(referenceColumns);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
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
