using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Messaging;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-26 D-C6 contra Postgres: el consumidor <c>shopify.city-synonym-events</c> lee del outbox de plataforma lo que
/// publica Geography, siembra los trabajos y marca el inbox en el mismo guardado. El evento se escribe a mano con el literal del
/// contrato: lo que Geography escribe lo amarra <c>GeographyEventPublisherTests</c>.
/// </summary>
public sealed class ShopifyCityCorrectionFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task InsertOutboxAsync(string connectionString, Guid id, string eventName, string payload) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts, last_error)
            VALUES (@id, @name, CAST(@payload AS jsonb), @correlation, now(), NULL, 0, NULL)
            """,
            ("id", id),
            ("name", eventName),
            ("payload", payload),
            ("correlation", id.ToString()));

    private static string Occurrence(Guid tenantId, Guid mirrorId, Guid orderId, Guid? customerId, bool partyCityOnly, bool customerCityOnly) =>
        $$"""
        {"occurrenceId":"{{Guid.CreateVersion7()}}","tenantId":"{{tenantId}}","source":"shopify","sourceRef":"{{mirrorId}}","orderId":"{{orderId}}","customerId":{{(customerId is { } id ? $"\"{id}\"" : "null")}},
         "partyCityOnly":{{(partyCityOnly ? "true" : "false")}},"customerCityOnly":{{(customerCityOnly ? "true" : "false")}}}
        """;

    // Dos pasadas del consumidor: la primera siembra un trabajo de pedido y uno de cliente para la primera ocurrencia y uno de
    // pedido para la segunda, y marca el mensaje procesado en el mismo guardado; la segunda no lo vuelve a tomar.
    [Fact]
    public async Task AResolvedEventInTheOutboxSeedsJobsOnceEvenIfProcessedTwice()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (messageId, synonymId, operatorId, tenantId) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (mirrorA, orderA, customerA, mirrorB, orderB) =
            (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertOutboxAsync(
            connectionString,
            messageId,
            CitySynonymEventsProcessor.ResolvedEvent,
            $$"""
            {"synonymId":"{{synonymId}}","departmentId":"{{Guid.CreateVersion7()}}","cityId":"{{Guid.CreateVersion7()}}","normalizedText":"CALLI","operatorSubjectId":"{{operatorId}}","operatorTenantId":"{{Guid.CreateVersion7()}}",
             "occurredAt":"2026-10-26T15:00:00+00:00",
             "occurrences":[{{Occurrence(tenantId, mirrorA, orderA, customerA, true, true)}},{{Occurrence(tenantId, mirrorB, orderB, null, true, false)}}]}
            """);
        var processor = factory.Services.GetRequiredService<CitySynonymEventsProcessor>();

        await processor.ProcessBatchAsync(Ct);
        await processor.ProcessBatchAsync(Ct);

        var seeded = await ScalarAsync<string[]>(
            connectionString,
            """
            SELECT array_agg(target || '|' || target_id) FROM shopify.city_correction_jobs
            WHERE synonym_id = @synonym AND source_message_id = @message AND kind = 'Resolve' AND status = 'Pending' AND attempts = 0
            """,
            ("synonym", synonymId),
            ("message", messageId));
        Assert.Equal(
            new[] { $"Customer|{customerA}", $"Order|{orderA}", $"Order|{orderB}" }.Order(StringComparer.Ordinal),
            seeded.Order(StringComparer.Ordinal));
        Assert.Equal(3L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.city_correction_jobs"));
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT attempts FROM shopify.inbox_messages WHERE consumer = 'shopify.city-synonym-events' AND message_id = @id AND processed_at IS NOT NULL",
            ("id", messageId)));

        // El trabajo vuelve del repositorio como se sembró: es lo que lee el worker que corrige.
        var jobId = await ScalarAsync<Guid>(
            connectionString, "SELECT id FROM shopify.city_correction_jobs WHERE target = 'Customer'");
        using var scope = factory.Services.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionJobRepository>().FindAsync(jobId, Ct);
        Assert.NotNull(job);
        Assert.Equal(
            (tenantId, ShopifyCityCorrectionJobKind.Resolve, customerA, (Guid?)mirrorA, (Guid?)null, operatorId, (Guid?)messageId, ShopifyCityCorrectionJobStatus.Pending, (string?)null, (DateTimeOffset?)null),
            (job.TenantId, job.Kind, job.TargetId, job.MirrorOrderId, job.CorrectionId, job.OperatorSubjectId, job.SourceMessageId, job.Status, job.SkipReason, job.ProcessedAt));
    }

    // Un payload que no se puede leer no se arregla reintentando: se marca procesado sin efecto (y se loguea en Warning), en
    // vez de volver cada 5 minutos para siempre.
    [Fact]
    public async Task AMalformedEventIsMarkedProcessedWithoutJobs()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var messageId = Guid.CreateVersion7();
        await InsertOutboxAsync(connectionString, messageId, CitySynonymEventsProcessor.CorrectedEvent, """{"synonymId":"not-a-guid"}""");

        await factory.Services.GetRequiredService<CitySynonymEventsProcessor>().ProcessBatchAsync(Ct);

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.city_correction_jobs"));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM shopify.inbox_messages WHERE message_id = @id AND processed_at IS NOT NULL",
            ("id", messageId)));
    }
}
