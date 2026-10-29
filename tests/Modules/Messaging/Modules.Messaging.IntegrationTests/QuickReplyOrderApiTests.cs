using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29 por HTTP y contra la base: el orden personal de las respuestas rápidas. <c>PUT …/quick-replies/order</c> con
/// el permiso de leerlas (no el de gestionarlas) guarda el orden de quien llama y responde la lista ya ordenada; el <c>GET</c> la devuelve
/// en ese orden sólo a quien lo guardó. Lo ajeno y lo repetido se ignoran, la lista vacía vuelve al orden por atajo y el guardado se
/// audita en la misma transacción.</summary>
public sealed class QuickReplyOrderApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] AdminPermissions = [MessagingPermissions.ConversationRead, MessagingPermissions.QuickReplyManage];

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Admin);

    private static string QuickRepliesUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/messaging/quick-replies";

    private static string OrderUrl(Guid tenantId) => $"{QuickRepliesUrl(tenantId)}/order";

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        return new Fixture(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AdminPermissions));
    }

    private static async Task<Guid> CreateAsync(Fixture f, string shortcut)
    {
        var response = await SendAsync(f.Admin, HttpMethod.Post, QuickRepliesUrl(f.Tenant.TenantId), new { shortcut, body = "Hola" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SaveOrderAsync(HttpClient client, Guid tenantId, params Guid[] ids) =>
        SendAsync(client, HttpMethod.Put, OrderUrl(tenantId), new { quickReplyIds = ids });

    private static string[] Shortcuts(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("shortcut").GetString()!).ToArray();

    private static async Task<string[]> ListShortcutsAsync(HttpClient client, Guid tenantId) =>
        Shortcuts(await client.GetFromJsonAsync<JsonElement>(QuickRepliesUrl(tenantId), Ct));

    private static async Task<(HttpClient Client, Guid MembershipId)> AdvisorAsync(Fixture f, string name)
    {
        var (membershipId, userId) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, name, "advisor");
        return (CreateClient(f.Factory, userId, f.Tenant.TenantId, ReadPermissions), membershipId);
    }

    [Fact]
    public async Task AnAdvisorWithOnlyReadSavesTheirOrderAndGetsTheListOrderedBack()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var aDomicilio = await CreateAsync(f, "a-domicilio");
        await CreateAsync(f, "horarios");
        var pago = await CreateAsync(f, "pago");
        var (advisor, membershipId) = await AdvisorAsync(f, "Andrea Gómez");
        using var client = advisor;

        var response = await SaveOrderAsync(client, f.Tenant.TenantId, pago, aDomicilio);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["pago", "a-domicilio", "horarios"], Shortcuts(saved));
        Assert.Equal(("Andrea Gómez", "Messaging Test Org"), (
            saved.GetProperty("variables").GetProperty("asesor").GetString(), saved.GetProperty("variables").GetProperty("empresa").GetString()));
        Assert.Equal(["pago", "a-domicilio", "horarios"], await ListShortcutsAsync(client, f.Tenant.TenantId));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE source = 'messaging' AND action = 'messaging.quick_reply.order_saved' AND resource_type = 'quick_reply_order' AND resource_id = @id",
            ("id", membershipId.ToString())));
    }

    [Fact]
    public async Task TheOrderIsPersonalAndOthersKeepTheShortcutOrder()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "a-domicilio");
        await CreateAsync(f, "horarios");
        var pago = await CreateAsync(f, "pago");
        var (first, _) = await AdvisorAsync(f, "Andrea Gómez");
        var (second, _) = await AdvisorAsync(f, "Bruno Díaz");
        using var andrea = first;
        using var bruno = second;

        Assert.Equal(HttpStatusCode.OK, (await SaveOrderAsync(andrea, f.Tenant.TenantId, pago)).StatusCode);

        Assert.Equal(["pago", "a-domicilio", "horarios"], await ListShortcutsAsync(andrea, f.Tenant.TenantId));
        Assert.Equal(["a-domicilio", "horarios", "pago"], await ListShortcutsAsync(bruno, f.Tenant.TenantId));
        Assert.Equal(["a-domicilio", "horarios", "pago"], await ListShortcutsAsync(f.Admin, f.Tenant.TenantId));
    }

    [Fact]
    public async Task SavingReplacesThePreviousOrderAndAnEmptyListResetsIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var aDomicilio = await CreateAsync(f, "a-domicilio");
        var horarios = await CreateAsync(f, "horarios");
        var pago = await CreateAsync(f, "pago");

        Assert.Equal(HttpStatusCode.OK, (await SaveOrderAsync(f.Admin, f.Tenant.TenantId, pago, horarios, aDomicilio)).StatusCode);
        var replaced = await SaveOrderAsync(f.Admin, f.Tenant.TenantId, horarios);
        Assert.Equal(["horarios", "a-domicilio", "pago"], Shortcuts(await replaced.Content.ReadFromJsonAsync<JsonElement>(Ct)));

        var reset = await SaveOrderAsync(f.Admin, f.Tenant.TenantId);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(["a-domicilio", "horarios", "pago"], Shortcuts(await reset.Content.ReadFromJsonAsync<JsonElement>(Ct)));
        Assert.Equal(["a-domicilio", "horarios", "pago"], await ListShortcutsAsync(f.Admin, f.Tenant.TenantId));
    }

    // Un borrado concurrente no hace fallar el guardado: lo que no es del tenant se ignora, y lo repetido conserva su primer lugar.
    [Fact]
    public async Task ForeignUnknownAndRepeatedIdsAreIgnoredAndNotStored()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "a-domicilio");
        var horarios = await CreateAsync(f, "horarios");
        var pago = await CreateAsync(f, "pago");
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherAdmin = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, AdminPermissions);
        var foreign = (await (await SendAsync(otherAdmin, HttpMethod.Post, QuickRepliesUrl(other.TenantId), new { shortcut = "ajena", body = "Hola" }))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var response = await SaveOrderAsync(f.Admin, f.Tenant.TenantId, foreign, pago, Guid.CreateVersion7(), horarios, pago);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["pago", "horarios", "a-domicilio"], Shortcuts(await response.Content.ReadFromJsonAsync<JsonElement>(Ct)));
        Assert.Equal(2L, await ScalarAsync<long>(f.ConnectionString,
            "SELECT cardinality(quick_reply_ids)::bigint FROM messaging.quick_reply_orders WHERE tenant_id = @t", ("t", f.Tenant.TenantId)));
    }

    [Fact]
    public async Task ADeletedReplyIsSkippedFromTheSavedOrder()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "a-domicilio");
        var horarios = await CreateAsync(f, "horarios");
        var pago = await CreateAsync(f, "pago");
        Assert.Equal(HttpStatusCode.OK, (await SaveOrderAsync(f.Admin, f.Tenant.TenantId, pago, horarios)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Admin, HttpMethod.Delete, $"{QuickRepliesUrl(f.Tenant.TenantId)}/{pago}?version=1")).StatusCode);

        Assert.Equal(["horarios", "a-domicilio"], await ListShortcutsAsync(f.Admin, f.Tenant.TenantId));
    }

    [Fact]
    public async Task AMissingBodyOrListIs422OnQuickReplyIdsAndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var url = OrderUrl(f.Tenant.TenantId);

        var noBody = await SendAsync(f.Admin, HttpMethod.Put, url);
        var noList = await SendAsync(f.Admin, HttpMethod.Put, url, new { });
        var tooMany = await SendAsync(f.Admin, HttpMethod.Put, url,
            new { quickReplyIds = Enumerable.Range(0, 501).Select(_ => Guid.CreateVersion7()).ToArray() });

        foreach (var response in new[] { noBody, noList, tooMany })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var (code, keys) = await ProblemAsync(response);
            Assert.Equal("validation.failed", code);
            Assert.Equal(["quickReplyIds"], keys);
        }

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_reply_orders"));
    }

    [Fact]
    public async Task WithoutReadFromAnotherTenantWithoutMembershipOrWithTheModuleOffIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var noRead = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingPermissions.QuickReplyManage);
        using var intruder = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), AdminPermissions);
        using var stranger = CreateClient(f.Factory, Guid.CreateVersion7(), f.Tenant.TenantId, ReadPermissions);

        var denied = await SaveOrderAsync(noRead, f.Tenant.TenantId);
        var cross = await SaveOrderAsync(intruder, f.Tenant.TenantId);
        var notMember = await SaveOrderAsync(stranger, f.Tenant.TenantId);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var moduleOff = await SaveOrderAsync(f.Admin, f.Tenant.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(cross)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, notMember.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(notMember)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, moduleOff.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(moduleOff)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_reply_orders"));
    }
}
