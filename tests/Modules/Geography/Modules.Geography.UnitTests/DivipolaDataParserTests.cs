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
    public void ParseCitiesKeepsBothFiveAndEightDigitEntries()
    {
        var json = """
            [
              {
                "code": "05001",
                "name": "MEDELLÍN",
                "department": { "code": "05", "name": "ANTIOQUIA" }
              },
              {
                "code": "05001000",
                "name": "MEDELLÍN, DISTRITO ESPECIAL",
                "department": { "code": "05", "name": "ANTIOQUIA" },
                "municipality": { "code": "05001", "name": "MEDELLÍN" }
              }
            ]
            """;

        var records = DivipolaDataParser.ParseCities(ToStream(json));

        Assert.Equal(2, records.Count);
        Assert.Contains(records, record =>
            record.DivipolaCode == "05001" &&
            record.Name == "MEDELLÍN" &&
            record.DepartmentCode == "05");
        Assert.Contains(records, record =>
            record.DivipolaCode == "05001000" &&
            record.Name == "MEDELLÍN, DISTRITO ESPECIAL" &&
            record.DepartmentCode == "05");
    }

    [Fact]
    public void ParseCitiesThrowsWhenCodeIsNotFiveOrEightDigits()
    {
        var json = """
            [
              {
                "code": "0500100",
                "name": "MEDELLÍN",
                "department": { "code": "05", "name": "ANTIOQUIA" }
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
                "department": { "code": "08", "name": "ATLÁNTICO" }
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
                "department": { "code": "05", "name": "ANTIOQUIA" }
              },
              {
                "code": "05001",
                "name": "MEDELLÍN OTRA VEZ",
                "department": { "code": "05", "name": "ANTIOQUIA" }
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
                "department": { "code": "05", "name": "ANTIOQUIA" }
              }
            ]
            """;

        Assert.Throws<InvalidOperationException>(
            () => DivipolaDataParser.ParseCities(ToStream(json)));
    }

    private static MemoryStream ToStream(string json) => new(Encoding.UTF8.GetBytes(json));
}
