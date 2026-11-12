using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Costos para las pruebas que llevan un pedido a pagado sin mirar el costo: todas las variantes buscadas y
/// ninguna con costo, que es lo que devolvería un catálogo sin costos cargados.</summary>
internal static class OrderCostFixtures
{
    public static OrderLineCosts WithoutCosts(this Order order) =>
        OrderLineCosts.Of(order.UncostedVariantIds, new Dictionary<Guid, decimal>());

    public static OrderLineCosts WithoutCosts(IEnumerable<OrderLineInput> lines) =>
        OrderLineCosts.Of(lines.Select(line => line.VariantId).ToArray(), new Dictionary<Guid, decimal>());

    public static OrderLineCosts WithoutCosts(params Guid[] variantIds) =>
        OrderLineCosts.Of(variantIds, new Dictionary<Guid, decimal>());
}
