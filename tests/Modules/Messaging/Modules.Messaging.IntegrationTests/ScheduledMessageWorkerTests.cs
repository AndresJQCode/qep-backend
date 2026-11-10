using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.ScheduledMessages;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-09 prueba 9 y «Worker y ejecución»: el worker de programados contra Postgres. El reclamo con <c>FOR UPDATE SKIP
/// LOCKED</c> manda una sola vez aunque corran dos pasadas a la vez y salta la fila que una edición tiene tomada; el cierre es CAS y audita
/// <c>System</c>; la recuperación de un lease vencido no suma intentos; la purga es diaria. El bucket es <see cref="FakeMessagingBucket"/> y
/// Graph el <see cref="FakeMetaGraphHandler"/> del harness.</summary>
public sealed class ScheduledMessageWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeMessagingBucket Bucket, string ConnectionString, RegisteredTenant Tenant,
        Guid ConversationId, Guid MemberId);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, Action<IServiceCollection>? configure = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var bucket = new FakeMessagingBucket();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessagingMediaStore>(bucket);
            services.AddSingleton<IMessagingUploadStaging>(bucket);
            configure?.Invoke(services);
        }));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(host);
        using var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var memberId = await OwnerMembershipIdAsync(connectionString, tenant);
        factory.MetaHandler.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.out"}""");
        factory.MetaHandler.Respond("/111/messages$", HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.scheduled"}]}""");
        return new Fixture(factory, host, bucket, connectionString, tenant, conversationId, memberId);
    }

    private static int MetaSends(Fixture f) =>
        f.Factory.MetaHandler.Requests.Count(request => request.Uri!.AbsolutePath == "/v24.0/111/messages" && !IsReadReceipt(request));

    /// <summary>Un texto ya vencido, guardado por el repositorio como lo deja el POST.</summary>
    private static async Task<ScheduledMessage> SeedDueTextAsync(Fixture f)
    {
        var now = DateTimeOffset.UtcNow;
        var message = ScheduledMessage.CreateText(
            Guid.CreateVersion7(), f.Tenant.TenantId, f.ConversationId, Guid.CreateVersion7(), "Te escribo como quedamos", now.AddSeconds(-5), f.MemberId, now.AddMinutes(-10));
        await AddAsync(f, message);
        return message;
    }

    private static async Task AddAsync(Fixture f, ScheduledMessage message)
    {
        using var scope = f.Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IScheduledMessageRepository>().AddAsync(message, Ct);
    }

    private static Task<string> StatusAsync(Fixture f, Guid id) =>
        ScalarAsync<string>(f.ConnectionString, "SELECT status FROM messaging.scheduled_messages WHERE id = @id", ("id", id));

    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    /// <summary>Un documento terminal (<c>Cancelled</c> por defecto) cuyo objeto sigue en el bucket: lo que el barrido de purga recoge.</summary>
    private static async Task<(ScheduledMessage Message, string Key)> SeedTerminalDocumentAsync(Fixture f, string status = "Cancelled")
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7();
        var key = MessagingMediaKeys.Scheduled(f.Tenant.TenantId, id, ".pdf");
        var pdf = Pdf();
        f.Bucket.Objects[key] = (pdf, "application/pdf");
        var message = ScheduledMessage.CreateMedia(
            id, f.Tenant.TenantId, f.ConversationId, Guid.CreateVersion7(), MessageKind.Document, isVoiceNote: false, "El contrato",
            new ScheduledMedia(key, "application/pdf", "contrato.pdf", pdf.Length, null), now.AddHours(1), f.MemberId, now);
        await AddAsync(f, message);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.scheduled_messages SET status = @status WHERE id = @id", ("status", status), ("id", id));
        return (message, key);
    }

    [Fact]
    public async Task ADueTextIsSentOnceAndTheRowBecomesSent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var scheduled = await SeedDueTextAsync(f);

        await DrainScheduledMessagesAsync(f.Host);
        await DrainScheduledMessagesAsync(f.Host);

        Assert.Equal(1, MetaSends(f));
        Assert.Equal("Sent", await StatusAsync(f, scheduled.Id));
        var messageId = await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT id FROM messaging.messages WHERE conversation_id = @c AND client_id = @client", ("c", f.ConversationId), ("client", scheduled.ClientId));
        Assert.Equal(messageId, await ScalarAsync<Guid>(f.ConnectionString, "SELECT sent_message_id FROM messaging.scheduled_messages WHERE id = @id", ("id", scheduled.Id)));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.scheduled_messages WHERE id = @id AND locked_until IS NULL AND attempts = 1",
            ("id", scheduled.Id)));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.scheduled_message.sent' AND resource_type = 'scheduled_message' AND resource_id = @id AND actor_type = 'System' AND actor_id = @empty",
            ("id", scheduled.Id.ToString()), ("empty", Guid.Empty)));
    }

    [Fact]
    public async Task TwoConcurrentDrainsSendOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var scheduled = await SeedDueTextAsync(f);

        await Task.WhenAll(DrainScheduledMessagesAsync(f.Host), DrainScheduledMessagesAsync(f.Host));

        Assert.Equal(1, MetaSends(f));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @c AND client_id = @client", ("c", f.ConversationId), ("client", scheduled.ClientId)));
        Assert.Equal("Sent", await StatusAsync(f, scheduled.Id));
    }

    [Fact]
    public async Task TwoConcurrentClaimsNeverReturnTheSameRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var scheduled = await SeedDueTextAsync(f);
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.scheduled_messages WHERE id = @id", ("id", scheduled.Id));

        async Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimAsync()
        {
            using var scope = f.Host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>()
                .ClaimDueAsync(20, DateTimeOffset.UtcNow, ScheduledMessageDispatcher.LeaseLength, Ct);
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(attempt => ClaimAsync()));

        var claimed = Assert.Single(claims.SelectMany(rows => rows));
        Assert.Equal((scheduled.Id, 1, MessageKind.Text, "Te escribo como quedamos"), (claimed.Id, claimed.Attempts, claimed.Kind, claimed.Text));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.scheduled_messages WHERE id = @id AND status = 'Sending' AND attempts = 1 AND locked_until > now() + interval '9 minutes' AND version = @version",
            ("id", scheduled.Id), ("version", version + 1)));
    }

    [Fact]
    public async Task AnEditHoldingTheRowMakesTheWorkerSkipIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var scheduled = await SeedDueTextAsync(f);

        using (var scope = f.Host.Services.CreateScope())
        {
            await using var rowLock = await scope.ServiceProvider.GetRequiredService<IScheduledMessageRepository>()
                .LockAsync(f.Tenant.TenantId, f.ConversationId, scheduled.Id, Ct);
            Assert.NotNull(rowLock);

            await DrainScheduledMessagesAsync(f.Host);

            Assert.Equal(0, MetaSends(f));
        }

        Assert.Equal("Scheduled", await StatusAsync(f, scheduled.Id));
        await DrainScheduledMessagesAsync(f.Host);
        Assert.Equal(1, MetaSends(f));
    }

    [Fact]
    public async Task ASentRowDeletesItsScheduledObject()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7();
        var key = MessagingMediaKeys.Scheduled(f.Tenant.TenantId, id, ".pdf");
        var pdf = Pdf();
        f.Bucket.Objects[key] = (pdf, "application/pdf");
        await AddAsync(f, ScheduledMessage.CreateMedia(
            id, f.Tenant.TenantId, f.ConversationId, Guid.CreateVersion7(), MessageKind.Document, isVoiceNote: false, "El contrato",
            new ScheduledMedia(key, "application/pdf", "contrato.pdf", pdf.Length, null), now.AddSeconds(-5), f.MemberId, now.AddMinutes(-10)));

        await DrainScheduledMessagesAsync(f.Host);

        Assert.Equal("Sent", await StatusAsync(f, id));
        Assert.Equal(1, MetaSends(f));
        Assert.False(f.Bucket.Objects.ContainsKey(key));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.scheduled_messages WHERE id = @id AND media_storage_key IS NULL", ("id", id)));
        var messageId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT sent_message_id FROM messaging.scheduled_messages WHERE id = @id", ("id", id));
        Assert.Equal(pdf, f.Bucket.Objects[MessagingMediaKeys.For(f.Tenant.TenantId, messageId)].Bytes);
    }

    [Fact]
    public async Task AnExpiredSendingRowWithoutMessageIsUnknownOutcome()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var scheduled = await SeedDueTextAsync(f);
        // Un worker que reclamó (intento 3) y murió: Sending con el lease vencido y sin fila de mensaje.
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.scheduled_messages SET status = 'Sending', attempts = 3, locked_until = now() - interval '1 minute' WHERE id = @id",
            ("id", scheduled.Id));
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.scheduled_messages WHERE id = @id", ("id", scheduled.Id));

        await DrainScheduledMessagesAsync(f.Host);

        Assert.Equal(0, MetaSends(f));
        // La recuperación no suma intentos: el reclamo de vencidos sólo renueva el lease y el cierre CAS usa el mismo valor.
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM messaging.scheduled_messages
             WHERE id = @id AND status = 'Failed' AND failure_code = 'messaging.scheduled.unknown_outcome' AND attempts = 3 AND locked_until IS NULL
               AND version = @version
            """,
            ("id", scheduled.Id), ("version", version + 1)));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.scheduled_message.failed' AND resource_id = @id AND actor_type = 'System'",
            ("id", scheduled.Id.ToString())));
    }

    [Fact]
    public async Task ClaimingExpiredRowsRenewsTheLeaseWithoutAddingAnAttempt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var scheduled = await SeedDueTextAsync(f);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.scheduled_messages SET status = 'Sending', attempts = 2, locked_until = now() - interval '1 minute' WHERE id = @id",
            ("id", scheduled.Id));

        using var scope = f.Host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>();
        var claimed = Assert.Single(await store.ClaimExpiredAsync(20, DateTimeOffset.UtcNow, ScheduledMessageDispatcher.LeaseLength, Ct));

        Assert.Equal((scheduled.Id, 2), (claimed.Id, claimed.Attempts));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.scheduled_messages WHERE id = @id AND status = 'Sending' AND attempts = 2 AND locked_until > now() + interval '9 minutes'",
            ("id", scheduled.Id)));
        // Ya con el lease renovado, otra pasada no la vuelve a tomar.
        Assert.Empty(await store.ClaimExpiredAsync(20, DateTimeOffset.UtcNow, ScheduledMessageDispatcher.LeaseLength, Ct));
    }

    [Fact]
    public async Task ALateCloseWithAnOldAttemptWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var scheduled = await SeedDueTextAsync(f);
        using var scope = f.Host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>();
        var claimed = Assert.Single(await store.ClaimDueAsync(20, DateTimeOffset.UtcNow, ScheduledMessageDispatcher.LeaseLength, Ct));

        var stale = await store.CompleteFailedAsync(claimed with { Attempts = claimed.Attempts - 1 }, MessagingErrorCodes.WindowClosed, DateTimeOffset.UtcNow, Ct);
        // Defensa en profundidad: el mismo id y el mismo intento con otro tenant no cierran nada.
        Assert.False(await store.CompleteSentAsync(claimed with { TenantId = Guid.CreateVersion7() }, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct));
        Assert.False(await store.CompleteFailedAsync(claimed with { TenantId = Guid.CreateVersion7() }, MessagingErrorCodes.WindowClosed, DateTimeOffset.UtcNow, Ct));
        var current = await store.CompleteFailedAsync(claimed, MessagingErrorCodes.WindowClosed, DateTimeOffset.UtcNow, Ct);
        var again = await store.CompleteSentAsync(claimed, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Ct);

        Assert.Equal((false, true, false), (stale, current, again));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.scheduled_messages WHERE id = @id AND status = 'Failed' AND failure_code = 'messaging.window_closed' AND sent_message_id IS NULL",
            ("id", scheduled.Id)));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_type = 'scheduled_message' AND resource_id = @id AND action IN ('messaging.scheduled_message.failed', 'messaging.scheduled_message.sent')",
            ("id", scheduled.Id.ToString())));
    }

    [Fact]
    public async Task APurgeSweepClearsTerminalRowsAndRunsOncePerInterval()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var (cancelled, cancelledKey) = await SeedTerminalDocumentAsync(f);
        var time = new SteppedTimeProvider();
        // Una instancia propia: el reloj es de la prueba y el tick de arranque del host no comparte su estado.
        var worker = new ScheduledMessageWorker(
            f.Host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MessagingWorkerOptions { PurgeIntervalHours = 24 }),
            time,
            NullLogger<ScheduledMessageWorker>.Instance);

        await worker.DrainAsync(Ct);

        Assert.False(f.Bucket.Objects.ContainsKey(cancelledKey));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.scheduled_messages WHERE id = @id AND media_storage_key IS NULL", ("id", cancelled.Id)));

        // Antes del intervalo, una pasada no barre; cumplido, sí.
        var (sent, sentKey) = await SeedTerminalDocumentAsync(f, "Sent");
        time.Advance(TimeSpan.FromHours(23));
        await worker.DrainAsync(Ct);
        Assert.True(f.Bucket.Objects.ContainsKey(sentKey));

        time.Advance(TimeSpan.FromHours(1));
        await worker.DrainAsync(Ct);
        Assert.False(f.Bucket.Objects.ContainsKey(sentKey));
        Assert.Equal(1, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.scheduled_messages WHERE id = @id AND media_storage_key IS NULL", ("id", sent.Id)));
    }

    [Fact]
    public async Task OneSweepPurgesMoreThanOneBatch()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var keys = new List<string>();
        for (var i = 0; i < ScheduledMessageDispatcher.BatchSize + 5; i++)
        {
            keys.Add((await SeedTerminalDocumentAsync(f)).Key);
        }

        var worker = new ScheduledMessageWorker(
            f.Host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MessagingWorkerOptions { PurgeIntervalHours = 24 }),
            new SteppedTimeProvider(),
            NullLogger<ScheduledMessageWorker>.Instance);

        await worker.DrainAsync(Ct);

        Assert.DoesNotContain(keys, f.Bucket.Objects.ContainsKey);
        Assert.Equal(0, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.scheduled_messages WHERE conversation_id = @c AND media_storage_key IS NOT NULL", ("c", f.ConversationId)));
    }

    [Fact]
    public async Task APurgeNeverTouchesAnActiveRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var (failed, failedKey) = await SeedTerminalDocumentAsync(f, "Failed");
        using var scope = f.Host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>();

        Assert.Empty(await store.ListPurgeableAsync(20, Ct));
        await store.ClearMediaKeyAsync(f.Tenant.TenantId, failed.Id, Ct);

        Assert.Equal(failedKey, await ScalarAsync<string>(f.ConnectionString,
            "SELECT media_storage_key FROM messaging.scheduled_messages WHERE id = @id", ("id", failed.Id)));
    }

    [Fact]
    public async Task ARecoveryFailureDoesNotStopTheDueRowsFromSending()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, services =>
            services.AddScoped<IScheduledMessageStore>(provider => new RecoveryFailingStore(ActivatorUtilities.CreateInstance<ScheduledMessageStore>(provider))));
        using var _ = f.Factory;
        using var __ = f.Host;
        var scheduled = await SeedDueTextAsync(f);

        await DrainScheduledMessagesAsync(f.Host);

        Assert.Equal(1, MetaSends(f));
        Assert.Equal("Sent", await StatusAsync(f, scheduled.Id));
    }

    /// <summary>El reclamo de vencidos falla como fallaría la base; el resto del store es el real.</summary>
    private sealed class RecoveryFailingStore(IScheduledMessageStore inner) : IScheduledMessageStore
    {
        public Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimDueAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
            inner.ClaimDueAsync(take, now, lease, cancellationToken);

        public Task<IReadOnlyList<ClaimedScheduledMessage>> ClaimExpiredAsync(int take, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The database is unavailable.");

        public Task<bool> CompleteSentAsync(ClaimedScheduledMessage message, Guid sentMessageId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.CompleteSentAsync(message, sentMessageId, now, cancellationToken);

        public Task<bool> CompleteFailedAsync(ClaimedScheduledMessage message, string failureCode, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.CompleteFailedAsync(message, failureCode, now, cancellationToken);

        public Task<IReadOnlyList<PurgeableScheduledMedia>> ListPurgeableAsync(int take, CancellationToken cancellationToken) =>
            inner.ListPurgeableAsync(take, cancellationToken);

        public Task ClearMediaKeyAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            inner.ClearMediaKeyAsync(tenantId, id, cancellationToken);
    }

    /// <summary>Un reloj que sólo avanza cuando la prueba lo mueve (la cadencia de la purga se mide con <c>GetTimestamp</c>).</summary>
    private sealed class SteppedTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }
}
