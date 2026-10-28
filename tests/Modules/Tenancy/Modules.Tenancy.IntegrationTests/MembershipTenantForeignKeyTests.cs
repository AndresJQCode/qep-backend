using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// <c>tenancy.memberships.tenant_id</c> referencia a <c>tenancy.tenants</c> en la base, no sólo
/// en el agregado. Es la mitad del ciclo que sí se puede declarar: la otra
/// (<c>tenants.owner_membership_id</c>) se omite a propósito, ver <c>TenancyDbContext</c>.
/// </summary>
public sealed class MembershipTenantForeignKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMembershipPointingToAMissingTenantIsRejectedByTheDatabase()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var orphan = Membership.CreateActive(
            MembershipId.New(),
            Guid.CreateVersion7(),
            TenantId.New(),
            [Membership.AdminRole],
            Membership.RegistrationOrigin,
            Now);
        dbContext.Memberships.Add(orphan);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            dbContext.SaveChangesAsync(TestContext.Current.CancellationToken));

        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
        Assert.Equal("FK_memberships_tenants_tenant_id", postgres.ConstraintName);
    }

    [Fact]
    public async Task TenantAndItsOwnerMembershipAreInsertedInTheSameSaveChanges()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        // Mismo orden que el registro: la membresía del owner se agrega al contexto antes que el
        // tenant que la nombra. La FK obliga a EF a insertar el tenant primero, y el ciclo lógico
        // (tenant → owner_membership_id, membership → tenant_id) no debe romper el ordenamiento.
        var tenantId = TenantId.New();
        var owner = Membership.CreateActive(
            MembershipId.New(),
            Guid.CreateVersion7(),
            tenantId,
            [Membership.AdminRole],
            Membership.RegistrationOrigin,
            Now);
        dbContext.Memberships.Add(owner);
        dbContext.Tenants.Add(Tenant.Create(
            tenantId,
            $"t-{Guid.NewGuid():N}"[..12],
            "FK Test",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            owner.Id,
            Now));

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await dbContext.Memberships.AsNoTracking()
            .SingleAsync(membership => membership.Id == owner.Id, TestContext.Current.CancellationToken);
        Assert.Equal(tenantId, stored.TenantId);
    }

    private static async Task<ServiceProvider> MigratedServicesAsync(PostgreSqlContainer database)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:QepDatabase"] = database.GetConnectionString() })
            .Build();
        var services = new ServiceCollection();
        services.AddTenancyInfrastructure(configuration);
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }
}
