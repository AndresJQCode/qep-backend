using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>Buyers → Quotations: un scope por llamada y mapeo campo a campo del gateway a los DTO del puerto.</summary>
public sealed class BuyerOrdersAdapterTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Account = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cancelled = new(2026, 11, 9, 11, 0, 0, TimeSpan.Zero);

    private sealed class Counter
    {
        public int Instances;
    }

    private sealed class RecordingBuyerOrderGateway : IBuyerOrderGateway
    {
        private readonly Counter _counter;

        public RecordingBuyerOrderGateway(Counter counter, BuyerOrderDetail? detail, Exception? cancelFailure)
        {
            _counter = counter;
            Detail = detail;
            CancelFailure = cancelFailure;
            Interlocked.Increment(ref _counter.Instances);
        }

        public BuyerOrderDetail? Detail { get; }

        public Exception? CancelFailure { get; }

        public Task<BuyerOrderPage> ListAsync(Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new BuyerOrderPage(
                [new BuyerOrderSummary(OrderId, "PED-1", Created, "Cancelled", "PaymentPending", "Prepared", true, 119.5m, "COP", 2, "Camisa", true)],
                page, pageSize, 7));

        public Task<BuyerOrderDetail?> FindAsync(Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(orderId == OrderId ? Detail : null);

        public Task<BuyerOrderDetail?> CancelAsync(Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken) =>
            CancelFailure is { } failure ? throw failure : Task.FromResult(Detail);

        public Task<bool> LinkAsync(Guid tenantId, Guid orderId, Guid buyerAccountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static BuyerOrderDetail SampleDetail() =>
        new(OrderId, "PED-1", Created, "Cancelled", "PaymentPending", "Prepared", true, 119.5m, "COP", 2, "Camisa",
            [new BuyerOrderLine("Camisa", 1.5m, 10m, 17.85m), new BuyerOrderLine("Gorra", 2m, 50m, 101.65m)],
            Cancelled, false, 84.03m, 15.97m, 19.5m, false, "Coordinadora", "BillingParty",
            new BuyerOrderBilling("Ana Pérez", "CC", "123456", "ana@test.co", "3001234567", "Calle 1 # 2-3", "CO", "Antioquia", "Medellín"),
            new BuyerOrderShipping("Luis Gómez", "3009876543", "Carrera 4 # 5-6", "CO", "Cundinamarca", "Bogotá"),
            "Entregar en portería");

    private static (BuyerOrdersAdapter Adapter, Counter Counter) CreateAdapter(
        bool withDetail = true, Exception? cancelFailure = null, BuyerOrderDetail? detail = null)
    {
        var counter = new Counter();
        var services = new ServiceCollection();
        services.AddScoped<IBuyerOrderGateway>(_ =>
            new RecordingBuyerOrderGateway(counter, withDetail ? detail ?? SampleDetail() : null, cancelFailure));
        var factory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new BuyerOrdersAdapter(factory), counter);
    }

    [Fact]
    public async Task CancelOpensOneScopePerCall()
    {
        var (adapter, counter) = CreateAdapter();

        await adapter.CancelAsync(Tenant, Account, OrderId, CancellationToken.None);
        await adapter.CancelAsync(Tenant, Account, OrderId, CancellationToken.None);

        Assert.Equal(2, counter.Instances);
    }

    [Fact]
    public async Task FindMapsEveryFieldIncludingLinesFlagAndCancellation()
    {
        var (adapter, _) = CreateAdapter();

        var detail = await adapter.FindAsync(Tenant, Account, OrderId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(OrderId, detail.Id);
        Assert.Equal("PED-1", detail.Number);
        Assert.Equal(Created, detail.CreatedAt);
        Assert.Equal("Cancelled", detail.Status);
        Assert.Equal("PaymentPending", detail.PaymentStatus);
        Assert.Equal("Prepared", detail.FulfillmentStatus);
        Assert.True(detail.Invoiced);
        Assert.Equal(119.5m, detail.Total);
        Assert.Equal("COP", detail.Currency);
        Assert.Equal(2, detail.LineCount);
        Assert.Equal("Camisa", detail.FirstLineName);
        Assert.Equal(Cancelled, detail.CancelledAt);
        Assert.False(detail.CanCancel);
        Assert.Collection(
            detail.Lines,
            line => Assert.Equal(new BuyerOrderLineDto("Camisa", 1.5m, 10m, 17.85m), line),
            line => Assert.Equal(new BuyerOrderLineDto("Gorra", 2m, 50m, 101.65m), line));
    }

    [Fact]
    public async Task FindMapsTheTotalsPartiesAndBuyerNotes()
    {
        var source = SampleDetail();
        var (adapter, _) = CreateAdapter();

        var detail = await adapter.FindAsync(Tenant, Account, OrderId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(84.03m, detail.Subtotal);
        Assert.Equal(15.97m, detail.TaxAmount);
        Assert.Equal(19.5m, detail.FreightTotal);
        Assert.False(detail.IsStorePickup);
        Assert.Equal("Coordinadora", detail.CarrierName);
        Assert.Equal("BillingParty", detail.BilledTo);
        Assert.Equal("Entregar en portería", detail.BuyerNotes);
        var billing = source.Billing!;
        Assert.Equal(
            new BuyerOrderBillingDto(
                billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Email, billing.Phone, billing.Address,
                billing.Country, billing.DepartmentName, billing.CityName),
            detail.Billing);
        var shipping = source.Shipping!;
        Assert.Equal(
            new BuyerOrderShippingDto(
                shipping.Name, shipping.Phone, shipping.Address, shipping.Country, shipping.DepartmentName, shipping.CityName),
            detail.Shipping);
    }

    [Fact]
    public async Task AMissingBillingAndShippingStayNull()
    {
        var (adapter, _) = CreateAdapter(detail: SampleDetail() with { Billing = null, Shipping = null, BilledTo = "FinalConsumer" });

        var detail = await adapter.FindAsync(Tenant, Account, OrderId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Null(detail.Billing);
        Assert.Null(detail.Shipping);
        Assert.Equal("FinalConsumer", detail.BilledTo);
    }

    [Theory]
    [InlineData(typeof(BuyerOrderDetail), typeof(BuyerOrderDetailDto))]
    [InlineData(typeof(BuyerOrderBilling), typeof(BuyerOrderBillingDto))]
    [InlineData(typeof(BuyerOrderShipping), typeof(BuyerOrderShippingDto))]
    [InlineData(typeof(BuyerOrderLine), typeof(BuyerOrderLineDto))]
    public void TheDetailDtosMirrorTheGatewayRecords(Type gateway, Type dto) =>
        Assert.Equal(
            gateway.GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal),
            dto.GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));

    [Fact]
    public async Task ListMapsPageAndSummaries()
    {
        var (adapter, _) = CreateAdapter();

        var page = await adapter.ListAsync(Tenant, Account, 3, 20, CancellationToken.None);

        Assert.Equal(3, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(7, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal(
            new BuyerOrderSummaryDto(
                OrderId, "PED-1", Created, "Cancelled", "PaymentPending", "Prepared", true, 119.5m, "COP", 2, "Camisa", true),
            item);
    }

    [Fact]
    public async Task AMissingOrderStaysNull()
    {
        var (adapter, _) = CreateAdapter(withDetail: false);

        Assert.Null(await adapter.FindAsync(Tenant, Account, Guid.CreateVersion7(), CancellationToken.None));
        Assert.Null(await adapter.CancelAsync(Tenant, Account, OrderId, CancellationToken.None));
    }

    [Fact]
    public async Task ConflictFromTheGatewaySurfacesUnchanged()
    {
        var failure = new ResourceConflictException("order.order.not_cancellable_by_buyer", "no");
        var (adapter, _) = CreateAdapter(cancelFailure: failure);

        var thrown = await Assert.ThrowsAsync<ResourceConflictException>(
            () => adapter.CancelAsync(Tenant, Account, OrderId, CancellationToken.None));

        Assert.Same(failure, thrown);
    }
}
