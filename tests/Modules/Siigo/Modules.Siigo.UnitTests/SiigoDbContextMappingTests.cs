using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Persistence;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// El modelo de EF de Siigo contra el snapshot, sin abrir una base (mismo criterio que StorageDbContextMappingTests). La columna
/// <c>siigo.settings.seller_id</c> se borró en un release posterior al que dejó de usarla (spec 2026-11-20): ya no hay imagen en
/// ejecución que la lea, así que no queda mapeada.
/// </summary>
public sealed class SiigoDbContextMappingTests
{
    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TheSellerColumnIsNoLongerMapped()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        Assert.Null(context.Model.FindEntityType(typeof(SiigoSettings))!.FindProperty("SellerId"));
    }

    [Fact]
    public void TheSellerLinksMapToTheirTable()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        var link = context.Model.FindEntityType(typeof(SiigoSellerLink))!;

        Assert.Equal(("seller_links", "siigo"), (link.GetTableName(), link.GetSchema()));
        Assert.Equal(["ConnectionId", "MembershipId"], link.FindPrimaryKey()!.Properties.Select(property => property.Name));
    }
}
