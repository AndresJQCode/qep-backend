using Microsoft.EntityFrameworkCore;
using Modules.Wholesale.Domain;
using Modules.Wholesale.Infrastructure.Persistence;

namespace Modules.Wholesale.UnitTests;

/// <summary>
/// El modelo de EF de Mayorista sin abrir una base (como <c>TenancyDbContextMappingTests</c>). Los nombres de índice importan:
/// <c>WholesaleUnitOfWork</c> traduce cada violación por nombre a su código del contrato.
/// </summary>
public sealed class WholesaleDbContextMappingTests
{
    [Fact]
    public void TheModelUsesTheWholesaleSchemaAndTheNamedUniqueIndexes()
    {
        using var context = new WholesaleDbContextFactory().CreateDbContext([]);
        var model = context.Model;

        var priceLists = model.FindEntityType(typeof(PriceList))!;
        var distributors = model.FindEntityType(typeof(Distributor))!;
        var invitations = model.FindEntityType(typeof(DistributorInvitation))!;
        Assert.Equal(("wholesale", "price_lists"), (priceLists.GetSchema(), priceLists.GetTableName()));
        Assert.Equal(("wholesale", "distributors"), (distributors.GetSchema(), distributors.GetTableName()));
        Assert.Equal(("wholesale", "distributor_invitations"), (invitations.GetSchema(), invitations.GetTableName()));

        var unique = model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes())
            .Where(index => index.IsUnique)
            .Select(index => index.GetDatabaseName())
            .ToHashSet();
        Assert.Contains(WholesaleDbContext.PriceListNameIndex, unique);
        Assert.Contains(WholesaleDbContext.DistributorIdentificationIndex, unique);
        Assert.Contains(WholesaleDbContext.DistributorTenantIndex, unique);
        Assert.Contains(WholesaleDbContext.InvitationTokenHashIndex, unique);
        Assert.Contains(WholesaleDbContext.InvitationReservedTenantIndex, unique);
        Assert.Equal(
            ["IX_price_lists_tenant_name", "IX_distributors_tenant_identification", "IX_distributors_tenant_distributor_tenant",
                "IX_distributor_invitations_token_hash", "IX_distributor_invitations_reserved_tenant"],
            [WholesaleDbContext.PriceListNameIndex, WholesaleDbContext.DistributorIdentificationIndex,
                WholesaleDbContext.DistributorTenantIndex, WholesaleDbContext.InvitationTokenHashIndex,
                WholesaleDbContext.InvitationReservedTenantIndex]);

        var linkedIndex = distributors.GetIndexes()
            .Single(index => index.GetDatabaseName() == WholesaleDbContext.DistributorTenantIndex);
        Assert.Equal("distributor_tenant_id IS NOT NULL", linkedIndex.GetFilter());

        var priceListForeignKey = distributors.GetForeignKeys().Single(key => key.PrincipalEntityType == priceLists);
        Assert.Equal(DeleteBehavior.Restrict, priceListForeignKey.DeleteBehavior);
        Assert.Equal(WholesaleDbContext.DistributorPriceListForeignKey, priceListForeignKey.GetConstraintName());
    }

    [Fact]
    public void TheTokenHashIsTheOnlyTokenColumn()
    {
        using var context = new WholesaleDbContextFactory().CreateDbContext([]);

        var tokenColumns = context.Model.GetEntityTypes()
            .Where(entity => entity.GetSchema() == WholesaleDbContext.Schema)
            .SelectMany(entity => entity.GetProperties())
            .Select(property => property.GetColumnName())
            .Where(column => column.Contains("token", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(["token_hash"], tokenColumns);
    }

    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new WholesaleDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
