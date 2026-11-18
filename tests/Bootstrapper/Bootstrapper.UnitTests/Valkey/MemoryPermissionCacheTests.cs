using Bootstrapper.Valkey.Permissions;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// La caché de permisos en memoria del pod, la que rige con <c>Valkey:Areas:Permissions</c> apagado: la misma versión de
/// acceso por tenant que la de Valkey, en un contador del proceso, y la misma vida de 60 s por entrada.
/// </summary>
public sealed class MemoryPermissionCacheTests
{
    private static readonly Guid User = Guid.CreateVersion7();
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly string[] Read = ["catalog.product.read"];

    [Fact]
    public void AStoredEntryIsServedWithTheVersionItWasBuiltWith()
    {
        using var cache = new MemoryPermissionCache(new ManualTimeProvider());
        var version = cache.CurrentVersion(Tenant);

        cache.Set(User, Tenant, version, Read);

        Assert.True(cache.TryGet(User, Tenant, out var permissions));
        Assert.Equal(Read, permissions);
    }

    [Fact]
    public void ANullResolutionIsCachedToo()
    {
        using var cache = new MemoryPermissionCache(new ManualTimeProvider());

        cache.Set(User, Tenant, cache.CurrentVersion(Tenant), null);

        Assert.True(cache.TryGet(User, Tenant, out var permissions));
        Assert.Null(permissions);
    }

    [Fact]
    public void ABumpDiscardsEveryEntryOfTheTenantAndOnlyThatTenant()
    {
        using var cache = new MemoryPermissionCache(new ManualTimeProvider());
        var otherTenant = Guid.CreateVersion7();
        cache.Set(User, Tenant, cache.CurrentVersion(Tenant), Read);
        cache.Set(User, otherTenant, cache.CurrentVersion(otherTenant), Read);

        cache.Bump(Tenant);

        Assert.False(cache.TryGet(User, Tenant, out _));
        Assert.True(cache.TryGet(User, otherTenant, out _));
    }

    // La carrera que cubre el segundo bump: la entrada que un request construye con la versión del medio no sobrevive.
    [Fact]
    public void AnEntryBuiltWithAnOlderVersionIsNotServed()
    {
        using var cache = new MemoryPermissionCache(new ManualTimeProvider());
        var before = cache.CurrentVersion(Tenant);

        cache.Bump(Tenant);
        cache.Set(User, Tenant, before, Read);

        Assert.False(cache.TryGet(User, Tenant, out _));
    }

    [Fact]
    public void AnEntryExpiresAfterSixtySeconds()
    {
        var time = new ManualTimeProvider();
        using var cache = new MemoryPermissionCache(time);
        cache.Set(User, Tenant, cache.CurrentVersion(Tenant), Read);

        time.Advance(MemoryPermissionCache.EntryTtl - TimeSpan.FromSeconds(1));
        Assert.True(cache.TryGet(User, Tenant, out _));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.TryGet(User, Tenant, out _));
    }

    // Leer la versión de un tenant que nunca subió no la registra: si no, cada id de tenant probado (también uno al azar)
    // dejaría una entrada para siempre en el contador del proceso, que no tiene tope ni vencimiento.
    [Fact]
    public void ReadingAnUnknownTenantLeavesNoVersionEntry()
    {
        using var cache = new MemoryPermissionCache(new ManualTimeProvider());

        for (var i = 0; i < 100; i++)
        {
            var tenant = Guid.NewGuid();
            Assert.Equal(0L, cache.CurrentVersion(tenant));
            Assert.False(cache.TryGet(User, tenant, out _));
            cache.Set(User, tenant, cache.CurrentVersion(tenant), Read);
            Assert.True(cache.TryGet(User, tenant, out _));
        }

        Assert.Equal(0, cache.TrackedTenants);

        cache.Bump(Tenant);
        Assert.Equal((1L, 1), (cache.CurrentVersion(Tenant), cache.TrackedTenants));
    }

    [Fact]
    public async Task TheAccessVersionBumpsTheSharedCounter()
    {
        using var cache = new MemoryPermissionCache(new ManualTimeProvider());
        var version = new MemoryTenantAccessVersion(cache);
        var before = cache.CurrentVersion(Tenant);

        await version.BumpAsync(Tenant, CancellationToken.None);

        Assert.Equal(before + 1, cache.CurrentVersion(Tenant));
    }
}
