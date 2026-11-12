using ClosedXML.Excel;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Excel;
using Modules.Tenancy.Application;

namespace Modules.Customers.UnitTests.Excel;

/// <summary>Spec 2026-10-26: la columna «Tipo de persona» del Excel de clientes va al final y no toca las de importación.</summary>
public sealed class ClosedXmlCustomerExportBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private readonly ClosedXmlCustomerExportBuilder builder = new();

    private static CustomerDto Customer(
        string? personType, IReadOnlyList<CustomerWithholdingTaxRateDto>? withholdingTaxRates = null) => new(
        Guid.NewGuid(),
        "CLI08000142",
        "Verde Esencial",
        null,
        "NIT",
        "900123456",
        "3109352187",
        "ficha@x.co",
        "Calle 10 # 45-12",
        "CO",
        null,
        null,
        null,
        null,
        [],
        false,
        false,
        true,
        Now,
        Now,
        true,
        [],
        new MarketingConsentDto(false, null, null),
        PersonType: personType,
        WithholdingTaxRates: withholdingTaxRates);

    private XLWorkbook BuildWorkbook(params CustomerDto[] customers) =>
        BuildWorkbook(new Dictionary<Guid, string>(), customers);

    private XLWorkbook BuildWorkbook(IReadOnlyDictionary<Guid, string> companyNumbers, params CustomerDto[] customers) =>
        new(new MemoryStream(
            builder.Build(
                customers,
                companyNumbers,
                new TenantCalendar(Now, Bogota),
                "yyyy-MM-dd HH:mm",
                CancellationToken.None).Content));

    private static string[] Headers(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheet("Clientes");
        return [.. sheet.Row(1).CellsUsed().Select(cell => cell.GetString())];
    }

    [Fact]
    public void PersonTypeIsTheLastColumnAfterCompleteness()
    {
        using var workbook = BuildWorkbook(Customer("Juridica"));

        var headers = Headers(workbook);

        Assert.Equal("Tipo de persona", headers[^1]);
        Assert.Equal("Estado de la ficha", headers[^2]);
    }

    [Fact]
    public void PersonTypeCellsAreNaturalJuridicaOrEmpty()
    {
        using var workbook = BuildWorkbook(Customer("Natural"), Customer("Juridica"), Customer(null));

        var sheet = workbook.Worksheet("Clientes");
        var column = Headers(workbook).Length;

        Assert.Equal("Natural", sheet.Cell(2, column).GetString());
        Assert.Equal("Juridica", sheet.Cell(3, column).GetString());
        Assert.Equal(string.Empty, sheet.Cell(4, column).GetString());
    }

    [Fact]
    public void ImportColumnsAreUnchanged()
    {
        using var workbook = BuildWorkbook(Customer(null));

        var headers = Headers(workbook);

        Assert.Equal(CustomerImportColumns.Ordered, headers.Take(CustomerImportColumns.Ordered.Count));
        Assert.DoesNotContain("Tipo de persona", headers.Take(CustomerImportColumns.Ordered.Count));
    }

    private static CustomerWithholdingTaxRateDto Rate(string name, string type, bool isActive = true) =>
        new(Guid.NewGuid(), name, type, "CO", 2.5m, isActive);

    private static string CellOf(XLWorkbook workbook, int excelRow, string header)
    {
        var column = Array.IndexOf(Headers(workbook), header) + 1;
        Assert.True(column > 0, $"Missing column '{header}'.");
        return workbook.Worksheet("Clientes").Cell(excelRow, column).GetString();
    }

    [Fact]
    public void ExportWritesRateNamesPerType()
    {
        using var workbook = BuildWorkbook(
            Customer(null, [Rate("Retefuente 2,5 %", "Retefuente"), Rate("ReteICA 0,966 %", "ReteICA")]),
            Customer(null, [Rate("ReteICA 0,966 %", "ReteICA")]),
            Customer(null));

        Assert.Equal("Retefuente 2,5 %", CellOf(workbook, 2, CustomerImportColumns.Retefuente));
        Assert.Equal("ReteICA 0,966 %", CellOf(workbook, 2, CustomerImportColumns.ReteIca));
        Assert.Equal(string.Empty, CellOf(workbook, 3, CustomerImportColumns.Retefuente));
        Assert.Equal("ReteICA 0,966 %", CellOf(workbook, 3, CustomerImportColumns.ReteIca));
        Assert.Equal(string.Empty, CellOf(workbook, 4, CustomerImportColumns.Retefuente));
        Assert.Equal(string.Empty, CellOf(workbook, 4, CustomerImportColumns.ReteIca));
        Assert.DoesNotContain("Con Retencion", Headers(workbook));
    }

    [Fact]
    public void TheExportCarriesTheCompanyDocumentNumber()
    {
        var contact = Customer("Natural");
        var alone = Customer(null);
        using var workbook = BuildWorkbook(new Dictionary<Guid, string> { [contact.Id] = "900111222" }, contact, alone);

        var headers = Headers(workbook);
        var sheet = workbook.Worksheet("Clientes");
        var column = Array.IndexOf(headers, CustomerImportColumns.Company) + 1;

        // La cabecera es la misma que el importador lee, así que el archivo exportado se puede corregir y volver a subir.
        Assert.True(column > 0);
        Assert.Equal("900111222", sheet.Cell(2, column).GetString());
        Assert.Equal(string.Empty, sheet.Cell(3, column).GetString());
        // Justo después de las de importación y antes de las propias del export.
        Assert.Equal(CustomerImportColumns.Ordered.Count + 1, column);
    }
}
