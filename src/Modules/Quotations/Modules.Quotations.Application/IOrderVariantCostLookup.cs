using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El costo manual vigente de las variantes, que el pedido congela en cada línea cuando cuenta como vendida (spec
/// 2026-11-10 §4.3). Puerto propio de Quotations, con la misma semántica que el de Pos: los módulos no se
/// referencian y el costo nunca viaja por los puertos que alimentan respuestas.
/// </summary>
public interface IOrderVariantCostLookup
{
    /// <summary>
    /// Por id de variante, sólo las del tenant que tienen costo en <paramref name="currency"/>; una variante sin
    /// costo en esa moneda simplemente no aparece.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> FindAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

/// <summary>Arma el <see cref="OrderLineCosts"/> que pide el dominio: todo lo pedido cuenta como buscado, tenga
/// costo o no. Sin variantes no consulta.</summary>
internal static class OrderLineCostsLoader
{
    public static async Task<OrderLineCosts> LoadAsync(
        IOrderVariantCostLookup lookup,
        Guid tenantId,
        string currency,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(variantIds);
        if (variantIds.Count == 0)
        {
            return OrderLineCosts.None;
        }

        var found = await lookup.FindAsync(tenantId, currency, variantIds, cancellationToken);
        return OrderLineCosts.Of(variantIds, found);
    }

    /// <summary>Lo que necesitan las líneas que se agregan a <paramref name="order"/>: sólo si está pagado, porque
    /// entonces lo nuevo ya cuenta como vendido y congela al entrar; si no, <see cref="OrderLineCosts.None"/> sin
    /// consultar. Siempre en la moneda del pedido.</summary>
    public static Task<OrderLineCosts> LoadForAdditionsAsync(
        IOrderVariantCostLookup lookup,
        Guid tenantId,
        Order order,
        IEnumerable<Guid> addedVariantIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(addedVariantIds);
        return order.PaymentStatus == OrderPaymentStatus.FullPaymentReceived
            ? LoadAsync(lookup, tenantId, order.Currency, addedVariantIds.Distinct().ToArray(), cancellationToken)
            : Task.FromResult(OrderLineCosts.None);
    }
}
