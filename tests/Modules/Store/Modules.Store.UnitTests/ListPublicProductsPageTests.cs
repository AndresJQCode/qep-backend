using Modules.Store.Application;

namespace Modules.Store.UnitTests;

public sealed class ListPublicProductsPageTests
{
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(37, 37)]
    [InlineData(ListPublicProductsHandler.MaxPage, ListPublicProductsHandler.MaxPage)]
    [InlineData(ListPublicProductsHandler.MaxPage + 1, ListPublicProductsHandler.MaxPage)]
    [InlineData(int.MaxValue, ListPublicProductsHandler.MaxPage)]
    public void APageIsClampedSoTheOffsetNeverOverflows(int requested, int expected)
    {
        var page = ListPublicProductsHandler.ClampPage(requested);

        Assert.Equal(expected, page);
        // Lo que de verdad importa: la posición del primer producto cabe en un int con el pageSize más grande.
        Assert.True(checked((page - 1) * ListPublicProductsHandler.MaxPageSize) >= 0);
    }
}
