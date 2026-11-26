namespace Modules.Wms.Domain.Inventory;

public static class LedgerDeltas
{
    /// <summary>
    /// El neto por clave de un lote: <c>-cantidad</c> al origen y <c>+cantidad</c> al destino. Una clave que es destino de una
    /// entrada y origen de otra se escribe una sola vez; las de neto cero se omiten (no hay nada que bloquear ni escribir). Sale en
    /// el orden de <see cref="StockKeyComparer"/>, que es el orden de bloqueo.
    /// </summary>
    public static IReadOnlyList<(StockKey Key, decimal Delta)> Net(IReadOnlyList<LedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var totals = new Dictionary<StockKey, decimal>();
        foreach (var entry in entries)
        {
            totals[entry.From] = totals.GetValueOrDefault(entry.From) - entry.Quantity;
            totals[entry.To] = totals.GetValueOrDefault(entry.To) + entry.Quantity;
        }

        return totals
            .Where(pair => pair.Value != 0m)
            .OrderBy(pair => pair.Key, StockKeyComparer.Instance)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
    }
}
