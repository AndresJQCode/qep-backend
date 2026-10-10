using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.3 por HTTP y contra la base: 201 con Sent y clientId; dos envíos concurrentes
/// con el mismo clientId → una llamada a Meta y el mismo mensaje; un Failed se reenvía; 190 → NeedsAttention
/// + connection_unavailable; 131047 → window_closed; timeout → -1; la fuga del token.</summary>
public sealed class SendMessageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Fixture(factory, connectionString, tenant, conversationId, client);
    }

    private static void ScriptSendOk(FakeMetaGraphHandler meta, string wamid = "wamid.out") =>
        meta.Respond("/111/messages", HttpStatusCode.OK, $$"""{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"{{wamid}}"}]}""");

    private static int SendCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));

    [Fact]
    public async Task AHappySendAnswers201SentWithTheClientIdAndUpdatesTheSnapshot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();
        // Fix 1, hallazgo 1: el envío no sube version ni updated_at (un resolver con la versión de antes no da 412).
        var before = await ScalarAsync<string>(f.ConnectionString, "SELECT version || '|' || updated_at::text FROM messaging.conversations");

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = " Sí, tenemos 12 unidades. " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Sent", message.GetProperty("status").GetString());
        Assert.Equal("Outbound", message.GetProperty("direction").GetString());
        Assert.Equal(clientId, message.GetProperty("clientId").GetGuid());
        Assert.Equal("Sí, tenemos 12 unidades.", message.GetProperty("text").GetString());
        Assert.False(string.IsNullOrEmpty(message.GetProperty("sentBy").GetProperty("displayName").GetString()));
        Assert.Equal(await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant), message.GetProperty("sentBy").GetProperty("memberId").GetGuid());
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Equal("/v24.0/111/messages", send.Uri!.AbsolutePath);
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", send.Authorization);
        Assert.Contains($"\"biz_opaque_callback_data\":\"qep:{message.GetProperty("id").GetGuid()}\"", send.Body, StringComparison.Ordinal);
        Assert.Contains("\"to\":\"573001234567\"", send.Body, StringComparison.Ordinal);
        Assert.Equal("1|wamid.out|2|1", await ScalarAsync<string>(f.ConnectionString, "SELECT m.status || '|' || m.wamid || '|' || c.last_message_direction || '|' || c.last_message_status FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.direction = 2"));
        Assert.Equal(before, await ScalarAsync<string>(f.ConnectionString, "SELECT version || '|' || updated_at::text FROM messaging.conversations"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT c.last_message_id = m.id AND c.last_activity_at = m.occurred_at AND c.last_message_preview = m.text FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.direction = 2"));
        var list = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);
        Assert.Equal("Outbound", list.GetProperty("items")[0].GetProperty("lastMessage").GetProperty("direction").GetString());
    }

    // Spec 2026-10-10 §6.1.4 (RF1): una conversación con BSUID y sin teléfono se responde por recipient.
    [Fact]
    public async Task ABsuidConversationIsAnsweredByRecipientWithoutTo()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedBsuidConversationAsync(factory, connectionString, tenant.TenantId, connectionId, "CO.1349120865530274", null, DateTimeOffset.UtcNow.AddMinutes(-5));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        ScriptSendOk(factory.MetaHandler);

        var response = await SendAsync(client, HttpMethod.Post, MessagesUrl(tenant.TenantId, conversationId), new { clientId = Guid.CreateVersion7(), text = "hola" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var send = Assert.Single(factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Contains("\"recipient\":\"CO.1349120865530274\"", send.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"to\"", send.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedClientIdAnswersTheSameMessageAndMetaSeesOneCall()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();

        var first = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = "una vez" });
        var second = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = "otro texto" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(firstBody.GetProperty("id").GetGuid(), secondBody.GetProperty("id").GetGuid());
        Assert.Equal("una vez", secondBody.GetProperty("text").GetString());
        Assert.Equal(clientId, secondBody.GetProperty("clientId").GetGuid());
        Assert.Equal(1, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task TwoConcurrentSendsWithTheSameClientIdCallMetaOnceAndAnswerTheSameMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.RespondWith("/111/messages", _ =>
        {
            Thread.Sleep(300);
            return FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"messages":[{"id":"wamid.once"}]}""");
        });
        var clientId = Guid.CreateVersion7();
        var body = new { clientId, text = "una vez" };

        var responses = await Task.WhenAll(
            SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), body),
            SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), body));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        }

        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(1, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Theory]
    [InlineData(401, 190, "messaging.connection_unavailable", "NeedsAttention|token_expired", 0)]
    [InlineData(400, 133010, "messaging.connection_unavailable", "NeedsAttention|number_unregistered", 0)]
    [InlineData(400, 131047, "messaging.window_closed", "Active|-", 1)]
    [InlineData(400, 131026, "messaging.message.rejected", "Active|-", 1)]
    public async Task AGraphErrorAnswersItsCodeRowAndConnectionState(int status, int code, string expectedCode, string expectedConnection, long expectedRows)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", (HttpStatusCode)status, FakeMetaGraphHandler.GraphError(code));

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, (await ProblemAsync(response)).Code);
        Assert.Equal(expectedConnection, await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections"));
        Assert.Equal(expectedRows, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        if (expectedRows == 1)
        {
            Assert.Equal($"4|{code}", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));
        }
    }

    [Fact]
    public async Task AFailedMessageIsResentOnTheSameRowAndATimeoutIsMinusOne()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Throw = new TaskCanceledException("timed out", new TimeoutException());
        var clientId = Guid.CreateVersion7();

        var timeout = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, timeout.StatusCode);
        Assert.Equal("messaging.message.rejected", (await ProblemAsync(timeout)).Code);
        Assert.Equal("4|-1", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));

        f.Factory.MetaHandler.Throw = null;
        ScriptSendOk(f.Factory.MetaHandler, "wamid.retry");
        var retry = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text = "x" });

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("Sent", (await retry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());
        Assert.Equal(2, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("1|wamid.retry|-", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || wamid || '|' || coalesce(failure_code::text, '-') FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AFiveHundredIsAnUnconfirmedFailure()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.ServiceUnavailable, FakeMetaGraphHandler.GraphError(131016));

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.message.rejected", (await ProblemAsync(response)).Code);
        Assert.Equal("4|-1", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));
    }

    [Theory]
    [InlineData("resolved", "messaging.conversation.not_open")]
    [InlineData("window-closed", "messaging.window_closed")]
    [InlineData("connection-paused", "messaging.connection_unavailable")]
    public async Task TheChecksBeforeMetaAnswer422WithoutCallingMetaOrSavingARow(string scenario, string expectedCode)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var sql = scenario switch
        {
            "resolved" => "UPDATE messaging.conversations SET status = 'Resolved'",
            "window-closed" => "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'",
            _ => "UPDATE integrations.connections SET status = 'Paused'",
        };
        await ExecuteAsync(f.ConnectionString, sql);

        var response = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, (await ProblemAsync(response)).Code);
        Assert.Equal(0, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task ValidationAnotherTenantModuleOffAndAMissingConversationHaveTheirCodes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var url = MessagesUrl(f.Tenant.TenantId, f.ConversationId);

        var invalid = await SendAsync(f.Client, HttpMethod.Post, url, new { text = "hola\u0000" });
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ManagePermissions);
        var cross = await SendAsync(otherClient, HttpMethod.Post, url, new { clientId = Guid.CreateVersion7(), text = "x" });
        var missing = await SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, Guid.CreateVersion7()), new { clientId = Guid.CreateVersion7(), text = "x" });
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var readOnly = await SendAsync(reader, HttpMethod.Post, url, new { clientId = Guid.CreateVersion7(), text = "x" });
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var off = await SendAsync(f.Client, HttpMethod.Post, url, new { clientId = Guid.CreateVersion7(), text = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var problem = await ProblemAsync(invalid);
        Assert.Equal("validation.failed", problem.Code);
        Assert.Equal(["clientId", "text"], problem.ErrorKeys);
        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(cross)).Code);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("messaging.conversation.not_found", (await ProblemAsync(missing)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(off)).Code);
        Assert.Equal(0, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task TheTokenNeverLeaksInResponsesLogsOrFailures()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var logs = new CapturedLogs();
        using var host = f.Factory.WithCapturedLogs(logs);
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, $$$"""{"error":{"message":"{{{SentinelMetaAccessToken}}}","code":100}}""");
        using var client = CreateClient(host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId = Guid.CreateVersion7(), text = "texto-SENTINEL-privado" });
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(SentinelMetaAccessToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelMetaAccessToken, logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("texto-SENTINEL-privado", logs.AllText, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, entry => entry.Contains("Graph send answered HTTP 400", StringComparison.Ordinal));
        Assert.DoesNotContain(SentinelMetaAccessToken, await ScalarAsync<string>(f.ConnectionString, "SELECT coalesce(string_agg(message || ' ' || detail, ' '), '') FROM platform.request_failures"), StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Categories, category => category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }
}
