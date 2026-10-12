using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-11 §6.2 (E3): nombre y color de las etiquetas de una campaña, sólo las que existen en el tenant; las borradas o
/// de otro tenant no aparecen y el color viaja por su nombre de contrato.</summary>
public sealed class CustomerTagLookupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItReturnsTheTenantsTagsWithTheirWireColorAndOmitsTheRest()
    {
        var tags = new EngagementTestBed.FakeTags();
        var mine = Tag.Create(TagId.New(), EngagementTestBed.TenantId, "Mayoristas", TagColor.Blue, EngagementTestBed.Now);
        var foreign = Tag.Create(TagId.New(), EngagementTestBed.OtherTenantId, "Ajena", TagColor.Red, EngagementTestBed.Now);
        tags.Add(mine);
        tags.Add(foreign);

        var found = await new CustomerTagLookup(tags).FindAsync(
            EngagementTestBed.TenantId, [mine.Id.Value, foreign.Id.Value, Guid.CreateVersion7()], Ct);

        Assert.Equal([new CustomerTagRef(mine.Id.Value, "Mayoristas", "blue")], found);
    }

    [Fact]
    public async Task NoIdsIsNothingWithoutAskingTheRepository()
    {
        var found = await new CustomerTagLookup(new EngagementTestBed.FakeTags()).FindAsync(EngagementTestBed.TenantId, [], Ct);

        Assert.Empty(found);
    }
}
