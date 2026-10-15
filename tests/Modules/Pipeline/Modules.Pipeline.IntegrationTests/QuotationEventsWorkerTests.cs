using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §4.4, §4.5, D6 y P12 del plan: el worker consume los tres eventos con reclamo e inbox propio, cada uno
/// una sola vez, y guarda el efecto, el historial y la auditoría en el mismo SaveChanges que el inbox.</summary>
public sealed class QuotationEventsWorkerTests
{
    private const string Sent = "quotations.quotation.sent.v1";
    private const string Converted = "quotations.quotation.converted.v1";
    private const string OrderCreated = "quotations.order.created.v1";
    private const string Cancelled = "quotations.order.cancelled.v1";

    private static object SentPayload(Guid tenantId, Guid quotationId, Guid clientId, decimal total = 1_000m) => new
    {
        tenantId,
        quotationId,
        quotationNumber = "COT-0001",
        clientId,
        advisorMemberId = Guid.CreateVersion7(),
        total,
        currency = "COP",
        occurredAt = DateTimeOffset.UtcNow,
    };

    private static object ConvertedPayload(Guid tenantId, Guid quotationId, Guid clientId, Guid orderId) => new
    {
        tenantId,
        quotationId,
        quotationNumber = "COT-0001",
        clientId,
        advisorMemberId = Guid.CreateVersion7(),
        total = 1_000m,
        currency = "COP",
        occurredAt = DateTimeOffset.UtcNow,
        orderId,
        orderNumber = "PED-0001",
    };

    private static Task<string> StageOfAsync(string connectionString, Guid tenantId, Guid customerId) =>
        ScalarAsync<string>(connectionString,
            "SELECT s.name FROM pipeline.deals d JOIN pipeline.stages s ON s.id = d.stage_id WHERE d.tenant_id = @t AND d.customer_id = @c",
            ("t", tenantId), ("c", customerId));

    [Fact]
    public async Task SentConvertedAndCancelledFlowThroughTheWorker()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var customerId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        await InsertOutboxAsync(connectionString, Sent, SentPayload(tenant.TenantId, quotationId, customerId));
        await DrainQuotationEventsAsync(factory);
        var afterSent = await StageOfAsync(connectionString, tenant.TenantId, customerId);
        await InsertOutboxAsync(connectionString, Converted, ConvertedPayload(tenant.TenantId, quotationId, customerId, orderId));
        await DrainQuotationEventsAsync(factory);
        var afterConverted = await StageOfAsync(connectionString, tenant.TenantId, customerId);
        await InsertOutboxAsync(connectionString, Cancelled, new { tenantId = tenant.TenantId, orderId, quotationId, occurredAt = DateTimeOffset.UtcNow });
        await DrainQuotationEventsAsync(factory);

        Assert.Equal(("Cotizado", "Ganado"), (afterSent, afterConverted));
        Assert.Equal("Cotizado", await StageOfAsync(connectionString, tenant.TenantId, customerId));
        Assert.Equal(3L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.inbox_messages WHERE consumer = 'pipeline.quotation-events' AND processed_at IS NOT NULL"));
        Assert.Equal(4L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.deal_stage_history WHERE tenant_id = @t AND actor_kind = 'System'", ("t", tenant.TenantId)));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.deal.reopened' AND actor_type = 'System'", ("t", tenant.TenantId)));
    }

    // Spec 2026-10-15 §7: un pedido sin cotización crea el negocio ganado con su foto, y su anulación lo reabre.
    [Fact]
    public async Task AnOrderWithoutQuotationAndItsCancellationFlowThroughTheWorker()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var customerId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        await InsertOutboxAsync(connectionString, OrderCreated, new
        {
            tenantId = tenant.TenantId,
            orderId,
            orderNumber = "PED-0001",
            clientId = customerId,
            advisorMemberId = Guid.CreateVersion7(),
            total = 500m,
            currency = "COP",
            source = "Shopify",
            quotationId = (Guid?)null,
            occurredAt = DateTimeOffset.UtcNow,
        });
        await DrainQuotationEventsAsync(factory);
        var afterCreated = await StageOfAsync(connectionString, tenant.TenantId, customerId);
        await InsertOutboxAsync(connectionString, Cancelled,
            new { tenantId = tenant.TenantId, orderId, quotationId = (Guid?)null, source = "Shopify", occurredAt = DateTimeOffset.UtcNow });
        await DrainQuotationEventsAsync(factory);

        Assert.Equal("Ganado", afterCreated);
        Assert.NotEqual("Ganado", await StageOfAsync(connectionString, tenant.TenantId, customerId));
        Assert.Equal("Order", await ScalarAsync<string>(connectionString,
            "SELECT source FROM pipeline.deals WHERE tenant_id = @t AND customer_id = @c", ("t", tenant.TenantId), ("c", customerId)));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.deal_orders WHERE tenant_id = @t AND order_id = @o AND deal_id IS NOT NULL AND cancelled_at IS NOT NULL AND source = 'Shopify'",
            ("t", tenant.TenantId), ("o", orderId)));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.deal.order_linked' AND actor_type = 'System'", ("t", tenant.TenantId)));
    }

    // Review Focus 1: el mismo mensaje dos veces no duplica el negocio.
    [Fact]
    public async Task AMessageIsAppliedOnceEvenIfDrainedTwice()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var customerId = Guid.CreateVersion7();
        await InsertOutboxAsync(connectionString, Sent, SentPayload(tenant.TenantId, Guid.CreateVersion7(), customerId));

        await DrainQuotationEventsAsync(factory);
        await DrainQuotationEventsAsync(factory);

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals WHERE customer_id = @c", ("c", customerId)));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.inbox_messages WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task AMalformedPayloadIsMarkedProcessedWithoutEffect()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var messageId = await InsertOutboxAsync(connectionString, Sent, new { tenantId = "nope" });

        await DrainQuotationEventsAsync(factory);

        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM pipeline.inbox_messages WHERE message_id = @id AND processed_at IS NOT NULL", ("id", messageId)));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals"));
    }

    // P12: sin la capacidad, procesado sin efecto; activarla después no rellena el pasado.
    [Fact]
    public async Task ATenantWithoutTheCapabilityIsProcessedWithoutEffect()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await InsertOutboxAsync(connectionString, Sent, SentPayload(tenant.TenantId, Guid.CreateVersion7(), Guid.CreateVersion7()));

        await DrainQuotationEventsAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        await DrainQuotationEventsAsync(factory);

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.inbox_messages WHERE processed_at IS NOT NULL"));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals"));
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deal_quotations"));
    }
}
