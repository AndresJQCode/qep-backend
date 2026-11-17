using BuildingBlocks.Domain.Taxes;

namespace Modules.Catalog.Domain;

/// <summary>
/// El valor de la unidad tributaria de un país en un año (la UVT colombiana). Es un dato de la plataforma:
/// no tiene <c>tenant_id</c> a propósito, igual que las ciudades de Geography. Lo administra el operador y
/// lo leen todos los tenants para convertir la base mínima de una retención a moneda.
/// </summary>
public sealed class TaxUnitValue
{
    public const int MinYear = 2000;
    public const int CountryLength = 2;

    // EF Core materializa por acá; Create es el único punto de entrada del código.
    private TaxUnitValue() => Country = string.Empty;

    private TaxUnitValue(string country, int year, decimal value, Guid updatedBy, DateTimeOffset occurredAt)
    {
        Country = country;
        Year = year;
        Value = value;
        UpdatedBy = updatedBy;
        UpdatedAt = occurredAt;
    }

    public string Country { get; private set; }

    public int Year { get; private set; }

    /// <summary>Mayor que cero, en la moneda tributaria del país.</summary>
    public decimal Value { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>SubjectId del operador que escribió el valor.</summary>
    public Guid UpdatedBy { get; private set; }

    public static TaxUnitValue Create(
        string country,
        int year,
        decimal value,
        Guid updatedBy,
        DateTimeOffset occurredAt,
        int currentYear)
    {
        var definition = TaxCountries.Find(country);
        if (definition?.TaxUnitName is null)
        {
            throw new CatalogDomainException(
                "catalog.tax_unit.country_without_unit",
                $"The country '{country}' has no tax unit.");
        }

        if (year < MinYear || year > currentYear + 1)
        {
            throw new CatalogDomainException(
                "catalog.tax_unit.year_out_of_range",
                $"The year must be between {MinYear} and {currentYear + 1}.");
        }

        return new(definition.Country, year, EnsurePositive(value), updatedBy, occurredAt);
    }

    public void Replace(decimal value, Guid updatedBy, DateTimeOffset occurredAt)
    {
        Value = EnsurePositive(value);
        UpdatedBy = updatedBy;
        UpdatedAt = occurredAt;
    }

    private static decimal EnsurePositive(decimal value) =>
        value > 0
            ? value
            : throw new CatalogDomainException(
                "catalog.tax_unit.value_invalid",
                "The tax unit value must be greater than zero.");
}
