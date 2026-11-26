using BuildingBlocks.Application;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

/// <summary>
/// Reenviar el correo de una orden ya enviada: no cambia la orden (ni su versión), sólo vuelve a intentar y guarda el resultado. El
/// destino es siempre el correo que el proveedor tiene guardado.
/// </summary>
public sealed class ResendPurchaseOrderEmailTests
{
    private const string SupplierEmail = "ventas@textiles.co";

    private static readonly Guid TenantId = PurchaseOrderTestData.TenantId;

    private readonly FakeSupplierLookup _suppliers = new();
    private readonly FakePurchaseOrderRepository _orders = new();
    private readonly FakePurchasesAudit _audit = new();
    private readonly FakePurchasesUnitOfWork _unitOfWork = new();
    private readonly FakePurchaseOrderEmail _email = new();
    private readonly FakePurchaseOrderDocumentBuilder _documents = new();

    public ResendPurchaseOrderEmailTests()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: SupplierEmail);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ResendPurchaseOrderEmailHandler Handler(params string[] permissions)
    {
        var context = new FakeExecutionContext(TenantId, permissions.Length == 0 ? [PurchasesPermissions.OrderManage] : permissions);
        return new ResendPurchaseOrderEmailHandler(
            context,
            _orders,
            _suppliers,
            new FakePurchasesClock(PurchaseOrderTestData.Now.AddDays(1)),
            new PurchaseOrderComposer(context, _suppliers, new FakeMemberDirectory()),
            new PurchaseOrderEmailDelivery(_email, _documents, _unitOfWork, _audit));
    }

    private Task<PurchaseOrderDetailDto> Resend(PurchaseOrder order, ResendPurchaseOrderEmailHandler? handler = null) =>
        (handler ?? Handler()).HandleAsync(new ResendPurchaseOrderEmailCommand(TenantId, order.Id.Value), Ct);

    [Fact]
    public async Task ResendFromSentSendsTheEmailAndStoresTheResult()
    {
        var order = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent));

        var detail = await Resend(order);

        var request = Assert.Single(_email.Requests);
        Assert.Equal((SupplierEmail, "OC-0001.pdf"), (request.ToAddress, request.FileName));
        Assert.Equal(PurchaseOrderEmailStatus.Sent, order.LastEmailStatus);
        Assert.Equal(PurchaseOrderTestData.Now.AddDays(1), order.LastEmailAt);
        Assert.Equal("Sent", detail.Email!.Status);
        Assert.Equal(PurchasesAuditActions.EmailSent, Assert.Single(_audit.Entries).Action);
    }

    [Fact]
    public async Task ResendDoesNotBumpTheVersionOrTheUpdateTime()
    {
        var order = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Confirmed));
        var (version, updatedAt) = (order.Version, order.UpdatedAt);

        var detail = await Resend(order);

        Assert.Equal((version, updatedAt), (order.Version, order.UpdatedAt));
        Assert.Equal(version, detail.Version);
    }

    [Fact]
    public async Task ResendAfterAFailedSendReplacesTheStoredResult()
    {
        var order = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent));
        order.RecordEmail(PurchaseOrderEmailStatus.Failed, SupplierEmail, PurchaseOrderTestData.Now);

        await Resend(order);

        Assert.Equal(PurchaseOrderEmailStatus.Sent, order.LastEmailStatus);
    }

    [Fact]
    public async Task ResendWhenTheChannelFailsReturnsFailedInsteadOfThrowing()
    {
        _email.Result = PurchaseOrderEmailResult.Failed;
        var order = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent));

        var detail = await Resend(order);

        Assert.Equal("Failed", detail.Email!.Status);
        Assert.Equal(PurchaseOrderEmailStatus.Failed, order.LastEmailStatus);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    public async Task ResendFromDraftOrCancelledIs409EmailNotAllowed(PurchaseOrderStatus status)
    {
        var order = _orders.Seed(PurchaseOrderTestData.OrderIn(status));

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Resend(order));

        Assert.Equal(PurchasesErrorCodes.EmailNotAllowed, error.Code);
        Assert.Empty(_email.Requests);
    }

    [Fact]
    public async Task ResendWithoutASupplierEmailIs422AndSendsNothing()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");
        var order = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent));

        var error = await Assert.ThrowsAsync<PurchasesDomainException>(() => Resend(order));

        Assert.Equal(PurchasesErrorCodes.SupplierEmailMissing, error.Code);
        Assert.Empty(_email.Requests);
        Assert.Null(order.LastEmailStatus);
    }

    [Fact]
    public async Task ResendOfAnUnknownOrderIs404()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler().HandleAsync(new ResendPurchaseOrderEmailCommand(TenantId, Guid.CreateVersion7()), Ct));
    }

    [Fact]
    public async Task ResendWithoutTheManagePermissionIsForbidden()
    {
        var order = _orders.Seed(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Resend(order, Handler(PurchasesPermissions.OrderRead)));

        Assert.Empty(_email.Requests);
    }
}
