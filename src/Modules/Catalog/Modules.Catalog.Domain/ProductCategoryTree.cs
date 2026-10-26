namespace Modules.Catalog.Domain;

/// <summary>
/// Pure tree rules over a tenant's flat category list. Kept out of the aggregate because a
/// single category cannot see its ancestors.
/// </summary>
public static class ProductCategoryTree
{
    /// <summary>
    /// Depth a category hanging from <paramref name="parentId"/> would have: 1 under null,
    /// otherwise 1 + the parent's. The visited set cuts a cycle or a parent missing from the
    /// list short instead of looping forever.
    /// </summary>
    public static int DepthOf(IReadOnlyList<ProductCategory> tenantCategories, ProductCategoryId? parentId)
    {
        var byId = tenantCategories.ToDictionary(c => c.Id);
        var visited = new HashSet<ProductCategoryId>();
        var depth = 1;
        var current = parentId;

        while (current is { } id && visited.Add(id) && byId.TryGetValue(id, out var parent))
        {
            depth++;
            current = parent.ParentId;
        }

        return depth;
    }

    public static void EnsureCanNestUnder(IReadOnlyList<ProductCategory> tenantCategories, ProductCategoryId? parentId)
    {
        if (parentId is { } id && tenantCategories.All(c => c.Id != id))
        {
            throw new CatalogDomainException(
                "catalog.category.parent_not_found",
                "The parent category does not exist.");
        }

        if (DepthOf(tenantCategories, parentId) > ProductCategory.MaxDepth)
        {
            throw new CatalogDomainException(
                "catalog.category.too_deep",
                $"Categories cannot be nested deeper than {ProductCategory.MaxDepth} levels.");
        }
    }

    /// <summary>The root and all its descendants (BFS); empty if the root is not in the list.</summary>
    public static IReadOnlyCollection<ProductCategoryId> WithDescendants(
        IReadOnlyList<ProductCategory> tenantCategories,
        ProductCategoryId rootId)
    {
        if (tenantCategories.All(c => c.Id != rootId))
        {
            return [];
        }

        var childrenByParent = tenantCategories
            .Where(c => c.ParentId is not null)
            .ToLookup(c => c.ParentId!.Value, c => c.Id);
        var visited = new HashSet<ProductCategoryId> { rootId };
        var queue = new Queue<ProductCategoryId>([rootId]);

        while (queue.TryDequeue(out var current))
        {
            foreach (var child in childrenByParent[current])
            {
                if (visited.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return visited;
    }
}
