using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Messaging.Infrastructure.Webhook;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §7.5 («Estados») y §8.2 («La carrera del sent antes que el wamid»), y Review
/// Focus 4: monotonía, Failed sólo sobre Sent, played → Read, -1 corregido por cualquier estado real,
/// callback que llega antes del wamid (reintento y éxito), status ajeno descartado en el acto, y la foto
/// del último mensaje sin subir la versión.</summary>
public sealed class StatusIngestionTests
{
    // `(bool)::text` da 'true'/'false', no 't'/'f': el CASE deja la bandera explícita.
    private const string DeliverySql =
        "SELECT attempts || '|' || CASE WHEN processed_at IS NOT NULL THEN 't' ELSE 'f' END FROM messaging.webhook_deliveries";

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId, Guid ConversationId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        return new Fixture(factory, connectionString, tenant.TenantId, connectionId, conversationId, factory.CreateClient());
    }

    private static async Task ApplyAsync(Fixture f, string wamid, string status, long timestamp, string? callback = null, int? errorCode = null)
    {
        await PostWebhookAsync(f.Client, MetaPayloads.Status("111", wamid, status, timestamp, callback, errorCode, errorCode is null ? null : "Some title"));
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<string> StateAsync(Fixture f, Guid messageId) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT m.status || '|' || coalesce(m.failure_code::text, '-') || '|' || coalesce(m.wamid, '-') || '|' || c.last_message_status || '|' || c.version FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.id = @id",
            ("id", messageId));

    private static async Task ReclaimAndDrainAsync(Fixture f)
    {
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.webhook_deliveries SET claimed_until = now() - interval '1 minute'");
        await DrainDeliveriesAsync(f.Factory);
    }

    [Fact]
    public async Task StatusesNeverGoBackwardsAndFailedOnlyOverridesSent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.out");

        await ApplyAsync(f, "wamid.out", "read", 1760000300);
        Assert.Equal("3|-|wamid.out|3|1", await StateAsync(f, id));
        await ApplyAsync(f, "wamid.out", "delivered", 1760000200);
        Assert.Equal("3|-|wamid.out|3|1", await StateAsync(f, id));
        await ApplyAsync(f, "wamid.out", "failed", 1760000400, errorCode: 131026);
        Assert.Equal("3|-|wamid.out|3|1", await StateAsync(f, id));

        var sent = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.two", occurredAtUnix: 1760000500);
        await ApplyAsync(f, "wamid.two", "failed", 1760000600, errorCode: 131047);
        Assert.Equal("4|131047|-|4|1", await StateAsync(f, sent));
        Assert.Equal("Some title", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_title FROM messaging.messages WHERE id = @id", ("id", sent)));

        // Review Focus 4, literal: un Delivered tampoco se vuelve Failed.
        var delivered = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.three", status: 2, occurredAtUnix: 1760000700);
        await ApplyAsync(f, "wamid.three", "failed", 1760000800, errorCode: 131026);
        Assert.Equal("2|-|wamid.three|2|1", await StateAsync(f, delivered));
    }

    [Fact]
    public async Task PlayedIsReadAndAnUnconfirmedSendIsCorrectedByAnyRealStatus()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var unconfirmed = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.u", status: 4, failureCode: -1);

        await ApplyAsync(f, "wamid.u", "sent", 1760000100);
        Assert.Equal("1|-|wamid.u|1|1", await StateAsync(f, unconfirmed));
        await ApplyAsync(f, "wamid.u", "played", 1760000200);
        Assert.Equal("3|-|wamid.u|3|1", await StateAsync(f, unconfirmed));
    }

    [Fact]
    public async Task ACallbackThatArrivesBeforeTheWamidIsRetriedAndThenApplied()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var messageId = Guid.CreateVersion7();

        await ApplyAsync(f, "wamid.late", "sent", 1760000100, callback: $"qep:{messageId}");
        Assert.Equal("1|f", await ScalarAsync<string>(f.ConnectionString, DeliverySql));

        // El envío commitea después (§8.3) y sin wamid: Meta no respondió a tiempo, así que la fila quedó
        // Failed -1 («sin confirmar»). Sólo el callback la encuentra, y el sent la corrige y le pone el wamid.
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, failure_code, created_at)
            VALUES (@id, @conversationId, @tenantId, @connectionId, now(), 2, 1, 4, 'x', -1, now())
            """, ("id", messageId), ("conversationId", f.ConversationId), ("tenantId", f.TenantId), ("connectionId", f.ConnectionId));
        await ReclaimAndDrainAsync(f);

        Assert.Equal("1|wamid.late", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || wamid FROM messaging.messages WHERE id = @id", ("id", messageId)));
        Assert.Equal("2|t", await ScalarAsync<string>(f.ConnectionString, DeliverySql));
    }

    [Fact]
    public async Task AForeignStatusWithoutCallbackIsDiscardedAtOnce()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await ApplyAsync(f, "wamid.phone-app", "sent", 1760000100);

        Assert.Equal("1|t", await ScalarAsync<string>(f.ConnectionString, DeliverySql));
    }

    [Fact]
    public async Task AfterEightAttemptsTheDeliveryIsGivenUpWithAnError()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ApplyAsync(f, "wamid.never", "sent", 1760000100, callback: $"qep:{Guid.CreateVersion7()}");
        // La curva de leases ya pasó de verdad: la entrega se recibió hace una hora.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.webhook_deliveries SET received_at = received_at - interval '1 hour'");

        for (var attempt = 2; attempt <= 8; attempt++)
        {
            await ReclaimAndDrainAsync(f);
        }

        Assert.Equal("8|t|status-before-wamid: gave up after 8 attempts", await ScalarAsync<string>(f.ConnectionString,
            "SELECT attempts || '|' || CASE WHEN processed_at IS NOT NULL THEN 't' ELSE 'f' END || '|' || last_error FROM messaging.webhook_deliveries"));
    }

    // Remarks de WebhookDeliveryWorker.Leases: attempts sube también cuando otra réplica reclama una entrega
    // grande cuyo lease venció a mitad de pasada. Ocho reclamos en segundos no son una hora de espera.
    [Fact]
    public async Task EightAttemptsInAFewSecondsDoNotGiveUpBeforeTheLeaseCurveHasElapsed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ApplyAsync(f, "wamid.soon", "sent", 1760000100, callback: $"qep:{Guid.CreateVersion7()}");

        for (var attempt = 2; attempt <= 9; attempt++)
        {
            await ReclaimAndDrainAsync(f);
        }

        Assert.Equal("9|f", await ScalarAsync<string>(f.ConnectionString, DeliverySql));
    }

    // Dos réplicas con el delivered y el read del mismo mensaje a la vez: el UPDATE del mensaje y el de la
    // foto van en una transacción, así que el candado de la fila los serializa y la foto termina en Read.
    [Fact]
    public async Task ConcurrentDeliveredAndReadLeaveTheSnapshotAtRead()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var wamid = $"wamid.race.{iteration}";
            var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, wamid, occurredAtUnix: 1760000000 + iteration);

            await Task.WhenAll(
                ApplyDirectAsync(f, new StatusUpdate(wamid, MessageStatus.Delivered, DateTimeOffset.UtcNow, null, null, null)),
                ApplyDirectAsync(f, new StatusUpdate(wamid, MessageStatus.Read, DateTimeOffset.UtcNow, null, null, null)));

            Assert.Equal($"3|-|{wamid}|3|1", await StateAsync(f, id));
        }
    }

    private static async Task ApplyDirectAsync(Fixture f, StatusUpdate update)
    {
        await Task.Yield();
        await using var scope = f.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        await StatusIngestion.ApplyAsync(dbContext, f.ConnectionId, update, TestContext.Current.CancellationToken);
    }

    // Decisión 7: con la conexión pausada los statuses sí se aplican.
    [Fact]
    public async Task StatusesApplyEvenWhenTheConnectionIsPaused()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.p");
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        await ApplyAsync(f, "wamid.p", "delivered", 1760000100);

        Assert.Equal("2|-|wamid.p|2|1", await StateAsync(f, id));
    }

    // D-M18: con el módulo apagado los statuses también se aplican; sólo los entrantes se descartan.
    [Fact]
    public async Task StatusesApplyEvenWhenTheModuleIsOff()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.off");
        // Sin la fila el módulo no está contratado: es como InboundIngestionTests arma «module-off».
        await ExecuteAsync(f.ConnectionString, "DELETE FROM tenancy.tenant_modules WHERE module_key = 'messaging'");

        await ApplyAsync(f, "wamid.off", "delivered", 1760000100);

        Assert.Equal("2|-|wamid.off|2|1", await StateAsync(f, id));
    }

    private const string StateByIdSql = "SELECT status || '|' || coalesce(wamid, '-') FROM messaging.messages WHERE id = @id";

    // Un failed rezagado del primer intento llega cuando el reintento ya fue aceptado: la fila ya es del intento nuevo (otro wamid),
    // y el callback no puede devolverla a Failed. Sólo el wamid del intento vigente (o el callback de una fila sin wamid) la identifica.
    private static async Task<Guid> SeedFailedFirstAttemptThenAcceptedRetryAsync(Fixture f)
    {
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.x");
        await ApplyAsync(f, "wamid.x", "failed", 1760000100, callback: $"qep:{id}", errorCode: 131042);
        Assert.Equal("4|131042|-|4|1", await StateAsync(f, id));

        // El reintento aceptado: lo que escribe CommitSentAsync, sin la foto (un UPDATE directo no la toca).
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.messages SET status = 1, wamid = 'wamid.y', failure_code = NULL WHERE id = @id", ("id", id));
        // Otro timestamp: el mismo cuerpo se deduplica por hash y no crearía una entrega, así que StatusIngestion no correría.
        await ApplyAsync(f, "wamid.x", "failed", 1760000200, callback: $"qep:{id}", errorCode: 131042);
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries"));
        return id;
    }

    [Fact]
    public async Task ALateFailedOfTheFirstAttemptDoesNotOverwriteAnAcceptedRetry()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var id = await SeedFailedFirstAttemptThenAcceptedRetryAsync(f);

        Assert.Equal("1|-|wamid.y|4|1", await StateAsync(f, id));
    }

    [Fact]
    public async Task AStaleStatusEndsAsProcessedAndIsNotRetried()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await SeedFailedFirstAttemptThenAcceptedRetryAsync(f);

        Assert.Equal("1|t", await ScalarAsync<string>(f.ConnectionString, DeliverySql + " ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public async Task AnUnconfirmedRetryIsResolvedByTheAckOfTheCurrentAttempt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, "wamid.x", status: 4, failureCode: 131042);
        var clientId = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.messages SET client_id = @clientId WHERE id = @id", ("clientId", clientId), ("id", id));

        // El reintento con el mismo clientId reclama la fila vieja y Meta no contesta a tiempo: queda «sin confirmar», y sin el wamid del intento anterior.
        await using (var scope = f.Factory.Services.CreateAsyncScope())
        {
            var outbound = scope.ServiceProvider.GetRequiredService<IOutboundMessages>();
            var draft = new OutboundDraft(
                Guid.CreateVersion7(), f.ConversationId, f.TenantId, f.ConnectionId, clientId, "x", null, DateTimeOffset.UtcNow,
                SentByApiKey: new OutboundApiKeyAuthor(Guid.CreateVersion7(), "integracion"));
            await using var claim = await outbound.ClaimAsync(draft, TestContext.Current.CancellationToken);
            Assert.False(claim.Inserted);
            await claim.CommitFailedAsync(-1, null, DateTimeOffset.UtcNow, null, TestContext.Current.CancellationToken);
        }

        await ApplyAsync(f, "wamid.y", "sent", 1760000200, callback: $"qep:{id}");

        Assert.Equal("1|wamid.y", await ScalarAsync<string>(f.ConnectionString, StateByIdSql, ("id", id)));
    }

    // Con un código real (131042) la versión vieja de la fila no cumple el WHERE del acuse: sólo el SELECT ... FOR UPDATE previo lo hace esperar.
    [Theory]
    [InlineData(-1)]
    [InlineData(131042)]
    public async Task AnAckDuringTheRetryWaitsForItAndAppliesOverItsResult(int failureCode)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, null, status: 4, failureCode: failureCode);

        await RunDuringRetryAsync(f, id, new StatusUpdate("wamid.y", MessageStatus.Delivered, DateTimeOffset.UtcNow, id, null, null));

        Assert.Equal("2|-|wamid.y|2|1", await StateAsync(f, id));
    }

    [Fact]
    public async Task AFailedOfTheNewAttemptDuringTheRetryEndsTheRowFailedWithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, null, status: 4, failureCode: 131042);

        await RunDuringRetryAsync(f, id, new StatusUpdate("wamid.y", MessageStatus.Failed, DateTimeOffset.UtcNow, id, 131047, "Re-engagement"));

        Assert.Equal("4|131047|-|4|1", await StateAsync(f, id));
    }

    // El reintento tiene la fila bloqueada mientras Meta responde (§8.3); el acuse del intento nuevo debe esperar ese candado y,
    // al soltarse, ver el wamid nuevo.
    private static async Task RunDuringRetryAsync(Fixture f, Guid id, StatusUpdate ackOfNewAttempt)
    {
        await using var connection = new Npgsql.NpgsqlConnection(f.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var retry = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var lockRow = new Npgsql.NpgsqlCommand("SELECT 1 FROM messaging.messages WHERE id = @id FOR UPDATE", connection, retry))
        {
            lockRow.Parameters.AddWithValue("id", id);
            await lockRow.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await using (var accept = new Npgsql.NpgsqlCommand("UPDATE messaging.messages SET status = 1, wamid = 'wamid.y', failure_code = NULL WHERE id = @id", connection, retry))
        {
            accept.Parameters.AddWithValue("id", id);
            await accept.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var ack = ApplyDirectAsync(f, ackOfNewAttempt);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(ack.IsCompleted);
        await retry.CommitAsync(TestContext.Current.CancellationToken);
        await ack;
    }

    [Fact]
    public async Task AnUnconfirmedRowWithoutAPreviousAttemptIsStillResolvedByCallback()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var id = await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.TenantId, f.ConnectionId, null, status: 4, failureCode: -1);

        await ApplyAsync(f, "wamid.z", "sent", 1760000100, callback: $"qep:{id}");

        Assert.Equal("1|wamid.z", await ScalarAsync<string>(f.ConnectionString, StateByIdSql, ("id", id)));
    }
}
