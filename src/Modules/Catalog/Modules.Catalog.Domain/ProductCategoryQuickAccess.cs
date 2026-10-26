namespace Modules.Catalog.Domain;

/// <summary>
/// The register's quick-access chips (spec 2026-10-26-categorias-pos-acceso-rapido, D-1/D-4): an
/// ordered list per tenant, kept as contiguous positions 1..N on the categories themselves. Rules
/// over the whole tenant list, like <see cref="ProductCategoryTree"/>, because one category cannot
/// see the others' positions.
/// </summary>
public static class ProductCategoryQuickAccess
{
    /// <summary>
    /// Replaces the whole list: index 0 gets position 1, every category not named is unpinned.
    /// Validates everything before touching anything, so a rejected list leaves no half-applied
    /// positions on tracked entities.
    /// </summary>
    public static void Replace(
        IReadOnlyList<ProductCategory> tenantCategories,
        IReadOnlyList<ProductCategoryId> orderedIds)
    {
        if (orderedIds.Distinct().Count() != orderedIds.Count)
        {
            throw new CatalogDomainException(
                "catalog.category.quick_access_duplicate",
                "A category can appear only once in the quick access list.");
        }

        var byId = tenantCategories.ToDictionary(c => c.Id);

        // Same code for unknown and foreign: telling them apart would confirm the id exists in
        // another tenant (same reasoning as ProductCategoryResolver).
        if (orderedIds.Any(id => !byId.ContainsKey(id)))
        {
            throw new CatalogDomainException(
                "catalog.category.not_found",
                "Every quick access category must exist in this tenant.");
        }

        var positions = orderedIds
            .Select((id, index) => (id, position: index + 1))
            .ToDictionary(pair => pair.id, pair => pair.position);
        foreach (var category in tenantCategories)
        {
            category.SetPosQuickAccessPosition(
                positions.TryGetValue(category.Id, out var position) ? position : null);
        }
    }

    /// <summary>
    /// Takes <paramref name="removedId"/> out of the list and closes the gap, keeping the others'
    /// relative order. Used before deleting a pinned category.
    /// </summary>
    public static void Remove(
        IReadOnlyList<ProductCategory> tenantCategories,
        ProductCategoryId removedId) =>
        Replace(
            tenantCategories,
            tenantCategories
                .Where(c => c.PosQuickAccessPosition is not null && c.Id != removedId)
                .OrderBy(c => c.PosQuickAccessPosition)
                .Select(c => c.Id)
                .ToList());
}
