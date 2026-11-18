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
        Assert.Equal(["confirm", "cancel"], sent.AllowedActions);
        Assert.Empty(received.AllowedActions);
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

    private PurchaseOrderComposer Composer(params string[] permissions) =>
        new(new FakeExecutionContext(PurchaseOrderTestData.TenantId, permissions), _suppliers, _members);
}
