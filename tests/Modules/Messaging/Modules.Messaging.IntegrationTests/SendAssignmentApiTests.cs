using System.Net;
using System.Net.Http.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.5 y §9.3: responder la de otro es 422 sin Meta (RF6); responder una sin asignar la toma
/// con AutoTaken y auditoría, aunque Meta rechace; la autoasignación nunca da 412 contra la ingesta (RF7); un clientId
/// ya enviado vuelve igual aunque otro la haya tomado.</summary>
[Collection(MessagingLoadGroup.Name)]
public sealed class SendAssignmentApiTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, Guid Owner, Guid Beatriz, HttpClient Client);

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

        var (beatriz, _) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        return new Fixture(factory, connectionString, tenant, await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations"),
            await OwnerMembershipIdAsync(connectionString, tenant), beatriz, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static void ScriptSendOk(FakeMetaGraphHandler meta) =>
        meta.Respond("/111/messages", HttpStatusCode.OK, """{"messaging_product":"whatsapp","contacts":[{"input":"CO.1","user_id":"CO.1"}],"messages":[{"id":"wamid.out"}]}""");

    private static int SendCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));

    private static Task<HttpResponseMessage> PostAsync(Fixture f, Guid clientId, string text = "hola") =>
        SendAsync(f.Client, HttpMethod.Post, MessagesUrl(f.Tenant.TenantId, f.ConversationId), new { clientId, text });

    [Fact]
    public async Task AnswerToSomeoneElsesConversationIs422WithoutCallingMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", f.Beatriz));

        var response = await PostAsync(f, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.conversation.assigned_to_other", (await ProblemAsync(response)).Code);
        Assert.Equal(0, SendCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AnswerToAnUnassignedOneTakesItEvenIfMetaRejects()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131026));

        var response = await PostAsync(f, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(f.Owner, await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
        Assert.Equal(f.Owner.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'actor' FROM messaging.messages WHERE details->>'type' = 'AutoTaken'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.auto_taken'"));
    }

    // RF7: la autoasignación es un UPDATE condicional sin token de concurrencia; la ingesta sube version en paralelo.
    [Fact]
    public async Task AutoTakeNeverConflictsWithAConcurrentInbound()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        using var anonymous = f.Factory.CreateClient();
        // Una versión vieja en mano (lo que tendría la pantalla) y un entrante que la sube antes de enviar.
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "¿sigues?"));
        await DrainDeliveriesAsync(f.Factory);
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.3", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "¿hola?"));

        var results = await Task.WhenAll(PostAsync(f, Guid.CreateVersion7()), DrainThenOkAsync(f));

        Assert.Equal(HttpStatusCode.Created, results[0].StatusCode);
        Assert.Equal(f.Owner, await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'AutoTaken'"));
        Assert.Equal(3L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
    }

    [Fact]
    public async Task ARetryOfASentClientIdAfterSomeoneElseTookItIsTheSameMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();
        var first = await PostAsync(f, clientId, "una vez");
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", f.Beatriz));

        var retry = await PostAsync(f, clientId, "otra");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid(),
            (await retry.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid());
        Assert.Equal(1, SendCalls(f.Factory.MetaHandler));
    }

    // Un reintento del mismo clientId cuya primera vuelta autoasignó y Meta rechazó: quien envía ya es el dueño, así
    // que no es assigned_to_other, y no se escribe un segundo AutoTaken.
    [Fact]
    public async Task ARetryAfterARejectedFirstAttemptIsNotAssignedToOtherNorASecondAutoTake()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131026));
        var clientId = Guid.CreateVersion7();
        var first = await PostAsync(f, clientId);
        f.Factory.MetaHandler.Reset();
        ScriptSendOk(f.Factory.MetaHandler);

        var retry = await PostAsync(f, clientId);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, HttpStatusCode.Created), (first.StatusCode, retry.StatusCode));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'AutoTaken'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.auto_taken'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2 AND status = 1"));
    }

    // El AutoTaken va antes del saliente en el orden (occurred_at, id) del hilo.
    [Fact]
    public async Task TheAutoTakenEventSortsBeforeTheOutboundMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        ScriptSendOk(f.Factory.MetaHandler);

        var response = await PostAsync(f, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("3,2", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(direction::text, ',' ORDER BY occurred_at, id) FROM messaging.messages WHERE direction = 2 OR details->>'type' = 'AutoTaken'"));
    }

    private static async Task<HttpResponseMessage> DrainThenOkAsync(Fixture f)
    {
        await DrainDeliveriesAsync(f.Factory);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
