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

    // Spec 2026-11-10 §5: el detalle trae los cuatro ejes, quién y cuándo de cada marca y el saldo, para que la
    // pantalla no los deduzca del historial ni reste comprobantes.
    [Fact]
    public async Task TheResponseCarriesTheAxesTheirMarksAndTheBalance()
    {
        var marker = new MemberId(Guid.CreateVersion7());
        var order = OrderBuilders.Pending(
            TenantId, AdvisorId, Now, ClientId, proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);
        order.MarkPaid(marker, requirePaymentProof: false, order.WithoutCosts(), Now.AddMinutes(1));
        order.Prepare(marker, Now.AddMinutes(2));
        order.Archive(marker, Now.AddMinutes(3));

        var response = await Composer().ComposeAsync(
            TenantId, new OrderDetailDto(order.ToDto(), null, []), TestContext.Current.CancellationToken);

        var result = response.Order;
        Assert.Equal(
            ("Archived", "FullPaymentReceived", "Prepared", false),
            (result.Status, result.PaymentStatus, result.FulfillmentStatus, result.Invoiced));
        Assert.Equal((Now.AddMinutes(1), marker.Value), (result.PaidMarkedAt!.Value, result.PaidMarkedBy!.Value));
        Assert.Equal((Now.AddMinutes(2), marker.Value), (result.PreparedAt!.Value, result.PreparedBy!.Value));
        Assert.Equal((Now.AddMinutes(3), marker.Value), (result.ArchivedAt!.Value, result.ArchivedBy!.Value));
        Assert.Equal(19_000m, result.Balance);
    }

    [Fact]
    public void TheApprovalFieldsAreGone()
    {
        Assert.Null(typeof(OrderResponse).GetProperty("ApprovedAt"));
        Assert.Null(typeof(OrderResponse).GetProperty("ApprovedBy"));
        Assert.Null(typeof(OrderDto).GetProperty("ApprovedAt"));
        Assert.Null(typeof(OrderDto).GetProperty("ApprovedBy"));
    }

    [Fact]
    public async Task TheOrderDetailCarriesTheClientsContacts()
    {
        var laura = new QuotationClientContact(Guid.CreateVersion7(), "Laura Gómez", "3115550101");
        var pedro = new QuotationClientContact(Guid.CreateVersion7(), "Pedro Ríos", "3125550102");

        var response = await ComposeAsync(laura, pedro);

        Assert.Equal(
            [new ClientContactDto(laura.Id, "Laura Gómez", "3115550101"), new ClientContactDto(pedro.Id, "Pedro Ríos", "3125550102")],
            response.ClientContacts);
    }

    [Fact]
    public async Task AClientWithoutContactsCarriesAnEmptyList()
    {
        var response = await ComposeAsync();

        Assert.NotNull(response.ClientContacts);
        Assert.Empty(response.ClientContacts);
    }

    [Fact]
    public async Task ContactsWithoutPhoneTravelWithPhoneNull()
    {
        var laura = new QuotationClientContact(Guid.CreateVersion7(), "Laura Gómez", null);

        var response = await ComposeAsync(laura);

        Assert.Equal(new ClientContactDto(laura.Id, "Laura Gómez", null), Assert.Single(response.ClientContacts));
    }

    private static async Task<OrderDetailResponse> ComposeAsync(params QuotationClientContact[] contacts)
    {
        var customers = new StubQuotationCustomerLookup(Customer);
        customers.Contacts.AddRange(contacts);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now, ClientId).ToDto();
        return await Composer(customers).ComposeAsync(
            TenantId, new OrderDetailDto(order, null, []), TestContext.Current.CancellationToken);
    }

    private static OrderResponseComposer Composer(StubQuotationCustomerLookup? customers = null) =>
        new(
            customers ?? new StubQuotationCustomerLookup(Customer),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()));
}
