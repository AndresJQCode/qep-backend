namespace Modules.Pricing.Domain;

/// <summary>
/// Una lista de precios de un tenant: nombre y prefijo. Mismo shape que
/// <c>ClientClassification</c> en Customers y <c>TaxRate</c> en Catalog — catálogo de referencia
/// chico, tenant-scoped, nombre y prefijo únicos por tenant, estado activo/inactivo reversible.
///
/// No guarda escalas ni precios: eso lo modela <c>PriceScale</c> en Catalog, que referencia esta
/// lista por id (sin FK real, cruza de módulo). Esta entidad es sólo el catálogo — qué listas
/// existen y cómo se llaman.
/// </summary>
public sealed class PriceList
{
    // Espeja el ancho de pricing.price_lists.name/prefix, por la misma razón que
    // ClientClassification: un valor demasiado largo falla como 422 con código de dominio en vez
    // de llegar a PostgreSQL y volver como 500 server.unexpected.
    public const int NameMaxLength = 120;

    public const int PrefixMaxLength = 20;

    // EF Core materializa por acá. El código nunca construye el agregado así: Create es el único
    // punto de entrada, y es el que hace cumplir los invariantes.
    private PriceList()
    {
        Name = string.Empty;
        Prefix = string.Empty;
    }

    private PriceList(
        PriceListId id, Guid tenantId, string name, string prefix, DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Prefix = prefix;
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public PriceListId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Único por tenant; la unicidad vive en IX_price_lists_tenant_name.</summary>
    public string Name { get; private set; }

    /// <summary>Único por tenant; la unicidad vive en IX_price_lists_tenant_prefix.</summary>
    public string Prefix { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista, como en <c>ClientClassification</c>, <c>TaxRate</c> y
    /// <c>Product</c>. Nace con el agregado en vez de agregarse después: sin él, dos escrituras
    /// que se solapan se pisan en silencio y <see cref="EnsureActive"/> se evalúa contra la copia
    /// en memoria del que escribe, no contra el estado real al momento del commit.
    /// </summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PriceList Create(
        PriceListId id, Guid tenantId, string name, string prefix, DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), NormalizePrefix(prefix), occurredAt);

    public void Update(string name, string prefix, DateTimeOffset occurredAt)
    {
        EnsureActive();

        Name = NormalizeName(name);
        Prefix = NormalizePrefix(prefix);
        Version++;
        UpdatedAt = occurredAt;
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
        if (!IsActive)
        {
            throw new PricingDomainException(
                "pricing.price_list.already_inactive",
                "The price list is already inactive.");
        }

        IsActive = false;
        Version++;
        UpdatedAt = occurredAt;
    }

    // La vuelta de Deactivate. Sin ella una lista inactiva quedaría atrapada: el PUT la rechaza
    // por EnsureActive(), y no habría forma de sacarla de ahí salvo un UPDATE por SQL.
    //
    // No revalida la unicidad del nombre ni del prefijo a propósito: los dos índices únicos son
    // sin filtro parcial, así que desactivar nunca libera el nombre ni el prefijo, y reactivar no
    // puede colisionar con nadie.
    public void Activate(DateTimeOffset occurredAt)
    {
        if (IsActive)
        {
            throw new PricingDomainException(
                "pricing.price_list.already_active",
                "The price list is already active.");
        }

        IsActive = true;
        Version++;
        UpdatedAt = occurredAt;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new PricingDomainException(
                "pricing.price_list.inactive",
                "An inactive price list cannot be edited.");
        }
    }

    // Recortar espacios es parte del invariante, no higiene del llamador: el índice único trata
    // " Mayorista" y "Mayorista" como dos nombres distintos, cosa que nadie leyendo la lista haría.
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PricingDomainException(
                "pricing.price_list.name_required", "The name is required.");
        }

        var trimmed = name.Trim();
        return trimmed.Length > NameMaxLength
            ? throw new PricingDomainException(
                "pricing.price_list.name_too_long",
                $"The name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new PricingDomainException(
                "pricing.price_list.prefix_required", "The prefix is required.");
        }

        var trimmed = prefix.Trim();
        return trimmed.Length > PrefixMaxLength
            ? throw new PricingDomainException(
                "pricing.price_list.prefix_too_long",
                $"The prefix cannot exceed {PrefixMaxLength} characters.")
            : trimmed;
    }
}
