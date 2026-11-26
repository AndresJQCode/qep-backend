using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Qué membresías retienen un rol ante su borrado (spec 2026-11-25, roles por tenant), contra PostgreSQL: la consulta
/// mira la colección primitiva de roles y sólo la base dice si se traduce.
/// </summary>
public sealed class MembershipRoleUsageTests(SharedTenancyHost host) : IClassFixture<SharedTenancyHost>
{
    private const string Role = "jefa-bodega";
    private static readonly string[] ReadProducts = ["catalog.product.read"];

    // Uno por prueba (xUnit crea una instancia por prueba): las pruebas de la clase comparten el host.
    private readonly TenantId _tenantId = new(Guid.CreateVersion7());

    [Theory]
    [InlineData(MembershipState.Invited)]
    [InlineData(MembershipState.Active)]
    [InlineData(MembershipState.Suspended)]
    public async Task AMembershipHoldsTheRoleInTheseStates(MembershipState state)
    {
        var factory = Factory();
        var tenant = await SeedTenantAsync(factory, _tenantId);
        await AddMembershipAsync(factory, tenant, state);

        Assert.True(await AnyHoldsRoleAsync(factory));
    }

    [Theory]
    [InlineData(MembershipState.Removed)]
    [InlineData(MembershipState.Expired)]
    public async Task RemovedAndExpiredDoNotHoldIt(MembershipState state)
    {
        // Al reinvitar, `Membership.Reinvite` reemplaza los roles: no quedan nombrando nada.
        var factory = Factory();
        var tenant = await SeedTenantAsync(factory, _tenantId);
        await AddMembershipAsync(factory, tenant, state);

        Assert.False(await AnyHoldsRoleAsync(factory));
    }

    [Fact]
    public async Task ALapsedInvitationStillHoldsIt()
    {
        // El vencimiento es perezoso: sigue en `Invited` y el administrador todavía le cambia los roles.
        var factory = Factory();
        var tenant = await SeedTenantAsync(factory, _tenantId);
        await AddMembershipAsync(factory, tenant, MembershipState.Invited, DateTimeOffset.UtcNow.AddDays(-10));

        Assert.True(await AnyHoldsRoleAsync(factory));
    }

    [Fact]
    public async Task AnotherTenantsMembershipDoesNotCount()
    {
        var factory = Factory();
        await SeedTenantAsync(factory, _tenantId);
        var other = await SeedTenantAsync(factory, new TenantId(Guid.CreateVersion7()));
        await AddMembershipAsync(factory, other, MembershipState.Active);

        Assert.False(await AnyHoldsRoleAsync(factory));
    }

    [Fact]
    public async Task DeletingARoleHeldByASuspendedMemberIs422InUse()
    {
        var factory = Factory();
        var tenant = await SeedTenantAsync(factory, _tenantId);
        using var client = Manager(factory);
        var rolesUrl = $"/api/v1/tenants/{_tenantId.Value}/authorization/roles";
        var created = await client.PostAsJsonAsync(
            rolesUrl,
            new { key = Role, displayName = "Jefa de bodega", description = string.Empty, permissions = ReadProducts },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = await created.Content.ReadFromJsonAsync<RolePayload>(TestContext.Current.CancellationToken);
        await AddMembershipAsync(factory, tenant, MembershipState.Suspended);

        var response = await client.DeleteAsync($"{rolesUrl}/{role!.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("authorization.role.in_use", body, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private QepApiFactory Factory() => host.Factory(connectionString => new QepApiFactory(connectionString));

    private async Task<bool> AnyHoldsRoleAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMembershipRoleUsage>()
            .AnyHoldsRoleAsync(_tenantId, Role, TestContext.Current.CancellationToken);
    }

    /// <summary>Como <c>MembershipApiTests</c>: el owner nunca se persiste como membresía y el slug es único, porque las
    /// pruebas de la clase comparten la base.</summary>
    private static async Task<Tenant> SeedTenantAsync(QepApiFactory factory, TenantId tenantId)
    {
        var tenant = Tenant.Create(
            tenantId,
            $"t-{Guid.NewGuid():N}",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            MembershipId.New(),
            DateTimeOffset.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tenant;
    }

    /// <summary>Una membresía con <see cref="Role"/>, llevada al estado por las transiciones del agregado.</summary>
    private static async Task AddMembershipAsync(
        QepApiFactory factory,
        Tenant tenant,
        MembershipState state,
        DateTimeOffset? invitedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        Membership membership;
        if (state is MembershipState.Invited or MembershipState.Expired)
        {
            // Para vencerla, la invitación tiene que haber nacido antes de su ventana.
            var at = invitedAt ?? (state == MembershipState.Expired ? now.AddDays(-10) : now);
            membership = Membership.Invite(
                MembershipId.New(),
                Guid.CreateVersion7(),
                tenant.Id,
                "Ana Pérez",
                [Role],
                "invitation",
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
                at,
                Membership.DefaultInvitationTimeToLive);
            if (state == MembershipState.Expired)
            {
                Assert.True(membership.Expire(now));
            }
        }
        else
        {
            membership = Membership.CreateActive(
                MembershipId.New(), Guid.CreateVersion7(), tenant.Id, [Role], "invitation", now);
            if (state == MembershipState.Suspended)
            {
                membership.Suspend(tenant, now);
            }
            else if (state == MembershipState.Removed)
            {
                membership.Remove(tenant, now);
            }
        }

        Assert.Equal(state, membership.State);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbContext.Memberships.Add(membership);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private HttpClient Manager(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantId.Value.ToString());
        client.DefaultRequestHeaders.Add(
            "X-Permissions",
            string.Join(',', TenancyPermissions.AdvisorshipRead, TenancyPermissions.AdvisorshipRolesManage));
        return client;
    }

    private sealed record RolePayload(Guid? Id);

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
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
