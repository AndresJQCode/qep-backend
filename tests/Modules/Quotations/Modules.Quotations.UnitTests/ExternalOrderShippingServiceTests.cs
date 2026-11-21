using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-11-21 §5.7: el pedido visto por una transportadora externa. Sólo lectura; el destinatario sale de la parte de envío
/// y, sin ella, de la ficha del cliente; la identificación, de la parte de facturación o de la ficha.</summary>
public sealed class ExternalOrderShippingServiceTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid CustomerCityId = Guid.CreateVersion7();
    private static readonly Guid ShippingCityId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 23, 30, 0, TimeSpan.Zero);

    private static readonly QuotationCustomerRef Customer = new(
        ClientId, TenantId, "CUC-1", true, "Pedro Pérez", "3189023450", "Calle 40 20-40", false, false,
        Email: "pedro@test.co", CityId: CustomerCityId, CityName: "Bogotá", IdentificationNumber: "900123456");

    private static Order NewOrder(QuotationParties? parties = null, ShippingDetails? shipping = null) =>
        Order.CreateManual(
            OrderId.New(),
            TenantId,
            "PED-2026-0042",
            new OrderHeader(ClientId, Advisor, "COP", "Transferencia", false, null, false, []),
            parties ?? QuotationParties.Empty,
            new QuotationBillingAccount { CompanyId = Guid.CreateVersion7(), BankName = "Bancolombia", AccountNumber = "7542", Currency = "COP" },
            shipping ?? ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"))],
            "Entregar en portería",
            Advisor,
            [],
            Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    private static ExternalOrderShippingService Service(Order order, bool customerUnresolvable = false) =>
        new(
            new StubOrderListRepository(order),
            new FixedTenantClock(Now),
            new StubQuotationCustomerLookup(Customer) { Unresolvable = customerUnresolvable });

    [Fact]
    public async Task WithoutAShippingPartyTheRecipientIsTheCustomerCard()
    {
        var order = NewOrder(shipping: new ShippingDetails(null, "Coordinadora", FreightChargeMode.Charged, 11_900m, 19, true));

        var snapshot = await Service(order).GetForShippingAsync(TenantId, order.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal(("PED-2026-0042", false, false, true, "Coordinadora", true), (
            snapshot.OrderNumber, snapshot.IsCancelled, snapshot.IsStorePickup, snapshot.RequiresShipping, snapshot.CarrierName, snapshot.CashOnDelivery));
        // 23:30 UTC es el 21 a las 18:30 en Bogotá: hoy en la zona del tenant.
        Assert.Equal(new DateOnly(2026, 11, 21), snapshot.Today);
        // Las líneas con IVA: 2 × 11.900 con el IVA incluido en el precio.
        Assert.Equal(23_800m, snapshot.ProductsTotal);
        Assert.Equal(order.NetTotal, snapshot.NetTotal);
        Assert.Equal("Entregar en portería", snapshot.Notes);
        Assert.Equal(order.Version, snapshot.Version);
        var line = Assert.Single(snapshot.Lines);
        Assert.Equal(("Aceite", "ABC", 2m), (line.Name, line.Code, line.Quantity));
        Assert.Equal(("Pedro Pérez", "3189023450", "Calle 40 20-40", CustomerCityId, "900123456"), (
            snapshot.Recipient.Name, snapshot.Recipient.Phone, snapshot.Recipient.Address, snapshot.Recipient.CityId, snapshot.Recipient.Identification));
    }

    [Fact]
    public async Task AShippingPartyWinsAndTheCustomerFillsWhatItLacks()
    {
        // Un pedido manual exige la parte de envío completa y, si la trae, la de facturación también; sin ésta, la identificación sale de la ficha.
        var parties = new QuotationParties(
            null,
            new QuotationPartyDetails { Name = "Portería Torre B", Phone = "3000000000", Email = "porteria@test.co", Address = "Carrera 7 1-10", DepartmentId = Guid.CreateVersion7(), CityId = ShippingCityId });
        var order = NewOrder(parties);

        var snapshot = await Service(order).GetForShippingAsync(TenantId, order.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal("Portería Torre B", snapshot.Recipient.Name);
        Assert.Equal("Carrera 7 1-10", snapshot.Recipient.Address);
        Assert.Equal(ShippingCityId, snapshot.Recipient.CityId);
        // El teléfono es el de la parte de envío; la identificación, que la parte de envío no lleva, la da la ficha.
        Assert.Equal("3000000000", snapshot.Recipient.Phone);
        Assert.Equal("900123456", snapshot.Recipient.Identification);
    }

    [Fact]
    public async Task AStorePickupAndACancelledOrderAreReportedNotHidden()
    {
        var pickup = NewOrder(new QuotationParties(null, null, IsStorePickup: true));
        var cancelled = NewOrder();
        cancelled.Cancel(Advisor, "Cliente desistió", Now.AddMinutes(1));

        var pickupSnapshot = await Service(pickup).GetForShippingAsync(TenantId, pickup.Id.Value, TestContext.Current.CancellationToken);
        var cancelledSnapshot = await Service(cancelled).GetForShippingAsync(TenantId, cancelled.Id.Value, TestContext.Current.CancellationToken);

        Assert.True(pickupSnapshot!.IsStorePickup);
        Assert.True(cancelledSnapshot!.IsCancelled);
    }

    [Fact]
    public async Task AMissingCustomerDoesNotBreakTheSnapshot()
    {
        var order = NewOrder();

        var snapshot = await Service(order, customerUnresolvable: true).GetForShippingAsync(TenantId, order.Id.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Recipient.Name);
        Assert.Null(snapshot.Recipient.Identification);
    }
}
