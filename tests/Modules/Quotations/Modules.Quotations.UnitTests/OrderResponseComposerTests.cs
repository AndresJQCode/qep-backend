using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El detalle del pedido ya no trae la solicitud de la tienda (spec 2026-11-09): el checkout crea el pedido directo, así que un pedido
/// de la tienda se compone como cualquier otro.
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
    public void TheDetailResponseHasNoStoreRequestProperty() =>
        Assert.Null(typeof(OrderDetailResponse).GetProperty("StoreRequest"));

    [Fact]
    public async Task AStoreOrderComposesLikeAnyOtherOrder()
    {
        // El pedido de la tienda ya es un Order completo: su detalle no le pide nada a Store.
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto() with { Source = "Store" };

        var response = await Composer().ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);

        Assert.Equal("Store", response.Order.Source);
        Assert.Equal("Asesora Uno", response.AdvisorName);
    }

    private static OrderResponseComposer Composer() =>
        new(
            new StubQuotationCustomerLookup(Customer),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()));
}
