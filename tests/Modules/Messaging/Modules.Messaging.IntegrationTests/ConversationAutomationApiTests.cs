using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 §4 y §6: pausar y devolver al asistente con If-Match, sus eventos y auditoría, y la pausa que
/// causan tomar, transferir y responder.</summary>
[Collection(MessagingLoadGroup.Name)]
public sealed class ConversationAutomationApiTests
{
    private const string MetaSendOk = """{"messaging_product":"whatsapp","contacts":[{"input":"CO.1","user_id":"CO.1"}],"messages":[{"id":"wamid.out"}]}""";

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, Guid Owner, Guid Beatriz, Guid BeatrizUser, HttpClient Client);

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

        var (beatriz, beatrizUser) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        await ActivateAutomationAsync(connectionString, conversationId, Guid.CreateVersion7());
        return new Fixture(factory, connectionString, tenant, conversationId, await OwnerMembershipIdAsync(connectionString, tenant), beatriz, beatrizUser,
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static string ActionUrl(Fixture f, string action) => $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/{action}";

    private static Task<long> VersionAsync(Fixture f) => ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

    private static Task<HttpResponseMessage> ActAsync(Fixture f, HttpClient client, string action, long version, object? body = null) =>
        SendAsync(client, HttpMethod.Post, ActionUrl(f, action), body, $"\"{version}\"");

    private static Task<long> AuditAsync(Fixture f, string action) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = @a", ("a", action));

    private static Task<long> EventsAsync(Fixture f, string type) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 3 AND details->>'type' = @t", ("t", type));

    // Orden del hilo: dos eventos del mismo cambio deben leerse en el orden en que ocurrieron.
    private static Task<string> EventTypesAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString, "SELECT string_agg(details->>'type', ',' ORDER BY occurred_at, id) FROM messaging.messages WHERE direction = 3 AND details->>'type' <> 'CustomerCreated'");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheSummaryCarriesChatbotAndStep()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var chatbotId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT automation_chatbot_id FROM messaging.conversations");
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET automation_step = 'Número de documento' WHERE id = @id", ("id", f.ConversationId));

        var response = await SendAsync(f.Client, HttpMethod.Get, ConversationUrl(f.Tenant.TenantId, f.ConversationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var automation = (await ReadAsync(response)).GetProperty("automation");
        Assert.Equal(chatbotId, automation.GetProperty("chatbotId").GetGuid());
        Assert.Equal("Número de documento", automation.GetProperty("stepTitle").GetString());
        Assert.Equal("Chatbot", automation.GetProperty("chatbotName").GetString());
    }

    [Fact]
    public async Task RecordBySystemWritesASystemActor()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await using (var scope = f.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IMessagingAuditRecorder>()
                .RecordBySystem(f.Tenant.TenantId, "messaging.conversation.automation_ended", f.ConversationId, DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<MessagingDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.automation_ended' AND resource_id = @id AND actor_type = 'System' AND actor_id = @empty",
            ("id", f.ConversationId.ToString()), ("empty", Guid.Empty)));
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndAStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var missing = await SendAsync(f.Client, HttpMethod.Post, ActionUrl(f, "automation/pause"));
        var stale = await ActAsync(f, f.Client, "automation/resume", await VersionAsync(f) - 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task PausingManuallyRecordsTheEventTheAuditAndTheActor()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await ActAsync(f, f.Client, "automation/pause", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var automation = (await ReadAsync(response)).GetProperty("automation");
        Assert.Equal(("Paused", "Manual"), (automation.GetProperty("status").GetString(), automation.GetProperty("pauseReason").GetString()));
        Assert.Equal(f.Owner, automation.GetProperty("pausedBy").GetProperty("memberId").GetGuid());
        var messages = await SendAsync(f.Client, HttpMethod.Get, MessagesUrl(f.Tenant.TenantId, f.ConversationId));
        Assert.Contains("AutomationPaused", await messages.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Manual", await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'pauseReason' FROM messaging.messages WHERE details->>'type' = 'AutomationPaused'"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.automation_paused"));
    }

    [Fact]
    public async Task PausingTwiceIsAlreadyPausedAndANoneConversationIsNotAvailable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await ActAsync(f, f.Client, "automation/pause", await VersionAsync(f));
        var twice = await ActAsync(f, f.Client, "automation/pause", await VersionAsync(f));
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET automation_status='None', automation_started_at=NULL, automation_paused_at=NULL, automation_paused_by_member_id=NULL, automation_pause_reason=NULL, version=version+1 WHERE id = @id",
            ("id", f.ConversationId));
        var none = await ActAsync(f, f.Client, "automation/pause", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, twice.StatusCode);
        Assert.Equal("messaging.automation.already_paused", (await ProblemAsync(twice)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Equal("messaging.automation.not_available", (await ProblemAsync(none)).Code);
    }

    [Fact]
    public async Task TakingPausesWithAdvisorTookAndReleasingKeepsItPaused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var taken = await ActAsync(f, f.Client, "take", await VersionAsync(f));
        var automation = (await ReadAsync(taken)).GetProperty("automation");
        var released = await ActAsync(f, f.Client, "release", await VersionAsync(f));

        Assert.Equal(("Paused", "AdvisorTook"), (automation.GetProperty("status").GetString(), automation.GetProperty("pauseReason").GetString()));
        Assert.Equal("Taken,AutomationPaused,Released", await EventTypesAsync(f));
        Assert.Equal("Paused", (await ReadAsync(released)).GetProperty("automation").GetProperty("status").GetString());
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.taken"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.automation_paused"));
    }

    [Fact]
    public async Task ResumingWithAnAssigneeReleasesAndReactivates()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ActAsync(f, f.Client, "take", await VersionAsync(f));

        var response = await ActAsync(f, f.Client, "automation/resume", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("assignedTo").ValueKind);
        Assert.Equal("Active", body.GetProperty("automation").GetProperty("status").GetString());
        Assert.Equal("Taken,AutomationPaused,Released,AutomationResumed", await EventTypesAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.automation_resumed"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.released"));
    }

    [Fact]
    public async Task ResumingAResolvedConversationIsNotOpen()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ActAsync(f, f.Client, "automation/pause", await VersionAsync(f));
        await ActAsync(f, f.Client, "resolve", await VersionAsync(f));

        var response = await ActAsync(f, f.Client, "automation/resume", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.conversation.not_open", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task PausingAResolvedConversationIsNotOpen()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ActAsync(f, f.Client, "resolve", await VersionAsync(f));

        var response = await ActAsync(f, f.Client, "automation/pause", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.conversation.not_open", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ResumingAnActiveConversationIsAlreadyActive()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await ActAsync(f, f.Client, "automation/resume", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.automation.already_active", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ResolvingClearsTheAutomation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var resolved = await ActAsync(f, f.Client, "resolve", await VersionAsync(f));
        var reopened = await ActAsync(f, f.Client, "reopen", await VersionAsync(f));

        Assert.Equal(JsonValueKind.Null, (await ReadAsync(resolved)).GetProperty("automation").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(reopened)).GetProperty("automation").ValueKind);
    }

    // Review Focus 1: autoasignar sin pausar viola CK_conversations_automation_assignment (500).
    [Fact]
    public async Task SendingInAnActiveConversationAutoAssignsAndPausesIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, MetaSendOk);

        var sent = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "hola" });

        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var summary = await ReadAsync(await SendAsync(f.Client, HttpMethod.Get, ConversationUrl(f.Tenant.TenantId, f.ConversationId)));
        Assert.Equal(f.Owner, summary.GetProperty("assignedTo").GetProperty("memberId").GetGuid());
        var automation = summary.GetProperty("automation");
        Assert.Equal(("Paused", "AdvisorReplied"), (automation.GetProperty("status").GetString(), automation.GetProperty("pauseReason").GetString()));
        Assert.Equal("AutoTaken,AutomationPaused", await EventTypesAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.automation_paused"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.auto_taken"));
    }

    [Fact]
    public async Task SendingAsTheAssigneeDoesNotWriteASecondPause()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, MetaSendOk);
        await ActAsync(f, f.Client, "take", await VersionAsync(f));

        var sent = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "hola" });

        Assert.True(sent.IsSuccessStatusCode);
        Assert.Equal(1L, await EventsAsync(f, "AutomationPaused"));
    }
}
