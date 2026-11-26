using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Tenancy.IntegrationTests.TenancyTestDatabase;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Spec 2026-11-25 (roles por tenant): cada tenant nace con los cinco roles de fábrica como filas suyas, y el arranque
/// siembra los que no tienen la marca sin tocar lo que el tenant ya borró o editó.
/// </summary>
/// <remarks>
/// Las pruebas de arranque no comparten host: necesitan arrancar dos veces sobre la misma base.
/// </remarks>
public sealed class TenantRoleSeedingTests(SharedTenancyHost host) : IClassFixture<SharedTenancyHost>
{
    private static readonly string[] AdvisorRoles = ["advisor"];
    private static readonly string[] BillingRoles = ["billing"];
    private static readonly string[] ReadCustomers = ["customers.customer.read"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASignedUpTenantIsBornWithTheFiveRoles()
    {
        var factory = host.Factory(connectionString => new QepApiFactory(connectionString));
        var tenantId = await RegisterTenantAsync(factory);

        var roles = await ListRolesAsync(factory, tenantId);

        var templates = factory.Services.GetServices<RoleTemplate>().ToArray();
        Assert.Equal(5, templates.Length);
        Assert.Equal(6, roles.Length);
        Assert.True(Assert.Single(roles, role => role.Role == "admin").IsSystem);
        Assert.All(templates, template =>
        {
            var role = Assert.Single(roles, item => item.Role == template.Key);
            Assert.False(role.IsSystem);
            Assert.NotNull(role.Id);
            Assert.Equal("Custom", role.Category);
            Assert.Equal(template.DisplayName, role.DisplayName);
            Assert.Equal(
                template.Permissions.Order(StringComparer.Ordinal),
                role.Permissions.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public async Task ASeededRoleCanBeInvitedRightAfterSignup()
    {
        var factory = host.Factory(connectionString => new QepApiFactory(connectionString));
        var tenantId = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/memberships",
            new { email = $"member-{Guid.NewGuid():N}@example.com", displayName = "Ana Pérez", roles = AdvisorRoles },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task StartupSeedsAnExistingTenantWithoutMarker()
    {
        await using var database = await StartDatabaseAsync();
        Guid tenantId;
        using (var first = new QepApiFactory(database.GetConnectionString()))
        {
            // El primer arranque ya pasó cuando se inserta: el tenant nace sin marca y sin roles.
            tenantId = await InsertTenantAsync(first, ownerUserId: null);
            Assert.Equal(["admin"], (await ListRolesAsync(first, tenantId)).Select(role => role.Role));
        }

        using var second = new QepApiFactory(database.GetConnectionString());

        var roles = await ListRolesAsync(second, tenantId);
        Assert.Equal(
            ["admin", "advisor", "billing", "cashier", "scheduling_manager", "scheduling_provider"],
            roles.Select(role => role.Role).Order(StringComparer.Ordinal));
    }

    // Review Focus 1: nadie pierde ni gana permisos al desplegar.
    [Fact]
    public async Task StartupKeepsTheEffectivePermissionsOfAnExistingMember()
    {
        await using var database = await StartDatabaseAsync();
        var userId = Guid.CreateVersion7();
        Guid tenantId;
        using (var first = new QepApiFactory(database.GetConnectionString()))
        {
            tenantId = await InsertTenantAsync(first, userId);
            // El hueco que el arranque cierra: la membresía nombra un rol que el tenant todavía no tiene.
            Assert.Empty(await ResolvePermissionsAsync(first, userId, tenantId));
        }

        using var second = new QepApiFactory(database.GetConnectionString());

        var resolved = await ResolvePermissionsAsync(second, userId, tenantId);
        // La plantilla es la lista que concedía el rol de sistema: ninguno es operator.* y, con todos los módulos prendidos,
        // nada queda enmascarado.
        var billing = second.Services.GetServices<RoleTemplate>().Single(template => template.Key == RoleTemplateKeys.Billing);
        Assert.Equal(
            billing.Permissions.Order(StringComparer.Ordinal),
            resolved.Order(StringComparer.Ordinal));
    }

    // Review Focus 2: con la marca puesta, un reinicio no revive lo borrado ni pisa lo editado.
    [Fact]
    public async Task StartupDoesNotReviveADeletedRoleNorOverwriteAnEditedOne()
    {
        await using var database = await StartDatabaseAsync();
        Guid tenantId;
        using (var first = new QepApiFactory(database.GetConnectionString()))
        {
            tenantId = await RegisterTenantAsync(first);
            var roles = await ListRolesAsync(first, tenantId);
            using var client = CreateClient(
                first,
                Guid.CreateVersion7(),
                tenantId,
                TenancyPermissions.AdvisorshipRead,
                TenancyPermissions.AdvisorshipRolesManage);
            var cashier = roles.Single(role => role.Role == "cashier");
            var deleted = await client.DeleteAsync(
                $"/api/v1/tenants/{tenantId}/authorization/roles/{cashier.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            var advisor = roles.Single(role => role.Role == "advisor");
            using var update = new HttpRequestMessage(
                HttpMethod.Put, $"/api/v1/tenants/{tenantId}/authorization/roles/{advisor.Id}")
            {
                Content = JsonContent.Create(new
                {
                    displayName = advisor.DisplayName,
                    description = string.Empty,
                    permissions = ReadCustomers,
                }),
            };
            update.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{advisor.Version}\""));
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(update, Ct)).StatusCode);
        }

        using var second = new QepApiFactory(database.GetConnectionString());

        var after = await ListRolesAsync(second, tenantId);
        Assert.DoesNotContain(after, role => role.Role == "cashier");
        Assert.Equal(ReadCustomers, after.Single(role => role.Role == "advisor").Permissions);
    }

    // ---- helpers ----

    private static async Task<Guid> RegisterTenantAsync(QepApiFactory factory)
    {
        using var bootstrap = CreateClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", $"owner-{Guid.NewGuid():N}@example.com");
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Acme Organization",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisteredPayload>(Ct);
        return registered!.TenantId;
    }

    /// <summary>Un tenant escrito directo en la base, sin pasar por el alta, con todos los módulos prendidos. Con
    /// <paramref name="ownerUserId"/>, además una membresía activa <c>billing</c> de esa persona.</summary>
    private static async Task<Guid> InsertTenantAsync(QepApiFactory factory, Guid? ownerUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenantId = TenantId.New();
        var membershipId = MembershipId.New();
        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(Tenant.Create(
            tenantId,
            $"t-{Guid.NewGuid():N}",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            membershipId,
            now));
        foreach (var key in TenantModuleKeys.All)
        {
            dbContext.Add(TenantModule.Create(tenantId, key, TenantModuleSources.Seed, now, note: null));
        }

        if (ownerUserId is { } userId)
        {
            dbContext.Memberships.Add(
                Membership.CreateActive(membershipId, userId, tenantId, BillingRoles, "test", now));
        }

        await dbContext.SaveChangesAsync(Ct);
        return tenantId.Value;
    }

    private static async Task<IReadOnlyCollection<string>> ResolvePermissionsAsync(
        QepApiFactory factory, Guid userId, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var permissions = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .ResolvePermissionsAsync(userId, tenantId, Ct);
        // null sería «sin membresía activa»: otra causa, que la prueba no puede confundir con «el rol no concede nada».
        Assert.NotNull(permissions);
        return permissions;
    }

    private static async Task<RolePayload[]> ListRolesAsync(QepApiFactory factory, Guid tenantId)
    {
        using var client = CreateClient(factory, Guid.CreateVersion7(), tenantId, TenancyPermissions.AdvisorshipRead);
        var roles = await client.GetFromJsonAsync<RolePayload[]>(
            $"/api/v1/tenants/{tenantId}/authorization/roles", Ct);
        Assert.NotNull(roles);
        return roles;
    }

    private static HttpClient CreateClient(
        QepApiFactory factory,
        Guid subjectId,
        Guid tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private sealed record RegisteredPayload(Guid TenantId, Guid OwnerUserId);

    private sealed record RolePayload(
        Guid? Id,
        string Role,
        string DisplayName,
        string Category,
        IReadOnlyCollection<string> Permissions,
        bool IsSystem,
        long Version);

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
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
