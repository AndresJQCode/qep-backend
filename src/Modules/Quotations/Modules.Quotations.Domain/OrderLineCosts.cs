namespace Modules.Quotations.Domain;

/// <summary>
/// Los costos de catálogo que Application ya buscó para las variantes que un pedido puede congelar (spec
/// 2026-11-10 §4.3). Distingue «busqué y no tiene costo» (se congela en null) de «no lo busqué»: lo segundo es
/// un error de programación, porque congelar sin buscar dejaría una línea vendida sin costo para siempre.
/// </summary>
public sealed class OrderLineCosts
{
    private readonly HashSet<Guid> _lookedUp;
    private readonly Dictionary<Guid, decimal> _found;

    private OrderLineCosts(HashSet<Guid> lookedUp, Dictionary<Guid, decimal> found)
    {
        _lookedUp = lookedUp;
        _found = found;
    }

    /// <summary>Nada buscado: para los caminos que no pueden dejar una línea vendida.</summary>
    public static OrderLineCosts None { get; } = new([], []);

    public static OrderLineCosts Of(IReadOnlyCollection<Guid> lookedUp, IReadOnlyDictionary<Guid, decimal> found)
    {
        ArgumentNullException.ThrowIfNull(lookedUp);
        ArgumentNullException.ThrowIfNull(found);
        return new OrderLineCosts([.. lookedUp], found.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    internal decimal? For(Guid variantId)
    {
        if (!_lookedUp.Contains(variantId))
        {
            throw new InvalidOperationException(
                $"The cost of variant '{variantId}' was not looked up before freezing the order line.");
        }

        return _found.TryGetValue(variantId, out var cost) ? cost : null;
    }
}
