using BuildingBlocks.Application;
using FluentValidation;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

/// <summary>
/// Mayorista fase 3 (Task 6): los casos de uso de una orden a un proveedor vinculado. El precio sale de la lista del mayorista
/// (D-9, D-11), enviar crea el pedido una vez y congela sus líneas (D-8), cancelar anula primero el pedido (D-12) y confirmar a
/// mano no existe (D-13).
/// </summary>
public sealed class LinkedPurchaseOrderHandlersTests
{
    private static readonly Guid TenantId = PurchaseOrderTestData.TenantId;
    private static readonly Guid Wholesaler = PurchaseOrderTestData.SupplierTenantId;

    private readonly FakeExecutionContext _context = new(TenantId, PurchasesPermissions.OrderManage, PurchasesPermissions.OrderRead);
    private readonly FakeSupplierLookup _suppliers = new();
    private readonly FakeCatalogLookup _catalog = new();
    private readonly FakeWholesaleCatalog _wholesale = new();
    private readonly FakeWholesaleOrderIntake _intake = new();
    private readonly FakePurchaseOrderRepository _orders = new();
    private readonly FakePurchasesUnitOfWork _unitOfWork = new();
    private readonly FakePurchasesAudit _audit = new();
    private readonly FakeMemberDirectory _members = new();

    public LinkedPurchaseOrderHandlersTests()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: Wholesaler);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PurchaseOrderLineBuilder Builder() => new(_suppliers, _catalog, _wholesale);

    private PurchaseOrderComposer Composer() => new(_context, _suppliers, _members);

    private CreatePurchaseOrderHandler CreateHandler() => new(
        _context, new FakeMembership(), new FakePurchasesClock(PurchaseOrderTestData.Now), Builder(), _orders,
        new FakePurchaseOrderNumbers(), _unitOfWork, _audit, Composer(), new CreatePurchaseOrderValidator());

    private UpdatePurchaseOrderHandler UpdateHandler() => new(
        _context, new FakeMembership(), new FakePurchasesClock(PurchaseOrderTestData.Now), Builder(), _orders, _unitOfWork,
        _audit, Composer(), new UpdatePurchaseOrderValidator());

    private PreviewPurchaseOrderHandler PreviewHandler() => new(_context, Builder(), new PreviewPurchaseOrderValidator());

    private TransitionPurchaseOrderHandler TransitionHandler() => new(
        _context, new FakeMembership(), new FakePurchasesClock(PurchaseOrderTestData.Now), _suppliers, _orders, _unitOfWork,
        _audit, new RecordingPurchaseOrderEvents(), Composer(), _intake,
        new PurchaseOrderEmailDelivery(new FakePurchaseOrderEmail(), new FakePurchaseOrderDocumentBuilder(), _unitOfWork, _audit));

    private DeletePurchaseOrderHandler DeleteHandler() => new(
        _context, new FakeMembership(), new FakePurchasesClock(PurchaseOrderTestData.Now), _orders, _unitOfWork, _audit, _intake);

    private ConfirmPurchaseOrderHandler ConfirmHandler() => new(
        _context, new FakeMembership(), new FakePurchasesClock(PurchaseOrderTestData.Now), _orders, _unitOfWork, _audit,
        Composer(), new ConfirmPurchaseOrderValidator());

    private static CreatePurchaseOrderCommand Create(params PurchaseOrderLineInput[] lines) =>
        new(TenantId, PurchaseOrderTestData.SupplierId, "COP", "Entregar en bodega", lines);

    [Fact]
    public async Task CreateWithALinkedSupplierPricesFromTheWholesaleList()
    {
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);

        var detail = await CreateHandler().HandleAsync(Create(new PurchaseOrderLineInput(item.VariantId, 3m, 1m)), Ct);

        var order = Assert.Single(_orders.Orders);
        Assert.Equal((Wholesaler, "USD"), (order.SupplierTenantId!.Value, order.Currency));
        var line = Assert.Single(order.Lines);
        // El costo del cuerpo (1) se ignora: es la lista sin IVA (11900 @19 → 10000).
        Assert.Equal((10000m, 19m, (Guid?)null, item.VariantId), (line.UnitCost, line.TaxRatePercent, line.VariantId, line.SupplierVariantId!.Value));
        Assert.Equal((12500m, 4.8m, 11900m), (line.ListBasePrice!.Value, line.ListDiscountPercent!.Value, line.ListUnitPrice!.Value));
        Assert.Equal(("Linked", "USD"), (detail.Mode, detail.Currency));
        Assert.Equal(new PurchaseOrderListPriceDto(12500m, 4.8m, 11900m), Assert.Single(detail.Lines).ListPrice);
    }

    [Fact]
    public async Task CreateWithAnInvisibleVariantIs422NotVisible()
    {
        var error = await Assert.ThrowsAsync<PurchasesDomainException>(() =>
            CreateHandler().HandleAsync(Create(new PurchaseOrderLineInput(Guid.CreateVersion7(), 1m, 1m)), Ct));

        Assert.Equal(PurchasesErrorCodes.ProductNotVisible, error.Code);
        Assert.Empty(_orders.Orders);
    }

    [Fact]
    public async Task CreateWithABlockedRelationshipIs403()
    {
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);
        _wholesale.Forbidden = true;

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            CreateHandler().HandleAsync(Create(new PurchaseOrderLineInput(item.VariantId, 1m, null)), Ct));

        Assert.Equal(PurchasesErrorCodes.RelationshipInactive, error.Code);
        Assert.Equal(0, _unitOfWork.Transactions);
    }

    [Fact]
    public async Task PreviewOfALinkedOrderCarriesTheListPrice()
    {
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);

        var preview = await PreviewHandler().HandleAsync(
            new PreviewPurchaseOrderCommand(TenantId, PurchaseOrderTestData.SupplierId, "COP", null,
                [new PurchaseOrderLineInput(item.VariantId, 2m, null)]),
            Ct);

        Assert.Equal("USD", preview.Currency);
        var line = Assert.Single(preview.Lines);
        Assert.Equal((item.VariantId, 20000m), (line.VariantId, line.Subtotal));
        Assert.Equal(new PurchaseOrderListPriceDto(12500m, 4.8m, 11900m), line.ListPrice);
        Assert.Equal(new PurchaseOrderTotalsDto(20000m, 3800m, 23800m), preview.Totals);
    }

    [Fact]
    public async Task AnExternalLineWithoutUnitCostFailsOnTheField()
    {
        var external = Guid.CreateVersion7();
        _suppliers.Add(external, "Textiles del Valle");
        var variant = _catalog.Add("SKU-1", "Camiseta", 19m);

        var error = await Assert.ThrowsAsync<ValidationException>(() => CreateHandler().HandleAsync(
            new CreatePurchaseOrderCommand(TenantId, external, "COP", null, [new PurchaseOrderLineInput(variant.VariantId, 1m, null)]),
            Ct));

        Assert.Equal("Lines[0].UnitCost", Assert.Single(error.Errors).PropertyName);
    }

    [Fact]
    public async Task SendCallsTheIntakeOnceAndFreezesItsLines()
    {
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder());
        var sourceLine = order.Lines[0];

        var detail = await TransitionHandler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, PurchaseOrderAction.Send), Ct);

        var request = Assert.Single(_intake.Submitted);
        Assert.Equal((TenantId, Wholesaler, order.Id.Value, "OC-0007"), (request.BuyerTenantId, request.WholesalerTenantId, request.PurchaseOrderId, request.PurchaseOrderNumber));
        // M2: el adaptador compara la moneda del pedido con la de la orden; nunca la cambia en silencio.
        Assert.Equal("COP", request.Currency);
        Assert.Equal([(sourceLine.Id, sourceLine.SupplierVariantId!.Value, 2m)], request.Lines.Select(line => (line.PurchaseOrderLineId, line.VariantId, line.Quantity)));
        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal((_intake.OrderId, "PED-0042"), (order.LinkedOrderId!.Value, order.LinkedOrderNumber));
        Assert.Equal("PED-0042", detail.Linked!.OrderNumber);
        Assert.Equal(PurchasesAuditActions.Sent, Assert.Single(_audit.Entries).Action);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    // Review Focus 1 (D-8): el pedido ya existía (falla entre los dos commits). La orden adopta las líneas guardadas.
    [Fact]
    public async Task SendRetryAfterTheOrderExistsAdoptsTheOrderLines()
    {
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder());
        var adopted = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        var existingOrderId = Guid.CreateVersion7();
        _intake.Existing = new WholesaleIntakeResult(
            existingOrderId,
            "PED-0099",
            "USD",
            [new LinkedLineSnapshot(adopted, variant, "OTRO-1", "Otro", 5m, 8000m, 19m, 10000m, 5m, 9520m)],
            AlreadyExisted: true);

        await TransitionHandler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, PurchaseOrderAction.Send), Ct);

        Assert.Single(_intake.Submitted);
        Assert.Equal((existingOrderId, "PED-0099"), (order.LinkedOrderId!.Value, order.LinkedOrderNumber));
        var line = Assert.Single(order.Lines);
        Assert.Equal((adopted, variant, 5m, 8000m), (line.Id, line.SupplierVariantId!.Value, line.Quantity!.Value, line.UnitCost));
    }

    [Fact]
    public async Task CancelOfASentLinkedOrderCancelsTheWholesaleOrderFirst()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());

        await TransitionHandler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, PurchaseOrderAction.Cancel), Ct);

        Assert.Equal(
            [(TenantId, Wholesaler, order.Id.Value, "OC-0007", (Guid?)PurchaseOrderTestData.LinkedOrderId)],
            _intake.Cancelled);
        Assert.Equal(PurchaseOrderStatus.Cancelled, order.Status);
    }

    // Revisión del bloque B, M1: si el envío creó el pedido y Compras no alcanzó a guardar, el borrador no sabe el id del
    // pedido. Cancelarlo le pide al mayorista anular el que haya salido de esta orden (por su clave), sin id.
    [Fact]
    public async Task CancelOfALinkedDraftAlsoCancelsAnOrderLeftByAFailedSend()
    {
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder());

        await TransitionHandler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, PurchaseOrderAction.Cancel), Ct);

        Assert.Equal([(TenantId, Wholesaler, order.Id.Value, "OC-0007", (Guid?)null)], _intake.Cancelled);
        Assert.Equal(PurchaseOrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public async Task DeleteOfALinkedDraftAlsoCancelsAnOrderLeftByAFailedSend()
    {
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder());

        await DeleteHandler().HandleAsync(new DeletePurchaseOrderCommand(TenantId, order.Id.Value, order.Version), Ct);

        Assert.Equal([(TenantId, Wholesaler, order.Id.Value, "OC-0007", (Guid?)null)], _intake.Cancelled);
        Assert.Empty(_orders.Orders);
    }

    // El pedido huérfano ya lo aprobó el mayorista: el borrador no se borra (409) y se puede volver a enviar (D-8 lo adopta).
    [Fact]
    public async Task DeleteOfALinkedDraftWhoseOrderWasApprovedIs409AndKeepsTheDraft()
    {
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder());
        _intake.CancelFailure = new ResourceConflictException(PurchasesErrorCodes.NotCancellable, "Approved.");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            DeleteHandler().HandleAsync(new DeletePurchaseOrderCommand(TenantId, order.Id.Value, order.Version), Ct));

        Assert.Equal(PurchasesErrorCodes.NotCancellable, error.Code);
        Assert.Single(_orders.Orders);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task DeleteOfAnExternalDraftDoesNotTouchTheWholesaler()
    {
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        await DeleteHandler().HandleAsync(new DeletePurchaseOrderCommand(TenantId, order.Id.Value, order.Version), Ct);

        Assert.Empty(_intake.Cancelled);
        Assert.Empty(_orders.Orders);
    }

    // Review Focus 2: el mayorista aprobó primero. El 409 deja la orden Sent con su versión hasta que llegue el evento.
    [Fact]
    public async Task CancelAfterApprovalIs409NotCancellable()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var version = order.Version;
        _intake.CancelFailure = new ResourceConflictException(PurchasesErrorCodes.NotCancellable, "Approved.");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => TransitionHandler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, version, PurchaseOrderAction.Cancel), Ct));

        Assert.Equal(PurchasesErrorCodes.NotCancellable, error.Code);
        Assert.Equal((PurchaseOrderStatus.Sent, version), (order.Status, order.Version));
        Assert.Equal(0, _unitOfWork.Saves);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task ConfirmOfALinkedOrderIs409()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => ConfirmHandler().HandleAsync(
            new ConfirmPurchaseOrderCommand(
                TenantId, order.Id.Value, order.Version,
                order.Lines.Select(line => new PurchaseOrderLineConfirmationInput(line.Id, 1m, 1m)).ToList()),
            Ct));

        Assert.Equal(PurchasesErrorCodes.InvalidTransition, error.Code);
    }

    // D-10: el modo se fijó al guardar. Un borrador de fase 2 a un proveedor que hoy está vinculado sigue siendo externo.
    [Fact]
    public async Task APhaseTwoDraftToALinkedSupplierStaysExternal()
    {
        var order = _orders.Seed(PurchaseOrderTestData.Order());

        var detail = await TransitionHandler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, PurchaseOrderAction.Send), Ct);

        Assert.Empty(_intake.Submitted);
        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal("External", detail.Mode);
        Assert.Null(detail.Linked);
    }

    // Spec 2026-11-19: con la lista oculta la vista previa no manda montos.
    private Task<PurchaseOrderPreviewDto> PreviewAsync(Guid supplierId, Guid variantId, decimal quantity) =>
        PreviewHandler().HandleAsync(
            new PreviewPurchaseOrderCommand(TenantId, supplierId, "COP", null, [new PurchaseOrderLineInput(variantId, quantity, null)]),
            Ct);

    [Fact]
    public async Task PreviewOfAHiddenPriceListHasNoAmounts()
    {
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);
        _wholesale.ShowPrices = false;

        var preview = await PreviewAsync(PurchaseOrderTestData.SupplierId, item.VariantId, 2m);

        Assert.False(preview.ShowPrices);
        Assert.Null(preview.Totals);
        var line = Assert.Single(preview.Lines);
        Assert.Equal((item.VariantId, "ACE-1", 19m), (line.VariantId, line.Code, line.TaxRatePercent));
        Assert.Null(line.Subtotal);
        Assert.Null(line.ListPrice);
        Assert.Equal("USD", preview.Currency);
    }

    [Fact]
    public async Task PreviewOfAVisiblePriceListKeepsItsAmounts()
    {
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);
        _wholesale.ShowPrices = true;

        var preview = await PreviewAsync(PurchaseOrderTestData.SupplierId, item.VariantId, 2m);

        Assert.True(preview.ShowPrices);
        Assert.Equal(new PurchaseOrderTotalsDto(20000m, 3800m, 23800m), preview.Totals);
        var line = Assert.Single(preview.Lines);
        Assert.Equal(20000m, line.Subtotal);
        Assert.Equal(new PurchaseOrderListPriceDto(12500m, 4.8m, 11900m), line.ListPrice);
    }

    [Fact]
    public async Task PreviewOfAnExternalSupplierIsNotMasked()
    {
        var external = Guid.CreateVersion7();
        _suppliers.Add(external, "Textiles del Valle");
        var variant = _catalog.Add("SKU-1", "Camiseta", 19m);
        _wholesale.ShowPrices = false; // el flag es del mayorista: no aplica a una orden externa.

        var preview = await PreviewHandler().HandleAsync(
            new PreviewPurchaseOrderCommand(TenantId, external, "COP", null, [new PurchaseOrderLineInput(variant.VariantId, 2m, 1000m)]),
            Ct);

        Assert.True(preview.ShowPrices);
        Assert.Equal(2000m, Assert.Single(preview.Lines).Subtotal);
        Assert.Equal(new PurchaseOrderTotalsDto(2000m, 380m, 2380m), preview.Totals);
    }

    // Spec 2026-11-19 (revisada): con la lista oculta la orden nace sin montos, y el dominio es el único que los descarta.
    [Fact]
    public async Task CreateWithAHiddenPriceListSavesLinesWithoutAmounts()
    {
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);
        _wholesale.ShowPrices = false;

        var detail = await CreateHandler().HandleAsync(Create(new PurchaseOrderLineInput(item.VariantId, 3m, null)), Ct);

        var line = Assert.Single(Assert.Single(_orders.Orders).Lines);
        Assert.Equal(3m, line.Quantity);
        Assert.Equal((null, null, null, null), (line.UnitCost, line.ListBasePrice, line.ListDiscountPercent, line.ListUnitPrice));
        Assert.False(detail.ShowPrices);
        Assert.Null(detail.Totals);
        Assert.Null(detail.RequestedTotal);
        var dto = Assert.Single(detail.Lines);
        Assert.Equal((null, null, null), (dto.UnitCost, dto.Subtotal, dto.ListPrice));
        Assert.Equal(("ACE-1", 3m, 19m), (dto.Code, dto.Quantity, dto.TaxRatePercent));
    }

    [Fact]
    public async Task CreateWithAVisiblePriceListKeepsAmountsAndSaysSo()
    {
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);

        var detail = await CreateHandler().HandleAsync(Create(new PurchaseOrderLineInput(item.VariantId, 3m, null)), Ct);

        Assert.True(detail.ShowPrices);
        Assert.Equal(new PurchaseOrderTotalsDto(30000m, 5700m, 35700m), detail.Totals);
        Assert.Equal(35700m, detail.RequestedTotal);
    }

    [Fact]
    public async Task UpdateRebuildsTheLinesWithTheLiveFlag()
    {
        var item = _wholesale.Add("ACE-1", 12500m, 4.8m, 11900m, 19m);
        var created = await CreateHandler().HandleAsync(Create(new PurchaseOrderLineInput(item.VariantId, 3m, null)), Ct);
        var order = Assert.Single(_orders.Orders);
        Assert.NotNull(order.Lines[0].UnitCost);
        _wholesale.ShowPrices = false;

        var hidden = await UpdateHandler().HandleAsync(
            new UpdatePurchaseOrderCommand(
                TenantId, order.Id.Value, created.Version, PurchaseOrderTestData.SupplierId, "COP", null,
                [new PurchaseOrderLineInput(item.VariantId, 4m, null)]),
            Ct);

        Assert.False(hidden.ShowPrices);
        Assert.Null(Assert.Single(order.Lines).UnitCost);
        Assert.Null(hidden.Totals);

        _wholesale.ShowPrices = true;
        var visible = await UpdateHandler().HandleAsync(
            new UpdatePurchaseOrderCommand(
                TenantId, order.Id.Value, hidden.Version, PurchaseOrderTestData.SupplierId, "COP", null,
                [new PurchaseOrderLineInput(item.VariantId, 4m, null)]),
            Ct);

        Assert.True(visible.ShowPrices);
        Assert.Equal(10000m, Assert.Single(order.Lines).UnitCost);
        Assert.Equal(new PurchaseOrderTotalsDto(40000m, 7600m, 47600m), visible.Totals);
    }

    [Fact]
    public async Task SendWithAHiddenListAdoptsTheOrderLinesWithoutAmountsEvenIfTheDraftHadThem()
    {
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder());
        _intake.ShowPrices = false;

        var detail = await TransitionHandler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, PurchaseOrderAction.Send), Ct);

        var line = Assert.Single(order.Lines);
        Assert.Equal((null, null, null, null), (line.UnitCost, line.ListBasePrice, line.ListDiscountPercent, line.ListUnitPrice));
        Assert.Equal(2m, line.Quantity);
        Assert.False(detail.ShowPrices);
        Assert.Null(detail.Totals);
        Assert.Null(Assert.Single(detail.Lines).UnitCost);
    }

    [Fact]
    public async Task SendWithAVisibleListGivesAPricelessDraftItsAmounts()
    {
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder(withPrices: false));
        _intake.ShowPrices = true;

        var detail = await TransitionHandler().HandleAsync(
            new TransitionPurchaseOrderCommand(TenantId, order.Id.Value, order.Version, PurchaseOrderAction.Send), Ct);

        Assert.Equal(10000m, Assert.Single(order.Lines).UnitCost);
        Assert.True(detail.ShowPrices);
        Assert.NotNull(detail.Totals);
    }
}
