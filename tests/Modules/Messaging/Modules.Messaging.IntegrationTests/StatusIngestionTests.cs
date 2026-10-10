using Microsoft.Extensions.DependencyInjection;
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
        Assert.Equal("4|131047|wamid.two|4|1", await StateAsync(f, sent));
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
}
