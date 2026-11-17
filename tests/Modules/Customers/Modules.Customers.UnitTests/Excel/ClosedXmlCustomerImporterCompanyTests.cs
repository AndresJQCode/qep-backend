using ClosedXML.Excel;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Excel;

namespace Modules.Customers.UnitTests.Excel;

/// <summary>G5: el importador lee por cabecera y exige las trece de <see cref="CustomerImportColumns.Ordered"/>; «Empresa» es
/// opcional, así que un archivo anterior a la columna sigue siendo válido y se lee como «sin empresa».</summary>
public sealed class ClosedXmlCustomerImporterCompanyTests
{
    private static MemoryStream Workbook(bool withCompanyColumn, string? company, params string[] skipHeaders)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Clientes");
        var headers = CustomerImportColumns.Ordered.Where(header => !skipHeaders.Contains(header)).ToList();
        if (withCompanyColumn)
        {
            headers.Add(CustomerImportColumns.Company);
        }

        for (var column = 0; column < headers.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = headers[column];
            sheet.Cell(2, column + 1).Value = headers[column] switch
            {
                CustomerImportColumns.Name => "Ana",
                CustomerImportColumns.Company => company ?? string.Empty,
                _ => "x",
            };
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static ExcelCustomerImportFile Parse(Stream content) =>
        new ClosedXmlCustomerImporter().Parse(content, CancellationToken.None);

    [Fact]
    public void AFileWithoutTheCompanyColumnIsStillValidAndReadsAsNoCompany()
    {
        var file = Parse(Workbook(withCompanyColumn: false, company: null));

        Assert.True(file.HasExpectedColumns);
        Assert.Null(Assert.Single(file.Rows).CompanyIdentificationNumber);
    }

    [Fact]
    public void TheCompanyColumnIsReadByItsHeader()
    {
        var file = Parse(Workbook(withCompanyColumn: true, company: "  900111222 "));

        Assert.True(file.HasExpectedColumns);
        Assert.Equal("900111222", Assert.Single(file.Rows).CompanyIdentificationNumber);
    }

    [Fact]
    public void AnEmptyCompanyCellReadsAsNull()
    {
        var file = Parse(Workbook(withCompanyColumn: true, company: null));

        Assert.Null(Assert.Single(file.Rows).CompanyIdentificationNumber);
    }

    [Fact]
    public void AMissingRequiredHeaderStillRejectsTheWholeFile()
    {
        var file = Parse(Workbook(withCompanyColumn: true, company: "900111222", CustomerImportColumns.Phone));

        Assert.False(file.HasExpectedColumns);
    }
}
