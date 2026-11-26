using Microsoft.Extensions.Logging.Abstractions;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

/// <summary>
/// Mayorista fase 3 (Task 6): lo que hace en el comprador el evento de aprobación o de anulación del mayorista. Un evento que no
/// calza se ignora (D-20), uno que llega antes de que la orden quede enviada se reintenta (X8b), y crear productos nunca frena la
/// confirmación (X2).
/// </summary>
public sealed class WholesaleOrderEventApplierTests
{
    private static readonly Guid TenantId = PurchaseOrderTestData.TenantId;
    private static readonly Guid Wholesaler = PurchaseOrderTestData.SupplierTenantId;
    private static readonly DateTimeOffset Later = PurchaseOrderTestData.Now.AddHours(3);

    private readonly FakePurchaseOrderRepository _orders = new();
    private readonly FakePurchasesCatalogWriter _writer = new();
    private readonly FakePurchasesAudit _audit = new();
    private readonly FakePurchasesUnitOfWork _unitOfWork = new();
    private readonly FakePurchasesProductImages _images = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleOrderEventApplier Applier() =>
        new(_orders, _writer, _images, _audit, _unitOfWork, NullLogger<WholesaleOrderEventApplier>.Instance);

    // Mayorista imagen del producto (spec 2026-11-21): la foto se copia después del commit, sólo a lo que Compras creó.
    [Fact]
    public async Task TheImageOfACreatedProductIsCopiedAfterTheApprovalIsApplied()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var line = order.Lines[0];
        var applier = Applier();

        await applier.ApplyApprovedAsync(Approval(order, Final(line, 2m)), Later, Ct);
        Assert.Empty(_images.Copies);

        await applier.CopyPendingImagesAsync(Later, Ct);

        var copy = Assert.Single(_images.Copies);
        Assert.Equal(
            (TenantId, Wholesaler, line.SupplierVariantId!.Value, _writer.Existing["SKU-1"]),
            (copy.BuyerTenantId, copy.SupplierTenantId, copy.SupplierVariantId, copy.ProductId));
    }

    [Fact]
    public async Task AProductThatAlreadyExistedGetsNoImage()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        _writer.Existing["SKU-1"] = Guid.CreateVersion7();
        var applier = Applier();

        await applier.ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m)), Later, Ct);
        await applier.CopyPendingImagesAsync(Later, Ct);

        Assert.Empty(_images.Copies);
    }

    [Fact]
    public async Task AFailingImageCopyNeverThrowsAndTheOtherCopiesStillRun()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder(
            [PurchaseOrderTestData.LinkedLine(code: "SKU-1"), PurchaseOrderTestData.LinkedLine(code: "SKU-2")]));
        _images.Fails = true;
        var applier = Applier();

        await applier.ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m), Final(order.Lines[1], 1m)), Later, Ct);
        await applier.CopyPendingImagesAsync(Later, Ct);

        Assert.Equal(2, _images.Copies.Count);
    }

    [Fact]
    public async Task ARepeatedApprovalLeavesNoImageToCopy()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        await Applier().ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m)), Later, Ct);

        var repeated = Applier();
        await repeated.ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m)), Later, Ct);
        await repeated.CopyPendingImagesAsync(Later, Ct);

        Assert.Empty(_images.Copies);
    }

    private static WholesaleApprovedLine Final(PurchaseOrderLine line, decimal quantity, decimal unitPrice = 11900m) =>
        new(line.Id, line.SupplierVariantId!.Value, line.Code, line.Name, quantity, unitPrice, line.TaxRatePercent);

    private static WholesaleApproval Approval(PurchaseOrder order, params WholesaleApprovedLine[] lines) =>
        new(Wholesaler, PurchaseOrderTestData.LinkedOrderId, TenantId, order.Id.Value, lines);

    private static WholesaleCancellation Cancellation(PurchaseOrder order) =>
        new(Wholesaler, PurchaseOrderTestData.LinkedOrderId, TenantId, order.Id.Value);

    [Fact]
    public async Task ApprovalConfirmsAndCreatesMissingProducts()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var line = order.Lines[0];

        var outcome = await Applier().ApplyApprovedAsync(Approval(order, Final(line, 2m)), Later, Ct);

        Assert.Equal(WholesaleEventOutcome.Applied, outcome);
        Assert.Equal(PurchaseOrderStatus.Confirmed, order.Status);
        Assert.Equal((2m, 10000m), (line.ConfirmedQuantity!.Value, line.ConfirmedUnitCost!.Value));
        Assert.Equal([("SKU-1", line.Name, "COP", (decimal?)10000m)], _writer.Calls);
        Assert.Equal((_writer.Existing["SKU-1"], true), (line.VariantId!.Value, line.CreatedInCatalog));
        var audit = Assert.Single(_audit.Entries);
        Assert.Equal((PurchasesAuditActions.Confirmed, Guid.Empty), (audit.Action, audit.ActorId));
        Assert.Equal(["wholesaler"], audit.ChangedFields);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    // Spec 2026-11-19 (D-10, D-18): una orden sin precio recibe los costos con la aprobación y crea el producto con ese costo.
    [Fact]
    public async Task ApprovingAPricelessOrderGivesItPricesAndCreatesTheProductWithTheConfirmedCost()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentPricelessOrder());
        var line = order.Lines[0];
        Assert.False(order.HasPrices);

        var outcome = await Applier().ApplyApprovedAsync(Approval(order, Final(line, 2m)), Later, Ct);

        Assert.Equal(WholesaleEventOutcome.Applied, outcome);
        Assert.True(order.HasPrices);
        Assert.Null(line.UnitCost);
        Assert.Equal((2m, 10000m), (line.ConfirmedQuantity!.Value, line.ConfirmedUnitCost!.Value));
        Assert.Equal([("SKU-1", line.Name, "COP", (decimal?)10000m)], _writer.Calls);
        Assert.Equal(PurchaseOrderLineStatus.Unchanged, line.Status);
        Assert.False(order.ConfirmedWithChanges);
        Assert.Null(order.RequestedTotals());
        Assert.Equal(new PurchaseOrderTotals(20000m, 3800m, 23800m), order.Totals());
    }

    // Review Focus 4: " ABC-1 " encuentra ABC-1 (inactivo o no) y no lo toca: la línea lo usa y no cuenta como creado.
    [Fact]
    public async Task AnInactiveVariantWithTheCodeIsReusedUntouched()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var existing = Guid.CreateVersion7();
        _writer.Existing["ABC-1"] = existing;
        var added = new WholesaleApprovedLine(null, Guid.CreateVersion7(), " ABC-1 ", "Agregado", 1m, 5950m, 19m);

        await Applier().ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m), added), Later, Ct);

        var addedLine = order.Lines.Single(line => line.Status == PurchaseOrderLineStatus.Added);
        Assert.Equal((existing, false), (addedLine.VariantId!.Value, addedLine.CreatedInCatalog));
        Assert.Equal("ABC-1", _writer.Calls.Single(call => call.Name == "Agregado").Code);
        Assert.Equal(5000m, addedLine.ConfirmedUnitCost);
    }

    [Fact]
    public async Task RejectedLinesDoNotTouchTheCatalog()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder(
            [PurchaseOrderTestData.LinkedLine(code: "SKU-1"), PurchaseOrderTestData.LinkedLine(code: "SKU-2")]));

        await Applier().ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m)), Later, Ct);

        var rejected = order.Lines.Single(line => line.Code == "SKU-2");
        Assert.Equal(PurchaseOrderLineStatus.Rejected, rejected.Status);
        Assert.Null(rejected.VariantId);
        Assert.Equal(["SKU-1"], _writer.Calls.Select(call => call.Code));
    }

    // Review Focus 3: el evento repetido no crea otro producto ni cambia nada.
    [Fact]
    public async Task RedeliveredApprovalCreatesProductsOnce()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var approval = Approval(order, Final(order.Lines[0], 2m));
        await Applier().ApplyApprovedAsync(approval, Later, Ct);
        var version = order.Version;

        var outcome = await Applier().ApplyApprovedAsync(approval, Later.AddMinutes(1), Ct);

        Assert.Equal(WholesaleEventOutcome.Ignored, outcome);
        Assert.Single(_writer.Calls);
        Assert.Equal(version, order.Version);
        Assert.Single(_audit.Entries);
    }

    // D-20: otro pedido, otro mayorista u otra orden: nada cambia y el evento se da por atendido.
    [Fact]
    public async Task AnEventForAnotherOrderIsIgnored()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var line = order.Lines[0];

        var otherOrder = await Applier().ApplyApprovedAsync(
            Approval(order, Final(line, 2m)) with { OrderId = Guid.CreateVersion7() }, Later, Ct);
        var otherWholesaler = await Applier().ApplyApprovedAsync(
            Approval(order, Final(line, 2m)) with { WholesalerTenantId = Guid.CreateVersion7() }, Later, Ct);
        var otherBuyer = await Applier().ApplyApprovedAsync(
            Approval(order, Final(line, 2m)) with { BuyerTenantId = Guid.CreateVersion7() }, Later, Ct);
        var otherCancellation = await Applier().ApplyCancelledAsync(
            Cancellation(order) with { OrderId = Guid.CreateVersion7() }, Later, Ct);

        Assert.All([otherOrder, otherWholesaler, otherBuyer, otherCancellation], outcome => Assert.Equal(WholesaleEventOutcome.Ignored, outcome));
        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Empty(_writer.Calls);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    // Ruling X8b: la aprobación que encuentra la orden todavía en borrador (la ventana entre los dos commits del envío, D-8)
    // no se descarta: se reintenta hasta que la orden quede enviada.
    [Fact]
    public async Task AnEventOnADraftOrderIsRetriedLater()
    {
        var order = _orders.Seed(PurchaseOrderTestData.LinkedOrder());

        var approved = await Applier().ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m)), Later, Ct);
        var cancelled = await Applier().ApplyCancelledAsync(Cancellation(order), Later, Ct);

        Assert.Equal((WholesaleEventOutcome.RetryLater, WholesaleEventOutcome.RetryLater), (approved, cancelled));
        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task CancellationOfAConfirmedOrderCancelsIt()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        await Applier().ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m)), Later, Ct);

        var outcome = await Applier().ApplyCancelledAsync(Cancellation(order), Later.AddHours(1), Ct);

        Assert.Equal(WholesaleEventOutcome.Applied, outcome);
        Assert.Equal(PurchaseOrderStatus.Cancelled, order.Status);
        var audit = _audit.Entries[^1];
        Assert.Equal((PurchasesAuditActions.Cancelled, Guid.Empty), (audit.Action, audit.ActorId));
        Assert.Equal(["wholesaler"], audit.ChangedFields);
    }

    // El distribuidor ya canceló su orden y el evento de vuelta (byBuyer) la encuentra cancelada: no hay nada que hacer.
    [Fact]
    public async Task CancellationOfAnAlreadyCancelledOrderIsIgnored()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        order.Cancel(PurchaseOrderTestData.MemberId, Later);

        var outcome = await Applier().ApplyCancelledAsync(Cancellation(order), Later, Ct);

        Assert.Equal(WholesaleEventOutcome.Ignored, outcome);
        Assert.Empty(_audit.Entries);
    }

    // Ruling X2: dos líneas con el mismo código recortado crean un solo producto y las dos lo usan.
    [Fact]
    public async Task RepeatedCodesInOneApprovalCreateOneProduct()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var first = new WholesaleApprovedLine(null, Guid.CreateVersion7(), "NEW-1", "Nuevo", 1m, 1190m, 19m);
        var second = new WholesaleApprovedLine(null, Guid.CreateVersion7(), " NEW-1", "Nuevo talla L", 2m, 1190m, 19m);

        await Applier().ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m), first, second), Later, Ct);

        Assert.Single(_writer.Calls, call => call.Code == "NEW-1");
        var added = order.Lines.Where(line => line.Status == PurchaseOrderLineStatus.Added).ToList();
        Assert.Equal(2, added.Count);
        Assert.All(added, line => Assert.Equal(_writer.Existing["NEW-1"], line.VariantId));
    }

    // Ruling X2: un alta que falla (índice único en carrera, nombre o código largo) deja esa línea sin variante, y la orden
    // igual queda confirmada: el evento nunca bloquea el inbox.
    [Fact]
    public async Task AFailedProductCreationLeavesTheLineUnlinkedAndStillConfirms()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder(
            [PurchaseOrderTestData.LinkedLine(code: "SKU-1"), PurchaseOrderTestData.LinkedLine(code: "SKU-2")]));
        _writer.Failing.Add("SKU-1");

        var outcome = await Applier().ApplyApprovedAsync(
            Approval(order, Final(order.Lines[0], 2m), Final(order.Lines[1], 2m)), Later, Ct);

        Assert.Equal(WholesaleEventOutcome.Applied, outcome);
        Assert.Equal(PurchaseOrderStatus.Confirmed, order.Status);
        Assert.Null(order.Lines.Single(line => line.Code == "SKU-1").VariantId);
        Assert.NotNull(order.Lines.Single(line => line.Code == "SKU-2").VariantId);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ABlankCodeIsNotCreated()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var blank = new WholesaleApprovedLine(null, Guid.CreateVersion7(), "   ", "Sin código", 1m, 1190m, 19m);

        await Applier().ApplyApprovedAsync(Approval(order, Final(order.Lines[0], 2m), blank), Later, Ct);

        Assert.DoesNotContain(_writer.Calls, call => call.Name == "Sin código");
        Assert.Null(order.Lines.Single(line => line.Status == PurchaseOrderLineStatus.Added).VariantId);
        Assert.Equal(PurchaseOrderStatus.Confirmed, order.Status);
    }

    // Revisión del bloque B, M2: un pedido en otra moneda que la orden no se aplica nunca en silencio; reintentar no lo arregla.
    [Fact]
    public async Task AnApprovalInAnotherCurrencyIsIgnoredAndChangesNothing()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());
        var version = order.Version;

        var outcome = await Applier().ApplyApprovedAsync(
            Approval(order, Final(order.Lines[0], 2m)) with { Currency = "USD" }, Later, Ct);

        Assert.Equal(WholesaleEventOutcome.Ignored, outcome);
        Assert.Equal((PurchaseOrderStatus.Sent, version), (order.Status, order.Version));
        Assert.Empty(_writer.Calls);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnApprovalInTheSameCurrencyIgnoringCaseApplies()
    {
        var order = _orders.Seed(PurchaseOrderTestData.SentLinkedOrder());

        var outcome = await Applier().ApplyApprovedAsync(
            Approval(order, Final(order.Lines[0], 2m)) with { Currency = "cop" }, Later, Ct);

        Assert.Equal(WholesaleEventOutcome.Applied, outcome);
    }
}
