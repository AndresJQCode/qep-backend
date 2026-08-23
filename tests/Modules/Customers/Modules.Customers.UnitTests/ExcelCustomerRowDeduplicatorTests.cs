using Modules.Customers.Application;

namespace Modules.Customers.UnitTests;

/// <summary>
/// La deduplicacion **dentro del archivo** de la importacion masiva (Fase 5): pura, sin base de
/// datos — opera solo sobre filas que ya pasaron <see cref="ExcelCustomerRowRules"/>, asi que
/// <c>IdentificationType</c>/<c>IdentificationNumber</c> siempre vienen no vacios aca.
/// </summary>
public sealed class ExcelCustomerRowDeduplicatorTests
{
    private static ExcelCustomerRow Row(
        int rowNumber, string identificationType, string identificationNumber) =>
        new(
            rowNumber,
            "Cliente " + rowNumber,
            identificationType,
            identificationNumber,
            null,
            null,
            null,
            "Antioquia",
            "Medellin",
            "Mayorista",
            "No");

    [Fact]
    public void RowsWithDistinctIdentificationsAreAllFirstOccurrences()
    {
        var rows = new[]
        {
            Row(2, "NIT", "900.111.111-1"),
            Row(3, "NIT", "900.222.222-2"),
            Row(4, "CC", "900.111.111-1") // mismo numero, tipo distinto: no es el mismo documento.
        };

        var (firstOccurrences, duplicates) = ExcelCustomerRowDeduplicator.Partition(rows);

        Assert.Equal(3, firstOccurrences.Count);
        Assert.Empty(duplicates);
    }

    // El caso central que pide la Fase 5: dos filas con la misma identificacion -> solo la
    // primera es candidata valida, la segunda se reporta como duplicado.
    [Fact]
    public void TheSecondRowWithTheSameIdentificationIsADuplicate()
    {
        var first = Row(2, "NIT", "900.123.456-1");
        var second = Row(5, "NIT", "900.123.456-1");
        var rows = new[] { first, second };

        var (firstOccurrences, duplicates) = ExcelCustomerRowDeduplicator.Partition(rows);

        Assert.Equal([first], firstOccurrences);
        Assert.Equal([second], duplicates);
    }

    // Recortado, como CustomerIdentification.Normalized(): " 900-1" y "900-1" son el mismo
    // documento para el indice unico de la base, y deberian serlo tambien aca.
    [Fact]
    public void IdentificationNumbersAreComparedTrimmed()
    {
        var first = Row(2, "NIT", "900-1");
        var second = Row(3, "NIT", "  900-1  ");

        var (firstOccurrences, duplicates) = ExcelCustomerRowDeduplicator.Partition([first, second]);

        Assert.Single(firstOccurrences);
        Assert.Single(duplicates);
    }

    // El tipo de documento se compara sin distinguir mayusculas ("NIT" y "nit" son el mismo tipo),
    // igual que IdentificationTypeParser.
    [Fact]
    public void IdentificationTypesAreComparedCaseInsensitively()
    {
        var first = Row(2, "NIT", "900.123.456-1");
        var second = Row(3, "nit", "900.123.456-1");

        var (firstOccurrences, duplicates) = ExcelCustomerRowDeduplicator.Partition([first, second]);

        Assert.Single(firstOccurrences);
        Assert.Single(duplicates);
    }

    [Fact]
    public void AThirdRepeatedRowIsAlsoADuplicate()
    {
        var rows = new[]
        {
            Row(2, "NIT", "900.123.456-1"),
            Row(3, "NIT", "900.123.456-1"),
            Row(4, "NIT", "900.123.456-1")
        };

        var (firstOccurrences, duplicates) = ExcelCustomerRowDeduplicator.Partition(rows);

        Assert.Single(firstOccurrences);
        Assert.Equal(2, duplicates.Count);
    }

    [Fact]
    public void AnEmptyListProducesNoOccurrencesAndNoDuplicates()
    {
        var (firstOccurrences, duplicates) = ExcelCustomerRowDeduplicator.Partition([]);

        Assert.Empty(firstOccurrences);
        Assert.Empty(duplicates);
    }
}
