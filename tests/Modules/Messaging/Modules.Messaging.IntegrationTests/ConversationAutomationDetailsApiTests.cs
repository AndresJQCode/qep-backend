using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 §5.5: «Datos capturados» que ve el asesor, <c>GET …/conversations/{id}/automation</c>, sobre un puerto que el
/// Bootstrapper adapta a Automations. El host tiene los dos esquemas: las filas de <c>automations.*</c> se siembran por SQL.</summary>
[Collection(MessagingLoadGroup.Name)]
public sealed class ConversationAutomationDetailsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        return new Fixture(factory, connectionString, tenant, conversationId);
    }

    private static string DetailsUrl(Fixture f) => $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/automation";

    /// <summary>Un chatbot con su versión 1 y una corrida de esa conversación, por SQL.</summary>
    private static async Task SeedRunAsync(Fixture f, string status, string variables, string trace, string? endedAt = null)
    {
        var chatbotId = Guid.CreateVersion7();
        var versionId = Guid.CreateVersion7();
        var by = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO automations.chatbots (id, tenant_id, name, draft_flow, draft_updated_at, draft_updated_by, created_at, created_by, updated_at)
            VALUES (@chatbot, @tenant, 'Servicio técnico', '{}', now(), @by, now(), @by, now());
            INSERT INTO automations.chatbot_versions (id, chatbot_id, tenant_id, number, flow, layout, published_at, published_by)
            VALUES (@version, @chatbot, @tenant, 1, '{}', '{}', now(), @by);
            INSERT INTO automations.conversation_runs
                (id, tenant_id, conversation_id, connection_id, chatbot_id, chatbot_version_id, status, current_node_id, step_number, variables, trace, started_at, updated_at, ended_at)
            VALUES (@run, @tenant, @conversation, @by, @chatbot, @version, @status, 'n2', 2, @variables::jsonb, @trace::jsonb, now(), now(), @ended::timestamptz);
            """,
            ("chatbot", chatbotId), ("version", versionId), ("tenant", f.Tenant.TenantId), ("by", by), ("run", Guid.CreateVersion7()),
            ("conversation", f.ConversationId), ("status", status), ("variables", variables), ("trace", trace),
            ("ended", endedAt is null ? DBNull.Value : endedAt));
    }

    private const string TwoStepTrace =
        """[{"at":"2026-10-22T10:00:00+00:00","nodeId":"n1","kind":"message","outcome":"sent"},{"at":"2026-10-22T10:00:05+00:00","nodeId":"n2","kind":"openQuestion","outcome":"waiting"}]""";

    [Fact]
    public async Task TheDetailsCarryNameVersionVariablesAndTrace()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedRunAsync(f, "Waiting", """{"cedula":"1"}""", TwoStepTrace);
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var response = await client.GetAsync(DetailsUrl(f), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Servicio técnico", body.GetProperty("chatbotName").GetString());
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Waiting", body.GetProperty("status").GetString());
        Assert.Equal("1", body.GetProperty("variables").GetProperty("cedula").GetString());
        var trace = body.GetProperty("trace");
        Assert.Equal(2, trace.GetArrayLength());
        Assert.Equal("n2", trace[1].GetProperty("nodeId").GetString());
        Assert.Equal("openQuestion", trace[1].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("endedAt").ValueKind);
    }

    [Fact]
    public async Task AConversationWithoutRunsIs404NotAvailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var response = await client.GetAsync(DetailsUrl(f), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("messaging.automation.not_available", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ReadPermissionIsEnough()
    {
        // ReadPermissions es sólo messaging.conversation.read: ningún automations.* viaja con el cliente (D-C10).
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedRunAsync(f, "HandedOff", "{}", "[]", endedAt: "2026-10-22T10:10:00+00:00");
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var response = await client.GetAsync(DetailsUrl(f), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("HandedOff", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedRunAsync(f, "Waiting", "{}", "[]");
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ReadPermissions);

        var response = await otherClient.GetAsync(DetailsUrl(f), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
