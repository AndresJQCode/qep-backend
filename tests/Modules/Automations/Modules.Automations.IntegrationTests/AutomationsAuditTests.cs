using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Automations.Api;
using Modules.Automations.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 §4.6: la auditoría de los cambios del chatbot y el tope por minuto del guardado del borrador.</summary>
public sealed class AutomationsAuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Digits() => Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ChatbotsUrl(Guid tenantId) => $"{AutomationsUrl(tenantId)}/chatbots";

    private sealed record Setup(string ConnectionString, RegisteredTenant Tenant, HttpClient Client);

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        return new Setup(connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static async Task<ChatbotEditorDto> CreateAsync(Setup setup)
    {
        var request = new CreateChatbotRequest(
            "Servicio técnico", "Atiende la línea de servicio.", "NewConversation", [], [], new ChatbotSourceRequest("blank", null, null, null));
        var response = await setup.Client.PostAsJsonAsync(ChatbotsUrl(setup.Tenant.TenantId), request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> SendAsync(Setup setup, HttpMethod method, Guid chatbotId, string suffix, long version, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"{ChatbotsUrl(setup.Tenant.TenantId)}/{chatbotId}{suffix}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return setup.Client.SendAsync(request, Ct);
    }

    private static async Task<long> SaveDraftAsync(Setup setup, Guid chatbotId, long version, string flowJson)
    {
        var response = await SendAsync(
            setup, HttpMethod.Put, chatbotId, "/draft", version,
            new SaveDraftRequest(JsonDocument.Parse(flowJson).RootElement, JsonDocument.Parse("""{ "positions": {} }""").RootElement));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!.Version;
    }

    private static Task<long> DraftEntriesAsync(Setup setup, Guid chatbotId) =>
        CountAsync(setup.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = 'automations.chatbot.draft_saved'", ("id", chatbotId.ToString()));

    [Fact]
    public async Task CreateUpdatePublishPauseAndDeleteAreAudited()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var id = editor.Chatbot.Id;

        var updated = await SendAsync(
            setup, HttpMethod.Put, id, string.Empty, editor.Chatbot.Version,
            new UpdateChatbotRequest("Servicio técnico 2", "Otra descripción.", "NewConversation", [], "reiniciar", 3, []));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var version = await SaveDraftAsync(setup, id, (await updated.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!.Chatbot.Version, AnnexA.Json);
        var published = await SendAsync(setup, HttpMethod.Post, id, "/publish", version);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var paused = await SendAsync(setup, HttpMethod.Post, id, "/pause", (await published.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!.Chatbot.Version);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var deleted = await SendAsync(setup, HttpMethod.Delete, id, string.Empty, (await paused.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!.Chatbot.Version);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT action, resource_type, actor_id FROM audit.entries WHERE resource_id = @id ORDER BY occurred_at, action", connection);
        command.Parameters.AddWithValue("id", id.ToString());
        var rows = new List<(string Action, string ResourceType, Guid Actor)>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetGuid(2)));
            }
        }

        Assert.Equal(
            ["automations.chatbot.created", "automations.chatbot.deleted", "automations.chatbot.draft_saved", "automations.chatbot.paused",
                "automations.chatbot.published", "automations.chatbot.updated"],
            rows.Select(row => row.Action).Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal(("chatbot", setup.Tenant.OwnerUserId), (row.ResourceType, row.Actor)));
    }

    // §4.6: el editor guarda solo; una entrada por guardado inundaría la auditoría.
    [Fact]
    public async Task DraftSavesAreAuditedAtMostOncePerMinute()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var clock = new TestClock();
        using var factory = new QepApiFactory(connectionString, null, clock);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);
        var id = editor.Chatbot.Id;
        const string blank = """{ "schema": 1, "start": { "firstNodeId": null }, "variables": [], "nodes": [] }""";

        var version = editor.Chatbot.Version;
        for (var save = 0; save < 3; save++)
        {
            version = await SaveDraftAsync(setup, id, version, blank);
        }

        Assert.Equal(1L, await DraftEntriesAsync(setup, id));

        clock.UtcNow = clock.UtcNow.AddSeconds(61);
        await SaveDraftAsync(setup, id, version, blank);

        Assert.Equal(2L, await DraftEntriesAsync(setup, id));
    }

    // Solo cuentan las entradas draft_saved: haber creado el chatbot hace un instante no silencia el primer guardado.
    [Fact]
    public async Task TheFirstDraftSaveAfterCreateIsAudited()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, null, new TestClock());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var editor = await CreateAsync(setup);

        await SaveDraftAsync(setup, editor.Chatbot.Id, editor.Chatbot.Version, """{ "schema": 1, "start": { "firstNodeId": null }, "variables": [], "nodes": [] }""");

        Assert.Equal(1L, await DraftEntriesAsync(setup, editor.Chatbot.Id));
    }
}
