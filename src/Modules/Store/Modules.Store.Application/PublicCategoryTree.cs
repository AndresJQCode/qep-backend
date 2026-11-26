namespace Modules.Store.Application;

/// <summary>
/// El árbol de categorías que ve el visitante, y la única acumulación hacia arriba que usa la tienda: una categoría suma los productos
/// propios y los de todas sus descendientes, que es lo que encuentra el filtro por categoría. Se acumula en memoria sobre la lista que
/// ya trajo el catálogo: el árbol de un tenant es pequeño.
/// </summary>
public static class PublicCategoryTree
{
    /// <summary>
    /// Respeta el orden de <paramref name="all"/>. Las categorías sin productos se descartan, y un padre que no sale (o que nunca
    /// existió) deja al hijo como raíz: nunca se apunta a un nodo ausente.
    /// </summary>
    public static IReadOnlyList<PublicCategoryDto> Build(
        IReadOnlyList<StoreCategoryCount> direct,
        IReadOnlyList<(Guid Id, string Name, Guid? ParentId)> all)
    {
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(all);

        var counts = direct
            .GroupBy(entry => entry.CategoryId)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.ProductCount));
        var children = all
            .Where(node => node.ParentId is not null)
            .ToLookup(node => node.ParentId!.Value, node => node.Id);
        var rolledUp = new Dictionary<Guid, int>();

        var visible = all
            .Select(node => (Node: node, Count: RollUp(node.Id, counts, children, rolledUp, [])))
            .Where(entry => entry.Count > 0)
            .ToList();
        var surviving = visible.Select(entry => entry.Node.Id).ToHashSet();

        return visible
            .Select(entry => new PublicCategoryDto(
                entry.Node.Id,
                entry.Node.Name,
                entry.Node.ParentId is { } parent && surviving.Contains(parent) ? parent : null,
                entry.Count))
            .ToList();
    }

    /// <summary>La categoría y todas sus descendientes; vacío si el id no está en el árbol del tenant.</summary>
    public static IReadOnlyList<Guid> WithDescendants(IReadOnlyList<(Guid Id, string Name, Guid? ParentId)> all, Guid categoryId)
    {
        ArgumentNullException.ThrowIfNull(all);

        if (all.All(node => node.Id != categoryId))
        {
            return [];
        }

        var children = all
            .Where(node => node.ParentId is not null)
            .ToLookup(node => node.ParentId!.Value, node => node.Id);
        var found = new HashSet<Guid> { categoryId };
        var pending = new Queue<Guid>([categoryId]);
        while (pending.TryDequeue(out var current))
        {
            foreach (var child in children[current])
            {
                // El conjunto corta un ciclo, que el dominio no deja crear, en vez de recorrerlo para siempre.
                if (found.Add(child))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return found.ToList();
    }

    // rollup(c) = directo(c) + Σ rollup(hijas), con memo; la rama en curso corta un ciclo.
    private static int RollUp(
        Guid id,
        Dictionary<Guid, int> direct,
        ILookup<Guid, Guid> children,
        Dictionary<Guid, int> rolledUp,
        HashSet<Guid> inProgress)
    {
        if (rolledUp.TryGetValue(id, out var known))
        {
            return known;
        }

        if (!inProgress.Add(id))
        {
            return 0;
        }

        var count = direct.GetValueOrDefault(id)
            + children[id].Sum(child => RollUp(child, direct, children, rolledUp, inProgress));
        inProgress.Remove(id);
        rolledUp[id] = count;
        return count;
    }
}
