using BuildingBlocks.Domain.Taxes;

namespace Modules.Catalog.Domain;

/// <summary>
/// Una tasa de impuesto del catálogo de un tenant (RF-025, porción "tasas de impuesto").
/// El cálculo de totales y su redondeo no viven acá: son de `quotes` (RN-013), y esa mitad de
/// P-008 sigue abierta. Este agregado sólo guarda el porcentaje vigente.
/// </summary>
public sealed class TaxRate
{
    // Espeja el ancho de catalog.tax_rates.name, por la misma razón que Product: un valor
    // demasiado largo falla como 422 con código de dominio en vez de llegar a PostgreSQL y
    // volver como 500 server.unexpected.
    public const int NameMaxLength = 120;

    // P-008 (2026-08-10) fijó el porcentaje en entero de 0 decimales. Reabierto por el owner el
    // 2026-11-10 (D15): el IVA sigue entero, pero las retenciones admiten hasta
    // TaxCountries.MaxFractionDigits decimales (0,966 % del ReteICA, 2,5 % de la retefuente).
    public const int MinPercentage = 0;
    public const int MaxPercentage = 100;

    // Espejan los anchos de catalog.tax_rates.type y .country, por la misma razón que NameMaxLength.
    public const int TypeMaxLength = 32;
    public const int CountryLength = 2;

    // EF Core materializa por acá. El código nunca construye el agregado así:
    // Create es el único punto de entrada, y es el que hace cumplir los invariantes.
    private TaxRate()
    {
        Name = string.Empty;
        Type = string.Empty;
        Country = string.Empty;
    }

    private TaxRate(
        TaxRateId id,
        Guid tenantId,
        string name,
        decimal percentage,
        string type,
        string country,
        decimal? minimumBase,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Percentage = percentage;
        Type = type;
        Country = country;
        MinimumBase = minimumBase;
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public TaxRateId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Único por tenant; la unicidad vive en IX_tax_rates_tenant_name.</summary>
    public string Name { get; private set; }

    /// <summary>
    /// De 0 a 100. Entero para los tipos que no admiten fracción (IVA); hasta
    /// <see cref="TaxCountries.MaxFractionDigits"/> decimales para los que retienen (D15).
    /// </summary>
    public decimal Percentage { get; private set; }

    /// <summary>"IVA", "Retefuente", "ReteICA"…: un tipo de <see cref="TaxCountries"/>. No cambia tras crear.</summary>
    public string Type { get; private set; }

    /// <summary>ISO 3166-1 alfa-2 del país cuyos tipos rigen esta tasa. No cambia tras crear.</summary>
    public string Country { get; private set; }

    /// <summary>
    /// Base mínima (en la unidad tributaria del país, o en moneda si no tiene) desde la cual aplica una
    /// retención. Null = aplica siempre. Sólo la admiten los tipos con <c>AllowsMinimumBase</c>.
    /// </summary>
    public decimal? MinimumBase { get; private set; }

    /// <summary>Lo calcula el tipo, no se guarda: EF lo ignora.</summary>
    public TaxBehavior Behavior =>
        TaxCountries.FindType(Country, Type)?.Behavior
        ?? throw new InvalidOperationException($"Unknown tax type '{Type}' for country '{Country}'.");

    public bool Withholds => Behavior == TaxBehavior.Withholds;

    /// <summary>
    /// El porcentaje como entero, para los adaptadores de líneas, que sólo reciben tasas de IVA. Lanza si la
    /// tasa retiene o tiene fracción: esos casos no deben llegar a una línea.
    /// </summary>
    public int VatPercentage =>
        Behavior == TaxBehavior.Adds && Percentage == decimal.Truncate(Percentage)
            ? (int)Percentage
            : throw new InvalidOperationException(
                "Only a whole-number tax rate that adds can be read as a VAT percentage.");

    public bool IsActive { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista, como en <c>Product</c>, <c>Tenant</c> y
    /// <c>Membership</c>. Nace con el agregado en vez de agregarse después: <c>Product</c> lo
    /// tuvo que sumar en la corrección de la revisión de 4 lentes de CAT-02, a la que llegaron
    /// dos lentes por separado. Sin él, dos escrituras que se solapan se pisan en silencio y
    /// <see cref="EnsureActive"/> se evalúa contra la copia en memoria del que escribe, no
    /// contra el estado real al momento del commit.
    /// </summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static TaxRate Create(
        TaxRateId id,
        Guid tenantId,
        string name,
        decimal percentage,
        string type,
        string country,
        decimal? minimumBase,
        DateTimeOffset occurredAt)
    {
        // El país sólo se normaliza; si no existe, FindType no halla el tipo y sale type_invalid.
        // country_not_available es de Application (spec §7.2).
        var normalizedCountry = country?.Trim().ToUpperInvariant() ?? string.Empty;
        var definition = TaxCountries.FindType(normalizedCountry, type)
            ?? throw new CatalogDomainException(
                "catalog.tax_rate.type_invalid",
                $"The tax type '{type}' does not exist for country '{normalizedCountry}'.");

        return new(
            id,
            tenantId,
            NormalizeName(name),
            EnsureValidPercentage(percentage, definition),
            definition.Type,
            normalizedCountry,
            EnsureValidMinimumBase(minimumBase, definition),
            occurredAt);
    }

    // El tipo y el país no se editan: cambiarlos reinterpretaría cada documento que ya usa la tasa.
    public void Update(string name, decimal percentage, decimal? minimumBase, DateTimeOffset occurredAt)
    {
        EnsureActive();

        var definition = TaxCountries.FindType(Country, Type)!;
        Name = NormalizeName(name);
        Percentage = EnsureValidPercentage(percentage, definition);
        MinimumBase = EnsureValidMinimumBase(minimumBase, definition);
        Version++;
        UpdatedAt = occurredAt;
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
        if (!IsActive)
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.already_inactive",
                "The tax rate is already inactive.");
        }

        IsActive = false;
        Version++;
        UpdatedAt = occurredAt;
    }

    // La vuelta de Deactivate, que hasta CAT-08 no existia. Sin ella una tasa inactiva que algun
    // producto usa quedaba atrapada: el PUT la rechaza por EnsureActive(), el DELETE de CAT-06 la
    // rechaza por la FK RESTRICT, y no habia forma de sacarla de ahi salvo un UPDATE por SQL.
    //
    // No revalida la unicidad del nombre a proposito. IX_tax_rates_tenant_name es unico **sin
    // filtro parcial**, asi que desactivar nunca libero el nombre y reactivar no puede colisionar
    // con nadie. Si alguien le agrega un filtro parcial al indice, CA-CAT-08-09 se cae y avisa.
    public void Activate(DateTimeOffset occurredAt)
    {
        if (IsActive)
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.already_active",
                "The tax rate is already active.");
        }

        IsActive = true;
        Version++;
        UpdatedAt = occurredAt;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.inactive",
                "An inactive tax rate cannot be edited.");
        }
    }

    // Los extremos son válidos: 0 es el exento colombiano y 100 es el límite superior. Por eso
    // la comparación es contra el rango cerrado y no contra el abierto. Después del rango, la
    // forma: sin fracción para el IVA y hasta MaxFractionDigits decimales para las retenciones
    // (los ceros finales no cuentan: 0,9660 tiene tres).
    private static decimal EnsureValidPercentage(decimal percentage, TaxTypeDefinition definition)
    {
        if (percentage is < MinPercentage or > MaxPercentage)
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.percentage_out_of_range",
                $"The tax rate percentage must be between {MinPercentage} and {MaxPercentage}.");
        }

        if (!definition.AllowsFraction)
        {
            return percentage == decimal.Truncate(percentage)
                ? percentage
                : throw new CatalogDomainException(
                    "catalog.tax_rate.percentage_not_integer",
                    $"The {definition.Type} percentage must be a whole number.");
        }

        return percentage == decimal.Round(percentage, TaxCountries.MaxFractionDigits)
            ? percentage
            : throw new CatalogDomainException(
                "catalog.tax_rate.percentage_too_precise",
                $"The tax rate percentage admits at most {TaxCountries.MaxFractionDigits} decimals.");
    }

    private static decimal? EnsureValidMinimumBase(decimal? minimumBase, TaxTypeDefinition definition)
    {
        if (minimumBase is null)
        {
            return null;
        }

        if (!definition.AllowsMinimumBase)
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.minimum_base_not_allowed",
                $"The {definition.Type} tax type does not admit a minimum base.");
        }

        return minimumBase < 0
            ? throw new CatalogDomainException(
                "catalog.tax_rate.minimum_base_invalid",
                "The minimum base cannot be negative.")
            : minimumBase;
    }

    // Recortar espacios es parte del invariante, no higiene del llamador: el índice único trata
    // " IVA general" e "IVA general" como dos nombres distintos, cosa que nadie leyendo la
    // lista haría.
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.name_required",
                "The tax rate name is required.");
        }

        var trimmed = name.Trim();
        return trimmed.Length > NameMaxLength
            ? throw new CatalogDomainException(
                "catalog.tax_rate.name_too_long",
                $"The tax rate name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }
}
