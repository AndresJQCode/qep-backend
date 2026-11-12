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
    public async Task InvoiceWithOnlyTheApprovePermissionIsForbiddenAndLeavesTheOrderUninvoiced()
    {
        var row = NewPaidRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewInvoiceHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.read", "quotations.order.approve"));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new InvoiceOrderCommand(TenantId, row.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(OrderStatus.Open, row.Status);
        Assert.Null(row.InvoicedAt);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task InvoiceWithTheInvoicePermissionRecordsTheMembershipAndAudits()
    {
        var row = NewPaidRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewInvoiceHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.invoice"));

        var invoiced = await handler.HandleAsync(
            new InvoiceOrderCommand(TenantId, row.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal("Open", invoiced.Status);
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
                new RevertOrderInvoicingCommand(TenantId, row.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.True(row.IsInvoiced);
        Assert.Equal(BillerId, row.InvoicedBy);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task RevertWithTheInvoicePermissionClearsTheMarkKeepsTheStatusAndAudits()
    {
        var row = NewInvoicedRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewRevertHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.invoice"));

        var reverted = await handler.HandleAsync(
            new RevertOrderInvoicingCommand(TenantId, row.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal("Open", reverted.Status);
        Assert.Null(reverted.InvoicedAt);
        Assert.Null(reverted.InvoicedBy);
        Assert.Equal(["quotation.order.invoice_reverted"], audit.Actions);
    }

    // Spec 2026-11-10 §3.4 y §6: el pago completo lo exige el ajuste del tenant, que arranca prendido.
    [Fact]
    public async Task InvoicingRequiresFullPaymentByDefault()
    {
        var row = NewUnpaidRow();
        var audit = new RecordingQuotationAuditPublisher();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            InvoiceAsync(NewInvoiceHandler(row, audit, Invoicer()), row));

        Assert.Equal("order.order.not_paid", error.Code);
        Assert.False(row.IsInvoiced);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task InvoicingWithoutFullPaymentWhenTheTenantAllowsIt()
    {
        var row = NewUnpaidRow();

        var invoiced = await InvoiceAsync(
            NewInvoiceHandler(
                row, new RecordingQuotationAuditPublisher(), Invoicer(),
                settings: new FixedQuotationSettingsStore { OrderSettings = new(false, false, false) }),
            row);

        Assert.NotNull(invoiced.InvoicedAt);
        Assert.NotEqual("FullPaymentReceived", invoiced.PaymentStatus);
    }

    // Las mismas validaciones de datos de facturación que marcar pagado (spec 2026-11-10 §3.4).
    [Fact]
    public async Task InvoicingRunsTheBillingDataChecks()
    {
        var row = NewPaidRow();
        var audit = new RecordingQuotationAuditPublisher();
        var withoutDocument = new StubQuotationCustomerLookup(new QuotationCustomerRef(
            Guid.CreateVersion7(), TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false,
            PersonType: "Natural", Country: "CO"));

        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() =>
            InvoiceAsync(NewInvoiceHandler(row, audit, Invoicer(), customer: withoutDocument), row));

        Assert.Equal("order.order.billed_document_required", error.Code);
        Assert.False(row.IsInvoiced);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task InvoicingDoesNotRequirePreparation()
    {
        var row = NewPaidRow();
        Assert.False(row.IsPrepared);

        var invoiced = await InvoiceAsync(NewInvoiceHandler(row, new RecordingQuotationAuditPublisher(), Invoicer()), row);

        Assert.NotNull(invoiced.InvoicedAt);
    }

    [Fact]
    public async Task RevertingKeepsTheOrderStatus()
    {
        var row = NewInvoicedRow();
        row.Archive(AdvisorId, Now);

        var reverted = await NewRevertHandler(row, new RecordingQuotationAuditPublisher(), Invoicer())
            .HandleAsync(new RevertOrderInvoicingCommand(TenantId, row.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal("Archived", reverted.Status);
        Assert.Null(reverted.InvoicedAt);
    }

    private static GrantedPermissionsExecutionContext Invoicer() =>
        new(SubjectId, TenantId, "quotations.order.invoice");

    private static Task<OrderDto> InvoiceAsync(InvoiceOrderHandler handler, Order row) =>
        handler.HandleAsync(new InvoiceOrderCommand(TenantId, row.Id.Value), TestContext.Current.CancellationToken);

    private static Order NewUnpaidRow() =>
        OrderBuilders.Pending(
            TenantId, AdvisorId, Now,
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);

    // Por defecto los ajustes del owner, Never y un cliente con documento: las pruebas de permisos no cambian.
    private static InvoiceOrderHandler NewInvoiceHandler(
        Order row,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext,
        FixedQuotationSettingsStore? settings = null,
        StubQuotationCustomerLookup? customer = null) =>
        new(
            new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new StubMembershipDirectory(BillerId.Value),
            executionContext,
            new FixedClock(Now),
            settings ?? new FixedQuotationSettingsStore(),
            new StubTenantDirectory
            {
                Regional = new(
                    "America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal,
                    "CO", PersonTypeRequirement.Never)
            },
            customer ?? new StubQuotationCustomerLookup(new QuotationCustomerRef(
                Guid.CreateVersion7(), TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false,
                PersonType: "Natural", Country: "CO",
                IdentificationNumber: "900123456", IdentificationType: "NIT")));

    private static RevertOrderInvoicingHandler NewRevertHandler(
        Order row,
        RecordingQuotationAuditPublisher audit,
        IExecutionContext executionContext) =>
        new(
            new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new StubMembershipDirectory(BillerId.Value),
            executionContext,
            new FixedClock(Now));

    private static Order NewPaidRow()
    {
        var order = OrderBuilders.Pending(
            TenantId, AdvisorId, Now,
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);
        order.MarkPaid(AdvisorId, requirePaymentProof: false, Now);
        return order;
    }

    private static Order NewInvoicedRow()
    {
        var row = NewPaidRow();
        row.Invoice(BillerId, requireFullPayment: true, Now);
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
