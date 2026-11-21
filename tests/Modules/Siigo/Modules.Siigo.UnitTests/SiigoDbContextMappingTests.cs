using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Persistence;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// El modelo de EF de Siigo contra el snapshot, sin abrir una base (mismo criterio que StorageDbContextMappingTests). La columna
/// <c>siigo.settings.seller_id</c> sigue mapeada aunque el dominio ya no tenga vendedor (spec 2026-11-20): las migraciones corren al
/// arrancar, y borrarla en este release dejaría a las réplicas viejas de un despliegue gradual —y a un rollback— leyendo una columna que
/// no existe. Se borra en un release posterior.
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
    public void TheSellerColumnStaysMappedUntilItsDropRelease()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        var seller = context.Model.FindEntityType(typeof(SiigoSettings))!.FindProperty("SellerId");

        Assert.NotNull(seller);
        Assert.True(seller.IsShadowProperty());
        Assert.Equal("seller_id", seller.GetColumnName());
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
