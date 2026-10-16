using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.4 y §8.5: read deja unreadCount en 0 y manda el acuse a Meta (best effort,
/// una sola vez, sólo con conexión Active); resolve/reopen con If-Match (428/412), sus 422, auditoría en
/// la misma transacción; y la versión que sube la ingesta hace chocar un resolve viejo. Spec 2026-10-14: unread
/// es el espejo del read, sube el contador a 1 sólo desde 0 y nunca habla con Meta. Spec 2026-10-16 §3.2 y §3.3:
/// <c>/read</c> ya no lo dispara abrir el hilo sino el botón «Marcar como leída»; responder y resolver también
/// quitan la marca y mandan el acuse, por <see cref="ReadReceipts"/>.</summary>
public sealed class ConversationLifecycleApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, long Version, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var item = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0];
        return new Fixture(factory, connectionString, tenant, item.GetProperty("id").GetGuid(), item.GetProperty("version").GetInt64(), client);
    }

    [Fact]
    public async Task ReadZeroesTheCounterAndAcknowledgesToMetaOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");

        var first = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read");
        var second = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        // Una sola versión nueva: el segundo read encuentra el contador en 0 y no toca nada.
        Assert.Equal($"0|{f.Version + 1}", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations"));
        var ack = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        Assert.Contains("\"status\":\"read\"", ack.Body, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":\"wamid.in\"", ack.Body, StringComparison.Ordinal);
    }

    // §8.4: read no lleva token de concurrencia. Con la ingesta subiendo version por SQL (§7.5) entre dos
    // reads, el segundo sigue siendo 204, deja el contador en 0 y acusa el entrante nuevo.
    [Fact]
    public async Task ReadAfterAnInboundThatBumpedTheVersionIsStill204()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");
        var url = $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read";
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, url)).StatusCode);
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "otra"));
        await DrainDeliveriesAsync(f.Factory);
        var versionAfterInbound = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        var read = await SendAsync(f.Client, HttpMethod.Post, url);

        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        Assert.Equal($"0|{versionAfterInbound + 1}", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations"));
        Assert.Contains(f.Factory.MetaHandler.Requests, request => request.Body?.Contains("\"message_id\":\"wamid.2\"", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ReadIsStill204WhenMetaFailsOrTheConnectionIsPaused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Throw = new HttpRequestException("down");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT unread_count::bigint FROM messaging.conversations"));

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 2");
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");
        f.Factory.MetaHandler.Throw = null;
        f.Factory.MetaHandler.Requests.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Empty(f.Factory.MetaHandler.Requests);
    }

    // Spec 2026-10-14 §2 y §5: la marca sube a 1 una sola vez, sin acuse a Meta, y la lista la dibuja como un
    // no leído cualquiera.
    [Fact]
    public async Task UnreadRaisesTheCounterToOneOnceAndNeverTalksToMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/read")).StatusCode);
        f.Factory.MetaHandler.Requests.Clear();

        var first = await SendAsync(f.Client, HttpMethod.Post, $"{url}/unread");
        var second = await SendAsync(f.Client, HttpMethod.Post, $"{url}/unread");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        // version + 1 por el read, + 1 por la primera marca; la segunda encuentra el 1 y no toca nada.
        Assert.Equal($"1|{f.Version + 2}", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations"));
        Assert.Empty(f.Factory.MetaHandler.Requests);
        var page = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);
        Assert.Equal(1, page.GetProperty("items")[0].GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, page.GetProperty("counts").GetProperty("unread").GetInt32());
    }

    // §2: con no leídos reales no hay nada que marcar; pisar un 3 con un 1 mentiría sobre cuántos entraron.
    [Fact]
    public async Task UnreadLeavesARealCounterAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 3");

        var response = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/unread");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal($"3|{f.Version}", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations"));
    }

    [Fact]
    public async Task UnreadRejectsTheReadPermissionAloneOtherTenantsAndADisabledModule()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var readOnly = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ManagePermissions);
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(readOnly, HttpMethod.Post, $"{url}/unread")).StatusCode);
        // 404 y no 403: el id es de otro tenant, y la revalidación de tenant y permiso ya pasó para el propio.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(otherClient, HttpMethod.Post, $"{ConversationUrl(other.TenantId, f.ConversationId)}/unread")).StatusCode);

        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/unread")).StatusCode);
        // La fixture entra con un no leído real: ninguno de los tres rechazos lo tocó.
        Assert.Equal($"1|{f.Version}", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations"));
    }

    // Spec 2026-10-14 §7: «desde este mensaje» cuenta los entrantes desde el ancla en el orden del hilo, nunca
    // baja un contador mayor, y el id de un mensaje ajeno es 404 del mensaje.
    [Fact]
    public async Task UnreadFromAMessageCountsTheInboundFromThereAndNeverLowersTheCounter()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");
        using var anonymous = f.Factory.CreateClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.2", now - 30, "segundo"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.3", now, "tercero"));
        await DrainDeliveriesAsync(f.Factory);
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/read")).StatusCode);
        var readVersion = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");
        var thread = (await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, f.ConversationId), Ct)).GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("direction").GetString() == "Inbound")
            .ToDictionary(item => item.GetProperty("text").GetString()!, item => item.GetProperty("id").GetGuid());
        Task<string> CounterAsync() => ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/messages/{thread["segundo"]}/unread")).StatusCode);
        Assert.Equal($"2|{readVersion + 1}", await CounterAsync());
        // Idempotente: el mismo ancla no sube nada.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/messages/{thread["segundo"]}/unread")).StatusCode);
        Assert.Equal($"2|{readVersion + 1}", await CounterAsync());
        // GREATEST: desde el tercero serían 1, y ya hay 2.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/messages/{thread["tercero"]}/unread")).StatusCode);
        Assert.Equal($"2|{readVersion + 1}", await CounterAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/messages/{thread["hola"]}/unread")).StatusCode);
        Assert.Equal($"3|{readVersion + 2}", await CounterAsync());
        // Ningún acuse a Meta por las marcas: sólo el del read.
        Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));
        var page = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);
        Assert.Equal(3, page.GetProperty("items")[0].GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task UnreadFromAMessageRejectsAForeignMessageAndTheReadPermissionAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573009999999", "wamid.other", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "otra conversación"));
        await DrainDeliveriesAsync(f.Factory);
        var items = (await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct)).GetProperty("items").EnumerateArray().ToArray();
        var otherConversation = items.Single(item => item.GetProperty("id").GetGuid() != f.ConversationId).GetProperty("id").GetGuid();
        var foreignMessage = (await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, otherConversation), Ct)).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("direction").GetString() == "Inbound").GetProperty("id").GetGuid();
        using var readOnly = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);

        var foreign = await SendAsync(f.Client, HttpMethod.Post, $"{url}/messages/{foreignMessage}/unread");
        var unknown = await SendAsync(f.Client, HttpMethod.Post, $"{url}/messages/{Guid.NewGuid()}/unread");
        var forbidden = await SendAsync(readOnly, HttpMethod.Post, $"{url}/messages/{foreignMessage}/unread");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("messaging.message.not_found", (await ProblemAsync(foreign)).Code);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal($"1|{f.Version}", await ScalarAsync<string>(f.ConnectionString, "SELECT unread_count || '|' || version FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId)));
    }

    [Fact]
    public async Task ResolveAndReopenFollowIfMatchWithTheirCodesAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);

        var without = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve");
        var stale = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: "\"999\"");
        var resolved = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{f.Version}\"");
        var summary = await resolved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var again = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{summary.GetProperty("version").GetInt64()}\"");
        var reopened = await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{summary.GetProperty("version").GetInt64()}\"");
        var reopenedSummary = await reopened.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var openAgain = await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{reopenedSummary.GetProperty("version").GetInt64()}\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, without.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ProblemAsync(without)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(stale)).Code);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal("Resolved", summary.GetProperty("status").GetString());
        Assert.Equal(f.Version + 1, summary.GetProperty("version").GetInt64());
        Assert.Equal("Ventas", summary.GetProperty("connectionName").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("messaging.conversation.already_resolved", (await ProblemAsync(again)).Code);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal("Open", reopenedSummary.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, openAgain.StatusCode);
        Assert.Equal("messaging.conversation.already_open", (await ProblemAsync(openAgain)).Code);
        Assert.Equal("messaging.conversation.reopened,messaging.conversation.resolved", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(action, ',' ORDER BY action) FROM audit.entries WHERE source = 'messaging' AND resource_type = 'conversation' AND resource_id = @id", ("id", f.ConversationId.ToString())));
    }

    private static async Task<JsonElement> ResolveAsync(Fixture f)
    {
        var response = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/resolve", ifMatch: $"\"{f.Version}\"");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // Spec 2026-10-16 §3.2: resolver con no leídos quita la marca en la misma transacción y acusa el último entrante, una vez.
    [Fact]
    public async Task ResolvingClearsTheMarkAndAcknowledgesOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");

        var summary = await ResolveAsync(f);

        Assert.Equal(0, summary.GetProperty("unreadCount").GetInt32());
        Assert.Equal(f.Version + 1, summary.GetProperty("version").GetInt64());
        var ack = Assert.Single(f.Factory.MetaHandler.Requests, IsReadReceipt);
        Assert.Contains("\"message_id\":\"wamid.in\"", ack.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvingWithNothingUnreadSendsNoReceipt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 0");
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");

        var summary = await ResolveAsync(f);

        Assert.Equal(0, summary.GetProperty("unreadCount").GetInt32());
        Assert.DoesNotContain(f.Factory.MetaHandler.Requests, IsReadReceipt);
    }

    // Fuera de la ventana de 30 días Meta ya no acepta el acuse: la marca se quita igual, sin llamada.
    [Fact]
    public async Task ResolvingAnOldConversationClearsWithoutAReceipt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '31 days'");
        f.Factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"success":true}""");

        var summary = await ResolveAsync(f);

        Assert.Equal(0, summary.GetProperty("unreadCount").GetInt32());
        Assert.DoesNotContain(f.Factory.MetaHandler.Requests, IsReadReceipt);
    }

    // §7.5: la ingesta sube version; un resolve con la versión vieja es 412.
    [Fact]
    public async Task AnInboundThatArrivesAfterLoadingMakesTheResolveConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "otra"));
        await DrainDeliveriesAsync(f.Factory);

        var stale = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/resolve", ifMatch: $"\"{f.Version}\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task ReadResolveAndReopenRejectOtherTenantsAndTheReadPermissionAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var readOnly = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(readOnly, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(otherClient, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/resolve", ifMatch: "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(otherClient, HttpMethod.Post, $"{ConversationUrl(other.TenantId, f.ConversationId)}/resolve", ifMatch: "\"1\"")).StatusCode);

        // Decisión 5: con el módulo apagado, los tres son 403 aunque el permiso esté.
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var url = ConversationUrl(f.Tenant.TenantId, f.ConversationId);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{f.Version}\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{f.Version}\"")).StatusCode);
    }
}
