using ClosedXML.Excel;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Excel;

/// <summary>
/// La plantilla anual de valores legales con ClosedXML. «Parámetros» lleva una fila por clave (30): sólo las 27 claves anuales vienen
/// prellenadas con <c>Rige desde</c> el 1 de enero siguiente y <c>Valor nuevo</c> igual al que seguiría rigiendo; las tres que no son
/// anuales (el recargo dominical, la jornada y el inicio de la conectividad) vienen vacías, porque arrastrar su valor al 1 de enero
/// movería la fecha desde la que rige la conectividad o cambiaría la jornada, que cambia el 15 de julio. «Tramos» trae el conjunto
/// vigente a esa misma fecha. Las columnas que son sólo lectura van en gris y bloqueadas.
/// </summary>
internal sealed class ClosedXmlLegalParameterTemplateBuilder : ILegalParameterTemplateBuilder
{
    private const string PercentFormat = "0.00%";
    private const string DateFormat = "yyyy-mm-dd";

    // «Parámetros»: Clave, Nombre, Valor vigente, Vigente desde, Rige desde, Valor nuevo.
    private const int KeyColumn = 1;
    private const int NameColumn = 2;
    private const int CurrentValueColumn = 3;
    private const int CurrentFromColumn = 4;
    private const int EffectiveFromColumn = 5;
    private const int NewValueColumn = 6;

    // «Tramos»: Clave, Rige desde, Desde, Hasta, Tasa.
    private const int BracketKeyColumn = 1;
    private const int BracketFromColumn = 2;
    private const int LowerColumn = 3;
    private const int UpperColumn = 4;
    private const int RateColumn = 5;

    private static readonly XLColor ReadOnlyFill = XLColor.FromHtml("#EEEEEE");

    public byte[] Build(LegalParameterTemplateData data)
    {
        using var workbook = new XLWorkbook();
        BuildParameters(workbook.Worksheets.Add(LegalParameterImportColumns.ParametersSheet), data);
        BuildBrackets(workbook.Worksheets.Add(LegalParameterImportColumns.BracketsSheet), data);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildParameters(IXLWorksheet sheet, LegalParameterTemplateData data)
    {
        string[] headers =
        [
            LegalParameterImportColumns.Key, LegalParameterImportColumns.Name, LegalParameterImportColumns.CurrentValue,
            LegalParameterImportColumns.CurrentFrom, LegalParameterImportColumns.EffectiveFrom, LegalParameterImportColumns.NewValue,
        ];
        for (var column = 0; column < headers.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = headers[column];
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        var row = 1;
        foreach (var item in data.Parameters)
        {
            row++;
            var yearly = LegalParameterLabels.Of(item.Key).Yearly;
            var format = item.Unit == LegalParameterUnit.Fraction ? PercentFormat : null;

            sheet.Cell(row, KeyColumn).Value = item.Key.ToString();
            sheet.Cell(row, NameColumn).Value = item.Name;
            if (item.CurrentValue is { } current)
            {
                sheet.Cell(row, CurrentValueColumn).Value = (double)current;
            }

            if (item.CurrentFrom is { } from)
            {
                sheet.Cell(row, CurrentFromColumn).Value = from.ToDateTime(TimeOnly.MinValue);
            }

            sheet.Cell(row, CurrentFromColumn).Style.DateFormat.Format = DateFormat;
            sheet.Cell(row, EffectiveFromColumn).Style.DateFormat.Format = DateFormat;
            if (format is not null)
            {
                sheet.Cell(row, CurrentValueColumn).Style.NumberFormat.Format = format;
                sheet.Cell(row, NewValueColumn).Style.NumberFormat.Format = format;
            }

            if (yearly)
            {
                sheet.Cell(row, EffectiveFromColumn).Value = data.NextYearStart.ToDateTime(TimeOnly.MinValue);
                if (item.CurrentValue is { } carried)
                {
                    sheet.Cell(row, NewValueColumn).Value = (double)carried;
                }
            }
        }

        // Lo de sólo lectura, gris y bloqueado; lo que el operador completa, desbloqueado. La protección no lleva clave: avisa, no encierra.
        var lastRow = Math.Max(row, 2);
        sheet.Range(2, KeyColumn, lastRow, CurrentFromColumn).Style.Fill.BackgroundColor = ReadOnlyFill;
        sheet.Range(2, EffectiveFromColumn, lastRow, NewValueColumn).Style.Protection.Locked = false;
        sheet.Protect();

        sheet.Columns(1, headers.Length).AdjustToContents();
    }

    private static void BuildBrackets(IXLWorksheet sheet, LegalParameterTemplateData data)
    {
        string[] headers =
        [
            LegalParameterImportColumns.Key, LegalParameterImportColumns.EffectiveFrom, LegalParameterImportColumns.LowerBound,
            LegalParameterImportColumns.UpperBound, LegalParameterImportColumns.Rate,
        ];
        for (var column = 0; column < headers.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = headers[column];
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        var row = 1;
        foreach (var bracket in data.CurrentBrackets)
        {
            row++;
            sheet.Cell(row, BracketKeyColumn).Value = nameof(LegalBracketKey.SolidarityFund);
            sheet.Cell(row, BracketFromColumn).Value = data.NextYearStart.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, LowerColumn).Value = (double)bracket.LowerBound;
            if (bracket.UpperBound is { } upper)
            {
                sheet.Cell(row, UpperColumn).Value = (double)upper;
            }

            sheet.Cell(row, RateColumn).Value = (double)bracket.Rate;
        }

        // Fechas y tasas con formato desde la primera fila hasta el tope: lo que se agregue a mano queda igual.
        var lastRow = Math.Max(row, LegalParameterImportColumns.MaxRows + 1);
        sheet.Range(2, BracketFromColumn, lastRow, BracketFromColumn).Style.DateFormat.Format = DateFormat;
        sheet.Range(2, RateColumn, lastRow, RateColumn).Style.NumberFormat.Format = PercentFormat;

        sheet.Columns(1, headers.Length).AdjustToContents();
    }
}
