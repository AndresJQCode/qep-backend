using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

internal static class PurchaseOrderTestData
{
    public static readonly Guid TenantId = Guid.Parse("0196b8a0-0000-7000-8000-000000000001");

    public static readonly Guid SupplierId = Guid.Parse("0196b8a0-0000-7000-8000-000000000002");

    public static readonly Guid MemberId = Guid.Parse("0196b8a0-0000-7000-8000-000000000003");

    public static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    public static PurchaseOrderLineDraft Line(
        decimal quantity = 2m,
        decimal unitCost = 10m,
        decimal taxRatePercent = 19m,
        Guid? variantId = null) =>
        new(variantId ?? Guid.CreateVersion7(), "SKU-1", "Camiseta – M / Azul", quantity, unitCost, taxRatePercent);

    public static PurchaseOrder Order(
        IReadOnlyList<PurchaseOrderLineDraft>? lines = null,
        string currency = "COP",
        string? notes = null) =>
        PurchaseOrder.Create(TenantId, "OC-0001", SupplierId, currency, notes, lines ?? [Line()], MemberId, Now);

    /// <summary>Confirma cada línea con lo pedido: todas quedan <c>Unchanged</c>.</summary>
    public static IReadOnlyList<PurchaseOrderLineConfirmation> AsRequested(PurchaseOrder order) =>
        order.Lines.Select(line => new PurchaseOrderLineConfirmation(line.Id, line.Quantity, line.UnitCost)).ToList();

    /// <summary>Una orden llevada hasta <paramref name="status"/> por el camino normal.</summary>
    public static PurchaseOrder OrderIn(PurchaseOrderStatus status)
    {
        var order = Order();
        if (status == PurchaseOrderStatus.Draft)
        {
            return order;
        }

        if (status == PurchaseOrderStatus.Cancelled)
        {
            order.Cancel(MemberId, Now);
            return order;
        }

        order.Send(MemberId, Now);
        if (status == PurchaseOrderStatus.Sent)
        {
            return order;
        }

        order.Confirm(AsRequested(order), MemberId, Now);
        if (status == PurchaseOrderStatus.Confirmed)
        {
            return order;
        }

        order.Receive(MemberId, Now);
        return order;
    }
}
