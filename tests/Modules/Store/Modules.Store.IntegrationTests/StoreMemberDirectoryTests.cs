using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Store.Application;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>El puerto de membresías de la tienda sobre Tenancy: quién está activo y quién es el owner. Necesitan Docker.</summary>
public sealed class StoreMemberDirectoryTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    [Fact]
    public async Task AnActiveMembershipOfTheTenantIsActive()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        await using var __ = factory;
        var member = await SeedMemberAsync(factory, "ana@test.co", "Ana", "advisor");

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IStoreMemberDirectory>();

        Assert.True(await directory.IsActiveMemberAsync(Tenant, member, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARemovedMembershipIsNotActive()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        await using var __ = factory;
        var member = await SeedMemberAsync(factory, "ana@test.co", "Ana", "advisor", removed: true);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IStoreMemberDirectory>();

        Assert.False(await directory.IsActiveMemberAsync(Tenant, member, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMembershipOfAnotherTenantIsNotActive()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        await using var __ = factory;
        await SeedTenantAsync(factory, OtherTenantId);
        var member = await SeedMemberAsync(factory, "otro@test.co", "Otro", "advisor", tenantId: OtherTenantId);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IStoreMemberDirectory>();

        Assert.False(await directory.IsActiveMemberAsync(Tenant, member, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheOwnerMembershipIsTheTenantsOwner()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        await using var __ = factory;

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IStoreMemberDirectory>();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var expected = (await tenancy.Tenants.AsNoTracking().SingleAsync(
            tenant => tenant.Id == new Modules.Tenancy.Domain.TenantId(Tenant), TestContext.Current.CancellationToken))
            .OwnerMembershipId.Value;

        Assert.Equal(expected, await directory.FindOwnerMembershipIdAsync(Tenant, TestContext.Current.CancellationToken));
    }
}
