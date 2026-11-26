using ClosedXML.Excel;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Excel;

/// <summary>
/// La plantilla de empleados con ClosedXML (calco de <c>ClosedXmlServiceContractsImportTemplateBuilder</c>): la hoja
/// <c>Empleados</c> con los 36 encabezados y la hoja <c>Valores válidos</c> con una columna por lista, y la validación de lista
/// sobre las columnas de <c>Empleados</c> que las usan, para que no se pueda teclear un valor que la importación rechace. Lo del
/// tenant (empresas activas, cargos, áreas, centros no archivados) y el catálogo de países salen de lo que se le pasa.
/// </summary>
internal sealed class ClosedXmlEmployeeImportTemplateBuilder : IEmployeeImportTemplateBuilder
{
    private const double MinimumColumnWidth = 14;

    // Filas con lista desplegable: el tope de filas de la importación.
    private const int ValidationRows = HrImportColumns.MaxRows;

    private static int ColumnOf(string header) => HrImportColumns.Ordered.ToList().IndexOf(header) + 1;

    /// <summary>Una lista de «Valores válidos» y la columna de «Empleados» que la usa (<c>null</c>: sólo referencia).</summary>
    private sealed record ValueList(string Title, IReadOnlyList<string> Values, string? Header);

    public byte[] Build(HrSettingsIndex settings, HrCountryIndex countries)
    {
        using var workbook = new XLWorkbook();
        var data = workbook.Worksheets.Add(HrImportColumns.DataSheetName);
        for (var column = 0; column < HrImportColumns.Ordered.Count; column++)
        {
            data.Cell(1, column + 1).Value = HrImportColumns.Ordered[column];
        }

        data.SheetView.FreezeRows(1);
        data.Row(1).Style.Font.Bold = true;

        // Fechas como fecha: Excel ofrece el selector y el lector las recibe en ISO, sin depender del formato regional.
        foreach (var header in new[]
                 {
                     HrImportColumns.BirthDate, HrImportColumns.StartDate, HrImportColumns.EndDate, HrImportColumns.RetirementDate,
                 })
        {
            data.Range(2, ColumnOf(header), ValidationRows + 1, ColumnOf(header)).Style.DateFormat.Format = "yyyy-mm-dd";
        }

        // Documentos, teléfonos y cuentas como texto: un cero a la izquierda no se pierde.
        foreach (var header in new[]
                 {
                     HrImportColumns.DocumentNumber, HrImportColumns.Phone, HrImportColumns.BankAccountNumber,
                 })
        {
            data.Range(2, ColumnOf(header), ValidationRows + 1, ColumnOf(header)).Style.NumberFormat.Format = "@";
        }

        var dataColumns = data.Columns(1, HrImportColumns.Ordered.Count);
        dataColumns.AdjustToContents();
        ApplyMinimumWidth(dataColumns);

        ValueList[] lists =
        [
            new(HrImportColumns.ValuesTitles.DocumentTypes, HrLabels.Of<HrDocumentType>().ToList(), HrImportColumns.DocumentType),
            new(HrImportColumns.ValuesTitles.Countries, countries.Names, HrImportColumns.Country),
            new(HrImportColumns.ValuesTitles.ContributorSubtypes, HrLabels.Of<ContributorSubtype>().ToList(), HrImportColumns.ContributorSubtype),
            new(HrImportColumns.ValuesTitles.YesNo, [HrLabels.Yes, HrLabels.No], HrImportColumns.ForeignPensionExempt),
            new(HrImportColumns.ValuesTitles.PaymentMethods, HrLabels.Of<PaymentMethod>().ToList(), HrImportColumns.PaymentMethod),
            new(HrImportColumns.ValuesTitles.BankAccountTypes, HrLabels.Of<BankAccountType>().ToList(), HrImportColumns.BankAccountType),
            // Los días van separados por coma en una celda: la lista es sólo referencia.
            new(HrImportColumns.ValuesTitles.RestDays, HrLabels.Of<DayOfWeek>().ToList(), null),
            new(HrImportColumns.ValuesTitles.ContractTypes, HrLabels.Of<ContractType>().ToList(), HrImportColumns.ContractType),
            new(HrImportColumns.ValuesTitles.Durations, HrLabels.Of<ContractDuration>().ToList(), HrImportColumns.Duration),
            new(HrImportColumns.ValuesTitles.SalaryTypes, HrLabels.Of<SalaryType>().ToList(), HrImportColumns.SalaryType),
            new(HrImportColumns.ValuesTitles.RetirementReasons, HrLabels.Of<TerminationReason>().ToList(), HrImportColumns.RetirementReason),
            new(HrImportColumns.ValuesTitles.Companies, settings.CompanyLabels, HrImportColumns.Company),
            new(HrImportColumns.ValuesTitles.Positions, settings.PositionLabels, HrImportColumns.Position),
            new(HrImportColumns.ValuesTitles.Areas, settings.AreaLabels, HrImportColumns.Area),
            new(HrImportColumns.ValuesTitles.Workplaces, settings.WorkplaceLabels, HrImportColumns.Workplace),
        ];

        var reference = workbook.Worksheets.Add(HrImportColumns.ValuesSheetName);
        for (var list = 0; list < lists.Length; list++)
        {
            var referenceColumn = list + 1;
            reference.Cell(1, referenceColumn).Value = lists[list].Title;
            for (var index = 0; index < lists[list].Values.Count; index++)
            {
                reference.Cell(index + 2, referenceColumn).Value = lists[list].Values[index];
            }

            if (lists[list].Header is { } header && lists[list].Values.Count > 0)
            {
                var letter = reference.Cell(1, referenceColumn).Address.ColumnLetter;
                var validation = data.Range(2, ColumnOf(header), ValidationRows + 1, ColumnOf(header)).CreateDataValidation();
                validation.List($"'{HrImportColumns.ValuesSheetName}'!${letter}$2:${letter}${lists[list].Values.Count + 1}", true);
                validation.IgnoreBlanks = true;
            }
        }

        // Teletrabajador usa la misma lista de Sí / No que el exento de pensión.
        var yesNoLetter = reference.Cell(1, Array.FindIndex(lists, list => list.Title == HrImportColumns.ValuesTitles.YesNo) + 1).Address.ColumnLetter;
        var teleworker = data.Range(2, ColumnOf(HrImportColumns.Teleworker), ValidationRows + 1, ColumnOf(HrImportColumns.Teleworker)).CreateDataValidation();
        teleworker.List($"'{HrImportColumns.ValuesSheetName}'!${yesNoLetter}$2:${yesNoLetter}$3", true);
        teleworker.IgnoreBlanks = true;

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
