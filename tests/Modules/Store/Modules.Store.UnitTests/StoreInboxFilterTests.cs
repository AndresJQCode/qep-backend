using Modules.Store.Application;

namespace Modules.Store.UnitTests;

/// <summary>Revisión final, M-2: <c>page=2147483647</c> desbordaba <c>(page - 1) * pageSize</c> y Postgres rechazaba el OFFSET negativo con un 500.</summary>
public sealed class StoreInboxFilterTests
{
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(37, 37)]
    [InlineData(StoreInboxFilter.MaxPage, StoreInboxFilter.MaxPage)]
    [InlineData(int.MaxValue, StoreInboxFilter.MaxPage)]
    public void APageIsClamped(int requested, int expected) => Assert.Equal(expected, StoreInboxFilter.ClampPage(requested));

    [Fact]
    public void TheOffsetNeverOverflowsEvenWithTheLargestPageAndPageSize()
    {
        var filter = new StoreInboxFilter(
            Guid.NewGuid(), StoreInboxView.All, Guid.NewGuid(), int.MaxValue, ListOrderRequestsValidator.MaxPageSize);

        Assert.True(filter.Skip >= 0);
        Assert.Equal((StoreInboxFilter.MaxPage - 1) * ListOrderRequestsValidator.MaxPageSize, filter.Skip);
    }

    [Fact]
    public void TheValidatorAcceptsAHugePageBecauseTheHandlerClampsIt() =>
        Assert.True(new ListOrderRequestsValidator()
            .Validate(new ListOrderRequestsQuery(Guid.NewGuid(), null, int.MaxValue, null)).IsValid);
}
