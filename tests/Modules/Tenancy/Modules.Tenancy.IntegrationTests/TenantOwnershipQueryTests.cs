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

    private static Tenant AddOwned(
        TenancyDbContext dbContext, TenantId tenantId, string slug, string name, Guid owner, DateTimeOffset now)
    {
        var membershipId = MembershipId.New();
        var tenant = Tenant.Create(tenantId, slug, name, "es-CO", "America/Bogota", "yyyy-MM-dd", membershipId, now);
        dbContext.Tenants.Add(tenant);
        dbContext.Memberships.Add(Membership.CreateActive(
            membershipId, owner, tenantId, [Membership.AdminRole], Membership.RegistrationOrigin, now));
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
