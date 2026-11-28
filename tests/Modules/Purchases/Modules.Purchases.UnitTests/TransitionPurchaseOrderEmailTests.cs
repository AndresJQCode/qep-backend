using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

/// <summary>
/// Enviar una orden (externa o vinculada) manda el correo al proveedor con el PDF. Un fallo del correo nunca deshace el envío:
/// la orden ya quedó en <c>Sent</c> y el resultado se guarda para poder reenviar.
/// </summary>
public sealed class TransitionPurchaseOrderEmailTests
{
    private const string SupplierEmail = "ventas@textiles.co";

    private static readonly Guid TenantId = PurchaseOrderTestData.TenantId;

    private readonly FakeExecutionContext _context = new(TenantId, PurchasesPermissions.OrderManage, PurchasesPermissions.OrderRead);
    private readonly FakeSupplierLookup _suppliers = new();
    private readonly FakePurchaseOrderRepository _orders = new();
    private readonly FakePurchasesAudit _audit = new();
    private readonly FakeMemberDirectory _members = new();
    private readonly FakeWholesaleOrderIntake _intake = new();
    private readonly FakePurchaseOrderEmail _email = new();
    private readonly FakePurchaseOrderDocumentBuilder _documents = new();
    private IPurchasesUnitOfWork _unitOfWork = new FakePurchasesUnitOfWork();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TransitionPurchaseOrderHandler Handler() => new(
        _context,
        new FakeMembership(),
        new FakePurchasesClock(PurchaseOrderTestData.Now),
        _suppliers,
        _orders,
        _unitOfWork,
        _audit,
        new RecordingPurchaseOrderEvents(),
        new PurchaseOrderComposer(_context, _suppliers, _members),
        _intake,
        new PurchaseOrderEmailDelivery(_email, _documents, _unitOfWork, _audit));

    private Task<PurchaseOrderDetailDto> Transition(PurchaseOrder order, PurchaseOrderAction action) =>
        Handler().HandleAsync(new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, action), Ct);

    [Fact]
    public async Task SendWithASupplierEmailSendsOneEmailWithThePdfAndStoresSent()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        var detail = await Transition(order, PurchaseOrderAction.Send);

        var request = Assert.Single(_email.Requests);
        Assert.Equal((SupplierEmail, "OC-0001.pdf"), (request.ToAddress, request.FileName));
        Assert.Equal(FakePurchaseOrderDocumentBuilder.Pdf, request.Pdf);
        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal((PurchaseOrderEmailStatus.Sent, SupplierEmail), (order.LastEmailStatus, order.LastEmailTo));
        Assert.Equal("Sent", detail.Email!.Status);
        Assert.Equal(SupplierEmail, detail.Email.To);
        Assert.Equal(
            [PurchasesAuditActions.Sent, PurchasesAuditActions.EmailSent],
            _audit.Entries.Select(entry => entry.Action));
    }

    [Fact]
    public async Task SendWithoutASupplierEmailStoresNoRecipientAndDoesNotCallTheChannel()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        var detail = await Transition(order, PurchaseOrderAction.Send);

        Assert.Empty(_email.Requests);
        Assert.Equal(0, _documents.Calls);
        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal(PurchaseOrderEmailStatus.NoRecipient, order.LastEmailStatus);
        Assert.Equal("NoRecipient", detail.Email!.Status);
        Assert.Equal([PurchasesAuditActions.Sent], _audit.Entries.Select(entry => entry.Action));
    }

    [Fact]
    public async Task SendWithABlankSupplierEmailIsNoRecipient()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: "   ");
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        await Transition(order, PurchaseOrderAction.Send);

        Assert.Empty(_email.Requests);
        Assert.Equal(PurchaseOrderEmailStatus.NoRecipient, order.LastEmailStatus);
    }

    [Fact]
    public async Task SendWhenTheChannelFailsKeepsTheOrderSentAndStoresFailed()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
        _email.Result = PurchaseOrderEmailResult.Failed;
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        var detail = await Transition(order, PurchaseOrderAction.Send);

        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal(PurchaseOrderEmailStatus.Failed, order.LastEmailStatus);
        Assert.Equal("Failed", detail.Email!.Status);
        Assert.Equal(PurchasesAuditActions.EmailFailed, _audit.Entries[^1].Action);
    }

    [Fact]
    public async Task SendWhenThePdfCannotBeRenderedStillSendsTheOrderAndStoresFailed()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
        _documents.Throw = true;
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        var detail = await Transition(order, PurchaseOrderAction.Send);

        Assert.Empty(_email.Requests);
        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal("Failed", detail.Email!.Status);
    }

    [Fact]
    public async Task SendWhenTheChannelThrowsStoresFailedAndDoesNotThrow()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
        _email.Throw = true;
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        var detail = await Transition(order, PurchaseOrderAction.Send);

        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal("Failed", detail.Email!.Status);
    }

    // Revisión final: el timeout de Infobip (30 s) es un TaskCanceledException, que es un OperationCanceledException; sin que el
    // cliente haya cancelado nada no puede tumbar un envío que ya se guardó.
    [Fact]
    public async Task SendWhenTheChannelTimesOutStoresFailedAndDoesNotThrow()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
        _email.TimeOut = true;
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        var detail = await Transition(order, PurchaseOrderAction.Send);

        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal("Failed", detail.Email!.Status);
    }

    [Fact]
    public async Task SendWhenTheClientCancelsTheRequestStillPropagatesTheCancellation()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
        _email.TimeOut = true;
        var order = _orders.Seed(PurchaseOrderTestData.Order());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, PurchaseOrderAction.Send), cancelled.Token));
    }

    [Fact]
    public async Task SendWhenSavingTheEmailResultFailsStillReturnsTheResult()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
        _unitOfWork = new FailingSecondSaveUnitOfWork();
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        var detail = await Transition(order, PurchaseOrderAction.Send);

        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal("Sent", detail.Email!.Status);
    }

    [Fact]
    public async Task SendOfALinkedOrderAlsoSendsTheEmail()
    {
        _suppliers.Add(
            PurchaseOrderTestData.SupplierId,
            "Mayorista Central",
            linkedTenantId: PurchaseOrderTestData.SupplierTenantId,
            email: SupplierEmail);
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder());

        await Transition(order, PurchaseOrderAction.Send);

        Assert.Single(_intake.Submitted);
        Assert.Equal(SupplierEmail, Assert.Single(_email.Requests).ToAddress);
        Assert.Equal(PurchaseOrderEmailStatus.Sent, order.LastEmailStatus);
    }

    [Fact]
    public async Task ReceiveAndCancelDoNotSendEmail()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
        var toReceive = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Confirmed));
        var toCancel = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent));

        await Transition(toReceive, PurchaseOrderAction.Receive);
        await Transition(toCancel, PurchaseOrderAction.Cancel);

        Assert.Empty(_email.Requests);
        Assert.Null(toReceive.LastEmailStatus);
        Assert.Null(toCancel.LastEmailStatus);
    }
}
