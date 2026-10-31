using Bootstrapper.Observability;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Observabilidad fase 2, tarea 3: profundidad y edad de las seis colas, leídas con SQL crudo contra el esquema migrado.
/// La edad sólo mira filas que nadie ha intentado (<c>attempts = 0</c>); el backlog las cuenta todas.</summary>
public sealed class QueueMetricsSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        WebApplicationFactory<Program> Host, string ConnectionString, Guid TenantId, Guid ConnectionId, Guid ConversationId);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var host = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(host, tenant.TenantId, connectionId, "573001234567");
        // El registro del tenant deja eventos sin despachar en el outbox compartido: sin cerrarlos, la cola no parte de cero.
        await ExecuteAsync(connectionString, "UPDATE platform.outbox_messages SET processed_at = now() WHERE processed_at IS NULL");
        return new Fixture(host, connectionString, tenant.TenantId, connectionId, conversationId);
    }

    private static async Task<QueueReading> ReadAsync(Fixture f, string queue)
    {
        using var scope = f.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQueueMetricsSource>().ReadAsync(queue, Ct);
    }

    private static Task<int> WebhookAsync(Fixture f, int secondsAgo, int attempts, bool processed = false) =>
        ExecuteAsync(f.ConnectionString, """
            INSERT INTO messaging.webhook_deliveries (provider, body_sha256, payload, received_at, attempts, processed_at)
            VALUES ('whatsapp', sha256(convert_to(gen_random_uuid()::text, 'UTF8')), '{}', now() - make_interval(secs => @ago), @attempts,
                    CASE WHEN @processed THEN now() END)
            """, ("ago", (double)secondsAgo), ("attempts", attempts), ("processed", processed));

    private static Task<int> InboxAsync(Fixture f, Guid conversationId, int secondsAgo, short attempts) =>
        ExecuteAsync(f.ConnectionString, """
            INSERT INTO messaging.automation_inbox (message_id, tenant_id, connection_id, conversation_id, opened, created_at, next_attempt_at, attempts)
            VALUES (gen_random_uuid(), @t, @k, @c, true, now() - make_interval(secs => @ago), now(), @attempts)
            """, ("t", f.TenantId), ("k", f.ConnectionId), ("c", conversationId), ("ago", (double)secondsAgo), ("attempts", attempts));

    private static async Task<Guid> MessageAsync(Fixture f, bool imported, int secondsAgo)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString, """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, created_at, imported)
            VALUES (@id, @c, @t, @k, now() - make_interval(secs => @ago), 1, 1, 2, 'x', now() - make_interval(secs => @ago), @imported)
            """, ("id", id), ("c", f.ConversationId), ("t", f.TenantId), ("k", f.ConnectionId), ("ago", (double)secondsAgo), ("imported", imported));
        return id;
    }

    private static Task<int> MediaAsync(Fixture f, Guid messageId, string nextAttemptAt, int attempts = 0) =>
        ExecuteAsync(f.ConnectionString, $"""
            INSERT INTO messaging.message_media (message_id, meta_media_id, mime_type, next_attempt_at, attempts)
            VALUES (@m, 'media-1', 'image/jpeg', '{nextAttemptAt}'::timestamptz, @attempts)
            """, ("m", messageId), ("attempts", attempts));

    private static async Task<Guid> CampaignAsync(Fixture f, string status, bool frozen)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString, """
            INSERT INTO messaging.campaigns (id, tenant_id, connection_id, created_by_member_id, name, template_name, template_language, template_category,
                parameters, status, audience_customer_ids, audience_tag_ids, audience_frozen_at, created_at, updated_at, version)
            VALUES (@id, @t, @k, gen_random_uuid(), 'c', 'plantilla', 'es', 'MARKETING', '{}', @status, '{}', ARRAY[gen_random_uuid()],
                    CASE WHEN @frozen THEN now() END, now(), now(), 1)
            """, ("id", id), ("t", f.TenantId), ("k", f.ConnectionId), ("status", status), ("frozen", frozen));
        return id;
    }

    private static Task<int> RecipientAsync(Fixture f, Guid campaignId, string status, int attempts = 0) =>
        ExecuteAsync(f.ConnectionString, """
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, attempts, next_attempt_at)
            VALUES (gen_random_uuid(), @c, @t, gen_random_uuid(), 'Beto', '573001112222', @status, @attempts, now())
            """, ("c", campaignId), ("t", f.TenantId), ("status", status), ("attempts", attempts));

    private static Task<int> OutboxAsync(Fixture f, int secondsAgo) =>
        ExecuteAsync(f.ConnectionString, """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (gen_random_uuid(), 'queue-metrics.test', '{}'::jsonb, 'queue-metrics-tests', now() - make_interval(secs => @ago), 0)
            """, ("ago", (double)secondsAgo));

    private static Task<int> ShopifyAsync(Fixture f, int secondsAgo) =>
        ExecuteAsync(f.ConnectionString, """
            INSERT INTO shopify.webhook_deliveries (webhook_id, topic, shop_domain, payload, received_at)
            VALUES (gen_random_uuid()::text, 'orders/create', 'tienda.myshopify.com', '{}', now() - make_interval(secs => @ago))
            """, ("ago", (double)secondsAgo));

    [Theory]
    [InlineData(QueueNames.Webhook)]
    [InlineData(QueueNames.Automation)]
    [InlineData(QueueNames.Media)]
    [InlineData(QueueNames.Campaign)]
    [InlineData(QueueNames.Outbox)]
    [InlineData(QueueNames.ShopifyWebhook)]
    public async Task AnEmptyQueueReadsZero(string queue)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);

        var reading = await ReadAsync(f, queue);

        Assert.Equal(queue, reading.Queue);
        Assert.Equal(0, reading.Backlog);
        Assert.Equal(0, reading.Retrying);
        // La campaña no tiene columna de edad: no publica, no dice cero.
        Assert.Equal(queue == QueueNames.Campaign ? null : 0d, reading.OldestAgeSeconds);
    }

    [Fact]
    public async Task AQueueOutsideTheListIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReadAsync(f, "nope"));
    }

    [Fact]
    public async Task WebhookAgeIgnoresRowsAlreadyRetried()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        await WebhookAsync(f, 3600, attempts: 3);
        await WebhookAsync(f, 60, attempts: 0);
        await WebhookAsync(f, 7200, attempts: 0, processed: true);

        var reading = await ReadAsync(f, QueueNames.Webhook);

        Assert.Equal(2, reading.Backlog);
        Assert.Equal(1, reading.Retrying);
        Assert.InRange(reading.OldestAgeSeconds!.Value, 55, 90);
    }

    [Fact]
    public async Task AutomationAgeSkipsRowsHeldByAnOlderPendingRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        var other = await SeedConversationAsync(f.Host, f.TenantId, f.ConnectionId, "573009998888");
        await InboxAsync(f, f.ConversationId, 600, attempts: 2);
        await InboxAsync(f, f.ConversationId, 300, attempts: 0);

        var held = await ReadAsync(f, QueueNames.Automation);

        Assert.Equal(2, held.Backlog);
        Assert.Equal(1, held.Retrying);
        Assert.Equal(0, held.OldestAgeSeconds);

        await InboxAsync(f, other, 30, attempts: 0);

        var free = await ReadAsync(f, QueueNames.Automation);

        Assert.InRange(free.OldestAgeSeconds!.Value, 25, 60);
    }

    [Fact]
    public async Task GivenUpMediaIsNotBacklog()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        var message = await MessageAsync(f, imported: false, secondsAgo: 120);
        await MediaAsync(f, message, "9999-01-01", attempts: 8);

        var reading = await ReadAsync(f, QueueNames.Media);

        Assert.Equal(0, reading.Backlog);
        Assert.Equal(0, reading.Retrying);
    }

    [Fact]
    public async Task ImportedMediaCountsAsBacklogButNotAge()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        var message = await MessageAsync(f, imported: true, secondsAgo: 7200);
        await MediaAsync(f, message, "2000-01-01");

        var reading = await ReadAsync(f, QueueNames.Media);

        Assert.Equal(1, reading.Backlog);
        Assert.Equal(0, reading.OldestAgeSeconds);
    }

    [Fact]
    public async Task LiveMediaAgeComesFromItsMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        var message = await MessageAsync(f, imported: false, secondsAgo: 90);
        await MediaAsync(f, message, "2000-01-01");

        var reading = await ReadAsync(f, QueueNames.Media);

        Assert.Equal(1, reading.Backlog);
        Assert.InRange(reading.OldestAgeSeconds!.Value, 85, 120);
    }

    [Fact]
    public async Task CampaignCountsOnlySendingAndFrozen()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        var unfrozen = await CampaignAsync(f, "Sending", frozen: false);
        await RecipientAsync(f, unfrozen, "Pending");

        Assert.Equal(0, (await ReadAsync(f, QueueNames.Campaign)).Backlog);

        var frozen = await CampaignAsync(f, "Sending", frozen: true);
        await RecipientAsync(f, frozen, "Sending", attempts: 1);
        await RecipientAsync(f, frozen, "Sent");

        var reading = await ReadAsync(f, QueueNames.Campaign);

        Assert.Equal(1, reading.Backlog);
        Assert.Equal(1, reading.Retrying);
        Assert.Null(reading.OldestAgeSeconds);
    }

    [Fact]
    public async Task OutboxAgeUsesOccurredAt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        await OutboxAsync(f, 45);

        var reading = await ReadAsync(f, QueueNames.Outbox);

        Assert.Equal(1, reading.Backlog);
        Assert.InRange(reading.OldestAgeSeconds!.Value, 40, 90);
    }

    [Fact]
    public async Task ShopifyWebhookAgeUsesReceivedAt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        await ShopifyAsync(f, 75);

        var reading = await ReadAsync(f, QueueNames.ShopifyWebhook);

        Assert.Equal(1, reading.Backlog);
        Assert.InRange(reading.OldestAgeSeconds!.Value, 70, 120);
    }
}
