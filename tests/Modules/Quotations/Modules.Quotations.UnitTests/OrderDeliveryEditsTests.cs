using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-11-24 §11.1.4: pasar de recoger a enviar con los datos del cliente también es una entrega que cambió, y un
/// pedido de Shopify sin parte propia no tiene qué mandar a la tienda.</summary>
public sealed class OrderDeliveryEditsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendingAPickupShopifyOrderToTheCustomerAddressIsRejected(bool strict)
    {
        var order = ShopifyOrder(isStorePickup: true);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => SendToTheCustomerAsync(order, strict));

        Assert.Equal(OrderDeliveryErrors.ShopifyPartyRequired, error.Code);
    }

    [Fact]
    public async Task ResendingTheCustomerAddressOfAShopifyOrderWithoutPartyChangesNothing()
    {
        var order = ShopifyOrder(isStorePickup: false);

        Assert.False(await SendToTheCustomerAsync(order, strict: true));
    }

    private static Task<bool> SendToTheCustomerAsync(Order order, bool strict) =>
        OrderDeliveryEdits.ApplyAsync(
            order,
            new OrderDeliveryEditRequest(IsStorePickup: false, Shipping: null),
            strict,
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-1", true, "Laura Gómez", "+573001112233", "Cra 5 # 10-20", false, false)),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            new NeverAskedDeliveryCheck(),
            TenantId,
            Advisor,
            Now.AddHours(1),
            Ct);

    private static Order ShopifyOrder(bool isStorePickup)
    {
        var account = new QuotationBillingAccount
        {
            CompanyId = Guid.CreateVersion7(),
            BankName = "Bancolombia",
            AccountNumber = "7542",
            Currency = "COP",
            Country = "CO",
        };
        var line = new OrderLineInput(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));
        return Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0001", new OrderHeader(ClientId, Advisor, "COP", null, false, null, false, []),
            new QuotationParties(null, null, IsStorePickup: isStorePickup), account, [line], OrderPaymentStatus.PaymentPending,
            notes: null, Advisor, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime), OrderSource.Shopify, "#1001",
            ShippingDetails.None, null, null, OrderLineCosts.None);
    }

    // Sin parte propia no hay nada que preguntarle a la plataforma.
    private sealed class NeverAskedDeliveryCheck : IExternalOrderDeliveryCheck
    {
        public Task<string?> CheckAsync(
            Guid tenantId, Guid orderId, string country, Guid? regionId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An order without its own shipping party is not checked.");
    }
}
