namespace Modules.Catalog.Domain;

/// <summary>
/// A node of a tenant's product category tree (spec 2026-10-26 §3.1). Categories are created on
/// the fly from the product form and are never edited or deactivated, so the aggregate has no
/// Update/Deactivate: there is no administration screen (spec §2).
/// </summary>
public sealed class ProductCategory
{
    // Mirrors the width of the name column so an over-long value fails as a 422 with a domain
    // code instead of reaching PostgreSQL and coming back as a 500.
    public const int NameMaxLength = 80;

    // Depth counts the category itself: a root is level 1.
    public const int MaxDepth = 10;

    // EF Core materializes through here; Create is the only entry point that enforces invariants.
    private ProductCategory() => Name = string.Empty;

    private ProductCategory(
        ProductCategoryId id,
        Guid tenantId,
        string name,
        ProductCategoryId? parentId,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        ParentId = parentId;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public ProductCategoryId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public ProductCategoryId? ParentId { get; private set; }

    /// <summary>Optimistic concurrency token, born with the aggregate as in <c>TaxRate</c>.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ProductCategory Create(
        ProductCategoryId id,
        Guid tenantId,
        string name,
        ProductCategoryId? parentId,
        DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), parentId, occurredAt);

    // Trimming is part of the invariant: " Bebidas" and "Bebidas" read as the same category.
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CatalogDomainException(
                "catalog.category.name_required",
                "The category name is required.");
        }

        var trimmed = name.Trim();
        return trimmed.Length > NameMaxLength
            ? throw new CatalogDomainException(
                "catalog.category.name_too_long",
                $"The category name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }
}
