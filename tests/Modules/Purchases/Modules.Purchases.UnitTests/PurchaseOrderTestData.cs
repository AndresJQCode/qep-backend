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

    public static readonly Guid SupplierTenantId = Guid.Parse("0196b8a0-0000-7000-8000-000000000004");

    public static readonly Guid LinkedOrderId = Guid.Parse("0196b8a0-0000-7000-8000-000000000005");

    /// <summary>Una línea de una orden vinculada: sin variante propia, con la foto de la lista del mayorista.</summary>
    public static PurchaseOrderLineDraft LinkedLine(
        decimal quantity = 2m,
        decimal unitCost = 10000m,
        decimal taxRatePercent = 19m,
        string code = "SKU-1") =>
        new(null, code, "Camiseta – M / Azul", quantity, unitCost, taxRatePercent,
            new LinkedLinePrice(Guid.CreateVersion7(), 12500m, 4.8m, 11900m));

    public static PurchaseOrder LinkedOrder(IReadOnlyList<PurchaseOrderLineDraft>? lines = null, bool withPrices = true) =>
        PurchaseOrder.Create(TenantId, "OC-0007", SupplierId, SupplierTenantId, "COP", null, lines ?? [LinkedLine()], MemberId, Now, withPrices);

    // El pedido del mayorista siempre trae precio (spec 2026-11-19): una orden sin precio lo recibe igual en la foto.
    private const decimal PricedCost = 10000m;

    private const decimal BasePrice = 12500m;

    private const decimal DiscountPercent = 4.8m;

    private const decimal UnitPrice = 11900m;

    /// <summary>La foto que devuelve el pedido del mayorista: una línea por línea de la orden, con ids nuevos.</summary>
    public static IReadOnlyList<LinkedLineSnapshot> SnapshotOf(PurchaseOrder order, decimal? quantity = null) =>
        order.Lines
            .Select(line => new LinkedLineSnapshot(
                Guid.CreateVersion7(),
                line.SupplierVariantId!.Value,
                line.Code,
                line.Name,
                quantity ?? line.Quantity!.Value,
                line.UnitCost ?? PricedCost,
                line.TaxRatePercent,
                line.ListBasePrice ?? BasePrice,
                line.ListDiscountPercent ?? DiscountPercent,
                line.ListUnitPrice ?? UnitPrice))
            .ToList();

    /// <summary>Una orden vinculada ya enviada al mayorista (pedido <c>PED-0042</c>).</summary>
    public static PurchaseOrder SentLinkedOrder(IReadOnlyList<PurchaseOrderLineDraft>? lines = null)
    {
        var order = LinkedOrder(lines);
        order.SendToWholesaler(LinkedOrderId, "PED-0042", SnapshotOf(order), MemberId, Now);
        return order;
    }

    /// <summary>Una orden vinculada enviada con la lista oculta: sin monto en ninguna línea.</summary>
    public static PurchaseOrder SentPricelessOrder(IReadOnlyList<PurchaseOrderLineDraft>? lines = null)
    {
        var order = LinkedOrder(lines, withPrices: false);
        order.SendToWholesaler(LinkedOrderId, "PED-0042", SnapshotOf(order), MemberId, Now, withPrices: false);
        return order;
    }

    /// <summary>La línea final del pedido aprobado que corresponde a <paramref name="line"/>.</summary>
    public static WholesalerFinalLine FinalOf(PurchaseOrderLine line, decimal? quantity = null, decimal? unitCost = null) =>
        new(line.Id, line.SupplierVariantId!.Value, line.Code, line.Name, quantity ?? line.Quantity!.Value, unitCost ?? line.UnitCost ?? PricedCost, line.TaxRatePercent);

    public static PurchaseOrder Order(
        IReadOnlyList<PurchaseOrderLineDraft>? lines = null,
        string currency = "COP",
        string? notes = null) =>
        PurchaseOrder.Create(TenantId, "OC-0001", SupplierId, null, currency, notes, lines ?? [Line()], MemberId, Now);

    /// <summary>Confirma cada línea con lo pedido: todas quedan <c>Unchanged</c>.</summary>
    public static IReadOnlyList<PurchaseOrderLineConfirmation> AsRequested(PurchaseOrder order) =>
        order.Lines.Select(line => new PurchaseOrderLineConfirmation(line.Id, line.Quantity!.Value, line.UnitCost!.Value)).ToList();

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
