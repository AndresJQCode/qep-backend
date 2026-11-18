using Modules.Purchases.Domain;
using static Modules.Purchases.UnitTests.PurchaseOrderTestData;

namespace Modules.Purchases.UnitTests;

public sealed class WholesaleReconciliationTests
{
    private static readonly DateTimeOffset Later = Now.AddHours(2);

    private static List<WholesalerFinalLine> AsSent(PurchaseOrder order) =>
        order.Lines.Select(line => FinalOf(line)).ToList();

    [Fact]
    public void ApprovalWithSameQuantityAndCostIsUnchanged()
    {
        var order = SentLinkedOrder();

        Assert.True(order.ApplyWholesalerApproval(AsSent(order), Later));

        Assert.Equal(PurchaseOrderStatus.Confirmed, order.Status);
        Assert.False(order.ConfirmedWithChanges);
        Assert.Equal(PurchaseOrderLineStatus.Unchanged, Assert.Single(order.Lines).Status);
    }

    [Fact]
    public void ApprovalWithAnotherQuantityOrCostIsChanged()
    {
        var order = SentLinkedOrder([LinkedLine(code: "A"), LinkedLine(code: "B")]);
        var first = order.Lines[0];
        var second = order.Lines[1];

        order.ApplyWholesalerApproval([FinalOf(first, quantity: 1m), FinalOf(second, unitCost: 9000m)], Later);

        Assert.True(order.ConfirmedWithChanges);
        Assert.Equal(PurchaseOrderLineStatus.Changed, first.Status);
        Assert.Equal(1m, first.ConfirmedQuantity);
        Assert.Equal(PurchaseOrderLineStatus.Changed, second.Status);
        Assert.Equal(9000m, second.ConfirmedUnitCost);
    }

    [Fact]
    public void ALineThatDoesNotComeBackIsRejectedWithItsCost()
    {
        var order = SentLinkedOrder([LinkedLine(code: "A"), LinkedLine(code: "B", unitCost: 7000m)]);
        var dropped = order.Lines[1];

        order.ApplyWholesalerApproval([FinalOf(order.Lines[0])], Later);

        Assert.Equal(PurchaseOrderLineStatus.Rejected, dropped.Status);
        Assert.Equal(0m, dropped.ConfirmedQuantity);
        Assert.Equal(7000m, dropped.ConfirmedUnitCost);
        Assert.True(order.ConfirmedWithChanges);
    }

    [Fact]
    public void ALineWithoutSourceIdIsAddedWithNullRequestedQuantity()
    {
        var order = SentLinkedOrder();
        var extra = new WholesalerFinalLine(null, Guid.CreateVersion7(), "NEW-1", "Gorra", 3m, 4000m, 19m);

        order.ApplyWholesalerApproval([.. AsSent(order), extra], Later);

        Assert.Equal(2, order.Lines.Count);
        var added = order.Lines[1];
        Assert.Equal(PurchaseOrderLineStatus.Added, added.Status);
        Assert.Null(added.Quantity);
        Assert.Null(added.VariantId);
        Assert.Equal(extra.SupplierVariantId, added.SupplierVariantId);
        Assert.Equal(("NEW-1", "Gorra", 19m), (added.Code, added.Name, added.TaxRatePercent));
        Assert.Equal(4000m, added.UnitCost);
        Assert.Equal(3m, added.ConfirmedQuantity);
        Assert.Equal(4000m, added.ConfirmedUnitCost);
        Assert.Equal(12000m, added.Subtotal);
        Assert.True(order.ConfirmedWithChanges);
    }

    [Fact]
    public void AnUnknownSourceIdIsAdded()
    {
        var order = SentLinkedOrder();
        var stranger = new WholesalerFinalLine(Guid.CreateVersion7(), Guid.CreateVersion7(), "X", "Otra", 1m, 100m, 0m);

        order.ApplyWholesalerApproval([.. AsSent(order), stranger], Later);

        Assert.Equal(PurchaseOrderLineStatus.Added, order.Lines[1].Status);
        Assert.NotEqual(stranger.SourceLineId, order.Lines[1].Id);
    }

    [Fact]
    public void ARepeatedSourceIdAddsTheSecondLine()
    {
        var order = SentLinkedOrder();
        var sent = order.Lines[0];

        order.ApplyWholesalerApproval([FinalOf(sent), FinalOf(sent, quantity: 1m)], Later);

        Assert.Equal(PurchaseOrderLineStatus.Unchanged, sent.Status);
        Assert.Equal(PurchaseOrderLineStatus.Added, order.Lines[1].Status);
    }

    [Fact]
    public void ApplyingTheApprovalTwiceChangesNothing()
    {
        var order = SentLinkedOrder();
        order.ApplyWholesalerApproval(AsSent(order), Later);
        var version = order.Version;
        var history = order.History.Count;

        Assert.False(order.ApplyWholesalerApproval([FinalOf(order.Lines[0], quantity: 9m)], Later.AddHours(1)));

        Assert.Equal(version, order.Version);
        Assert.Equal(history, order.History.Count);
        Assert.Equal(PurchaseOrderLineStatus.Unchanged, order.Lines[0].Status);
    }

    [Fact]
    public void ApprovalOutsideSentChangesNothing()
    {
        var draft = LinkedOrder();
        Assert.False(draft.ApplyWholesalerApproval([], Later));
        Assert.Equal(PurchaseOrderStatus.Draft, draft.Status);

        var cancelled = SentLinkedOrder();
        cancelled.Cancel(MemberId, Now);
        Assert.False(cancelled.ApplyWholesalerApproval(AsSent(cancelled), Later));
        Assert.Equal(PurchaseOrderStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public void ApprovalHistoryIsSignedByTheWholesaler()
    {
        var order = SentLinkedOrder();

        order.ApplyWholesalerApproval(AsSent(order), Later);

        var entry = order.History[^1];
        Assert.Equal((PurchaseOrderStatus.Confirmed, Later, Guid.Empty), (entry.Status, entry.OccurredAt, entry.MemberId));
    }

    [Fact]
    public void WholesalerCancellationCancelsFromDraftSentOrConfirmed()
    {
        var sent = SentLinkedOrder();
        Assert.True(sent.ApplyWholesalerCancellation(Later));
        Assert.Equal(PurchaseOrderStatus.Cancelled, sent.Status);
        Assert.Equal(Guid.Empty, sent.History[^1].MemberId);

        var confirmed = SentLinkedOrder();
        confirmed.ApplyWholesalerApproval(AsSent(confirmed), Later);
        Assert.True(confirmed.ApplyWholesalerCancellation(Later));
        Assert.Equal(PurchaseOrderStatus.Cancelled, confirmed.Status);

        Assert.True(LinkedOrder().ApplyWholesalerCancellation(Later));
    }

    [Fact]
    public void CancellationAfterReceivedIsIgnored()
    {
        var order = SentLinkedOrder();
        order.ApplyWholesalerApproval(AsSent(order), Later);
        order.Receive(MemberId, Later);
        var version = order.Version;

        Assert.False(order.ApplyWholesalerCancellation(Later.AddHours(1)));

        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        Assert.Equal(version, order.Version);

        var cancelled = SentLinkedOrder();
        cancelled.ApplyWholesalerCancellation(Later);
        Assert.False(cancelled.ApplyWholesalerCancellation(Later));
    }

    [Fact]
    public void LinesNeedingCatalogSkipsRejectedAndAlreadyLinkedLines()
    {
        var order = SentLinkedOrder([LinkedLine(code: "A"), LinkedLine(code: "B"), LinkedLine(code: "C")]);
        Assert.Empty(order.LinesNeedingCatalog());
        var (linked, rejected, pending) = (order.Lines[0], order.Lines[1], order.Lines[2]);
        order.ApplyWholesalerApproval([FinalOf(linked), FinalOf(rejected, quantity: 0m), FinalOf(pending)], Later);
        order.LinkLineToCatalog(linked.Id, Guid.CreateVersion7(), created: false);

        Assert.Equal([pending.Id], order.LinesNeedingCatalog().Select(line => line.Id));
        Assert.Empty(OrderIn(PurchaseOrderStatus.Confirmed).LinesNeedingCatalog());
    }

    [Fact]
    public void LinkLineToCatalogDoesNotBumpTheVersion()
    {
        var order = SentLinkedOrder();
        order.ApplyWholesalerApproval(AsSent(order), Later);
        var version = order.Version;
        var variantId = Guid.CreateVersion7();

        order.LinkLineToCatalog(order.Lines[0].Id, variantId, created: true);

        Assert.Equal(version, order.Version);
        Assert.Equal(variantId, order.Lines[0].VariantId);
        Assert.True(order.Lines[0].CreatedInCatalog);
        Assert.Throws<ArgumentException>(() => order.LinkLineToCatalog(Guid.CreateVersion7(), variantId, created: true));
    }
}
