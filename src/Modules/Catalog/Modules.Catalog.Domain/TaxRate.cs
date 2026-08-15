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

    // P-008, decidido por el owner el 2026-08-10: el porcentaje es entero de 0 decimales. No
    // admite retenciones con fracción, y eso está declarado como límite de alcance en el gate.
    public const int MinPercentage = 0;
    public const int MaxPercentage = 100;

    // EF Core materializa por acá. El código nunca construye el agregado así:
    // Create es el único punto de entrada, y es el que hace cumplir los invariantes.
    private TaxRate() => Name = string.Empty;

    private TaxRate(
        TaxRateId id,
        Guid tenantId,
        string name,
        int percentage,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Percentage = percentage;
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public TaxRateId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Único por tenant; la unicidad vive en IX_tax_rates_tenant_name.</summary>
    public string Name { get; private set; }

    /// <summary>Entero de 0 a 100, sin decimales (P-008).</summary>
    public int Percentage { get; private set; }

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
        int percentage,
        DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), EnsurePercentageInRange(percentage), occurredAt);

    public void Update(string name, int percentage, DateTimeOffset occurredAt)
    {
        EnsureActive();

        Name = NormalizeName(name);
        Percentage = EnsurePercentageInRange(percentage);
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
    // la comparación es contra el rango cerrado y no contra el abierto.
    private static int EnsurePercentageInRange(int percentage) =>
        percentage is < MinPercentage or > MaxPercentage
            ? throw new CatalogDomainException(
                "catalog.tax_rate.percentage_out_of_range",
                $"The tax rate percentage must be between {MinPercentage} and {MaxPercentage}.")
            : percentage;

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
