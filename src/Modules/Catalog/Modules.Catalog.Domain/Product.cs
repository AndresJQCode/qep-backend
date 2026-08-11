namespace Modules.Catalog.Domain;

/// <summary>
/// A product in a tenant's catalogue (RF-020). Holds the live master data only: price lists
/// and validity belong to `pricing`, and a document freezes its own copy of what it sold.
/// </summary>
public sealed class Product
{
    // Mirrors the column widths of catalog.products. Guarding here means an over-long value
    // fails as a 422 with a domain code instead of reaching PostgreSQL and coming back as
    // 500 server.unexpected.
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 60;

    // EF Core materializes through this. Code never builds the aggregate this way:
    // Create is the only entry point, and it is the one that enforces the invariants.
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

    /// <summary>Unique per tenant; the uniqueness lives in IX_products_tenant_code.</summary>
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

    // Trimming is part of the invariant, not caller hygiene: the unique index treats
    // " VS-001" and "VS-001" as two different codes, which no person reading the list would.
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
