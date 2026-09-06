using ClosedXML.Excel;
using Modules.Customers.Application;
using Modules.Customers.Infrastructure.Excel;

namespace Modules.Customers.UnitTests.Excel;

/// <summary>
/// Cubre lo que <c>CustomerStatusAndImportApiTests.DownloadTemplateReturnsAnExcelWithTheExpectedColumns</c>
/// no cubre: que las columnas con catalogo cerrado (Tipo Identificacion, Departamento,
/// Clasificacion) traigan un desplegable, y que Ciudad traiga uno en cascada — solo las ciudades
/// del departamento que esa misma fila declaro, no las 1122 del pais.
/// </summary>
public sealed class ClosedXmlCustomerImportTemplateBuilderTests
{
    private static readonly CustomerImportDepartmentOption[] Departments =
    [
        new("Antioquia", "05", ["Medellin", "Envigado"]),
        new("Valle del Cauca", "76", ["Cali", "Palmira", "Buga"])
    ];

    private static readonly string[] Classifications = ["Mayorista", "Minorista"];

    private static readonly string[] IdentificationTypes = ["NIT", "CC", "CE", "PASAPORTE"];

    private readonly ClosedXmlCustomerImportTemplateBuilder builder = new();

    private XLWorkbook BuildWorkbook() =>
        new(new MemoryStream(
            builder.Build(Departments, Classifications, IdentificationTypes, CancellationToken.None)));

    [Fact]
    public void IdentificationTypeColumnListsTheFourSupportedTypesSorted()
    {
        using var workbook = BuildWorkbook();

        var validation = FindValidation(workbook, CustomerImportColumns.IdentificationType);

        Assert.Equal("Referencia!$C$2:$C$5", validation.Value);
    }

    [Fact]
    public void DepartmentColumnListsEveryDepartmentName()
    {
        using var workbook = BuildWorkbook();

        var validation = FindValidation(workbook, CustomerImportColumns.Department);

        Assert.Equal("Referencia!$A$2:$A$3", validation.Value);
        var reference = workbook.Worksheet("Referencia");
        Assert.Equal("Antioquia", reference.Cell(2, 1).GetString());
        Assert.Equal("Valle del Cauca", reference.Cell(3, 1).GetString());
    }

    [Fact]
    public void ClassificationColumnListsEveryActiveClassificationName()
    {
        using var workbook = BuildWorkbook();

        var validation = FindValidation(workbook, CustomerImportColumns.Classification);

        Assert.Equal("Referencia!$B$2:$B$3", validation.Value);
    }

    [Fact]
    public void CityColumnResolvesTheDropdownFromTheRowsOwnDepartmentCell()
    {
        using var workbook = BuildWorkbook();

        var validation = FindValidation(workbook, CustomerImportColumns.City);

        // La letra sale del orden real de las columnas: agregar una antes de Departamento
        // (Razon Social lo hizo) corre la referencia, y fijarla a mano deja la prueba en falso.
        var department = (char)('A' + CustomerImportColumns.Ordered.ToList()
            .IndexOf(CustomerImportColumns.Department));
        Assert.Equal(
            $"INDIRECT(VLOOKUP(${department}2,Referencia!$A:$D,4,FALSE))", validation.Value);
    }

    [Fact]
    public void CucIsTheFirstColumnAndHasNoDropdown()
    {
        using var workbook = BuildWorkbook();
        var dataSheet = workbook.Worksheet("Clientes");

        Assert.Equal(CustomerImportColumns.Cuc, dataSheet.Cell(1, 1).GetString());
        Assert.DoesNotContain(
            dataSheet.DataValidations,
            validation => validation.Ranges.Any(range =>
                (range.RangeAddress.ToString() ?? string.Empty).StartsWith("A2:", StringComparison.Ordinal)));
    }

    [Fact]
    public void ClassificationColumnHeaderIsTamano()
    {
        Assert.Equal("Tamano", CustomerImportColumns.Classification);
    }

    [Fact]
    public void EachDepartmentGetsANamedRangeWithOnlyItsOwnCitiesSorted()
    {
        using var workbook = BuildWorkbook();

        var antioquiaCities = ReadNamedRange(workbook, "Dept_05");
        var valleCities = ReadNamedRange(workbook, "Dept_76");

        Assert.Equal(["Envigado", "Medellin"], antioquiaCities);
        Assert.Equal(["Buga", "Cali", "Palmira"], valleCities);
    }

    [Fact]
    public void TheCityListsSheetIsHiddenFromTheSheetTabBar()
    {
        using var workbook = BuildWorkbook();

        var sheet = workbook.Worksheet("ListasCiudades");

        Assert.Equal(XLWorksheetVisibility.VeryHidden, sheet.Visibility);
    }

    [Fact]
    public void ADepartmentWithNoCitiesGetsNoNamedRangeAndDoesNotBreakTheOthers()
    {
        CustomerImportDepartmentOption[] departmentsWithAnEmptyOne =
            [new("Amazonas", "91", []), .. Departments];

        using var workbook = new XLWorkbook(new MemoryStream(
            builder.Build(
                departmentsWithAnEmptyOne, Classifications, IdentificationTypes, CancellationToken.None)));

        Assert.DoesNotContain(workbook.DefinedNames, name => name.Name == "Dept_91");
        Assert.Equal(["Envigado", "Medellin"], ReadNamedRange(workbook, "Dept_05"));
    }

    private static IXLDataValidation FindValidation(XLWorkbook workbook, string columnHeader)
    {
        var dataSheet = workbook.Worksheet("Clientes");
        var columnNumber = CustomerImportColumns.Ordered.ToList().IndexOf(columnHeader) + 1;
        var columnLetter = dataSheet.Cell(1, columnNumber).Address.ColumnLetter;

        return Assert.Single(
            dataSheet.DataValidations,
            validation => validation.Ranges.Any(range =>
                (range.RangeAddress.ToString() ?? string.Empty)
                    .StartsWith($"{columnLetter}2:", StringComparison.Ordinal)));
    }

    private static string[] ReadNamedRange(XLWorkbook workbook, string name)
    {
        var definedName = Assert.Single(workbook.DefinedNames, dn => dn.Name == name);
        return definedName.Ranges
            .SelectMany(range => range.Cells())
            .Select(cell => cell.GetString())
            .ToArray();
    }
}
