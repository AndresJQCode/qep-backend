using ClosedXML.Excel;
using Modules.Payroll.Application;
using Modules.Payroll.Infrastructure.Excel;

namespace Modules.Payroll.IntegrationTests;

/// <summary>The structural reader of the novelties file: it must not walk a file past the row limit (a hostile workbook can claim a million rows).</summary>
public sealed class ClosedXmlInputsImporterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryStream Workbook(int dataRows, int? strayRow = null)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(InputsImportColumns.DataSheetName);
        for (var column = 0; column < InputsImportColumns.Ordered.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = InputsImportColumns.Ordered[column];
        }

        for (var row = 0; row < dataRows; row++)
        {
            sheet.Cell(row + 2, 1).Value = "1001";
            sheet.Cell(row + 2, 3).Value = "Comisiones";
            sheet.Cell(row + 2, 5).Value = "1";
        }

        if (strayRow is { } stray)
        {
            sheet.Cell(stray, 1).Value = "1001";
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task ItStopsOneRowPastTheLimit()
    {
        await using var stream = Workbook(InputsImportColumns.MaxRows + 50);

        var rows = await new ClosedXmlInputsImporter().ReadAsync(stream, Ct);

        // MaxRows + 1 is enough for the analyzer to answer too_many_rows; the rest is never read.
        Assert.Equal(InputsImportColumns.MaxRows + 1, rows.Count);
    }

    [Fact]
    public async Task AFileWithinTheLimitIsReadWhole()
    {
        await using var stream = Workbook(InputsImportColumns.MaxRows, strayRow: 700_000);

        var rows = await new ClosedXmlInputsImporter().ReadAsync(stream, Ct);

        Assert.Equal(InputsImportColumns.MaxRows + 1, rows.Count);
        Assert.Equal(700_000, rows[^1].RowNumber);
    }
}
