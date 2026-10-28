using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.1 (D-A1) y §11: las membresías activas del tenant cuyos roles conceden manage, por
/// nombre; ni las de otro tenant, ni las suspendidas, ni las sin manage.</summary>
public sealed class AssigneesApiTests
{
    [Fact]
    public async Task OnlyActiveMembersThatCanReplyAreListedByName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var (beatriz, _) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        await SeedMemberAsync(connectionString, tenant.TenantId, "Carlos", "billing");
        await SeedMemberAsync(connectionString, tenant.TenantId, "Diana", "advisor", state: "Suspended");
        await SeedMemberAsync(connectionString, other.TenantId, "Elena", "advisor");
        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var body = await client.GetFromJsonAsync<JsonElement>(AssigneesUrl(tenant.TenantId), TestContext.Current.CancellationToken);

        var items = body.GetProperty("items").EnumerateArray().Select(item => (item.GetProperty("memberId").GetGuid(), item.GetProperty("displayName").GetString())).ToArray();
        Assert.Contains((beatriz, "Beatriz"), items);
        Assert.Contains(items, item => item.Item1 == owner);
        Assert.Equal(2, items.Length);
        Assert.Equal(items.OrderBy(item => item.Item2, StringComparer.CurrentCultureIgnoreCase), items);
    }

    [Fact]
    public async Task ReadingAssigneesNeedsManage()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var response = await client.GetAsync(AssigneesUrl(tenant.TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
