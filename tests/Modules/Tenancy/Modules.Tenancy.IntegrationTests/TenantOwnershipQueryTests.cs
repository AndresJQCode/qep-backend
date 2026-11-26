using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Tenancy.IntegrationTests.TenancyTestDatabase;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (mayorista): los tenants que un usuario puede vincular como distribuidor son los que administra como
/// owner, activos y por nombre. Una membresía de admin en un tenant ajeno no cuenta, ni un tenant inactivo.
/// </summary>
public sealed class TenantOwnershipQueryTests
{
    [Fact]
    public async Task ListsOnlyActiveTenantsOwnedByTheUserOrderedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartDatabaseAsync();
        using var factory = new OwnershipFactory(database.GetConnectionString());
        var user = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var zeta = TenantId.New();
        var alfa = TenantId.New();
        var foreign = TenantId.New();
        var suspended = TenantId.New();
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var dbContext = seed.ServiceProvider.GetRequiredService<TenancyDbContext>();
            AddOwned(dbContext, zeta, "zeta-norte", "Zeta Norte", user, now);
            AddOwned(dbContext, alfa, "alfa-sur", "Alfa Sur", user, now);

            // Ajeno: el usuario es admin, pero el owner es otro.
            AddOwned(dbContext, foreign, "ajeno", "Ajeno", Guid.CreateVersion7(), now);
            dbContext.Memberships.Add(Membership.CreateActive(
                MembershipId.New(), user, foreign, [Membership.AdminRole], Membership.RegistrationOrigin, now));

            var inactive = AddOwned(dbContext, suspended, "inactivo", "Inactivo", user, now);
            inactive.Suspend(ChangeReason.Nonpayment, now);
            await dbContext.SaveChangesAsync(ct);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var owned = await scope.ServiceProvider.GetRequiredService<ITenantOwnership>()
            .ListOwnedActiveTenantsAsync(user, ct);

        Assert.Equal(
            [new OwnedTenant(alfa.Value, "Alfa Sur"), new OwnedTenant(zeta.Value, "Zeta Norte")],
            owned);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ITenantOwnership>()
            .ListOwnedActiveTenantsAsync(Guid.CreateVersion7(), ct));
    }

    // Spec 2026-11-19 (onboarding por link): se vincula un tenant que el usuario administra, como owner o como miembro
    // activo con el rol de sistema admin. Un rol personalizado o una membresía suspendida no cuentan.

    [Fact]
    public async Task AnAdminMemberSeesTheTenant()
    {
        var user = Guid.CreateVersion7();
        var tenantId = TenantId.New();

        var administered = await ListAdministeredAsync(user, (dbContext, now) =>
        {
            AddOwned(dbContext, tenantId, "ajeno-admin", "Ajeno Admin", Guid.CreateVersion7(), now);
            dbContext.Memberships.Add(Membership.CreateActive(
                MembershipId.New(), user, tenantId, [Membership.AdminRole], Membership.RegistrationOrigin, now));
        });

        Assert.Equal([tenantId.Value], administered);
    }

    [Fact]
    public async Task AMemberWithAnotherRoleDoesNotSeeTheTenant()
    {
        var user = Guid.CreateVersion7();
        var tenantId = TenantId.New();

        var administered = await ListAdministeredAsync(user, (dbContext, now) =>
        {
            AddOwned(dbContext, tenantId, "ajeno-billing", "Ajeno Billing", Guid.CreateVersion7(), now);
            dbContext.Memberships.Add(Membership.CreateActive(
                MembershipId.New(), user, tenantId, ["billing"], Membership.RegistrationOrigin, now));
        });

        Assert.Empty(administered);
    }

    [Fact]
    public async Task AnOwnerSeesTheTenantWithoutTheAdminRole()
    {
        var user = Guid.CreateVersion7();
        var tenantId = TenantId.New();

        var administered = await ListAdministeredAsync(user, (dbContext, now) =>
            AddOwned(dbContext, tenantId, "propio-billing", "Propio Billing", user, now, ["billing"]));

        Assert.Equal([tenantId.Value], administered);
    }

    [Fact]
    public async Task ASuspendedAdminMembershipDoesNotSeeTheTenant()
    {
        var user = Guid.CreateVersion7();
        var tenantId = TenantId.New();

        var administered = await ListAdministeredAsync(user, (dbContext, now) =>
        {
            var tenant = AddOwned(dbContext, tenantId, "ajeno-suspendido", "Ajeno Suspendido", Guid.CreateVersion7(), now);
            var membership = Membership.CreateActive(
                MembershipId.New(), user, tenantId, [Membership.AdminRole], Membership.RegistrationOrigin, now);
            membership.Suspend(tenant, now);
            dbContext.Memberships.Add(membership);
        });

        Assert.Empty(administered);
    }

    [Fact]
    public async Task AnInactiveTenantIsNotListed()
    {
        var user = Guid.CreateVersion7();
        var owned = TenantId.New();
        var administeredTenant = TenantId.New();

        var administered = await ListAdministeredAsync(user, (dbContext, now) =>
        {
            AddOwned(dbContext, owned, "propio-inactivo", "Propio Inactivo", user, now)
                .Suspend(ChangeReason.Nonpayment, now);
            var other = AddOwned(dbContext, administeredTenant, "ajeno-inactivo", "Ajeno Inactivo", Guid.CreateVersion7(), now);
            other.Suspend(ChangeReason.Nonpayment, now);
            dbContext.Memberships.Add(Membership.CreateActive(
                MembershipId.New(), user, administeredTenant, [Membership.AdminRole], Membership.RegistrationOrigin, now));
        });

        Assert.Empty(administered);
    }

    private static async Task<IReadOnlyList<Guid>> ListAdministeredAsync(
        Guid user, Action<TenancyDbContext, DateTimeOffset> seed)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartDatabaseAsync();
        using var factory = new OwnershipFactory(database.GetConnectionString());
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            seed(dbContext, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(ct);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var administered = await scope.ServiceProvider.GetRequiredService<ITenantOwnership>()
            .ListAdministeredActiveTenantsAsync(user, ct);
        return [.. administered.Select(tenant => tenant.TenantId)];
    }

    private static Tenant AddOwned(
        TenancyDbContext dbContext,
        TenantId tenantId,
        string slug,
        string name,
        Guid owner,
        DateTimeOffset now,
        IReadOnlyList<string>? ownerRoles = null)
    {
        var membershipId = MembershipId.New();
        var tenant = Tenant.Create(tenantId, slug, name, "es-CO", "America/Bogota", "yyyy-MM-dd", membershipId, now);
        dbContext.Tenants.Add(tenant);
        dbContext.Memberships.Add(Membership.CreateActive(
            membershipId, owner, tenantId, ownerRoles ?? [Membership.AdminRole], Membership.RegistrationOrigin, now));
        return tenant;
    }

    private sealed class OwnershipFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Pinned, never inherited: the developer's user-secrets may turn the seed on.
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
