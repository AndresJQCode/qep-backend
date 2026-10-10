using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Facturar y revertir tienen permiso propio (<c>quotations.order.invoice</c>, spec 2026-10-05,
/// decisión 5) y no el de aprobar. Las pruebas de API conceden los dos —hay que aprobar antes de
/// facturar—, así que un handler que revalidara con <c>quotations.order.approve</c> pasaría todas;
/// estas fijan la mitad del handler (Review Focus 1). La de la política del endpoint la cubre
/// <c>OrderApiTests</c>.
/// </summary>
public sealed class OrderInvoicingHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly MemberId BillerId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InvoiceWithOnlyTheApprovePermissionIsForbiddenAndLeavesTheOrderApproved()
    {
        var row = NewApprovedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewInvoiceHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.read", "quotations.order.approve"));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new InvoiceOrderCommand(TenantId, row.Order.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(OrderStatus.Approved, row.Order.Status);
        Assert.Null(row.Order.InvoicedAt);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task InvoiceWithTheInvoicePermissionRecordsTheMembershipAndAudits()
    {
        var row = NewApprovedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewInvoiceHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.invoice"));

        var invoiced = await handler.HandleAsync(
            new InvoiceOrderCommand(TenantId, row.Order.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal("Invoiced", invoiced.Status);
        Assert.Equal(Now, invoiced.InvoicedAt);
        Assert.Equal(BillerId.Value, invoiced.InvoicedBy);
        Assert.Equal(["quotation.order.invoiced"], audit.Actions);
    }

    [Fact]
    public async Task RevertWithOnlyTheApprovePermissionIsForbiddenAndLeavesTheOrderInvoiced()
    {
        var row = NewInvoicedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewRevertHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.read", "quotations.order.approve"));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new RevertOrderInvoicingCommand(TenantId, row.Order.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(OrderStatus.Invoiced, row.Order.Status);
        Assert.Equal(BillerId, row.Order.InvoicedBy);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task RevertWithTheInvoicePermissionReturnsTheOrderApprovedAndAudits()
    {
        var row = NewInvoicedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewRevertHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.invoice"));

        var reverted = await handler.HandleAsync(
            new RevertOrderInvoicingCommand(TenantId, row.Order.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal("Approved", reverted.Status);
        Assert.Null(reverted.InvoicedAt);
        Assert.Null(reverted.InvoicedBy);
        Assert.Equal(["quotation.order.invoice_reverted"], audit.Actions);
    }

    private static InvoiceOrderHandler NewInvoiceHandler(
        OrderWithQuotation row,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext) =>
        new(
            new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new StubMembershipDirectory(BillerId.Value),
            executionContext,
            new FixedClock(Now));

    private static RevertOrderInvoicingHandler NewRevertHandler(
        OrderWithQuotation row,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext) =>
        new(
            new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new StubMembershipDirectory(BillerId.Value),
            executionContext,
            new FixedClock(Now));

    private static OrderWithQuotation NewApprovedRow()
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
            defaultCurrency: "COP",
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
        order.Approve(AdvisorId, Now);

        return new OrderWithQuotation(order, quotation);
    }

    private static OrderWithQuotation NewInvoicedRow()
    {
        var row = NewApprovedRow();
        row.Order.Invoice(BillerId, Now);
        return row;
    }

    /// <summary>Al revés de <see cref="StubExecutionContext"/>: concede sólo lo que se le nombra.
    /// Lo que estas pruebas miran es que un permiso vecino (aprobar) no alcanza, y eso no se puede
    /// decir negando uno.</summary>
    private sealed class GrantedPermissionsExecutionContext(
        Guid subjectId, Guid tenantId, params string[] grantedPermissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = subjectId;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => grantedPermissions.Contains(permission);
    }
}
