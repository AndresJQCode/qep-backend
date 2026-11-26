using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Inventory;

public sealed class LedgerDeltasTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Variant = Guid.CreateVersion7();
    private static readonly Guid Rec = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static StockKey Key(Guid location) => new(Owner, location, Variant, null, StockStatus.Available);

    private static LedgerEntry Move(Guid from, Guid to, decimal quantity) =>
        new(Key(from), Key(to), quantity, null, MovementReason.InternalMove, null, Now);

    [Fact]
    public void TheSumOfAllDeltasIsZero()
    {
        var deltas = LedgerDeltas.Net([Move(Rec, A, 10m), Move(A, B, 4m), Move(B, C, 1m)]);

        Assert.Equal(0m, deltas.Sum(delta => delta.Delta));
    }

    [Fact]
    public void AKeyThatIsBothSourceAndTargetIsWrittenOnce()
    {
        var deltas = LedgerDeltas.Net([Move(Rec, A, 10m), Move(A, B, 4m)]);

        Assert.Single(deltas, delta => delta.Key == Key(A));
        Assert.Equal(6m, deltas.Single(delta => delta.Key == Key(A)).Delta);
    }

    [Fact]
    public void AKeyWithNetZeroIsOmitted()
    {
        var deltas = LedgerDeltas.Net([Move(Rec, A, 5m), Move(A, B, 5m)]);

        Assert.DoesNotContain(deltas, delta => delta.Key == Key(A));
        Assert.Equal(2, deltas.Count);
    }

    [Fact]
    public void DeltasComeOutInComparerOrder()
    {
        var deltas = LedgerDeltas.Net([Move(C, B, 1m), Move(B, A, 2m), Move(Rec, C, 3m)]);

        var keys = deltas.Select(delta => delta.Key).ToArray();
        Assert.Equal(keys.Order(StockKeyComparer.Instance).ToArray(), keys);
    }
}
