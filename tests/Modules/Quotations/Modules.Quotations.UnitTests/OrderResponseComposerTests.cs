using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El detalle del pedido trae la solicitud de la tienda sólo cuando el pedido salió de ella: un pedido de otro origen nunca consulta
/// el puerto, porque todos los detalles pasan por este compositor y la consulta sería un viaje de más por cada uno.
/// </summary>
public sealed class OrderResponseComposerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 15, 0, 0, TimeSpan.Zero);

    private static readonly QuotationCustomerRef Customer = new(
        ClientId, TenantId, "CUC-001", IsActive: true, "Ana Pérez",
        "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

    [Fact]
    public async Task AManualOrderHasANullStoreRequestAndNoLookup()
    {
        var lookup = new RecordingOrderStoreRequestLookup(null);
        var detail = new OrderDetailDto(OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto(), null, []);

        var response = await Composer(lookup).ComposeAsync(TenantId, detail, TestContext.Current.CancellationToken);

        Assert.Null(response.StoreRequest);
        Assert.Empty(lookup.Calls);
    }

    [Fact]
    public async Task AStoreOrderCarriesItsRequestFromTheLookup()
    {
        var request = new OrderStoreRequestRef(
            Guid.CreateVersion7(), "T-0001", ClientId, "Ana Pérez", "300 123 4567", "+573001234567", "ana@test.co",
            119_000m, "COP", TotalDifference: -5_000m);
        var lookup = new RecordingOrderStoreRequestLookup(request);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with { Source = "Store" };

        var response = await Composer(lookup).ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Equal((TenantId, order.Id), Assert.Single(lookup.Calls));
        Assert.Equal(
            new OrderStoreRequestResponse(
                request.Id, "T-0001", ClientId, "Ana Pérez", "300 123 4567", "+573001234567", "ana@test.co", 119_000m, "COP",
                -5_000m),
            response.StoreRequest);
    }

    [Fact]
    public async Task AStoreOrderWithoutARequestHasANullStoreRequest()
    {
        var lookup = new RecordingOrderStoreRequestLookup(null);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with { Source = "Store" };

        var response = await Composer(lookup).ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Null(response.StoreRequest);
        Assert.Single(lookup.Calls);
    }

    private static OrderResponseComposer Composer(IOrderStoreRequestLookup storeRequests) =>
        new(
            new StubQuotationCustomerLookup(Customer),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            storeRequests);

    private sealed class RecordingOrderStoreRequestLookup(OrderStoreRequestRef? result) : IOrderStoreRequestLookup
    {
        public List<(Guid TenantId, Guid OrderId)> Calls { get; } = [];

        public Task<OrderStoreRequestRef?> FindAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, orderId));
            return Task.FromResult(result);
        }
    }
}
