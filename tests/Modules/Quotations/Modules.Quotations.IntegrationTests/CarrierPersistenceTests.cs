using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La transportadora ida y vuelta por el DbContext (spec 2026-10-17): sus nombres son una colección
/// propia con clave (carrier_id, normalized_name), y editar sin cambiar un nombre no puede chocar
/// consigo mismo.
/// </summary>
public sealed class CarrierPersistenceTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-00000000e001");
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EditingACarrierKeepingItsNameAndChangingOneAliasRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var setup = NewContext(connectionString))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var id = CarrierId.New();
        await using (var create = NewContext(connectionString))
        {
            create.Carriers.Add(Carrier.Create(id, TenantId, "Servientrega", ["Servi", "SVE"], true, 1, Now));
            await new QuotationsUnitOfWork(create).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var edit = NewContext(connectionString))
        {
            var carrier = await edit.Carriers.SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);
            carrier.Update("Servientrega", ["SERVI", "SVE Express"], true, Now.AddMinutes(1));
            await new QuotationsUnitOfWork(edit).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = NewContext(connectionString);
        var reloaded = await read.Carriers.SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);
        Assert.Equal(2, reloaded.Version);
        Assert.Equal(
            ["SERVI", "SVE Express"],
            reloaded.ExternalNames.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["servi", "servientrega", "sve express"],
            reloaded.Names.Select(n => n.NormalizedName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACollidingNameInTheSameTenantIsCarrierNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var setup = NewContext(connectionString))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            setup.Carriers.Add(Carrier.Create(CarrierId.New(), TenantId, "Servientrega", [], false, 1, Now));
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var second = NewContext(connectionString);
        second.Carriers.Add(Carrier.Create(CarrierId.New(), TenantId, "Envíos", ["SERVIENTREGA"], false, 2, Now));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            new QuotationsUnitOfWork(second).SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("quotations.carrier.name_taken", error.Code);
    }

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);
}
