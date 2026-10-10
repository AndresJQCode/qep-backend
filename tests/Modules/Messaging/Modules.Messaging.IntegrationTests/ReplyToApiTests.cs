using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.1 y §8.6: replyTo saliente → context.message_id en Meta y replyTo en la respuesta; un
/// replyTo que no se puede citar es 422 en replyTo sin llamar a Meta; repetir el clientId devuelve la cita guardada.</summary>
public sealed class ReplyToApiTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, Guid ConnectionId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "¿Tienen?"));
            await DrainDeliveriesAsync(factory);
        }

        factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"messages":[{"id":"wamid.out"}]}""");
        return new Fixture(factory, connectionString, tenant, await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations"), connectionId,
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static Task<HttpResponseMessage> ReplyAsync(Fixture f, Guid clientId, Guid replyTo, string text = "Sí") =>
        SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text, replyTo });

    [Fact]
    public async Task AQuotedReplyCarriesTheContextAndAnswersTheReplyTo()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var inbound = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE wamid = 'wamid.in'");
        var clientId = Guid.CreateVersion7();

        var response = await ReplyAsync(f, clientId, inbound);
        var other = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 3");
        var retry = await ReplyAsync(f, clientId, other);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal((inbound, "Inbound", "¿Tienen?"), (body.GetProperty("replyTo").GetProperty("id").GetGuid(), body.GetProperty("replyTo").GetProperty("direction").GetString(), body.GetProperty("replyTo").GetProperty("preview").GetString()));
        Assert.Contains("\"context\":{\"message_id\":\"wamid.in\"}", Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        Assert.Equal($"{inbound}|wamid.in", await ScalarAsync<string>(f.ConnectionString, "SELECT reply_to_message_id::text || '|' || reply_to_wamid FROM messaging.messages WHERE direction = 2"));
        // §5.1: el reintento devuelve la cita guardada aunque el cuerpo traiga otra (y aunque ésa no se pudiera citar).
        Assert.Equal(inbound, (await retry.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("replyTo").GetProperty("id").GetGuid());
    }

    // Un request que no pasa la validación no cambia estado: una cita inválida no autoasigna la conversación sin dueño.
    [Fact]
    public async Task AnInvalidQuoteLeavesAnUnassignedConversationUntouched()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.2", null);
        var foreign = await SeedOutboundAsync(f.ConnectionString, other, f.Tenant.TenantId, f.ConnectionId, "wamid.otra");
        var before = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations WHERE id = @c", ("c", f.ConversationId));

        var response = await ReplyAsync(f, Guid.CreateVersion7(), foreign);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(("validation.failed", "replyTo"), (problem.Code, Assert.Single(problem.ErrorKeys)));
        Assert.Equal(before, await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations WHERE id = @c", ("c", f.ConversationId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE id = @c AND assigned_member_id IS NOT NULL", ("c", f.ConversationId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'AutoTaken'"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.auto_taken'"));
    }

    [Fact]
    public async Task AReplyToThatCannotBeQuotedIs422OnReplyToWithoutCallingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.2", null);
        var foreign = await SeedOutboundAsync(f.ConnectionString, other, f.Tenant.TenantId, f.ConnectionId, "wamid.otra");
        var withoutWamid = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.Tenant.TenantId, f.ConnectionId, null, status: 4, failureCode: -1);
        var reaction = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid, details, created_at) VALUES (@id, @c, @t, @n, now(), 1, 9, 2, '👍', 'wamid.reaction', '{}', now())""",
            ("id", reaction), ("c", f.ConversationId), ("t", f.Tenant.TenantId), ("n", f.ConnectionId));
        var anEvent = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE direction = 3 AND conversation_id = @c", ("c", f.ConversationId));

        foreach (var replyTo in new[] { foreign, withoutWamid, reaction, anEvent, Guid.CreateVersion7() })
        {
            var response = await ReplyAsync(f, Guid.CreateVersion7(), replyTo);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await ProblemAsync(response);
            Assert.Equal(("validation.failed", "replyTo"), (problem.Code, Assert.Single(problem.ErrorKeys)));
        }

        Assert.DoesNotContain(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
    }
}
