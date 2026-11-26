using System.Globalization;
using ClosedXML.Excel;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Hr.Infrastructure.Excel;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// El lector y la plantilla del Excel de valores legales (spec 2026-11-22, «Carga por Excel»). No levantan contenedor: los archivos se
/// arman con ClosedXML dentro de la prueba.
/// </summary>
public sealed class ClosedXmlLegalParameterWorkbookTests
{
    private static readonly DateOnly NextYearStart = new(2027, 1, 1);

    private static readonly string[] ParameterHeaders =
        [LegalParameterImportColumns.Key, LegalParameterImportColumns.EffectiveFrom, LegalParameterImportColumns.NewValue];

    private static readonly string[] BracketHeaders =
    [
        LegalParameterImportColumns.Key, LegalParameterImportColumns.EffectiveFrom, LegalParameterImportColumns.LowerBound,
        LegalParameterImportColumns.UpperBound, LegalParameterImportColumns.Rate,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static decimal ValueOf(LegalParameterKey key) => LegalParameterLabels.Of(key).Unit switch
    {
        LegalParameterUnit.Fraction => key == LegalParameterKey.HealthEmployeeRate ? 0.04m : 0.05m,
        LegalParameterUnit.Currency => 1_750_905m,
        LegalParameterUnit.Hours => 42m,
        LegalParameterUnit.Flag => 1m,
        _ => 25m,
    };

    private static LegalParameterTemplateData Data() => new(
        NextYearStart,
        Enum.GetValues<LegalParameterKey>()
            .Select(key => new LegalParameterTemplateRow(
                key,
                LegalParameterLabels.Of(key).Name,
                LegalParameterLabels.Of(key).Unit,
                ValueOf(key),
                new DateOnly(2026, 1, 1)))
            .ToList(),
        [
            new LegalBracketInput(4m, 16m, 0.01m),
            new LegalBracketInput(16m, 17m, 0.02m),
            new LegalBracketInput(17m, 18m, 0.025m),
            new LegalBracketInput(18m, 19m, 0.03m),
            new LegalBracketInput(19m, 20m, 0.035m),
            new LegalBracketInput(20m, null, 0.04m),
        ]);

    private static MemoryStream Stream(byte[] bytes) => new(bytes);

    private static MemoryStream Book(Action<XLWorkbook> build)
    {
        using var workbook = new XLWorkbook();
        build(workbook);
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static IXLWorksheet AddSheet(XLWorkbook workbook, string name, params string[] headers)
    {
        var sheet = workbook.Worksheets.Add(name);
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        return sheet;
    }

    [Fact]
    public async Task TheTemplateRoundTripsThroughTheReader()
    {
        var data = Data();
        var bytes = new ClosedXmlLegalParameterTemplateBuilder().Build(data);

        var workbook = await new ClosedXmlLegalParameterImporter().ReadAsync(Stream(bytes), Ct);

        Assert.Equal(30, workbook.Parameters.Count);
        Assert.Equal(6, workbook.Brackets.Count);
        foreach (var row in data.Parameters)
        {
            var read = workbook.Parameters.Single(item => item.Key == row.Key.ToString());
            if (LegalParameterLabels.Of(row.Key).Yearly)
            {
                Assert.Equal("2027-01-01", read.EffectiveFrom);
                Assert.Equal(row.CurrentValue!.Value.ToString(CultureInfo.InvariantCulture), read.NewValue);
            }
            else
            {
                Assert.Null(read.EffectiveFrom);
                Assert.Null(read.NewValue);
            }
        }

        Assert.Equal(27, workbook.Parameters.Count(item => item.NewValue is not null));
        Assert.All(workbook.Brackets, row => Assert.Equal("2027-01-01", row.EffectiveFrom));
        Assert.All(workbook.Brackets, row => Assert.Equal("SolidarityFund", row.Key));
        Assert.Equal(["4", "16", "17", "18", "19", "20"], workbook.Brackets.Select(row => row.LowerBound));
        Assert.Null(workbook.Brackets[^1].UpperBound);
        Assert.Equal("0.025", workbook.Brackets[2].Rate);
        Assert.Equal([2, 3, 4, 5, 6, 7], workbook.Brackets.Select(row => row.RowNumber));
    }

    [Fact]
    public void RateCellsArePercentFormatted()
    {
        var bytes = new ClosedXmlLegalParameterTemplateBuilder().Build(Data());

        using var workbook = new XLWorkbook(Stream(bytes));
        var sheet = workbook.Worksheet(LegalParameterImportColumns.ParametersSheet);
        var row = sheet.RowsUsed().Skip(1).Single(item => item.Cell(1).GetString() == "HealthEmployeeRate");
        var valueCell = row.Cell(6);

        Assert.Equal("0.00%", valueCell.Style.NumberFormat.Format);
        Assert.Equal(0.04, valueCell.GetDouble());
        Assert.Equal(LegalParameterImportColumns.NewValue, sheet.Cell(1, 6).GetString());
        var brackets = workbook.Worksheet(LegalParameterImportColumns.BracketsSheet);
        Assert.Equal("0.00%", brackets.Cell(2, 5).Style.NumberFormat.Format);
    }

    [Fact]
    public void OnlyTheEditableColumnsAreUnlocked()
    {
        var bytes = new ClosedXmlLegalParameterTemplateBuilder().Build(Data());

        using var workbook = new XLWorkbook(Stream(bytes));
        var sheet = workbook.Worksheet(LegalParameterImportColumns.ParametersSheet);

        Assert.True(sheet.Cell(2, 1).Style.Protection.Locked);
        Assert.True(sheet.Cell(2, 3).Style.Protection.Locked);
        Assert.False(sheet.Cell(2, 5).Style.Protection.Locked);
        Assert.False(sheet.Cell(2, 6).Style.Protection.Locked);
    }

    [Fact]
    public async Task AMissingHeaderIsHeaderMismatch()
    {
        using var stream = Book(workbook =>
        {
            AddSheet(
                workbook,
                LegalParameterImportColumns.ParametersSheet,
                LegalParameterImportColumns.Key,
                LegalParameterImportColumns.EffectiveFrom);
            AddSheet(workbook, LegalParameterImportColumns.BracketsSheet, BracketHeaders);
        });

        var error = await Assert.ThrowsAsync<HrDomainException>(
            () => new ClosedXmlLegalParameterImporter().ReadAsync(stream, Ct));

        Assert.Equal(HrErrorCodes.LegalParameterImportHeaderMismatch, error.Code);
    }

    [Fact]
    public async Task AMissingSheetIsHeaderMismatch()
    {
        using var stream = Book(workbook => AddSheet(workbook, LegalParameterImportColumns.ParametersSheet, ParameterHeaders));

        var error = await Assert.ThrowsAsync<HrDomainException>(
            () => new ClosedXmlLegalParameterImporter().ReadAsync(stream, Ct));

        Assert.Equal(HrErrorCodes.LegalParameterImportHeaderMismatch, error.Code);
    }

    [Fact]
    public async Task GarbageIsUnreadable()
    {
        using var stream = new MemoryStream("esto no es un excel"u8.ToArray());

        var error = await Assert.ThrowsAsync<HrDomainException>(
            () => new ClosedXmlLegalParameterImporter().ReadAsync(stream, Ct));

        Assert.Equal(HrErrorCodes.LegalParameterImportUnreadable, error.Code);
    }

    [Fact]
    public async Task BlankRowsAreSkippedAndHeadersIgnoreCaseAndAccents()
    {
        using var stream = Book(workbook =>
        {
            var parameters = AddSheet(workbook, "parametros", "clave", "RIGE DESDE", "valor nuevo");
            parameters.Cell(2, 1).Value = "Smmlv";
            parameters.Cell(2, 2).Value = new DateTime(2027, 1, 1);
            parameters.Cell(2, 3).Value = 2_000_000;
            parameters.Cell(4, 1).Value = "WeeklyHours";
            parameters.Cell(4, 2).Value = "2027-07-15";
            parameters.Cell(4, 3).Value = 42;
            var brackets = AddSheet(workbook, "tramos", BracketHeaders);
            brackets.Cell(2, 1).Value = "SolidarityFund";
            brackets.Cell(2, 2).Value = new DateTime(2027, 1, 1);
            brackets.Cell(2, 3).Value = 4;
            brackets.Cell(2, 4).Value = 16;
            brackets.Cell(2, 5).Value = 0.01;
            brackets.Cell(4, 1).Value = "SolidarityFund";
            brackets.Cell(4, 2).Value = new DateTime(2027, 1, 1);
            brackets.Cell(4, 3).Value = 16;
            brackets.Cell(4, 5).Value = 0.02;
        });

        var workbook = await new ClosedXmlLegalParameterImporter().ReadAsync(stream, Ct);

        Assert.Equal([2, 4], workbook.Parameters.Select(row => row.RowNumber));
        Assert.Equal("2027-01-01", workbook.Parameters[0].EffectiveFrom);
        Assert.Equal("2000000", workbook.Parameters[0].NewValue);
        Assert.Equal("2027-07-15", workbook.Parameters[1].EffectiveFrom);
        Assert.Equal([2, 4], workbook.Brackets.Select(row => row.RowNumber));
        Assert.Null(workbook.Brackets[1].UpperBound);
        Assert.Equal("0.02", workbook.Brackets[1].Rate);
    }
}
