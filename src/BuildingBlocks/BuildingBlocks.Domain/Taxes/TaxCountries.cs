namespace BuildingBlocks.Domain.Taxes;

/// <summary>Cómo actúa un tipo de impuesto sobre el total: suma al documento o se retiene de él.</summary>
public enum TaxBehavior
{
    Adds,
    Withholds,
}

/// <summary>Un tipo de impuesto de un país. <c>Type</c> viaja como texto, nunca como enum de dominio.</summary>
public sealed record TaxTypeDefinition(string Type, TaxBehavior Behavior, bool AllowsFraction, bool AllowsMinimumBase);

/// <summary>Un país con sus tipos de impuesto.</summary>
/// <param name="Country">ISO 3166-1 alfa-2, el mismo formato que <c>Company.Country</c>.</param>
/// <param name="TaxUnitName">"UVT", o null si el país no tiene unidad tributaria.</param>
/// <param name="TaxCurrency">La moneda en la que el país discrimina impuestos. Generaliza
/// <c>QuotationItem.ItemizesTaxIn</c> (QuotationItem.cs:305-307).</param>
/// <param name="Types">Los tipos del país; el valor de <c>Type</c> es único dentro de la lista.</param>
public sealed record TaxCountryDefinition(
    string Country,
    string? TaxUnitName,
    string TaxCurrency,
    IReadOnlyList<TaxTypeDefinition> Types);

/// <summary>La única definición de tipos de impuesto y unidades tributarias por país. Un país nuevo es
/// una entrada más de <see cref="All"/>; ni Catalog, ni Customers, ni Quotations guardan su propia copia.</summary>
public static class TaxCountries
{
    /// <summary>Decimales máximos de un porcentaje que admite fracción.</summary>
    public const int MaxFractionDigits = 3;

    public const string IvaType = "IVA";
    public const string RetefuenteType = "Retefuente";
    public const string ReteIcaType = "ReteICA";

    public static readonly TaxCountryDefinition Colombia = new("CO", "UVT", "COP",
    [
        new(IvaType, TaxBehavior.Adds, AllowsFraction: false, AllowsMinimumBase: false),
        new(RetefuenteType, TaxBehavior.Withholds, AllowsFraction: true, AllowsMinimumBase: true),
        new(ReteIcaType, TaxBehavior.Withholds, AllowsFraction: true, AllowsMinimumBase: true),
    ]);

    public static IReadOnlyList<TaxCountryDefinition> All { get; } = [Colombia];

    /// <summary>Busca el país recortando espacios y sin distinguir mayúsculas.</summary>
    public static TaxCountryDefinition? Find(string? country)
    {
        var code = country?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(code) ? null : All.FirstOrDefault(c => c.Country == code);
    }

    /// <summary>Busca el tipo dentro del país; el tipo se compara exacto (<c>Ordinal</c>).</summary>
    public static TaxTypeDefinition? FindType(string? country, string? type) =>
        Find(country)?.Types.FirstOrDefault(t => string.Equals(t.Type, type, StringComparison.Ordinal));
}
