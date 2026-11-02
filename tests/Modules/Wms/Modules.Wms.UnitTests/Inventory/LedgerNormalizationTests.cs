using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Inventory;

public sealed class LedgerNormalizationTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Variant = Guid.CreateVersion7();

    [Fact]
    public void AnOccurredAtWithANonZeroOffsetIsStoredInUtc()
    {
        var local = new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.FromHours(-5));

        var entry = new LedgerEntry(
            new StockKey(Owner, Guid.CreateVersion7(), Variant, null, StockStatus.Available),
            new StockKey(Owner, Guid.CreateVersion7(), Variant, null, StockStatus.Available),
            1m, null, MovementReason.InternalMove, null, local);

        Assert.Equal(TimeSpan.Zero, entry.OccurredAt.Offset);
        Assert.Equal(local, entry.OccurredAt);
    }

    [Fact]
    public void AReceiptWithAShortHashIsRejected() =>
        Assert.Throws<ArgumentException>(() => OperationReceipt.Create(Guid.CreateVersion7(), "k", "abc", "{}", DateTimeOffset.UtcNow));

    [Fact]
    public void AReceiptWithA64HexHashIsAccepted() =>
        Assert.Equal(64, OperationReceipt.Create(Guid.CreateVersion7(), "k", new string('a', 64), "{}", DateTimeOffset.UtcNow).RequestHash.Length);
}
