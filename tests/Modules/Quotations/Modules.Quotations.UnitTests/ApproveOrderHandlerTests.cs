using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Aprobar tiene permiso propio (<c>quotations.order.approve</c>) y no el de gestión: quien
/// registra el pedido —la asesora, con <c>quotations.order.manage</c>— no es quien lo revisa.
/// Estas pruebas fijan la mitad del handler; la de la política del endpoint la cubre
/// <c>OrderApiTests</c>.
/// </summary>
public sealed class ApproveOrderHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApproveWithOnlyTheManagePermissionIsForbiddenAndLeavesTheOrderPending()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.read", "quotations.order.manage"));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new ApproveOrderCommand(TenantId, row.Order.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(OrderStatus.Pending, row.Order.Status);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task ApproveWithTheApprovePermissionApprovesWithoutNeedingTheManagePermission()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.approve"));

        var approved = await handler.HandleAsync(
            new ApproveOrderCommand(TenantId, row.Order.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal("Approved", approved.Status);
        Assert.Equal(["quotation.order.approved"], audit.Actions);
    }

    private static ApproveOrderHandler NewHandler(
        OrderWithQuotation row,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext) =>
        new(
            new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new StubMembershipDirectory(AdvisorId.Value),
            executionContext,
            new FixedClock(Now));

    private static OrderWithQuotation NewRow()
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            Guid.CreateVersion7(),
            AdvisorId,
            new DateOnly(2026, 10, 30),
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            defaultCurrency: QuotationCurrency.Cop,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

        var order = Order.Create(
            OrderId.New(),
            TenantId,
            "PED-2026-0001",
            quotation.Id,
            OrderPaymentStatus.FullPaymentReceived,
            notes: null,
            AdvisorId,
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)],
            Now);

        return new OrderWithQuotation(order, quotation);
    }

    /// <summary>Al revés de <see cref="StubExecutionContext"/>: concede sólo lo que se le nombra.
    /// Lo que estas pruebas miran es que un permiso de más (gestionar) no alcanza, y eso no se
    /// puede decir negando uno.</summary>
    private sealed class GrantedPermissionsExecutionContext(
        Guid subjectId, Guid tenantId, params string[] grantedPermissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = subjectId;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => grantedPermissions.Contains(permission);
    }
}
