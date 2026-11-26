using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using static Modules.Quotations.UnitTests.WholesaleOrderFixtures;

namespace Modules.Quotations.UnitTests;

/// <summary>D-1, D-2 y D-22: aprobar el pedido mayorista con el permiso de gestión, con su historial, su auditoría y el evento
/// de vuelta al distribuidor con todas las líneas finales.</summary>
public sealed class ApproveWholesaleOrderTests
{
    private static readonly Guid Approver = Guid.CreateVersion7();

    private readonly RecordingQuotationLifecycleEventPublisher _events = new();
    private readonly RecordingQuotationAuditPublisher _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApproveWholesaleOrderHandler Handler(StubOrderListRepository orders, IExecutionContext? context = null) =>
        new(
            orders, new NoOpQuotationsUnitOfWork(), _audit, _events, new StubMembershipDirectory(Approver),
            context ?? new StubExecutionContext(SubjectId, TenantId), new FixedClock(Now.AddHours(2)));

    [Fact]
    public async Task ApprovePublishesEveryFinalLineWithItsSourceId()
    {
        var sourceLine = Guid.CreateVersion7();
        var fromOrder = Guid.CreateVersion7();
        var added = Guid.CreateVersion7();
        var order = Wholesale(Line(fromOrder, 2m, 11_900m, sourceLine, taxPercentage: 19));
        order.AddItem(OrderItemId.New(), added, added, 1m, 5_000m, 0m, 0, OrderLineCosts.None, Advisor, Now);
        var orders = new StubOrderListRepository(order);

        var result = await Handler(orders).HandleAsync(new ApproveWholesaleOrderCommand(TenantId, order.Id.Value), Ct);

        Assert.Equal(Now.AddHours(2), order.WholesaleApprovedAt);
        Assert.Equal(Approver, order.WholesaleApprovedBy!.Value.Value);
        Assert.Equal(order.Version, result.Version);
        var approved = Assert.Single(_events.WholesaleApproved);
        Assert.Equal(
            [(sourceLine, fromOrder, 2m, 11_900m, 19m), ((Guid?)null, added, 1m, 5_000m, 0m)],
            approved.Select(line => (line.SourcePurchaseOrderLineId, line.VariantId, line.Quantity, line.UnitPrice, line.TaxPercentage)));
        Assert.Equal(["quotation.order.wholesale_approved"], _audit.Actions);
        var history = Assert.Single(orders.History);
        Assert.Equal((OrderHistoryEventType.WholesaleApproved, Approver), (history.EventType, history.MemberId!.Value.Value));
    }

    [Fact]
    public async Task ApproveNeedsOrderManage()
    {
        var order = Wholesale(Line(Guid.CreateVersion7(), 1m, 8_500m));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(new StubOrderListRepository(order), new StubExecutionContext(SubjectId, TenantId, OrdersPermissions.OrderManage))
                .HandleAsync(new ApproveWholesaleOrderCommand(TenantId, order.Id.Value), Ct));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Null(order.WholesaleApprovedAt);
    }

    [Fact]
    public async Task ApproveAnotherTenantsOrderIs404()
    {
        // El repositorio acota por tenant: el pedido de otro tenant no vuelve.
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler(new StubOrderListRepository()).HandleAsync(new ApproveWholesaleOrderCommand(TenantId, Guid.CreateVersion7()), Ct));

        Assert.Equal("order.order.not_found", error.Code);
        Assert.Empty(_events.WholesaleApproved);
    }

    [Fact]
    public async Task ApprovingANonWholesaleOrderIsSourceInvalid()
    {
        var variant = Guid.CreateVersion7();
        var order = Manual(new OrderLineInput(variant, variant, 1m, 8_500m, 0m, 0));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            Handler(new StubOrderListRepository(order)).HandleAsync(new ApproveWholesaleOrderCommand(TenantId, order.Id.Value), Ct));

        Assert.Equal(WholesaleOrderErrors.SourceInvalid, error.Code);
        Assert.Empty(_audit.Actions);
    }
}
