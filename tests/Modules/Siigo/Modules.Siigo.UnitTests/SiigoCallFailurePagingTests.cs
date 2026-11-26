using Modules.Siigo.Application.CallFailures;

namespace Modules.Siigo.UnitTests;

/// <summary>La página de las listas de Siigo (fallas y tandas): lo normal pasa igual y una enorme no desborda el desplazamiento.</summary>
public sealed class SiigoCallFailurePagingTests
{
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(500, 500)]
    [InlineData(SiigoCallFailurePaging.MaxPage, SiigoCallFailurePaging.MaxPage)]
    [InlineData(int.MaxValue, SiigoCallFailurePaging.MaxPage)]
    public void ThePageIsClampedToTheAllowedRange(int requested, int expected) =>
        Assert.Equal(expected, SiigoCallFailurePaging.NormalizePage(requested));

    [Fact]
    public void TheOffsetOfTheLastPageFitsInAnInt() =>
        Assert.True((SiigoCallFailurePaging.MaxPage - 1L) * SiigoCallFailurePaging.MaxPageSize <= int.MaxValue);
}
