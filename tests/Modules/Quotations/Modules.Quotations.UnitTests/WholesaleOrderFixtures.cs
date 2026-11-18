using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>El pedido mayorista de las pruebas de la fase 3 (Task 5): un tenant mayorista, un distribuidor y su orden.</summary>
internal static class WholesaleOrderFixtures
{
    public static readonly Guid TenantId = Guid.CreateVersion7();
    public static readonly Guid BuyerTenantId = Guid.CreateVersion7();
    public static readonly Guid PurchaseOrderId = Guid.CreateVersion7();
    public static readonly Guid ClientId = Guid.CreateVersion7();
    public static readonly Guid SubjectId = Guid.CreateVersion7();
    public static readonly MemberId Advisor = new(Guid.CreateVersion7());
    public static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    public const string PurchaseOrderNumber = "OC-0007";

    public static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
        Country = "CO",
    };

    public static OrderLineInput Line(
        Guid variantId, decimal quantity, decimal unitPrice, Guid? sourceLineId = null, int taxPercentage = 0) =>
        new(
            variantId, variantId, quantity, unitPrice, 0m, taxPercentage, new QuotationProductSnapshot("SKU-" + variantId.ToString()[..4], "Aceite"),
            SourcePurchaseOrderLineId: sourceLineId ?? Guid.CreateVersion7());

    public static Order Wholesale(params OrderLineInput[] lines) => Wholesale(BuyerTenantId, PurchaseOrderId, lines);

    public static Order Wholesale(Guid buyerTenantId, Guid purchaseOrderId, params OrderLineInput[] lines) =>
        Order.CreateFromWholesale(
            OrderId.New(),
            TenantId,
            "PED-2026-0042",
            new OrderHeader(ClientId, Advisor, "COP", null, IsRetail: false, null, false, []),
            QuotationParties.Empty,
            Account,
            lines,
            null,
            Advisor,
            Now,
            DateOnly.FromDateTime(Now.UtcDateTime),
            buyerTenantId,
            purchaseOrderId,
            PurchaseOrderNumber);

    public static Order Manual(params OrderLineInput[] lines) =>
        Order.CreateManual(
            OrderId.New(),
            TenantId,
            "PED-2026-0001",
            new OrderHeader(ClientId, Advisor, "COP", null, IsRetail: false, null, false, []),
            QuotationParties.Empty,
            Account,
            ShippingDetails.None,
            lines,
            null,
            Advisor,
            [],
            Now,
            DateOnly.FromDateTime(Now.UtcDateTime));

    /// <summary>Una variante con escala del 10 % desde 5 unidades: un pedido mayorista nunca la aplica (D-5).</summary>
    public static QuotationVariantPricingRef Variant(Guid variantId, decimal priceCop, int? tax = 19) =>
        new(
            variantId,
            variantId,
            TenantId,
            "Aceite",
            "SKU-" + variantId.ToString()[..4],
            true,
            new Dictionary<string, decimal> { ["COP"] = priceCop },
            [new QuotationPriceScaleRef(5, 999, 10m, QuotationPriceScaleRestriction.Multiple, 1, [], AllowGrouping: false)],
            tax);
}

/// <summary>El precio de lista del distribuidor (D-5) en memoria, con las llamadas que recibió.</summary>
internal sealed class StubWholesaleLinePricing(IReadOnlyDictionary<Guid, decimal>? prices = null) : IWholesaleLinePricing
{
    public List<(Guid TenantId, Guid BuyerTenantId, string Currency, IReadOnlyCollection<Guid> VariantIds)> Calls { get; } = [];

    public Task<IReadOnlyDictionary<Guid, decimal>> ListPricesAsync(
        Guid tenantId,
        Guid buyerTenantId,
        string currency,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        Calls.Add((tenantId, buyerTenantId, currency, variantIds));
        IReadOnlyDictionary<Guid, decimal> found = (prices ?? new Dictionary<Guid, decimal>())
            .Where(pair => variantIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return Task.FromResult(found);
    }
}

/// <summary>El consecutivo de pedidos en memoria.</summary>
internal sealed class SequentialOrderNumberGenerator : IOrderNumberGenerator
{
    private long _next;

    public Task<long> NextAsync(Guid tenantId, int year, CancellationToken cancellationToken) => Task.FromResult(++_next);
}
