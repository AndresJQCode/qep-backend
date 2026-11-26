using Modules.Purchases.Domain;
using static Modules.Purchases.UnitTests.PurchaseOrderTestData;

namespace Modules.Purchases.UnitTests;

public sealed class PurchaseOrderTests
{
    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<PurchasesDomainException>(act).Code);

    [Fact]
    public void CreateStartsAsDraftWithVersionOneAndADraftHistoryEntry()
    {
        var order = Order();

        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
        Assert.Equal(1, order.Version);
        Assert.Equal(TenantId, order.TenantId);
        Assert.Equal("OC-0001", order.Number);
        Assert.Equal(MemberId, order.CreatedByMemberId);
        Assert.Equal(Now, order.CreatedAt);
        Assert.Equal(Now, order.UpdatedAt);
        Assert.False(order.ConfirmedWithChanges);
        var entry = Assert.Single(order.History);
        Assert.Equal(PurchaseOrderStatus.Draft, entry.Status);
        Assert.Equal(Now, entry.OccurredAt);
        Assert.Equal(MemberId, entry.MemberId);
    }

    [Fact]
    public void CreateWithoutLinesThrowsEmpty() =>
        AssertCode("purchases.order.empty", () => Order(lines: []));

    [Fact]
    public void CreateTrimsNotesAndTurnsBlankIntoNull()
    {
        Assert.Null(Order(notes: "  ").Notes);
        Assert.Equal("ok", Order(notes: " ok ").Notes);
    }

    [Fact]
    public void CreateUppercasesTheCurrency() =>
        Assert.Equal("COP", Order(currency: "cop").Currency);

    [Fact]
    public void ReplaceOnADraftSwapsLinesAndBumpsVersion()
    {
        var order = Order(lines: [Line(), Line()]);
        var previousIds = order.Lines.Select(line => line.Id).ToList();
        var later = Now.AddMinutes(5);

        order.Replace(SupplierId, null, "usd", "nuevo", [Line(quantity: 7m), Line(), Line()], later);

        Assert.Equal(2, order.Version);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal("USD", order.Currency);
        Assert.Equal("nuevo", order.Notes);
        Assert.Equal(3, order.Lines.Count);
        Assert.Equal(7m, order.Lines[0].Quantity);
        Assert.DoesNotContain(order.Lines, line => previousIds.Contains(line.Id));
        Assert.Single(order.History);
    }

    [Fact]
    public void ReplaceOutsideDraftThrowsNotDraft()
    {
        var order = OrderIn(PurchaseOrderStatus.Sent);

        AssertCode("purchases.order.not_draft", () => order.Replace(SupplierId, null, "COP", null, [Line()], Now));
    }

    [Fact]
    public void EnsureDeletableOutsideDraftThrowsNotDraft()
    {
        OrderIn(PurchaseOrderStatus.Draft).EnsureDeletable();

        AssertCode("purchases.order.not_draft", () => OrderIn(PurchaseOrderStatus.Sent).EnsureDeletable());
    }

    [Fact]
    public void RecordEmailStoresTheResultWithoutBumpingTheVersion()
    {
        var order = OrderIn(PurchaseOrderStatus.Sent);
        var version = order.Version;
        var updatedAt = order.UpdatedAt;
        var at = Now.AddHours(2);

        order.RecordEmail(PurchaseOrderEmailStatus.Sent, "ventas@proveedor.co", at);

        Assert.Equal(PurchaseOrderEmailStatus.Sent, order.LastEmailStatus);
        Assert.Equal("ventas@proveedor.co", order.LastEmailTo);
        Assert.Equal(at, order.LastEmailAt);
        Assert.Equal(version, order.Version);
        Assert.Equal(updatedAt, order.UpdatedAt);
    }

    [Fact]
    public void RecordEmailOnADraftThrowsEmailNotAllowed() =>
        AssertCode(
            "purchases.order.email_not_allowed",
            () => OrderIn(PurchaseOrderStatus.Draft).RecordEmail(PurchaseOrderEmailStatus.Sent, "a@b.co", Now));

    [Fact]
    public void SendMovesDraftToSentAndAppendsHistory()
    {
        var order = Order();
        var later = Now.AddHours(1);
        var sender = Guid.CreateVersion7();

        order.Send(sender, later);

        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal(2, order.Version);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal([PurchaseOrderStatus.Draft, PurchaseOrderStatus.Sent], order.History.Select(entry => entry.Status));
        Assert.Equal(sender, order.History[^1].MemberId);
        Assert.Equal(later, order.History[^1].OccurredAt);
        Assert.Equal([1, 2], order.History.Select(entry => entry.Sequence));
    }

    [Fact]
    public void ConfirmWithEveryLineUnchangedLeavesConfirmedWithChangesFalse()
    {
        var order = OrderIn(PurchaseOrderStatus.Sent);

        order.Confirm(AsRequested(order), MemberId, Now);

        Assert.Equal(PurchaseOrderStatus.Confirmed, order.Status);
        Assert.False(order.ConfirmedWithChanges);
        Assert.All(order.Lines, line => Assert.Equal(PurchaseOrderLineStatus.Unchanged, line.Status));
        Assert.Equal(PurchaseOrderStatus.Confirmed, order.History[^1].Status);
    }

    [Fact]
    public void ConfirmWithAChangedCostMarksTheLineChangedAndTheOrderWithChanges()
    {
        var order = Order(lines: [Line(quantity: 2m, unitCost: 10m), Line(quantity: 1m, unitCost: 5m)]);
        order.Send(MemberId, Now);

        order.Confirm(
            [
                new PurchaseOrderLineConfirmation(order.Lines[0].Id, 2m, 11m),
                new PurchaseOrderLineConfirmation(order.Lines[1].Id, 1m, 5m),
            ],
            MemberId,
            Now);

        Assert.Equal(PurchaseOrderLineStatus.Changed, order.Lines[0].Status);
        Assert.Equal(11m, order.Lines[0].ConfirmedUnitCost);
        Assert.Equal(PurchaseOrderLineStatus.Unchanged, order.Lines[1].Status);
        Assert.True(order.ConfirmedWithChanges);
    }

    [Fact]
    public void ConfirmWithZeroQuantityMarksTheLineRejected()
    {
        var order = OrderIn(PurchaseOrderStatus.Sent);
        var line = Assert.Single(order.Lines);

        order.Confirm([new PurchaseOrderLineConfirmation(line.Id, 0m, line.UnitCost!.Value)], MemberId, Now);

        Assert.Equal(PurchaseOrderLineStatus.Rejected, line.Status);
        Assert.True(order.ConfirmedWithChanges);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicated")]
    [InlineData("foreign")]
    public void ConfirmWithAMissingDuplicatedOrForeignLineThrowsMismatch(string kind)
    {
        var order = Order(lines: [Line(), Line()]);
        order.Send(MemberId, Now);
        var version = order.Version;
        var first = order.Lines[0];
        var second = order.Lines[1];
        IReadOnlyList<PurchaseOrderLineConfirmation> lines = kind switch
        {
            "missing" => [new(first.Id, 2m, 10m)],
            "duplicated" => [new(first.Id, 2m, 10m), new(first.Id, 2m, 10m)],
            _ => [new(first.Id, 2m, 10m), new(Guid.CreateVersion7(), 2m, 10m)],
        };

        AssertCode("purchases.order.confirm_lines_mismatch", () => order.Confirm(lines, MemberId, Now));

        Assert.Equal(PurchaseOrderStatus.Sent, order.Status);
        Assert.Equal(version, order.Version);
        Assert.Null(first.ConfirmedQuantity);
        Assert.Equal(PurchaseOrderLineStatus.Pending, second.Status);
    }

    [Fact]
    public void ReceiveOnlyFromConfirmed()
    {
        AssertCode(
            "purchases.order.invalid_transition",
            () => OrderIn(PurchaseOrderStatus.Sent).Receive(MemberId, Now));

        var order = OrderIn(PurchaseOrderStatus.Confirmed);
        order.Receive(MemberId, Now);
        Assert.Equal(PurchaseOrderStatus.Received, order.Status);
        Assert.Equal(PurchaseOrderStatus.Received, order.History[^1].Status);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Sent)]
    [InlineData(PurchaseOrderStatus.Confirmed)]
    public void CancelFromDraftSentAndConfirmedSucceeds(PurchaseOrderStatus from)
    {
        var order = OrderIn(from);
        var version = order.Version;

        order.Cancel(MemberId, Now);

        Assert.Equal(PurchaseOrderStatus.Cancelled, order.Status);
        Assert.Equal(version + 1, order.Version);
        Assert.Equal(PurchaseOrderStatus.Cancelled, order.History[^1].Status);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    public void CancelFromReceivedOrCancelledThrowsInvalidTransition(PurchaseOrderStatus from) =>
        AssertCode("purchases.order.invalid_transition", () => OrderIn(from).Cancel(MemberId, Now));

    [Fact]
    public void SendFromSentThrowsInvalidTransition() =>
        AssertCode("purchases.order.invalid_transition", () => OrderIn(PurchaseOrderStatus.Sent).Send(MemberId, Now));

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, new[] { PurchaseOrderAction.Edit, PurchaseOrderAction.Delete, PurchaseOrderAction.Send, PurchaseOrderAction.Cancel })]
    [InlineData(PurchaseOrderStatus.Sent, new[] { PurchaseOrderAction.Confirm, PurchaseOrderAction.Cancel })]
    [InlineData(PurchaseOrderStatus.Confirmed, new[] { PurchaseOrderAction.Receive, PurchaseOrderAction.Cancel })]
    [InlineData(PurchaseOrderStatus.Received, new PurchaseOrderAction[0])]
    [InlineData(PurchaseOrderStatus.Cancelled, new PurchaseOrderAction[0])]
    public void AllowedActionsFollowTheState(PurchaseOrderStatus status, PurchaseOrderAction[] expected) =>
        Assert.Equal(expected, OrderIn(status).AllowedActions());
}
