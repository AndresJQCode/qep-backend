namespace Modules.Catalog.Domain;

/// <summary>
/// A node of a tenant's product category tree (spec 2026-10-26 §3.1). Born from the product form
/// or the Categorías screen, which can also rename, move and delete it (spec
/// 2026-10-26-categorias-pos-acceso-rapido). There is no Deactivate: a category with products or
/// children cannot be deleted, and the person moves them first.
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

    /// <summary>
    /// Place among the register's quick-access chips, 1..N contiguous per tenant; null when not
    /// pinned. Contiguity is kept by <see cref="ProductCategoryQuickAccess"/>, which sees the whole
    /// tenant list: a single category cannot.
    /// </summary>
    public int? PosQuickAccessPosition { get; private set; }

    public static ProductCategory Create(
        ProductCategoryId id,
        Guid tenantId,
        string name,
        ProductCategoryId? parentId,
        DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), parentId, occurredAt);

    /// <summary>
    /// Rename and/or move. Tree rules (parent in the tenant, no cycle, depth) need the whole list,
    /// so the caller runs <see cref="ProductCategoryTree.EnsureCanMove"/> first.
    /// </summary>
    public void Update(string name, ProductCategoryId? parentId, DateTimeOffset occurredAt)
    {
        Name = NormalizeName(name);
        ParentId = parentId;
        UpdatedAt = occurredAt;
        Version++;
    }

    // Neither Version nor UpdatedAt move (spec D-5): the position is not the category's content,
    // and bumping the version would make an open rename or delete fail with 412 for nothing.
    internal void SetPosQuickAccessPosition(int? position)
    {
        // Only ProductCategoryQuickAccess calls this, numbering from 1: a 0 here is a bug, not input.
        if (position is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "Positions start at 1.");
        }

        PosQuickAccessPosition = position;
    }

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
