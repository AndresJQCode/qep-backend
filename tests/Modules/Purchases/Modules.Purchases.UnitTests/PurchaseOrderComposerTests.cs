using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

public sealed class PurchaseOrderComposerTests
{
    private readonly FakeSupplierLookup _suppliers = new();
    private readonly FakeMemberDirectory _members = new();

    [Fact]
    public async Task DetailCarriesSupplierCreatorHistoryNamesAndTotals()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", isActive: false);
        _members.Names[PurchaseOrderTestData.MemberId] = "Ana Compras";
        var order = PurchaseOrderTestData.Order(
            [PurchaseOrderTestData.Line(quantity: 3m, unitCost: 10.5m, taxRatePercent: 19m)], notes: "Urgente");

        var detail = await Composer(PurchasesPermissions.OrderRead).DetailAsync(order, CancellationToken.None);

        Assert.Equal(order.Id.Value, detail.Id);
        Assert.Equal("OC-0001", detail.Number);
        Assert.Equal("Draft", detail.Status);
        Assert.Equal(1, detail.Version);
        Assert.Equal(new PurchaseOrderSupplierDto(PurchaseOrderTestData.SupplierId, "Textiles del Valle", false), detail.Supplier);
        Assert.Equal("COP", detail.Currency);
        Assert.Equal("Urgente", detail.Notes);
        Assert.Equal(new PurchaseOrderMemberDto(PurchaseOrderTestData.MemberId, "Ana Compras"), detail.CreatedBy);
        var history = Assert.Single(detail.History);
        Assert.Equal(new PurchaseOrderHistoryDto("Draft", PurchaseOrderTestData.Now, "Ana Compras"), history);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("Pending", line.LineStatus);
        Assert.Equal(31.5m, line.Subtotal);
        Assert.Null(line.ConfirmedQuantity);
        Assert.Equal(new PurchaseOrderTotalsDto(31.5m, 5.99m, 37.49m), detail.Totals);
        Assert.Equal(37.49m, detail.RequestedTotal);
    }

    [Fact]
    public async Task ConfirmedTotalsDifferFromTheRequestedTotal()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");
        var order = PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent);
        var line = order.Lines[0];
        order.Confirm([new PurchaseOrderLineConfirmation(line.Id, 1m, 10m)], PurchaseOrderTestData.MemberId, PurchaseOrderTestData.Now);

        var detail = await Composer(PurchasesPermissions.OrderRead).DetailAsync(order, CancellationToken.None);

        Assert.True(detail.ConfirmedWithChanges);
        Assert.Equal("Changed", detail.Lines[0].LineStatus);
        Assert.Equal(new PurchaseOrderTotalsDto(10m, 1.9m, 11.9m), detail.Totals);
        Assert.Equal(23.8m, detail.RequestedTotal);
    }

    [Fact]
    public async Task APricelessLinkedOrderHasNoAmountsAndSaysShowPricesFalse()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central");
        var order = PurchaseOrderTestData.SentPricelessOrder([PurchaseOrderTestData.LinkedLine(quantity: 120m)]);

        var detail = await Composer(PurchasesPermissions.OrderRead).DetailAsync(order, CancellationToken.None);

        Assert.False(detail.ShowPrices);
        Assert.Null(detail.Totals);
        Assert.Null(detail.RequestedTotal);
        var line = Assert.Single(detail.Lines);
        Assert.Equal((120m, null, null, null, null, "Pending"), (line.Quantity, line.UnitCost, line.ConfirmedUnitCost, line.Subtotal, line.ListPrice, line.LineStatus));

        var summary = Assert.Single(await Composer(PurchasesPermissions.OrderRead)
            .SummariesAsync(order.TenantId, [order], CancellationToken.None));
        Assert.Null(summary.Total);
        Assert.False(summary.ShowPrices);
    }

    [Fact]
    public async Task AnApprovedPricelessOrderShowsConfirmedAmountsButNoRequestedTotal()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central");
        var order = PurchaseOrderTestData.SentPricelessOrder([PurchaseOrderTestData.LinkedLine(quantity: 120m)]);
        order.ApplyWholesalerApproval([PurchaseOrderTestData.FinalOf(order.Lines[0], quantity: 100m, unitCost: 10000m)], PurchaseOrderTestData.Now);

        var detail = await Composer(PurchasesPermissions.OrderRead).DetailAsync(order, CancellationToken.None);

        Assert.True(detail.ShowPrices);
        Assert.Equal(new PurchaseOrderTotalsDto(1_000_000m, 190_000m, 1_190_000m), detail.Totals);
        Assert.Null(detail.RequestedTotal);
        var line = Assert.Single(detail.Lines);
        Assert.Equal((null, 10000m, 1_000_000m, "Changed"), (line.UnitCost, line.ConfirmedUnitCost, line.Subtotal, line.LineStatus));
        Assert.True(detail.ConfirmedWithChanges);

        var summary = Assert.Single(await Composer(PurchasesPermissions.OrderRead)
            .SummariesAsync(order.TenantId, [order], CancellationToken.None));
        Assert.Equal((1_190_000m, true), (summary.Total, summary.ShowPrices));
    }

    [Fact]
    public async Task APricedOrderAndAnExternalOneSayShowPricesTrue()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");

        var external = await Composer(PurchasesPermissions.OrderRead)
            .DetailAsync(PurchaseOrderTestData.Order(), CancellationToken.None);
        var linked = await Composer(PurchasesPermissions.OrderRead)
            .DetailAsync(PurchaseOrderTestData.LinkedOrder(), CancellationToken.None);

        Assert.True(external.ShowPrices);
        Assert.True(linked.ShowPrices);
        Assert.NotNull(linked.Totals);
    }

    [Fact]
    public async Task AMissingMemberIsSinNombre()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");

        var detail = await Composer(PurchasesPermissions.OrderRead)
            .DetailAsync(PurchaseOrderTestData.Order(), CancellationToken.None);

        Assert.Equal("Sin nombre", detail.CreatedBy.Name);
        Assert.Equal("Sin nombre", detail.History[0].ActorName);
    }

    [Fact]
    public async Task AllowedActionsAreEmptyWithoutTheManagePermission()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");

        var detail = await Composer(PurchasesPermissions.OrderRead)
            .DetailAsync(PurchaseOrderTestData.Order(), CancellationToken.None);

        Assert.Empty(detail.AllowedActions);
    }

    [Fact]
    public async Task AllowedActionsAreLowercaseInTheContractOrder()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");
        var composer = Composer(PurchasesPermissions.OrderRead, PurchasesPermissions.OrderManage);

        var draft = await composer.DetailAsync(PurchaseOrderTestData.Order(), CancellationToken.None);
        var sent = await composer.DetailAsync(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent), CancellationToken.None);
        var received = await composer.DetailAsync(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Received), CancellationToken.None);

        Assert.Equal(["edit", "delete", "send", "cancel"], draft.AllowedActions);
        Assert.Equal(["confirm", "cancel", "resend_email"], sent.AllowedActions);
        Assert.Equal(["resend_email"], received.AllowedActions);
    }

    [Fact]
    public async Task ResendEmailIsAllowedFromSentOnAndNeverOnDraftOrCancelled()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");
        var manage = Composer(PurchasesPermissions.OrderRead, PurchasesPermissions.OrderManage);
        var read = Composer(PurchasesPermissions.OrderRead);

        var confirmed = await manage.DetailAsync(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Confirmed), CancellationToken.None);
        var cancelled = await manage.DetailAsync(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Cancelled), CancellationToken.None);
        var draft = await manage.DetailAsync(PurchaseOrderTestData.Order(), CancellationToken.None);
        var sentWithoutManage = await read.DetailAsync(PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent), CancellationToken.None);

        Assert.Contains("resend_email", confirmed.AllowedActions);
        Assert.DoesNotContain("resend_email", cancelled.AllowedActions);
        Assert.DoesNotContain("resend_email", draft.AllowedActions);
        Assert.Empty(sentWithoutManage.AllowedActions);
    }

    [Fact]
    public async Task DetailExposesTheRecipientAndTheLastEmailResult()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle", email: "ventas@textiles.co");
        var order = PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent);
        var at = PurchaseOrderTestData.Now.AddHours(1);
        order.RecordEmail(PurchaseOrderEmailStatus.Failed, "ventas@textiles.co", at);

        var detail = await Composer(PurchasesPermissions.OrderRead).DetailAsync(order, CancellationToken.None);

        Assert.Equal(new PurchaseOrderEmailDto("Failed", "ventas@textiles.co", at, "ventas@textiles.co"), detail.Email);
    }

    [Fact]
    public async Task DetailWithoutAnEmailAttemptHasNullStatusAndTheCurrentRecipient()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");

        var detail = await Composer(PurchasesPermissions.OrderRead)
            .DetailAsync(PurchaseOrderTestData.Order(), CancellationToken.None);

        Assert.Equal(new PurchaseOrderEmailDto(null, null, null, null), detail.Email);
    }

    [Fact]
    public async Task SummariesResolveSuppliersInOneCall()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");
        var orders = new[]
        {
            PurchaseOrderTestData.Order(),
            PurchaseOrderTestData.Order([PurchaseOrderTestData.Line(), PurchaseOrderTestData.Line()]),
            PurchaseOrderTestData.OrderIn(PurchaseOrderStatus.Sent),
        };

        var summaries = await Composer(PurchasesPermissions.OrderRead)
            .SummariesAsync(PurchaseOrderTestData.TenantId, orders, CancellationToken.None);

        Assert.Equal(1, _suppliers.FindManyCalls);
        Assert.Equal(3, summaries.Count);
        Assert.All(summaries, summary => Assert.Equal("Textiles del Valle", summary.Supplier.Name));
        Assert.Equal(2, summaries[1].LineCount);
        Assert.Equal(47.6m, summaries[1].Total);
        Assert.Equal("Sent", summaries[2].Status);
    }

    // Mayorista fase 3 (contrato y adenda): modo, pedido del mayorista, precio de lista por línea, la línea agregada sin cantidad
    // pedida y los conteos del banner.
    [Fact]
    public async Task LinkedDetailShowsListPriceAndAddedLines()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: PurchaseOrderTestData.SupplierTenantId);
        var order = PurchaseOrderTestData.SentLinkedOrder(
            [PurchaseOrderTestData.LinkedLine(code: "A"), PurchaseOrderTestData.LinkedLine(code: "B"), PurchaseOrderTestData.LinkedLine(code: "C")]);
        var (kept, changed, rejected) = (order.Lines[0], order.Lines[1], order.Lines[2]);
        order.ApplyWholesalerApproval(
            [
                PurchaseOrderTestData.FinalOf(kept),
                PurchaseOrderTestData.FinalOf(changed, quantity: 1m),
                PurchaseOrderTestData.FinalOf(rejected, quantity: 0m),
                new WholesalerFinalLine(null, Guid.CreateVersion7(), "D", "Agregado", 4m, 500m, 19m),
            ],
            PurchaseOrderTestData.Now);
        order.LinkLineToCatalog(kept.Id, Guid.CreateVersion7(), created: true);

        var detail = await Composer(PurchasesPermissions.OrderRead).DetailAsync(order, CancellationToken.None);

        Assert.Equal(("Linked", true), (detail.Mode, detail.Supplier.IsLinked));
        Assert.Equal(new PurchaseOrderLinkedDto("PED-0042"), detail.Linked);
        Assert.Equal(new PurchaseOrderChangesDto(1, 1, 1, 1), detail.Changes);
        var first = detail.Lines[0];
        Assert.Equal((kept.SupplierVariantId, true), (first.SupplierVariantId, first.CreatedInCatalog));
        Assert.Equal(new PurchaseOrderListPriceDto(12500m, 4.8m, 11900m), first.ListPrice);
        var added = detail.Lines[^1];
        Assert.Equal(("Added", (decimal?)null, (PurchaseOrderListPriceDto?)null), (added.LineStatus, added.Quantity, added.ListPrice));
    }

    [Fact]
    public async Task AnExternalDetailHasNoLinkedBlock()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Textiles del Valle");

        var detail = await Composer(PurchasesPermissions.OrderRead).DetailAsync(PurchaseOrderTestData.Order(), CancellationToken.None);
        var summary = Assert.Single(await Composer(PurchasesPermissions.OrderRead)
            .SummariesAsync(PurchaseOrderTestData.TenantId, [PurchaseOrderTestData.LinkedOrder()], CancellationToken.None));

        Assert.Equal(("External", false), (detail.Mode, detail.Supplier.IsLinked));
        Assert.Null(detail.Linked);
        Assert.Null(detail.Lines[0].ListPrice);
        Assert.Equal(("Linked", true), (summary.Mode, summary.IsLinked));
    }

    // D-19: lo que hizo el mayorista (sin miembro del comprador) se lee «El mayorista».
    [Fact]
    public async Task WholesalerHistoryEntriesReadElMayorista()
    {
        _suppliers.Add(PurchaseOrderTestData.SupplierId, "Mayorista Central", linkedTenantId: PurchaseOrderTestData.SupplierTenantId);
        _members.Names[PurchaseOrderTestData.MemberId] = "Ana Compras";
        var order = PurchaseOrderTestData.SentLinkedOrder();
        order.ApplyWholesalerApproval([PurchaseOrderTestData.FinalOf(order.Lines[0])], PurchaseOrderTestData.Now);
        order.ApplyWholesalerCancellation(PurchaseOrderTestData.Now);

        var detail = await Composer(PurchasesPermissions.OrderRead).DetailAsync(order, CancellationToken.None);

        Assert.Equal(
            [("Draft", "Ana Compras"), ("Sent", "Ana Compras"), ("Confirmed", "El mayorista"), ("Cancelled", "El mayorista")],
            detail.History.Select(entry => (entry.Status, entry.ActorName)));
    }

    private PurchaseOrderComposer Composer(params string[] permissions) =>
        new(new FakeExecutionContext(PurchaseOrderTestData.TenantId, permissions), _suppliers, _members);
}
