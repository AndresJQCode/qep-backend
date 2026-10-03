using System.Text;
using Modules.Geography.Infrastructure.Seed;

namespace Modules.Geography.UnitTests;

public sealed class DivipolaDataParserTests
{
    [Fact]
    public void ParseDepartmentsReturnsOneRecordPerValidEntry()
    {
        var json = """
            [
              { "code": "05", "name": "ANTIOQUIA" },
              { "code": "08", "name": "ATLÁNTICO" }
            ]
            """;

        var records = DivipolaDataParser.ParseDepartments(ToStream(json));

        Assert.Equal(2, records.Count);
        Assert.Contains(records, record => record.Code == "05" && record.Name == "ANTIOQUIA");
        Assert.Contains(records, record => record.Code == "08" && record.Name == "ATLÁNTICO");
    }

    [Fact]
    public void ParseDepartmentsThrowsOnDuplicateCode()
    {
        var json = """
            [
              { "code": "05", "name": "ANTIOQUIA" },
              { "code": "05", "name": "ANTIOQUIA OTRA VEZ" }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseDepartments(ToStream(json)));
    }

    [Fact]
    public void ParseDepartmentsThrowsWhenCodeIsNotTwoDigits()
    {
        var json = """
            [
              { "code": "5", "name": "ANTIOQUIA" }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseDepartments(ToStream(json)));
    }

    [Fact]
    public void ParseDepartmentsThrowsWhenNameIsEmpty()
    {
        var json = """
            [
              { "code": "05", "name": "" }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseDepartments(ToStream(json)));
    }

    [Fact]
    public void ParseCitiesSkipsEightDigitPopulatedCenterEntries()
    {
        var json = """
            [
              {
                "code": "05001",
                "name": "MEDELLÍN",
                "departmentCode": "05"
              },
              {
                "code": "05001000",
                "name": "MEDELLÍN, DISTRITO ESPECIAL",
                "departmentCode": "05"
              }
            ]
            """;

        var records = DivipolaDataParser.ParseCities(ToStream(json));

        Assert.Single(records);
        Assert.Contains(records, record =>
            record.DivipolaCode == "05001" &&
            record.Name == "MEDELLÍN" &&
            record.DepartmentCode == "05");
    }

    [Fact]
    public void ParseCitiesSkipsEntriesWithCodeLengthOtherThanFive()
    {
        var json = """
            [
              {
                "code": "0500100",
                "name": "MEDELLÍN",
                "departmentCode": "05"
              }
            ]
            """;

        var records = DivipolaDataParser.ParseCities(ToStream(json));

        Assert.Empty(records);
    }

    [Fact]
    public void ParseCitiesThrowsWhenFiveDigitCodeIsNotAllDigits()
    {
        var json = """
            [
              {
                "code": "ABCDE",
                "name": "MEDELLÍN",
                "departmentCode": "05"
              }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseCities(ToStream(json)));
    }

    [Fact]
    public void ParseCitiesThrowsWhenCodeDoesNotStartWithDeclaredDepartmentCode()
    {
        var json = """
            [
              {
                "code": "05001",
                "name": "MEDELLÍN",
                "departmentCode": "08"
              }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseCities(ToStream(json)));
    }

    [Fact]
    public void ParseCitiesThrowsOnDuplicateCode()
    {
        var json = """
            [
              {
                "code": "05001",
                "name": "MEDELLÍN",
                "departmentCode": "05"
              },
              {
                "code": "05001",
                "name": "MEDELLÍN OTRA VEZ",
                "departmentCode": "05"
              }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseCities(ToStream(json)));
    }

    [Fact]
    public void ParseCitiesThrowsWhenNameIsEmpty()
    {
        var json = """
            [
              {
                "code": "05001",
                "name": "",
                "departmentCode": "05"
              }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseCities(ToStream(json)));
    }

    [Fact]
    public void ParseCoordinadoraNamesReturnsOneRecordPerValidEntry()
    {
        var json = """
            [
              { "code": "05001", "name": "MEDELLIN (ANT)" },
              { "code": "05002", "name": "ABEJORRAL (ANT)" }
            ]
            """;

        var records = DivipolaDataParser.ParseCoordinadoraNames(ToStream(json));

        Assert.Equal(2, records.Count);
        Assert.Contains(records, record => record.DivipolaCode == "05001" && record.Name == "MEDELLIN (ANT)");
        Assert.Contains(records, record => record.DivipolaCode == "05002" && record.Name == "ABEJORRAL (ANT)");
    }

    [Fact]
    public void ParseCoordinadoraNamesThrowsOnDuplicateCode()
    {
        var json = """
            [
              { "code": "05002", "name": "ABEJORRAL (ANT)" },
              { "code": "05002", "name": "ABEJORRAL OTRA VEZ (ANT)" }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseCoordinadoraNames(ToStream(json)));
    }

    // A diferencia de ParseCities, un código que no es de municipio no se descarta: el snapshot ya
    // viene filtrado a municipios, así que un código de 8 dígitos es una regeneración mal hecha.
    [Theory]
    [InlineData("5002")]
    [InlineData("05002000")]
    [InlineData("ABCDE")]
    [InlineData("")]
    public void ParseCoordinadoraNamesThrowsWhenCodeIsNotFiveDigits(string code)
    {
        var json = $$"""
            [
              { "code": "{{code}}", "name": "ABEJORRAL (ANT)" }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseCoordinadoraNames(ToStream(json)));
    }

    [Fact]
    public void ParseCoordinadoraNamesThrowsWhenNameIsEmpty()
    {
        var json = """
            [
              { "code": "05002", "name": "  " }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseCoordinadoraNames(ToStream(json)));
    }

    // El seeder ignora un código del snapshot que no sea de una ciudad sembrada, para no tumbar el
    // arranque. Esta prueba es la que no lo deja pasar: una regeneración mala se cae en CI.
    [Fact]
    public void EveryCodeOfTheEmbeddedCoordinadoraSnapshotIsASeededMunicipality()
    {
        var municipalities = DivipolaDataParser
            .ParseCities(GeographySeeder.OpenResource(GeographySeeder.CitiesResourceSuffix))
            .Select(record => record.DivipolaCode)
            .ToHashSet(StringComparer.Ordinal);

        var snapshot = DivipolaDataParser.ParseCoordinadoraNames(
            GeographySeeder.OpenResource(GeographySeeder.CoordinadoraCitiesResourceSuffix));

        // Materializado: si falla, el mensaje lista los códigos que sobran.
        var unknownCodes = snapshot
            .Select(record => record.DivipolaCode)
            .Where(code => !municipalities.Contains(code))
            .ToArray();

        Assert.NotEmpty(snapshot);
        Assert.Empty(unknownCodes);
    }

    private static MemoryStream ToStream(string json) => new(Encoding.UTF8.GetBytes(json));
}
