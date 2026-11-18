using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.UnitTests.WholesaleOrderFixtures;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Ruling X1: un comprador que presenta el id de la orden de compra de otro comprador no ve ni toca el pedido ajeno. La clave
/// lleva al comprador (X8a) y el pedido encontrado se vuelve a comprobar contra él.
/// </summary>
public sealed class WholesaleCrossTenantGuardTests
{
    private static readonly Guid OtherBuyer = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WholesaleOrderGateway Gateway(StubOrderListRepository orders) =>
        new(
            null!, new StubQuotationProductPricingLookup(), new FixedQuotationSettingsStore(), new FixedTenantPriceRounding(),
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, null, IsActive: true, "Cliente", null, null, WithRetention: false, VatSurplus: false)),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()), orders,
            new NoOpQuotationsUnitOfWork(), new RecordingQuotationAuditPublisher(), new RecordingQuotationLifecycleEventPublisher(),
            new SequentialOrderNumberGenerator(), new DefaultNumberingFormats(), new FixedTenantClock(Now),
            new StubQuotationWithholdingLookup());

    [Fact]
    public void TheExternalKeyCarriesTheBuyerTenant()
    {
        Assert.NotEqual(
            Order.WholesaleExternalKey(BuyerTenantId, PurchaseOrderId), Order.WholesaleExternalKey(OtherBuyer, PurchaseOrderId));
        Assert.Contains(BuyerTenantId.ToString("D"), Order.WholesaleExternalKey(BuyerTenantId, PurchaseOrderId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherBuyersPurchaseOrderIdReturnsNothing()
    {
        var orders = new StubOrderListRepository(Wholesale(Line(Guid.CreateVersion7(), 1m, 8_500m)));

        var found = await Gateway(orders).FindAsync(TenantId, OtherBuyer, PurchaseOrderId, Ct);

        Assert.Null(found);
    }

    [Fact]
    public async Task TheOwnBuyerFindsItsOrder()
    {
        var order = Wholesale(Line(Guid.CreateVersion7(), 1m, 8_500m));
        var orders = new StubOrderListRepository(order);

        var found = await Gateway(orders).FindAsync(TenantId, BuyerTenantId, PurchaseOrderId, Ct);

        Assert.Equal(order.Id.Value, found!.OrderId);
        Assert.True(found.AlreadyExisted);
    }

    [Fact]
    public async Task AnotherBuyerCannotCancelTheOrder()
    {
        var order = Wholesale(Line(Guid.CreateVersion7(), 1m, 8_500m));
        var orders = new StubOrderListRepository(order);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => Gateway(orders).CancelByBuyerAsync(
            TenantId, order.Id.Value, OtherBuyer, PurchaseOrderId, PurchaseOrderNumber, Ct));

        Assert.Equal(BuyerOrderErrors.NotCancellableByBuyer, error.Code);
        Assert.Equal(OrderStatus.Open, order.Status);
    }

    [Fact]
    public async Task AnUnknownOrderCannotBeCancelled()
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => Gateway(new StubOrderListRepository())
            .CancelByBuyerAsync(TenantId, Guid.CreateVersion7(), BuyerTenantId, PurchaseOrderId, PurchaseOrderNumber, Ct));

        Assert.Equal(BuyerOrderErrors.NotCancellableByBuyer, error.Code);
    }
}
