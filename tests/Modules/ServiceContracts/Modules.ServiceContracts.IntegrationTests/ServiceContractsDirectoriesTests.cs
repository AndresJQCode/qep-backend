using Microsoft.Extensions.DependencyInjection;
using Modules.ServiceContracts.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>
/// Los adaptadores de Bootstrapper que cruzan ServiceContracts con Customers, Tenancy, Identity y Authorization (spec §3.3). Se
/// resuelven por el puerto público desde el contenedor real, que es lo que los handlers reciben.
/// </summary>
public sealed class ServiceContractsDirectoriesTests
{
    private static readonly Guid Tenant = Guid.Parse(ServiceContractsApiHarness.TenantId);
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);

    private sealed record SeededMembers(Guid Admin, Guid BillingRole, Guid Advisor, Guid Removed, Guid Unnamed);

    [Fact]
    public async Task FindAsyncReturnsNameEmailActivityAndCanManageOfEachMember()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var members = await SeedMembersAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IServiceContractsMemberDirectory>();
        var found = await directory.FindAsync(
            Tenant,
            [members.Admin, members.Advisor, members.Removed, members.Unnamed, Guid.CreateVersion7()],
            TestContext.Current.CancellationToken);

        // Un id que no existe en el tenant no aparece: el llamador decide qué hace con el faltante.
        Assert.Equal(4, found.Count);
        var admin = Assert.Single(found, member => member.MemberId == members.Admin);
        Assert.Equal(new ServiceContractsMemberRef(members.Admin, "Ana Admin", "ana@example.com", true, true), admin);
        var advisor = Assert.Single(found, member => member.MemberId == members.Advisor);
        Assert.True(advisor.IsActive);
        Assert.False(advisor.CanManage);
        var removed = Assert.Single(found, member => member.MemberId == members.Removed);
        Assert.False(removed.IsActive);
        // Aunque su rol conceda billing.service.manage, una membresía quitada no gestiona.
        Assert.False(removed.CanManage);
        var unnamed = Assert.Single(found, member => member.MemberId == members.Unnamed);
        Assert.Equal("Sin nombre", unnamed.DisplayName);
        Assert.Equal("sin-nombre@example.com", unnamed.Email);
    }

    [Fact]
    public async Task FindAsyncDoesNotReachMembersOfAnotherTenant()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var members = await SeedMembersAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IServiceContractsMemberDirectory>();
        var found = await directory.FindAsync(
            Guid.Parse(ServiceContractsApiHarness.OtherTenantId), [members.Admin], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public async Task ListManagersReturnsOnlyActiveMembersWhoseRolesGrantManage()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var members = await SeedMembersAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IServiceContractsMemberDirectory>();
        var managers = await directory.ListManagersAsync(Tenant, TestContext.Current.CancellationToken);

        // admin y billing sí; advisor no; la quitada tampoco aunque su rol lo conceda.
        Assert.Equal(
            new[] { members.Admin, members.BillingRole, members.Unnamed }.Order(),
            managers.Select(manager => manager.MemberId).Order());
        Assert.All(managers, manager =>
        {
            Assert.True(manager.IsActive);
            Assert.True(manager.CanManage);
        });
    }

    [Fact]
    public async Task CustomerLookupReturnsPhoneE164AndEmail()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var withoutContact = await SeedCustomerAsync(factory, Tenant, "Sin contacto");

        await using var scope = factory.Services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IServiceContractsCustomerLookup>();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(
            new ServiceContractsCustomerRef(customerId, "Panadería La Esquina", "+573001234567", "pan@example.com", true),
            await lookup.FindAsync(Tenant, customerId, ct));
        var many = await lookup.FindManyAsync(Tenant, [customerId, withoutContact, Guid.CreateVersion7()], ct);
        Assert.Equal(2, many.Count);
        var bare = Assert.Single(many, customer => customer.Id == withoutContact);
        Assert.Null(bare.PhoneE164);
        Assert.Null(bare.Email);
        // Otro tenant no ve al cliente.
        Assert.Null(await lookup.FindAsync(Guid.Parse(ServiceContractsApiHarness.OtherTenantId), customerId, ct));
    }

    [Fact]
    public async Task SearchIdsMatchesTheCustomerNameCaseInsensitively()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var match = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina");
        await SeedCustomerAsync(factory, Tenant, "Ferretería Central");
        var otherTenant = await SeedCustomerAsync(factory, Guid.Parse(ServiceContractsApiHarness.OtherTenantId), "Panadería ajena");

        await using var scope = factory.Services.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IServiceContractsCustomerLookup>();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal([match], await lookup.SearchIdsAsync(Tenant, "panader", ct));
        Assert.Equal([match], await lookup.SearchIdsAsync(Tenant, "ESQUINA", ct));
        Assert.Empty(await lookup.SearchIdsAsync(Tenant, "zzz", ct));
        // Un comodín del término es literal, no un patrón: «%» no trae a todos.
        Assert.Empty(await lookup.SearchIdsAsync(Tenant, "%", ct));
        Assert.DoesNotContain(otherTenant, await lookup.SearchIdsAsync(Tenant, "panader", ct));
    }

    private static async Task<SeededMembers> SeedMembersAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var tenant = await tenancy.Tenants.FindAsync([new TenantId(Tenant)], ct);

        Membership Active(string email, string? displayName, string role)
        {
            var user = User.CreateInvited(UserId.New(), email, Now);
            identity.Users.Add(user);
            var membership = Membership.CreateActive(
                MembershipId.New(), user.Id.Value, new TenantId(Tenant), [role], "test", Now);
            if (displayName is not null)
            {
                membership.UpdateProfile(displayName, null, Now);
            }

            tenancy.Memberships.Add(membership);
            return membership;
        }

        var admin = Active("ana@example.com", "Ana Admin", "admin");
        var billing = Active("bea@example.com", "Bea Facturación", "billing");
        var advisor = Active("caro@example.com", "Caro Asesora", "advisor");
        var removed = Active("rita@example.com", "Rita Quitada", "admin");
        var unnamed = Active("sin-nombre@example.com", null, "admin");
        removed.Remove(tenant!, Now);

        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return new SeededMembers(admin.Id.Value, billing.Id.Value, advisor.Id.Value, removed.Id.Value, unnamed.Id.Value);
    }
}
