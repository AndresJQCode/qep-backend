using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Messaging;
using Modules.Shopify.Infrastructure.Workers;
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

    // Spec 2026-10-26 §4.4 de punta a punta, sin dobles en el camino: un pedido «CALLI» entra por el webhook sin parte de envío (la
    // dirección cruda en las notas), el operador confirma CALLI → Cali (la fila por SQL y el trabajo sembrado) y el worker que
    // corrige pone la parte en Cali, quita la línea cruda de las notas y deja la huella y la auditoría en el tenant del pedido.
    [Fact]
    public async Task TheWorkerCorrectsARealPendingOrder()
    {
        await using var shop = await CalliShop.StartAsync();
        var job = await shop.SeedResolveAsync();

        await shop.DrainCorrectionsAsync();

        Assert.Equal("Applied|none|1", await shop.JobAsync(job));
        Assert.Equal(
            $"{shop.CaliId}|false",
            await ScalarAsync<string>(
                shop.ConnectionString,
                """
                SELECT p.city_id || '|' || (o.notes LIKE '%Dirección de envío de Shopify: %')
                FROM quotations.orders o JOIN quotations.order_parties p ON p.order_id = o.id AND p.role = 'Shipping'
                WHERE o.id = @order AND o.tenant_id = @tenant
                """,
                ("order", shop.OrderId),
                ("tenant", shop.World.TenantId)));
        Assert.Equal(
            $"Correction|{shop.CaliId}|{shop.MirrorId}",
            await ScalarAsync<string>(
                shop.ConnectionString,
                """
                SELECT origin || '|' || city_id || '|' || mirror_order_id FROM shopify.city_corrections
                WHERE tenant_id = @tenant AND synonym_id = @synonym AND target = 'Order' AND target_id = @order
                """,
                ("tenant", shop.World.TenantId),
                ("synonym", shop.SynonymId),
                ("order", shop.OrderId)));
        Assert.Equal(
            $"System|order|{shop.SynonymId}|{shop.OperatorId}|{job}",
            await ScalarAsync<string>(
                shop.ConnectionString,
                """
                SELECT actor_type || '|' || resource_type || '|' || (changed_fields ->> 'synonymId') || '|'
                       || (changed_fields ->> 'operatorSubjectId') || '|' || (changed_fields ->> 'jobId')
                FROM audit.entries WHERE tenant_id = @tenant AND action = 'shopify.order.city_resolved' AND resource_id = @order
                """,
                ("tenant", shop.World.TenantId),
                ("order", shop.OrderId.ToString())));
    }

    // D-C6: lo que lanza deja el reclamo vivo y vuelve al vencer el lease (acá vencido por SQL entre pasadas). Al quinto intento
    // el trabajo termina Failed con el tipo y el mensaje del error, y una pasada más no lo vuelve a tomar.
    [Fact]
    public async Task AJobThatKeepsFailingEndsFailedWithItsError()
    {
        await using var shop = await CalliShop.StartAsync(services =>
        {
            services.RemoveAll<IShopifyCityCorrectionWriter>();
            services.AddSingleton<IShopifyCityCorrectionWriter, QuotationsDownWriter>();
        });
        var job = await shop.SeedResolveAsync();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await shop.DrainCorrectionsAsync();
            Assert.Equal(
                attempt < 5 ? $"Pending|none|{attempt}" : "Failed|InvalidOperationException: Quotations is down.|5",
                await shop.JobAsync(job));
            await ExecuteAsync(
                shop.ConnectionString,
                "UPDATE shopify.city_correction_jobs SET lease_until = now() - interval '1 second' WHERE id = @id",
                ("id", job));
        }

        await shop.DrainCorrectionsAsync();

        Assert.Equal("Failed|InvalidOperationException: Quotations is down.|5", await shop.JobAsync(job));
        Assert.Equal(0L, await ScalarAsync<long>(
            shop.ConnectionString, "SELECT count(*) FROM quotations.order_parties WHERE order_id = @order", ("order", shop.OrderId)));
    }

    /// <summary>Un escritor que siempre falla, como Quotations caído.</summary>
    private sealed class QuotationsDownWriter : IShopifyCityCorrectionWriter
    {
        public Task<ShopifyCityWriteResult> AttachShippingAsync(
            Guid tenantId, Guid orderId, ShopifyPartyDraft party, string notesLinePrefix, CancellationToken cancellationToken) =>
            throw Down();

        public Task<ShopifyCityWriteResult> CorrectShippingCityAsync(
            Guid tenantId, Guid orderId, ShopifyShippingFingerprint expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken) =>
            throw Down();

        public Task<ShopifyCityWriteResult> CompleteCustomerAsync(
            Guid tenantId, Guid customerId, long expectedVersion, ShopifyCompleteCustomer data, CancellationToken cancellationToken) =>
            throw Down();

        public Task<ShopifyCityWriteResult> CorrectCustomerCityAsync(
            Guid tenantId, Guid customerId, long expectedVersion, Guid previousCityId, Guid cityId, CancellationToken cancellationToken) =>
            throw Down();

        private static InvalidOperationException Down() => new("Quotations is down.");
    }

    /// <summary>Una tienda lista con un pedido «CALLI» del Valle ya importado por el webhook (sin parte de envío) y el sinónimo
    /// CALLI → Cali escrito por SQL, como lo deja confirmarlo en la consola del operador.</summary>
    private sealed class CalliShop : IAsyncDisposable
    {
        private readonly TestDatabase database;

        private CalliShop(TestDatabase database, QepApiFactory factory, ShopifyWorld world)
        {
            this.database = database;
            Factory = factory;
            World = world;
        }

        public QepApiFactory Factory { get; }

        public ShopifyWorld World { get; }

        public string ConnectionString => database.GetConnectionString();

        public Guid SynonymId { get; } = Guid.CreateVersion7();

        public Guid OperatorId { get; } = Guid.CreateVersion7();

        public Guid CaliId { get; private set; }

        public Guid MirrorId { get; private set; }

        public Guid OrderId { get; private set; }

        public static async Task<CalliShop> StartAsync(Action<IServiceCollection>? configure = null)
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString()) { ConfigureTestServices = configure };
            var world = await ShopifyWorld.ArrangeAsync(factory, database.GetConnectionString());
            await world.ConfigureStoreAsync(factory);
            var shop = new CalliShop(database, factory, world);
            await shop.ImportCalliAsync();
            return shop;
        }

        public async Task<Guid> SeedResolveAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var job = ShopifyCityCorrectionJob.Resolve(
                World.TenantId, SynonymId, ShopifyCityCorrectionTarget.Order, OrderId, MirrorId, OperatorId, Guid.CreateVersion7(),
                DateTimeOffset.UtcNow);
            scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionJobRepository>().Add(job);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
            return job.Id;
        }

        public Task DrainCorrectionsAsync() =>
            ActivatorUtilities.CreateInstance<ShopifyCityCorrectionWorker>(Factory.Services).DrainAsync(Ct);

        /// <summary><c>status|last_error|attempts</c> del trabajo.</summary>
        public Task<string> JobAsync(Guid id) =>
            ScalarAsync<string>(
                ConnectionString,
                "SELECT status || '|' || coalesce(last_error, 'none') || '|' || attempts FROM shopify.city_correction_jobs WHERE id = @id",
                ("id", id));

        public async ValueTask DisposeAsync()
        {
            World.Tenant.Seeder.Dispose();
            await Factory.DisposeAsync();
            await database.DisposeAsync();
        }

        private async Task ImportCalliAsync()
        {
            var order = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
            var address = order["shipping_address"]!.AsObject();
            address["city"] = "CALLI";
            address["province"] = "Valle del Cauca";
            address["province_code"] = "VAC";
            using var webhooks = Factory.CreateClient();
            var response = await PostWebhookAsync(webhooks, "orders/create", "wh-calli", Encoding.UTF8.GetBytes(order.ToJsonString()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await ActivatorUtilities.CreateInstance<ShopifyWebhookDeliveryWorker>(Factory.Services).DrainAsync(Ct);

            var imported = (await ScalarAsync<string>(
                ConnectionString,
                "SELECT id || '|' || qep_order_id || '|' || import_state FROM shopify.orders WHERE shopify_order_id = 5550000000001"))
                .Split('|');
            Assert.Equal("Imported", imported[2]);
            (MirrorId, OrderId) = (Guid.Parse(imported[0]), Guid.Parse(imported[1]));

            var cali = (await ScalarAsync<string>(
                ConnectionString, "SELECT id || '|' || department_id FROM geography.cities WHERE divipola_code = '76001'"))
                .Split('|');
            CaliId = Guid.Parse(cali[0]);
            await ExecuteAsync(
                ConnectionString,
                """
                INSERT INTO geography.city_synonyms
                    (id, department_id, normalized_text, display_text, city_id, created_by, created_at, updated_by, updated_at, version)
                VALUES (@id, @department, 'CALLI', 'Calli', @city, @operator, now(), @operator, now(), 1)
                """,
                ("id", SynonymId),
                ("department", Guid.Parse(cali[1])),
                ("city", CaliId),
                ("operator", OperatorId));
        }
    }
}
