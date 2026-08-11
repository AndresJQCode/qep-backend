namespace Modules.Catalog.Domain;

/// <summary>
/// Un producto del catálogo de un tenant (RF-020). Guarda sólo los datos maestros vivos: las
/// listas de precio y su vigencia son de `pricing`, y un documento congela su copia de lo vendido.
/// </summary>
public sealed class Product
{
    // Espeja los anchos de columna de catalog.products. Guardar acá significa que un valor
    // demasiado largo falla como 422 con código de dominio en vez de llegar a PostgreSQL y
    // volver como 500 server.unexpected.
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 60;

    // EF Core materializa por acá. El código nunca construye el agregado así:
    // Create es el único punto de entrada, y es el que hace cumplir los invariantes.
    private Product()
    {
        Name = string.Empty;
        Code = string.Empty;
    }

    private Product(
        ProductId id,
        Guid tenantId,
        string name,
        string code,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Code = code;
        IsActive = true;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public ProductId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Único por tenant; la unicidad vive en IX_products_tenant_code.</summary>
    public string Code { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Product Create(
        ProductId id,
        Guid tenantId,
        string name,
        string code,
        DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), NormalizeCode(code), occurredAt);

    public void Update(string name, string code, DateTimeOffset occurredAt)
    {
        EnsureActive();

        Name = NormalizeName(name);
        Code = NormalizeCode(code);
        UpdatedAt = occurredAt;
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
        if (!IsActive)
        {
            throw new CatalogDomainException(
                "catalog.product.already_inactive",
                "The product is already inactive.");
        }

        IsActive = false;
        UpdatedAt = occurredAt;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new CatalogDomainException(
                "catalog.product.inactive",
                "An inactive product cannot be edited.");
        }
    }

    // Recortar espacios es parte del invariante, no higiene del llamador: el índice único trata
    // " VS-001" y "VS-001" como dos códigos distintos, cosa que nadie leyendo la lista haría.
    private static string NormalizeName(string name) =>
        Normalize(
            name,
            NameMaxLength,
            "catalog.product.name_required",
            "The product name is required.",
            "catalog.product.name_too_long",
            $"The product name cannot exceed {NameMaxLength} characters.");

    private static string NormalizeCode(string code) =>
        Normalize(
            code,
            CodeMaxLength,
            "catalog.product.code_required",
            "The product code is required.",
            "catalog.product.code_too_long",
            $"The product code cannot exceed {CodeMaxLength} characters.");

    private static string Normalize(
        string value,
        int maxLength,
        string requiredCode,
        string requiredMessage,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CatalogDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CatalogDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }
}
