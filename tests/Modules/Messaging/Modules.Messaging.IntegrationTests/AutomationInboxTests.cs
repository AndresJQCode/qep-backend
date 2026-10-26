using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 D-M1 y D-M16: la ingesta deja una fila en <c>messaging.automation_inbox</c> en su misma transacción,
/// sólo con el flag prendido, sin asesor y con la automatización en None o Active; el puerto la reclama en orden por conversación,
/// con lease escalonado, y lee el entrante que el motor va a procesar.</summary>
public sealed class AutomationInboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, string ConnectionString, RegisteredTenant Tenant, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool enqueue = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString, enqueue);
        // Un almacén y un Graph falsos para la copia de medios de ReadInboundReportsWhetherMediaIsStored.
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(new MemoryMediaStore())));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, host, connectionString, tenant, host.CreateClient());
    }

    private sealed class MemoryMediaStore : IMessagingMediaStore
    {
        private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            objects[key] = buffer.ToArray();
        }

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(objects.TryGetValue(key, out var bytes) ? new MediaStreamDto(new MemoryStream(bytes), "image/jpeg", bytes.Length, null) : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}?signed=1", UriKind.Absolute));
    }

    private static async Task PostAsync(Fixture f, string json)
    {
        await PostWebhookAsync(f.Client, json);
        await DrainDeliveriesAsync(f.Host);
    }

    private static Task PostTextAsync(Fixture f, string wamid, long timestamp = 0) =>
        PostAsync(f, MetaPayloads.InboundText("111", "573001234567", wamid, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600 + timestamp, "hola"));

    private static Task<long> InboxCountAsync(Fixture f) => CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.automation_inbox");

    private static async Task<T> WithInboxAsync<T>(Fixture f, Func<IAutomationInbox, Task<T>> action)
    {
        using var scope = f.Host.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAutomationInbox>());
    }

    [Fact]
    public async Task AnInboundToAnUnassignedConversationIsEnqueuedAsOpened()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostTextAsync(f, "w1");

        Assert.Equal("true|0|false", await ScalarAsync<string>(f.ConnectionString,
            "SELECT opened::text || '|' || attempts || '|' || (processed_at IS NOT NULL)::text FROM messaging.automation_inbox"));
        Assert.Equal(1L, await InboxCountAsync(f));
    }

    [Fact]
    public async Task ASecondInboundIsEnqueuedAsNotOpened()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostTextAsync(f, "w1");
        await PostTextAsync(f, "w2", 10);

        Assert.Equal("true,false", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(opened::text, ',' ORDER BY created_at, message_id) FROM messaging.automation_inbox"));
    }

    [Fact]
    public async Task AReopeningInboundIsEnqueuedAsOpened()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved'");

        await PostTextAsync(f, "w2", 10);

        Assert.Equal("true,true", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(opened::text, ',' ORDER BY created_at, message_id) FROM messaging.automation_inbox"));
    }

    [Fact]
    public async Task AnInboundToAnAssignedConversationIsNotEnqueued()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        var owner = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now()", ("m", owner));

        await PostTextAsync(f, "w2", 10);

        Assert.Equal(1L, await InboxCountAsync(f));
    }

    [Fact]
    public async Task AnInboundToAPausedAutomationIsNotEnqueued()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET automation_status = 'Paused', automation_started_at = now(), automation_chatbot_id = @c, automation_paused_at = now(), automation_pause_reason = 'Manual'",
            ("c", Guid.CreateVersion7()));

        await PostTextAsync(f, "w2", 10);

        Assert.Equal(1L, await InboxCountAsync(f));
    }

    [Fact]
    public async Task MetaRedeliveryDoesNotEnqueueTwice()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;

        await PostAsync(f, MetaPayloads.InboundText("111", "573001234567", "w1", at, "hola"));
        // Mismo wamid con otra hora (bytes distintos): otra entrega, el mismo mensaje.
        await PostAsync(f, MetaPayloads.InboundText("111", "573001234567", "w1", at + 1, "hola"));

        Assert.Equal(1L, await InboxCountAsync(f));
    }

    /// <summary>Meta reintenta una entrega fallida hasta horas después (precedente 2026-10-22: un «hola» de las 21:42 llegó a las 22:01,
    /// detrás de otro de las 21:56 que el bot ya había contestado, y el bot lo contestó otra vez). Un entrante anterior al último que la
    /// conversación ya tiene entra al hilo en su lugar, pero no le toca al bot.</summary>
    [Fact]
    public async Task ALateInboundOlderThanTheLastOneIsNotEnqueued()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostTextAsync(f, "w-new", 10);
        await PostTextAsync(f, "w-old");

        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal(1L, await InboxCountAsync(f));
        Assert.Equal("w-new", await ScalarAsync<string>(f.ConnectionString,
            "SELECT message.wamid FROM messaging.automation_inbox AS inbox JOIN messaging.messages AS message ON message.id = inbox.message_id"));
    }

    /// <summary>El timestamp de Meta tiene resolución de segundos: dos entrantes del mismo segundo no están fuera de orden.</summary>
    [Fact]
    public async Task AnInboundInTheSameSecondAsTheLastOneIsEnqueued()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostTextAsync(f, "w1", 10);
        await PostTextAsync(f, "w2", 10);

        Assert.Equal(2L, await InboxCountAsync(f));
    }

    [Fact]
    public async Task WithTheFlagOffNothingIsEnqueued()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, enqueue: false);
        using var _ = f.Factory;

        await PostTextAsync(f, "w1");

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal(0L, await InboxCountAsync(f));
    }

    [Fact]
    public async Task ClaimReturnsOnlyTheOldestPendingPerConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        await PostTextAsync(f, "w2", 10);
        var first = await ScalarAsync<Guid>(f.ConnectionString, "SELECT message_id FROM messaging.automation_inbox ORDER BY created_at, message_id LIMIT 1");
        var later = DateTimeOffset.UtcNow.AddMinutes(5);

        var claimed = await WithInboxAsync(f, inbox => inbox.ClaimAsync(later, 10, Ct));

        var item = Assert.Single(claimed);
        Assert.Equal(first, item.MessageId);
        Assert.Equal(f.Tenant.TenantId, item.TenantId);
        Assert.True(item.Opened);
        Assert.Equal(1, item.Attempts);
        // Mientras la primera sigue pendiente (aunque esté en lease) la segunda espera.
        Assert.Empty(await WithInboxAsync(f, inbox => inbox.ClaimAsync(later.AddSeconds(5), 10, Ct)));

        Assert.True(await WithInboxAsync(f, inbox => inbox.CompleteAsync(f.Tenant.TenantId, first, item.Attempts, "handled", Ct)));
        var next = Assert.Single(await WithInboxAsync(f, inbox => inbox.ClaimAsync(later, 10, Ct)));

        Assert.NotEqual(first, next.MessageId);
        Assert.False(next.Opened);
        Assert.Equal("handled", await ScalarAsync<string>(f.ConnectionString, "SELECT outcome FROM messaging.automation_inbox WHERE message_id = @id", ("id", first)));
    }

    [Fact]
    public async Task ClaimLeasesAndRetryLaterFailsAtFiveAttempts()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        var id = await ScalarAsync<Guid>(f.ConnectionString, "SELECT message_id FROM messaging.automation_inbox");
        var now = DateTimeOffset.UtcNow.AddMinutes(5);

        // El primer reclamo toma un lease de 30 s: antes de que venza, nadie más lo ve.
        Assert.Single(await WithInboxAsync(f, inbox => inbox.ClaimAsync(now, 10, Ct)));
        Assert.Empty(await WithInboxAsync(f, inbox => inbox.ClaimAsync(now.AddSeconds(20), 10, Ct)));
        var claimed = Assert.Single(await WithInboxAsync(f, inbox => inbox.ClaimAsync(now.AddSeconds(31), 10, Ct)));
        // El segundo es de 2 min.
        Assert.Empty(await WithInboxAsync(f, inbox => inbox.ClaimAsync(now.AddSeconds(31).AddSeconds(100), 10, Ct)));

        for (var attempt = 3; attempt <= 5; attempt++)
        {
            var token = claimed.Attempts;
            Assert.True(await WithInboxAsync(f, inbox => inbox.RetryLaterAsync(f.Tenant.TenantId, id, token, TimeSpan.FromSeconds(1), Ct)));
            Assert.Equal("false", await ScalarAsync<string>(f.ConnectionString, "SELECT (processed_at IS NOT NULL)::text FROM messaging.automation_inbox"));
            var at = now.AddHours(attempt);
            claimed = Assert.Single(await WithInboxAsync(f, inbox => inbox.ClaimAsync(at, 10, Ct)));
        }

        Assert.Equal(5, claimed.Attempts);
        Assert.True(await WithInboxAsync(f, inbox => inbox.RetryLaterAsync(f.Tenant.TenantId, id, claimed.Attempts, TimeSpan.FromSeconds(1), Ct)));

        Assert.Equal("failed|true", await ScalarAsync<string>(f.ConnectionString, "SELECT outcome || '|' || (processed_at IS NOT NULL)::text FROM messaging.automation_inbox"));
        Assert.Empty(await WithInboxAsync(f, inbox => inbox.ClaimAsync(now.AddDays(1), 10, Ct)));
    }

    /// <summary>M4: un worker que se pasó de su lease no cierra ni reprograma la fila que otra réplica ya volvió a reclamar.</summary>
    [Fact]
    public async Task AWorkerWhoseLeaseWasReclaimedCannotCloseOrRescheduleTheRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        var item = Assert.Single(await WithInboxAsync(f, inbox => inbox.ClaimAsync(DateTimeOffset.UtcNow.AddMinutes(5), 10, Ct)));
        Assert.Equal(1, item.Attempts);
        // Otra réplica la reclamó tras vencer el lease: attempts avanzó y el testigo del primer worker quedó viejo.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.automation_inbox SET attempts = 2");

        Assert.False(await WithInboxAsync(f, inbox => inbox.CompleteAsync(f.Tenant.TenantId, item.MessageId, item.Attempts, "handled", Ct)));
        Assert.False(await WithInboxAsync(f, inbox => inbox.RetryLaterAsync(f.Tenant.TenantId, item.MessageId, item.Attempts, TimeSpan.FromDays(1), Ct)));
        // Tampoco cierra desde otro tenant, aun con el testigo vigente.
        Assert.False(await WithInboxAsync(f, inbox => inbox.CompleteAsync(Guid.CreateVersion7(), item.MessageId, 2, "handled", Ct)));

        Assert.Equal("true", await ScalarAsync<string>(f.ConnectionString,
            "SELECT (processed_at IS NULL AND outcome IS NULL AND next_attempt_at < now() + interval '1 hour')::text FROM messaging.automation_inbox"));
    }

    [Fact]
    public async Task ReadInboundExtractsTheReplyId()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostAsync(f, MetaPayloads.InboundButtonReply("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600, "tech", "Soporte técnico"));
        var id = await ScalarAsync<Guid>(f.ConnectionString, "SELECT message_id FROM messaging.automation_inbox");

        var inbound = await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(f.Tenant.TenantId, id, Ct));

        Assert.NotNull(inbound);
        Assert.Equal(MessageKind.Interactive, inbound.Kind);
        Assert.Equal("tech", inbound.ReplyId);
        Assert.Equal("Soporte técnico", inbound.Text);
        Assert.Null(inbound.Media);
        // Otro tenant no lee el mensaje.
        Assert.Null(await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(Guid.CreateVersion7(), id, Ct)));
    }

    [Fact]
    public async Task ReadInboundCarriesTheWamid()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "wamid.ABC");
        var id = await ScalarAsync<Guid>(f.ConnectionString, "SELECT message_id FROM messaging.automation_inbox");

        var inbound = await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(f.Tenant.TenantId, id, Ct));

        Assert.NotNull(inbound);
        Assert.Equal("wamid.ABC", inbound.Wamid);
    }

    /// <summary>Final review F1 y F2 (spec 2026-10-26 D-5): el mensaje que cita el entrante, como id de QEP (<c>reply_to_message_id</c>,
    /// no el wamid), y su conversación, para que Política de datos ate la respuesta a su pregunta y a su conversación.</summary>
    [Fact]
    public async Task ReadInboundCarriesTheQuotedMessageAndTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "wamid.Q1");
        var quoted = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE wamid = 'wamid.Q1'");
        await PostAsync(f, MetaPayloads.Inbound(
            "111", MetaPayloads.UserIdFor("573001234567"), "573001234567", "wamid.Q2", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 590, "sí",
            quotedWamid: "wamid.Q1"));
        var reply = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE wamid = 'wamid.Q2'");
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT conversation_id FROM messaging.messages WHERE id = @id", ("id", reply));

        var quoting = await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(f.Tenant.TenantId, reply, Ct));
        var unquoted = await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(f.Tenant.TenantId, quoted, Ct));

        Assert.Equal(quoted, quoting!.ReplyToMessageId);
        Assert.Equal(conversationId, quoting.ConversationId);
        Assert.Null(unquoted!.ReplyToMessageId);
        Assert.Equal(conversationId, unquoted.ConversationId);
    }

    [Fact]
    public async Task ReadInboundReportsWhetherMediaIsStored()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var bytes = Encoding.UTF8.GetBytes("imagen de prueba");
        f.Factory.MetaHandler.Respond("/media-1", System.Net.HttpStatusCode.OK,
            $$"""{"url":"https://lookaside.test/m/1","mime_type":"image/jpeg","sha256":"{{Convert.ToHexStringLower(SHA256.HashData(bytes))}}","file_size":{{bytes.Length}},"id":"media-1"}""");
        f.Factory.MetaHandler.RespondWith("^/m/1$", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await PostAsync(f, MetaPayloads.InboundMedia("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "image", "media-1", "image/jpeg", caption: "leyenda"));
        var id = await ScalarAsync<Guid>(f.ConnectionString, "SELECT message_id FROM messaging.automation_inbox");

        var before = await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(f.Tenant.TenantId, id, Ct));
        await DrainMediaAsync(f.Host);
        var after = await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(f.Tenant.TenantId, id, Ct));

        Assert.Equal(MessageKind.Image, before!.Kind);
        Assert.Equal(new InboundMediaForAutomation("image/jpeg", null, false), before.Media);
        Assert.Equal(new InboundMediaForAutomation("image/jpeg", null, true), after!.Media);
        // P2-R4: sin texto, el motor lee la leyenda (una palabra clave también se dispara con la de una foto, D-C6).
        Assert.Equal("leyenda", before.Text);
    }

    [Fact]
    public async Task FindLastInboundIdIgnoresOutboundsAndEvents()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        await PostTextAsync(f, "w2", 10);
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var last = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.messages WHERE wamid = 'w2'");
        // Un saliente y un evento más nuevos no cuentan: el aviso del traspaso suena como el último entrante (D-M5).
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, details, created_at)
            SELECT gen_random_uuid(), c.id, c.tenant_id, c.connection_id, now() + make_interval(mins => n), d, k, 2, t, j::jsonb, now()
              FROM messaging.conversations AS c,
                   (VALUES (1, 2, 1, 'x', NULL), (2, 3, 13, NULL, '{"type":"Released"}')) AS v(n, d, k, t, j)
            """);

        Assert.Equal(last, await WithInboxAsync(f, inbox => inbox.FindLastInboundIdAsync(f.Tenant.TenantId, conversationId, Ct)));
        Assert.Null(await WithInboxAsync(f, inbox => inbox.FindLastInboundIdAsync(Guid.CreateVersion7(), conversationId, Ct)));
    }

    [Fact]
    public async Task TwoMessagesOfOneChangeAreClaimedInTheOrderMetaSentThem()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostAsync(f, MetaPayloads.InboundTwoTexts("111", "573001234567", ("w1", "primero"), ("w2", "segundo"), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600));
        var later = DateTimeOffset.UtcNow.AddMinutes(5);

        var first = Assert.Single(await WithInboxAsync(f, inbox => inbox.ClaimAsync(later, 10, Ct)));
        Assert.Equal("primero", (await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(f.Tenant.TenantId, first.MessageId, Ct)))!.Text);
        await WithInboxAsync(f, inbox => inbox.CompleteAsync(f.Tenant.TenantId, first.MessageId, first.Attempts, "handled", Ct));

        var second = Assert.Single(await WithInboxAsync(f, inbox => inbox.ClaimAsync(later, 10, Ct)));
        Assert.Equal("segundo", (await WithInboxAsync(f, inbox => inbox.ReadInboundAsync(f.Tenant.TenantId, second.MessageId, Ct)))!.Text);
        // M5: el microsegundo por posición sólo corre la cola; los dos mensajes guardan el mismo instante de ingesta.
        Assert.Equal(1L, await ScalarAsync<long>(f.ConnectionString, "SELECT count(DISTINCT created_at) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal(2L, await ScalarAsync<long>(f.ConnectionString, "SELECT count(DISTINCT created_at) FROM messaging.automation_inbox"));
    }

    [Fact]
    public async Task APoisonRowIsClosedAsFailedAfterFiveClaimsWithoutRetryLater()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        var now = DateTimeOffset.UtcNow.AddMinutes(5);

        // El worker se cuelga: reclama, nunca llama a Complete ni a RetryLater, y el lease vence.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Single(await WithInboxAsync(f, inbox => inbox.ClaimAsync(now.AddHours(attempt), 10, Ct)));
        }

        Assert.Empty(await WithInboxAsync(f, inbox => inbox.ClaimAsync(now.AddHours(6), 10, Ct)));
        Assert.Equal("failed|true", await ScalarAsync<string>(f.ConnectionString, "SELECT outcome || '|' || (processed_at IS NOT NULL)::text FROM messaging.automation_inbox"));
    }

    /// <summary>Ruling P2-R7: la purga borra por lotes sólo lo procesado antes del corte; lo reciente y lo pendiente (aunque sea viejo)
    /// quedan.</summary>
    [Fact]
    public async Task PurgeProcessedBeforeDeletesOnlyOldProcessedRowsInBatches()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await PostTextAsync(f, "w1");
        await PostTextAsync(f, "w2", 10);
        await PostTextAsync(f, "w3", 20);
        await PostTextAsync(f, "w4", 30);
        await ExecuteAsync(f.ConnectionString,
            """
            UPDATE messaging.automation_inbox AS inbox
               SET processed_at = CASE message.wamid WHEN 'w3' THEN now() - interval '6 days' ELSE now() - interval '8 days' END,
                   outcome = 'handled'
              FROM messaging.messages AS message WHERE message.id = inbox.message_id AND message.wamid IN ('w1', 'w2', 'w3')
            """);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.automation_inbox SET created_at = now() - interval '30 days'");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);

        Assert.Equal(1, await WithInboxAsync(f, inbox => inbox.PurgeProcessedBeforeAsync(cutoff, 1, Ct)));
        Assert.Equal(1, await WithInboxAsync(f, inbox => inbox.PurgeProcessedBeforeAsync(cutoff, 1000, Ct)));
        Assert.Equal(0, await WithInboxAsync(f, inbox => inbox.PurgeProcessedBeforeAsync(cutoff, 1000, Ct)));

        Assert.Equal("w3:handled,w4:-", await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT string_agg(message.wamid || ':' || coalesce(inbox.outcome, '-'), ',' ORDER BY message.wamid)
              FROM messaging.automation_inbox AS inbox JOIN messaging.messages AS message ON message.id = inbox.message_id
            """));
    }

    [Fact]
    public async Task KindsTheEngineDoesNotReadAreNotEnqueued()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await PostAsync(f, MetaPayloads.Change("messages", MetaPayloads.LocationValue("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600, 4.6, -74.1, "Casa", "Calle 1")));

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal(0L, await InboxCountAsync(f));
    }

    [Fact]
    public async Task AnInstagramDirectMessageIsNotEnqueued()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedInstagramConnectionAsync(f.Host, f.Tenant.TenantId, "Instagram Ventas", InstagramPayloads.DefaultAccountId);
        f.Factory.MetaHandler.Respond($@"^/v24\.0/{InstagramPayloads.DefaultUserId}\?", System.Net.HttpStatusCode.OK, """{"name":"Laura","username":"laura","id":"1234567890123456"}""");

        await PostInstagramWebhookAsync(f.Client, InstagramPayloads.Text("m1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600, "hola"));
        await DrainDeliveriesAsync(f.Host);

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 1"));
        Assert.Equal(0L, await InboxCountAsync(f));
    }
}
