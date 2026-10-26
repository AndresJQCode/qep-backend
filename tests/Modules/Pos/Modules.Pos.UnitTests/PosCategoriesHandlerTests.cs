using BuildingBlocks.Application;
using Modules.Pos.Application;
using static Modules.Pos.UnitTests.PosFixtures;

namespace Modules.Pos.UnitTests;

/// <summary>
/// Las categorías de la caja (spec 2026-10-26 §3.4). El handler no calcula nada: el acumulado y el total
/// los entrega el adaptador, y la respuesta los copia tal cual.
/// </summary>
public sealed class PosCategoriesHandlerTests
{
    private static readonly string[] Seller = [PosPermissions.SaleCreate];

    private static ListPosCategoriesHandler List(PosTestBed bed, params string[] permissions) =>
        new(bed.Categories, bed.Context(permissions));

    [Fact]
    public async Task ListMapsTheLookupOneToOne()
    {
        var bed = new PosTestBed();
        var bebidas = new PosCategoryRef(Guid.CreateVersion7(), "Bebidas", null, 7, null);
        var gaseosas = new PosCategoryRef(Guid.CreateVersion7(), "Gaseosas", bebidas.Id, 4, 1);
        bed.Categories.Categories.Add((TenantId, bebidas));
        bed.Categories.Categories.Add((TenantId, gaseosas));
        bed.Categories.Totals[TenantId] = 9;

        var response = await List(bed, Seller).HandleAsync(
            new ListPosCategoriesQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new PosCategoryResponse(bebidas.Id, "Bebidas", null, 7, null),
                new PosCategoryResponse(gaseosas.Id, "Gaseosas", bebidas.Id, 4, 1),
            ],
            response.Items);
        Assert.Equal(9, response.Total);
    }

    [Fact]
    public async Task ListWithoutSaleCreateIsForbidden()
    {
        var bed = new PosTestBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => List(bed, PosPermissions.SaleRead).HandleAsync(
            new ListPosCategoriesQuery(TenantId), TestContext.Current.CancellationToken));
    }
}
