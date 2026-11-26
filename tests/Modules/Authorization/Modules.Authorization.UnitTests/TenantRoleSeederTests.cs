using BuildingBlocks.Application;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;

namespace Modules.Authorization.UnitTests;

public sealed class TenantRoleSeederTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly RoleTemplate Sales = new(
        "ventas", "Ventas", "Vende.", ["catalog.product.read", "customers.customer.read"]);

    private static readonly RoleTemplate Warehouse = new(
        "bodega", "Bodega", "Despacha.", ["catalog.product.read"]);

    private sealed class Store : ITenantRoleSeedStore
    {
        public HashSet<Guid> Seeded { get; } = [];

        public List<string> ExistingKeys { get; } = [];

        public List<(Guid TenantId, IReadOnlyCollection<Role> Roles, DateTimeOffset SeededAt)> Saves { get; } = [];

        public bool SaveResult { get; init; } = true;

        public (Guid TenantId, Exception Error)? Failure { get; init; }

        public Task<bool> IsSeededAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Seeded.Contains(tenantId));

        public Task<IReadOnlySet<Guid>> SeededTenantIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>>(Seeded.ToHashSet());

        public Task<IReadOnlyCollection<string>> RoleKeysAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<string>>(ExistingKeys.ToArray());

        public Task<bool> TrySaveAsync(
            Guid tenantId,
            IReadOnlyCollection<Role> roles,
            DateTimeOffset seededAt,
            CancellationToken cancellationToken)
        {
            if (Failure is { } failure && failure.TenantId == tenantId)
            {
                return Task.FromException<bool>(failure.Error);
            }

            Saves.Add((tenantId, roles, seededAt));
            return Task.FromResult(SaveResult);
        }
    }

    private sealed class Tenants(params Guid[] ids) : IRoleSeedTenantSource
    {
        public Task<IReadOnlyCollection<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<Guid>>(ids);
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static TenantRoleSeeder Seeder(Store store, params Guid[] tenantIds) =>
        new([Sales, Warehouse], store, new Tenants(tenantIds), new StubClock());

    [Fact]
    public async Task AnUnseededTenantGetsEveryTemplateAndTheMarker()
    {
        var store = new Store();

        var seeded = await Seeder(store).SeedAsync(Tenant, TestContext.Current.CancellationToken);

        Assert.True(seeded);
        var save = Assert.Single(store.Saves);
        Assert.Equal(Tenant, save.TenantId);
        Assert.Equal(Now, save.SeededAt);
        Assert.Equal(2, save.Roles.Count);
        foreach (var template in new[] { Sales, Warehouse })
        {
            var role = Assert.Single(save.Roles, role => role.Key == template.Key);
            Assert.Equal(Tenant, role.TenantId);
            Assert.Equal(template.DisplayName, role.DisplayName);
            Assert.Equal(template.Description, role.Description);
            Assert.Equal(
                template.Permissions.Order(StringComparer.Ordinal),
                role.Permissions.Order(StringComparer.Ordinal));
            Assert.Equal(Now, role.CreatedAt);
        }
    }

    [Fact]
    public async Task AnExistingKeyIsNotRecreated()
    {
        var store = new Store();
        store.ExistingKeys.Add(Sales.Key);

        await Seeder(store).SeedAsync(Tenant, TestContext.Current.CancellationToken);

        var save = Assert.Single(store.Saves);
        Assert.Equal(Warehouse.Key, Assert.Single(save.Roles).Key);
    }

    [Fact]
    public async Task ATenantWithEveryKeyStillGetsTheMarker()
    {
        // Sin la marca, un rol que el tenant borre mañana volvería en el siguiente arranque.
        var store = new Store();
        store.ExistingKeys.AddRange([Sales.Key, Warehouse.Key]);

        var seeded = await Seeder(store).SeedAsync(Tenant, TestContext.Current.CancellationToken);

        Assert.True(seeded);
        Assert.Empty(Assert.Single(store.Saves).Roles);
    }

    [Fact]
    public async Task ASeededTenantIsLeftAlone()
    {
        var store = new Store();
        store.Seeded.Add(Tenant);

        var seeded = await Seeder(store).SeedAsync(Tenant, TestContext.Current.CancellationToken);

        Assert.False(seeded);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task ALostRaceCountsAsAlreadySeeded()
    {
        var store = new Store { SaveResult = false };

        var seeded = await Seeder(store).SeedAsync(Tenant, TestContext.Current.CancellationToken);

        Assert.False(seeded);
    }

    [Fact]
    public async Task SeedMissingSeedsOnlyTenantsWithoutMarker()
    {
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var store = new Store();
        store.Seeded.Add(b);

        var count = await Seeder(store, a, b, c).SeedMissingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        Assert.Equal([a, c], store.Saves.Select(save => save.TenantId));
    }

    [Fact]
    public async Task SeedMissingPropagatesAFailure()
    {
        // Al arrancar, atender sin los roles dejaría miembros sin permisos y sin error: la falla tiene que subir.
        var a = Guid.CreateVersion7();
        var failure = new InvalidOperationException("The database is down.");
        var store = new Store { Failure = (a, failure) };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Seeder(store, a).SeedMissingAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
    }
}
