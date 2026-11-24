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

        var distributors = model.FindEntityType(typeof(Distributor))!;
        var invitations = model.FindEntityType(typeof(DistributorInvitation))!;
        Assert.Equal(("wholesale", "distributors"), (distributors.GetSchema(), distributors.GetTableName()));
        Assert.Equal(("wholesale", "distributor_invitations"), (invitations.GetSchema(), invitations.GetTableName()));

        var unique = model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes())
            .Where(index => index.IsUnique)
            .Select(index => index.GetDatabaseName())
            .ToHashSet();
        Assert.Contains(WholesaleDbContext.DistributorIdentificationIndex, unique);
        Assert.Contains(WholesaleDbContext.DistributorTenantIndex, unique);
        Assert.Contains(WholesaleDbContext.InvitationTokenHashIndex, unique);
        Assert.Contains(WholesaleDbContext.InvitationReservedTenantIndex, unique);
        Assert.Equal(
            ["IX_distributors_tenant_identification", "IX_distributors_tenant_distributor_tenant",
                "IX_distributor_invitations_token_hash", "IX_distributor_invitations_reserved_tenant"],
            [WholesaleDbContext.DistributorIdentificationIndex,
                WholesaleDbContext.DistributorTenantIndex, WholesaleDbContext.InvitationTokenHashIndex,
                WholesaleDbContext.InvitationReservedTenantIndex]);

        var linkedIndex = distributors.GetIndexes()
            .Single(index => index.GetDatabaseName() == WholesaleDbContext.DistributorTenantIndex);
        Assert.Equal("distributor_tenant_id IS NOT NULL", linkedIndex.GetFilter());

    }

    // Spec 2026-11-22 (D-8): la lista vive en catalog.price_lists, otro esquema. El distribuidor guarda el id sin FK, y la tabla de
    // listas ya no está en el modelo de Mayorista.
    [Fact]
    public void TheDistributorPriceListIsASoftReference()
    {
        using var context = new WholesaleDbContextFactory().CreateDbContext([]);
        var distributors = context.Model.FindEntityType(typeof(Distributor))!;

        Assert.Empty(distributors.GetForeignKeys());
        Assert.Equal("price_list_id", distributors.FindProperty(nameof(Distributor.PriceListId))!.GetColumnName());
        Assert.DoesNotContain(context.Model.GetEntityTypes(), entity => entity.GetTableName() == "price_lists");
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
    public void TheDistributorCountryAndTypeAreRequiredAndTheIndexCoversTheType()
    {
        using var context = new WholesaleDbContextFactory().CreateDbContext([]);
        var distributors = context.Model.FindEntityType(typeof(Distributor))!;

        var country = distributors.FindProperty(nameof(Distributor.Country))!;
        Assert.Equal(("country", (int?)2, false), (country.GetColumnName(), country.GetMaxLength(), country.IsNullable));
        var type = distributors.FindProperty(nameof(Distributor.IdentificationType))!;
        Assert.Equal(("identification_type", (int?)16, false), (type.GetColumnName(), type.GetMaxLength(), type.IsNullable));

        var index = distributors.GetIndexes()
            .Single(item => item.GetDatabaseName() == WholesaleDbContext.DistributorIdentificationIndex);
        Assert.Equal(
            [nameof(Distributor.TenantId), nameof(Distributor.IdentificationType), nameof(Distributor.IdentificationNumber)],
            index.Properties.Select(property => property.Name));
    }

    // Spec 2026-11-18 (aviso por WhatsApp): el destino del aviso en el distribuidor y su estado en la invitación, todo
    // nullable salvo la generación, que arranca en 0 para las filas existentes.
    [Fact]
    public void TheInvitationNoticeColumnsHaveTheirTypesAndLengths()
    {
        using var context = new WholesaleDbContextFactory().CreateDbContext([]);
        var distributors = context.Model.FindEntityType(typeof(Distributor))!;
        var invitations = context.Model.FindEntityType(typeof(DistributorInvitation))!;

        var phone = distributors.FindProperty(nameof(Distributor.Phone))!;
        Assert.Equal(("phone", (int?)16, true), (phone.GetColumnName(), phone.GetMaxLength(), phone.IsNullable));
        Assert.Equal(Distributor.PhoneMaxLength, phone.GetMaxLength());

        var connection = distributors.FindProperty(nameof(Distributor.WhatsAppConnectionId))!;
        Assert.Equal(("whatsapp_connection_id", typeof(Guid?), true),
            (connection.GetColumnName(), connection.ClrType, connection.IsNullable));

        var generation = invitations.FindProperty(nameof(DistributorInvitation.Generation))!;
        Assert.Equal(("generation", typeof(int), false, (object?)0),
            (generation.GetColumnName(), generation.ClrType, generation.IsNullable, generation.GetDefaultValue()));
        // D-4: el resultado de un aviso leído antes de un cambio de generación no se escribe sobre la nueva.
        Assert.True(generation.IsConcurrencyToken);

        var status = invitations.FindProperty(nameof(DistributorInvitation.NoticeStatus))!;
        Assert.Equal(("notice_status", (int?)16, true, typeof(string)),
            (status.GetColumnName(), status.GetMaxLength(), status.IsNullable, status.GetProviderClrType()));

        var failure = invitations.FindProperty(nameof(DistributorInvitation.NoticeFailureCode))!;
        Assert.Equal(("notice_failure_code", (int?)128, true),
            (failure.GetColumnName(), failure.GetMaxLength(), failure.IsNullable));
        Assert.Equal(DistributorInvitation.NoticeFailureCodeMaxLength, failure.GetMaxLength());
    }

    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new WholesaleDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
