using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Application;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 D9 y §5.3: los tres adaptadores de Bootstrapper contra las tablas reales de Messaging, Customers y
/// Tenancy. Nada cruza tenants.</summary>
public sealed class PipelineDirectoriesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConversationsAreReadFromMessagingWithoutCrossingTenants()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var (customerId, _) = await SeedCustomerAsync(factory, tenantId, "Laura");
        var now = DateTimeOffset.UtcNow;
        var open = await SeedConversationAsync(factory, connectionString, tenantId, customerId, now.AddDays(-2), now.AddDays(-2));
        await SeedConversationAsync(factory, connectionString, tenantId, customerId, now.AddHours(-1), now.AddMinutes(-5));
        await SeedConversationAsync(factory, connectionString, tenantId, customerId);
        var resolved = await SeedConversationAsync(factory, connectionString, tenantId, customerId, now.AddDays(-9), now.AddMinutes(-1), resolved: true);
        await SeedConversationAsync(factory, connectionString, tenantId, customerId: null, now, now);
        var foreign = await SeedConversationAsync(factory, connectionString, Guid.CreateVersion7(), customerId, now, now);

        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IPipelineConversationDirectory>();
        var activity = await directory.FindActivityAsync(tenantId, [customerId], Ct);

        var customer = activity[customerId];
        Assert.Equal(4, customer.ConversationIds.Count);
        Assert.DoesNotContain(foreign, customer.ConversationIds);
        Assert.Equal(now.AddMinutes(-1).ToUnixTimeSeconds(), customer.LastMessageAt!.Value.ToUnixTimeSeconds());
        Assert.True((await directory.FindAsync(tenantId, open, Ct))!.IsOpen);
        Assert.False((await directory.FindAsync(tenantId, resolved, Ct))!.IsOpen);
        Assert.Null(await directory.FindAsync(tenantId, foreign, Ct));
    }

    [Fact]
    public async Task CustomersAreReadFromCustomersWithTheirCompleteness()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var (customerId, name) = await SeedCustomerAsync(factory, tenantId, "Laura");

        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IPipelineCustomerDirectory>();
        var found = await directory.FindAsync(tenantId, [customerId], Ct);

        Assert.Equal(new PipelineCustomerRef(customerId, name, false), found[customerId]);
        Assert.Empty(await directory.FindAsync(Guid.CreateVersion7(), [customerId], Ct));
    }

    // Spec 2026-10-24 §4 «lo que llega tarde»: el adaptador lee merged_into_id por el puerto de Customers. Un id de otro tenant es
    // «no se fusionó»: el negocio se abre a nombre del id que llegó, como antes de la fusión.
    [Fact]
    public async Task ResolveSurvivorReturnsTheSurvivorOrTheSameId()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var (survivor, _) = await SeedCustomerAsync(factory, tenantId, "Laura");
        var (merged, _) = await SeedCustomerAsync(factory, tenantId, "Laura G");
        await MarkMergedAsync(connectionString, merged, survivor);

        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IPipelineCustomerDirectory>();

        Assert.Equal(survivor, await directory.ResolveSurvivorAsync(tenantId, merged, Ct));
        Assert.Equal(survivor, await directory.ResolveSurvivorAsync(tenantId, survivor, Ct));
        Assert.Equal(merged, await directory.ResolveSurvivorAsync(Guid.CreateVersion7(), merged, Ct));
        var unknown = Guid.CreateVersion7();
        Assert.Equal(unknown, await directory.ResolveSurvivorAsync(tenantId, unknown, Ct));
    }

    [Fact]
    public async Task MemberNamesAreTheTenantsOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        var (memberId, _) = await SeedMemberAsync(connectionString, tenant.TenantId, "Laura Gómez");
        var foreignOwner = await OwnerMembershipIdAsync(connectionString, other);

        using var scope = factory.Services.CreateScope();
        var names = await scope.ServiceProvider.GetRequiredService<IPipelineMemberNames>()
            .FindAsync(tenant.TenantId, [memberId, foreignOwner, Guid.CreateVersion7()], Ct);

        Assert.Equal("Laura Gómez", Assert.Single(names).Value);
        Assert.Equal(memberId, names.Keys.Single());
    }

    // D21 del spec del frontend: la lista de dueños posibles es la misma regla que valida al dueño (membresía Active del tenant).
    [Fact]
    public async Task ListActiveReturnsOnlyTheTenantsActiveMemberships()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        var (advisor, _) = await SeedMemberAsync(connectionString, tenant.TenantId, "Laura Gómez");
        foreach (var state in new[] { "Suspended", "Removed", "Invited", "Expired" })
        {
            await SeedMemberAsync(connectionString, tenant.TenantId, $"Inactiva {state}", state: state);
        }

        await SeedMemberAsync(connectionString, other.TenantId, "Intrusa Activa");

        using var scope = factory.Services.CreateScope();
        var members = scope.ServiceProvider.GetRequiredService<IPipelineActiveMembers>();
        var active = await members.ListActiveAsync(tenant.TenantId, Ct);

        Assert.Equal(new[] { owner, advisor }.Order(), active.Order());
        Assert.Empty(await members.ListActiveAsync(Guid.CreateVersion7(), Ct));
    }
}
