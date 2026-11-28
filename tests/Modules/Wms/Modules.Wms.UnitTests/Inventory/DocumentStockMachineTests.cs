using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Inventory;

/// <summary>Spec inventario básico §5.2: la máquina por documento, una prueba por fila de la tabla.</summary>
public sealed class DocumentStockMachineTests
{
    private static InventoryEvent Event(
        InventoryEventKind kind,
        long version = 10,
        string? origin = "Manual",
        bool isPrepared = false,
        bool fromPlatform = false,
        bool awaiting = false) =>
        new(kind, version, origin, isPrepared, fromPlatform, awaiting);

    private static StockDecision Decide(DocumentStockStatus current, InventoryEvent evt, long lastVersion = 1) =>
        DocumentStockMachine.Decide(current, lastVersion, evt);

    private static void Expect(StockDecision decision, StockAction action, DocumentStockStatus next)
    {
        Assert.Equal(action, decision.Action);
        Assert.Equal(next, decision.Next);
    }

    [Fact]
    public void NoneCreatedReserves() =>
        Expect(Decide(DocumentStockStatus.None, Event(InventoryEventKind.OrderCreated)), StockAction.Reserve, DocumentStockStatus.Reserved);

    [Fact]
    public void NoneWholesaleApprovedReserves() =>
        Expect(
            Decide(DocumentStockStatus.None, Event(InventoryEventKind.OrderWholesaleApproved)),
            StockAction.Reserve,
            DocumentStockStatus.Reserved);

    [Fact]
    public void ACreatedAwaitingWholesaleApprovalDoesNothing() =>
        Expect(
            Decide(DocumentStockStatus.None, Event(InventoryEventKind.OrderCreated, awaiting: true)),
            StockAction.None,
            DocumentStockStatus.None);

    [Fact]
    public void ReservedEditedReplacesTheReservation() =>
        Expect(
            Decide(DocumentStockStatus.Reserved, Event(InventoryEventKind.OrderEdited)),
            StockAction.ReplaceReservation,
            DocumentStockStatus.Reserved);

    [Fact]
    public void ReservedPreparedTakesOut() =>
        Expect(
            Decide(DocumentStockStatus.Reserved, Event(InventoryEventKind.OrderPrepared)),
            StockAction.TakeOut,
            DocumentStockStatus.TakenOutByPrepare);

    [Fact]
    public void ReservedArchivedUnpreparedTakesOut() =>
        Expect(
            Decide(DocumentStockStatus.Reserved, Event(InventoryEventKind.OrderArchived, isPrepared: false)),
            StockAction.TakeOut,
            DocumentStockStatus.TakenOutByArchive);

    [Fact]
    public void ReservedCancelledReleases() =>
        Expect(
            Decide(DocumentStockStatus.Reserved, Event(InventoryEventKind.OrderCancelled)),
            StockAction.Release,
            DocumentStockStatus.Released);

    [Fact]
    public void TakenOutByPrepareUnpreparedCompensatesAndReserves() =>
        Expect(
            Decide(DocumentStockStatus.TakenOutByPrepare, Event(InventoryEventKind.OrderUnprepared)),
            StockAction.CompensateAndReserve,
            DocumentStockStatus.Reserved);

    [Theory]
    [InlineData(InventoryEventKind.OrderArchived)]
    [InlineData(InventoryEventKind.OrderUnarchived)]
    public void TakenOutByPrepareArchiveAndUnarchiveOfAPreparedOrderDoNothing(InventoryEventKind kind) =>
        Expect(
            Decide(DocumentStockStatus.TakenOutByPrepare, Event(kind, isPrepared: true)),
            StockAction.None,
            DocumentStockStatus.TakenOutByPrepare);

    [Fact]
    public void PrepareArchiveUnarchiveReturnsNothing()
    {
        var state = DocumentStockStatus.None;
        var steps = new[]
        {
            (InventoryEventKind.OrderCreated, false, StockAction.Reserve),
            (InventoryEventKind.OrderPrepared, false, StockAction.TakeOut),
            (InventoryEventKind.OrderArchived, true, StockAction.None),
            (InventoryEventKind.OrderUnarchived, true, StockAction.None),
        };

        long version = 1;
        foreach (var (kind, isPrepared, expected) in steps)
        {
            var decision = DocumentStockMachine.Decide(state, version - 1, Event(kind, version, isPrepared: isPrepared));
            Assert.Equal(expected, decision.Action);
            state = decision.Next;
            version++;
        }

        Assert.Equal(DocumentStockStatus.TakenOutByPrepare, state);
    }

    [Fact]
    public void TakenOutByPrepareCancelledFromThePlatformReturnsNothing() =>
        Expect(
            Decide(DocumentStockStatus.TakenOutByPrepare, Event(InventoryEventKind.OrderCancelled, fromPlatform: true)),
            StockAction.None,
            DocumentStockStatus.Released);

    [Fact]
    public void TakenOutByArchiveUnarchivedCompensatesAndReserves() =>
        Expect(
            Decide(DocumentStockStatus.TakenOutByArchive, Event(InventoryEventKind.OrderUnarchived, isPrepared: false)),
            StockAction.CompensateAndReserve,
            DocumentStockStatus.Reserved);

    [Fact]
    public void UnarchiveAfterArchiveWithoutPrepareCompensatesAndReserves()
    {
        var archived = DocumentStockMachine.Decide(
            DocumentStockStatus.Reserved, 1, Event(InventoryEventKind.OrderArchived, 2, isPrepared: false));
        var unarchived = DocumentStockMachine.Decide(
            archived.Next, 2, Event(InventoryEventKind.OrderUnarchived, 3, isPrepared: false));

        Assert.Equal(StockAction.TakeOut, archived.Action);
        Assert.Equal(StockAction.CompensateAndReserve, unarchived.Action);
        Assert.Equal(DocumentStockStatus.Reserved, unarchived.Next);
    }

    [Fact]
    public void TakenOutByArchiveCancelledFromThePlatformReturnsNothing() =>
        Expect(
            Decide(DocumentStockStatus.TakenOutByArchive, Event(InventoryEventKind.OrderCancelled, fromPlatform: true)),
            StockAction.None,
            DocumentStockStatus.Released);

    [Fact]
    public void WmsPackingOriginIsIgnored() =>
        Expect(
            Decide(DocumentStockStatus.Reserved, Event(InventoryEventKind.OrderPrepared, origin: "WmsPacking")),
            StockAction.None,
            DocumentStockStatus.Reserved);

    [Fact]
    public void HandedToOutboundOnlyListensToCancel()
    {
        foreach (var kind in new[]
        {
            InventoryEventKind.OrderEdited, InventoryEventKind.OrderPrepared, InventoryEventKind.OrderArchived,
            InventoryEventKind.OrderUnarchived, InventoryEventKind.OrderUnprepared,
        })
        {
            Expect(Decide(DocumentStockStatus.HandedToOutbound, Event(kind)), StockAction.None, DocumentStockStatus.HandedToOutbound);
        }

        Expect(
            Decide(DocumentStockStatus.HandedToOutbound, Event(InventoryEventKind.OrderCancelled)),
            StockAction.Release,
            DocumentStockStatus.Released);
    }

    [Fact]
    public void ReleasedIgnoresEverything() =>
        Expect(
            Decide(DocumentStockStatus.Released, Event(InventoryEventKind.OrderEdited)),
            StockAction.None,
            DocumentStockStatus.Released);

    [Fact]
    public void AnOlderVersionDoesNothing() =>
        Expect(
            Decide(DocumentStockStatus.None, Event(InventoryEventKind.OrderCreated, version: 3), lastVersion: 3),
            StockAction.None,
            DocumentStockStatus.None);

    [Fact]
    public void ACancelBeforeCreatedLeavesNoneAndBlocksTheLateCreated()
    {
        var cancelled = DocumentStockMachine.Decide(
            DocumentStockStatus.None, 0, Event(InventoryEventKind.OrderCancelled, 5));
        var lateCreated = DocumentStockMachine.Decide(cancelled.Next, 5, Event(InventoryEventKind.OrderCreated, 2));

        Expect(cancelled, StockAction.None, DocumentStockStatus.None);
        Expect(lateCreated, StockAction.None, DocumentStockStatus.None);
    }

    [Fact]
    public void APurchaseReceivedManuallyReceives() =>
        Expect(
            Decide(DocumentStockStatus.None, Event(InventoryEventKind.PurchaseReceived)),
            StockAction.Receive,
            DocumentStockStatus.Received);

    [Fact]
    public void WmsReceiptOriginIsIgnored() =>
        Expect(
            Decide(DocumentStockStatus.None, Event(InventoryEventKind.PurchaseReceived, origin: "WmsReceipt")),
            StockAction.None,
            DocumentStockStatus.None);

    [Fact]
    public void AManualReceiptOfAnOrderTheWmsAlreadyReceivedDoesNothing() =>
        Expect(
            Decide(DocumentStockStatus.ReceivedByWms, Event(InventoryEventKind.PurchaseReceived)),
            StockAction.None,
            DocumentStockStatus.ReceivedByWms);

    [Fact]
    public void APosSaleShips() =>
        Expect(Decide(DocumentStockStatus.None, Event(InventoryEventKind.PosCreated)), StockAction.Ship, DocumentStockStatus.Shipped);

    [Fact]
    public void AVoidedPosSaleCompensates() =>
        Expect(
            Decide(DocumentStockStatus.Shipped, Event(InventoryEventKind.PosVoided)),
            StockAction.Compensate,
            DocumentStockStatus.Voided);

    [Fact]
    public void AVoidedPosSaleThatNeverShippedDoesNothing() =>
        Expect(Decide(DocumentStockStatus.None, Event(InventoryEventKind.PosVoided)), StockAction.None, DocumentStockStatus.None);
}
