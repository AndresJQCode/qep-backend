using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>Spec 2026-10-08 §3: el lado de escritura de la consola contra Postgres de verdad.</summary>
public sealed class TenantChangePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListByTenantReturnsInactiveRowsAndTracksThem()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);

        await using (var scope = provider.CreateAsyncScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<ITenantModuleRepository>()
                .ListByTenantAsync(tenantId, TestContext.Current.CancellationToken);
            rows.Single(row => row.ModuleKey == TenantModuleKeys.Reporting).Deactivate(Now);
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = provider.CreateAsyncScope();
        var stored = await read.ServiceProvider.GetRequiredService<ITenantModuleRepository>()
            .ListByTenantAsync(tenantId, TestContext.Current.CancellationToken);
        Assert.Equal(6, stored.Count);
        Assert.Equal(TenantModuleStatus.Inactive, stored.Single(row => row.ModuleKey == TenantModuleKeys.Reporting).Status);
    }

    [Fact]
    public async Task BothKindsOfChangeRoundTripThroughTheTable()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        var batchId = Guid.CreateVersion7();

        await using (var scope = provider.CreateAsyncScope())
        {
            var changes = scope.ServiceProvider.GetRequiredService<ITenantChangeRepository>();
            changes.Add(TenantChange.ForModule(tenantId, batchId, TenantModuleKeys.Pos, null, TenantModuleStatus.Active,
                ChangeReason.Courtesy, "prueba", Guid.CreateVersion7(), Now));
            changes.Add(TenantChange.ForTenantStatus(tenantId, Guid.CreateVersion7(), TenantStatus.Active, TenantStatus.Suspended,
                ChangeReason.Nonpayment, null, Guid.CreateVersion7(), Now));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = provider.CreateAsyncScope();
        var rows = await read.ServiceProvider.GetRequiredService<TenancyDbContext>().TenantChanges.AsNoTracking()
            .Where(change => change.TenantId == tenantId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var module = rows.Single(row => row.Kind == TenantChangeKind.Module);
        Assert.Same(TenantModuleKeys.Pos, module.ModuleKey);
        Assert.Null(module.FromStatus);
        Assert.Equal("active", module.ToStatus);
        Assert.Equal(ChangeReason.Courtesy, module.Reason);
        var status = rows.Single(row => row.Kind == TenantChangeKind.TenantStatus);
        Assert.Null(status.ModuleKey);
        Assert.Equal("Suspended", status.ToStatus);
    }

    // §3: el candado serializa dos operaciones sobre el mismo tenant (Review Focus 3).
    [Fact]
    public async Task TheLockSerializesTwoScopesOnTheSameTenant()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var holder = await first.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>()
            .BeginTenantChangeScopeAsync(tenantId, TestContext.Current.CancellationToken);
        var waiting = second.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>()
            .BeginTenantChangeScopeAsync(tenantId, TestContext.Current.CancellationToken);

        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)));
        await holder.CommitAsync(TestContext.Current.CancellationToken);
        await holder.DisposeAsync();
        await using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheLockDoesNotBlockAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var one = await SeedTenantAsync(provider);
        var other = await SeedTenantAsync(provider);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        await using var holder = await first.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>()
            .BeginTenantChangeScopeAsync(one, TestContext.Current.CancellationToken);
        await using var free = await second.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>()
            .BeginTenantChangeScopeAsync(other, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // §3: dos activaciones simultáneas de una clave sin fila; sin la traducción sería un 500.
    [Fact]
    public async Task ADuplicateModuleRowIsNoChangesNotA500()
    {
        await using var database = await StartDatabaseAsync();
        await using var provider = await MigratedServicesAsync(database);
        var tenantId = await SeedTenantAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantModuleRepository>()
                .Add(TenantModule.Create(tenantId, TenantModuleKeys.Pos, TenantModuleSources.Operator, Now, null));
            await scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var late = provider.CreateAsyncScope();
        late.ServiceProvider.GetRequiredService<ITenantModuleRepository>()
            .Add(TenantModule.Create(tenantId, TenantModuleKeys.Pos, TenantModuleSources.Operator, Now, null));
        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            late.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.modules.no_changes", error.Code);
    }

    private static async Task<TenantId> SeedTenantAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = Tenant.Create(TenantId.New(), $"t-{Guid.NewGuid():N}"[..12], "Persistence Test", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now);
        dbContext.Tenants.Add(tenant);
        foreach (var key in TenantModuleKeys.DefaultForNewTenants)
        {
            dbContext.TenantModules.Add(TenantModule.Create(tenant.Id, key, TenantModuleSources.Signup, Now, null));
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tenant.Id;
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
