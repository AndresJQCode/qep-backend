using Modules.Purchases.Application;
using Modules.Purchases.Domain;
using Modules.Purchases.Infrastructure.Persistence;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Purchases.UnitTests;

/// <summary>Un publicador grabador: arma el mismo contrato que el real y lo guarda en memoria.</summary>
internal sealed class RecordingPurchaseOrderEvents : IPurchaseOrderEventPublisher
{
    public List<PurchaseOrderReceivedEvent> Received { get; } = [];

    public void PublishReceived(PurchaseOrder order, string origin, DateTimeOffset occurredAt) =>
        Received.Add(PurchaseOrderReceivedEvent.From(order, origin, occurredAt));
}

/// <summary>
/// <c>purchases.order.received.v1</c>: lo que viaja al recibir una orden. Sólo el cambio a <c>Received</c> publica, con las
/// cantidades confirmadas y la versión que dejó esa transición.
/// </summary>
public sealed class PurchaseOrderReceivedEventTests
{
    private static readonly Guid TenantId = PurchaseOrderTestData.TenantId;

    private readonly FakePurchaseOrderRepository _orders = new();
    private readonly FakePurchaseOrderEmail _email = new();
    private readonly FakePurchasesAudit _audit = new();
    private readonly RecordingPurchaseOrderEvents _events = new();
    private readonly IPurchasesUnitOfWork _unitOfWork = new FakePurchasesUnitOfWork();
    private readonly FakeExecutionContext _context = new(TenantId, PurchasesPermissions.OrderManage, PurchasesPermissions.OrderRead);
    private readonly FakeSupplierLookup _suppliers = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TransitionPurchaseOrderHandler Handler() => new(
        _context,
        new FakeMembership(),
        new FakePurchasesClock(PurchaseOrderTestData.Now),
        _suppliers,
        _orders,
        _unitOfWork,
        _audit,
        _events,
        new PurchaseOrderComposer(_context, _suppliers, new FakeMemberDirectory()),
        new FakeWholesaleOrderIntake(),
        new PurchaseOrderEmailDelivery(_email, new FakePurchaseOrderDocumentBuilder(), _unitOfWork, _audit));

    private Task<PurchaseOrderDetailDto> Transition(PurchaseOrder order, PurchaseOrderAction action) =>
        Handler().HandleAsync(new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, action), Ct);

    private static PurchaseOrder ConfirmedOrder(
        IReadOnlyList<PurchaseOrderLineDraft> lines,
        Func<PurchaseOrder, IReadOnlyList<PurchaseOrderLineConfirmation>> confirmation)
    {
        var order = PurchaseOrderTestData.Order(lines);
        order.Send(PurchaseOrderTestData.MemberId, PurchaseOrderTestData.Now);
        order.Confirm(confirmation(order), PurchaseOrderTestData.MemberId, PurchaseOrderTestData.Now);
        return order;
    }

    [Fact]
    public async Task ReceivingPublishesTheConfirmedLinesWithManualOrigin()
    {
        var variant = Guid.CreateVersion7();
        var order = _orders.Seed(ConfirmedOrder(
            [PurchaseOrderTestData.Line(quantity: 5m, variantId: variant)],
            // El proveedor confirmó menos de lo pedido: viaja lo confirmado.
            created => [new PurchaseOrderLineConfirmation(created.Lines[0].Id, 3m, 10m)]));

        await Transition(order, PurchaseOrderAction.Receive);

        var published = Assert.Single(_events.Received);
        Assert.Equal(PurchaseOrderEventOrigins.Manual, published.origin);
        Assert.Equal((TenantId, order.Id.Value), (published.tenantId, published.purchaseOrderId));
        Assert.Equal(order.Version, published.version);
        Assert.Equal(PurchaseOrderTestData.Now, published.occurredAt);
        var line = Assert.Single(published.lines);
        Assert.Equal((order.Lines[0].Id, (Guid?)variant, 3m), (line.lineId, line.variantId, line.quantity));
    }

    // El inventario del WMS lee este evento por nombre de campo: se parsea el payload REAL del publicador con el lector de Wms.
    [Fact]
    public void TheRealPayloadParsesWithTheWmsReader()
    {
        var variant = Guid.CreateVersion7();
        var order = ConfirmedOrder(
            [PurchaseOrderTestData.Line(quantity: 5m, variantId: variant)],
            created => [new PurchaseOrderLineConfirmation(created.Lines[0].Id, 3m, 10m)]);
        order.Receive(PurchaseOrderTestData.MemberId, PurchaseOrderTestData.Now);
        using var context = new PurchasesDbContextFactory().CreateDbContext([]);

        new PurchaseOrderEventPublisher(context).PublishReceived(order, PurchaseOrderEventOrigins.Manual, PurchaseOrderTestData.Now);

        var message = Assert.Single(context.Outbox.Local);
        Assert.True(InventoryEventPayloads.TryParse(message.EventName, message.PayloadJson, out var parsed), message.PayloadJson);
        Assert.Equal(InventoryEventKind.PurchaseReceived, parsed.Kind);
        Assert.Equal(InventoryDocumentTypes.PurchaseOrder, parsed.DocumentType);
        Assert.Equal((TenantId, order.Id.Value, order.Version), (parsed.TenantId, parsed.DocumentId, parsed.Version));
        Assert.Equal(PurchaseOrderEventOrigins.Manual, parsed.Origin);
        var line = Assert.Single(parsed.Lines);
        Assert.Equal((order.Lines[0].Id, (Guid?)variant, 3m), (line.LineId, line.VariantId, line.Quantity));
    }

    [Fact]
    public async Task ARejectedLineDoesNotTravel()
    {
        var order = _orders.Seed(ConfirmedOrder(
            [PurchaseOrderTestData.Line(quantity: 2m), PurchaseOrderTestData.Line(quantity: 4m)],
            created =>
            [
                new PurchaseOrderLineConfirmation(created.Lines[0].Id, 2m, 10m),
                new PurchaseOrderLineConfirmation(created.Lines[1].Id, 0m, 10m),
            ]));

        await Transition(order, PurchaseOrderAction.Receive);

        var published = Assert.Single(_events.Received);
        Assert.Equal([order.Lines[0].Id], published.lines.Select(line => line.lineId).ToArray());
    }

    [Fact]
    public async Task ALinkedLineWithoutVariantTravelsWithNullVariant()
    {
        // Una orden vinculada cuya línea confirmada todavía no se enlazó al catálogo propio.
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        order.ApplyWholesalerApproval(order.Lines.Select(line => PurchaseOrderTestData.FinalOf(line)).ToList(), PurchaseOrderTestData.Now);
        Assert.Null(order.Lines[0].VariantId);

        await Transition(order, PurchaseOrderAction.Receive);

        var line = Assert.Single(Assert.Single(_events.Received).lines);
        Assert.Null(line.variantId);
        Assert.Equal(order.Lines[0].ConfirmedQuantity, line.quantity);
    }

    [Fact]
    public async Task CancellingPublishesNothing()
    {
        var order = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Confirmed));

        await Transition(order, PurchaseOrderAction.Cancel);

        Assert.Empty(_events.Received);
    }
}
