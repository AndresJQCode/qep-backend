using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Inventory;

public sealed class LedgerBatchTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Variant = Guid.CreateVersion7();
    private static readonly Guid LocationA = Guid.CreateVersion7();
    private static readonly Guid LocationB = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static StockKey Key(Guid location, StockStatus status = StockStatus.Available) =>
        new(Owner, location, Variant, null, status);

    private static LedgerEntry Entry(StockKey from, StockKey to, decimal quantity = 1m) =>
        new(from, to, quantity, null, MovementReason.InternalMove, null, Now);

    [Fact]
    public void AnEntryWithZeroQuantityIsRejected()
    {
        var exception = Assert.Throws<WmsDomainException>(() => Entry(Key(LocationA), Key(LocationB), 0m));

        Assert.Equal("wms.movement.quantity_invalid", exception.Code);
    }

    [Fact]
    public void AnEntryFromAKeyToItselfIsRejected()
    {
        var exception = Assert.Throws<WmsDomainException>(() => Entry(Key(LocationA), Key(LocationA)));

        Assert.Equal("wms.movement.same_key", exception.Code);
    }

    [Fact]
    public void AnEntryBetweenDifferentVariantsIsRejected()
    {
        var other = new StockKey(Owner, LocationB, Guid.CreateVersion7(), null, StockStatus.Available);

        var exception = Assert.Throws<WmsDomainException>(() => Entry(Key(LocationA), other));

        Assert.Equal("wms.movement.same_key", exception.Code);
    }

    [Fact]
    public void ABatchAboveFiveHundredEntriesIsRejected()
    {
        var entries = Enumerable.Range(0, LedgerBatch.MaxEntries + 1)
            .Select(_ => Entry(Key(LocationA), Key(LocationB)))
            .ToArray();

        var exception = Assert.Throws<WmsDomainException>(() => NewBatch(entries));

        Assert.Equal("wms.batch.too_large", exception.Code);
    }

    [Fact]
    public void ABatchOfExactlyFiveHundredEntriesIsAccepted()
    {
        var entries = Enumerable.Range(0, LedgerBatch.MaxEntries)
            .Select(_ => Entry(Key(LocationA), Key(LocationB)))
            .ToArray();

        Assert.Equal(LedgerBatch.MaxEntries, NewBatch(entries).Entries.Count);
    }

    [Fact]
    public void ABatchWithoutEntriesIsRejected() =>
        Assert.Throws<ArgumentException>(() => NewBatch([]));

    [Fact]
    public void ReversingAnEntrySwapsTheKeysAndPointsToTheOriginalWithItsDate()
    {
        var original = Entry(Key(LocationA), Key(LocationB), 4m);
        var originalId = Guid.CreateVersion7();
        var originalAt = Now.AddDays(-3);

        var reversal = LedgerEntry.Reverse(original, originalId, originalAt, Now, "error de digitación");

        Assert.Equal(original.To, reversal.From);
        Assert.Equal(original.From, reversal.To);
        Assert.Equal(4m, reversal.Quantity);
        Assert.Equal(MovementReason.Reversal, reversal.Reason);
        Assert.Equal(originalId, reversal.ReversesMovementId);
        Assert.Equal(originalAt, reversal.ReversesOccurredAt);
        Assert.Equal("error de digitación", reversal.OverrideReason);
        Assert.Equal(Now, reversal.OccurredAt);
    }

    [Fact]
    public void AReversalReferenceWithoutItsDateIsRejected() =>
        Assert.Throws<ArgumentException>(() => new LedgerEntry(
            Key(LocationA), Key(LocationB), 1m, null, MovementReason.Reversal, null, Now, reversesMovementId: Guid.CreateVersion7()));

    private static LedgerBatch NewBatch(IReadOnlyList<LedgerEntry> entries) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "key-1", new LedgerActor(Guid.CreateVersion7(), LedgerActorType.System, null), entries);
}
