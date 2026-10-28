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

    private static CustomerDto Customer(string? personType) => new(
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
        PersonType: personType);

    private XLWorkbook BuildWorkbook(params CustomerDto[] customers) =>
        new(new MemoryStream(
            builder.Build(
                customers,
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
}
