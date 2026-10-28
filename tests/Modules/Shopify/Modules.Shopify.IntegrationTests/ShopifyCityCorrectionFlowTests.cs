using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Messaging;
using Modules.Shopify.Infrastructure.Workers;
using Npgsql;
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
    // el trabajo termina Failed con la causa de fondo (GetBaseException: la de la base, no la que la envuelve), y una pasada más no
    // lo vuelve a tomar.
    [Fact]
    public async Task AJobThatKeepsFailingEndsFailedWithItsError()
    {
        await using var shop = await CalliShop.StartAsync(services =>
        {
            services.RemoveAll<IShopifyCityCorrectionWriter>();
            services.AddSingleton<IShopifyCityCorrectionWriter>(new QuotationsDownWriter(failing: null));
        });
        var job = await shop.SeedResolveAsync();
        const string failed = "Failed|TimeoutException: The database did not answer.|5";

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await shop.DrainCorrectionsAsync();
            Assert.Equal(attempt < 5 ? $"Pending|none|{attempt}" : failed, await shop.JobAsync(job));
            await ExecuteAsync(
                shop.ConnectionString,
                "UPDATE shopify.city_correction_jobs SET lease_until = now() - interval '1 second' WHERE id = @id",
                ("id", job));
        }

        await shop.DrainCorrectionsAsync();

        Assert.Equal(failed, await shop.JobAsync(job));
        Assert.Equal(0L, await ScalarAsync<long>(
            shop.ConnectionString, "SELECT count(*) FROM quotations.order_parties WHERE order_id = @order", ("order", shop.OrderId)));
    }

    // Un scope por trabajo: el que falla deja su reclamo vivo y el siguiente del mismo lote se corrige en la misma pasada.
    [Fact]
    public async Task AFailingJobDoesNotBlockItsSiblingInTheSameBatch()
    {
        var failing = new System.Runtime.CompilerServices.StrongBox<Guid>();
        await using var shop = await CalliShop.StartAsync(
            services =>
            {
                var real = services.Single(descriptor => descriptor.ServiceType == typeof(IShopifyCityCorrectionWriter));
                services.Remove(real);
                services.AddScoped<IShopifyCityCorrectionWriter>(provider => new QuotationsDownWriter(
                    failing, (IShopifyCityCorrectionWriter)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!)));
            },
            withSibling: true);
        failing.Value = shop.OrderId;
        var broken = await shop.SeedResolveAsync();
        var healthy = await shop.SeedResolveAsync(shop.SiblingOrderId, shop.SiblingMirrorId);

        await shop.DrainCorrectionsAsync();

        Assert.Equal("Pending|none|1", await shop.JobAsync(broken));
        Assert.Equal("Applied|none|1", await shop.JobAsync(healthy));
        Assert.Equal(shop.CaliId, await ScalarAsync<Guid>(
            shop.ConnectionString,
            "SELECT city_id FROM quotations.order_parties WHERE order_id = @order AND role = 'Shipping'",
            ("order", shop.SiblingOrderId)));
    }

    // D-C6 y el candado del sinónimo: mientras el worker que corrige tiene el candado (acá, otra conexión) y escribe su huella, el
    // evento corrected espera; al soltarlo, la lista de huellas ya ve la que se escribió bajo el candado y le siembra su
    // recorrección. Sin el candado, el evento listaría antes y esa huella quedaría en la ciudad vieja para siempre.
    [Fact]
    public async Task ACorrectedEventWaitsForTheSynonymLockAndSeesTheCorrectionWrittenUnderIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (synonymId, valle, cali, calima) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (tenantId, orderId) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('shopify.city-synonym:' || @id::text, 0))", holder, transaction))
        {
            lockCommand.Parameters.AddWithValue("id", synonymId);
            await lockCommand.ExecuteNonQueryAsync(Ct);
        }

        await InsertOutboxAsync(
            connectionString,
            Guid.CreateVersion7(),
            CitySynonymEventsProcessor.CorrectedEvent,
            $$"""
            {"synonymId":"{{synonymId}}","departmentId":"{{valle}}","previousCityId":"{{calima}}","cityId":"{{cali}}","operatorSubjectId":"{{Guid.CreateVersion7()}}","operatorTenantId":"{{Guid.CreateVersion7()}}","occurredAt":"2026-10-26T15:00:00+00:00"}
            """);
        var applying = factory.Services.GetRequiredService<CitySynonymEventsProcessor>().ProcessBatchAsync(Ct);
        await WaitForAnAdvisoryLockWaiterAsync(connectionString);
        Assert.False(applying.IsCompleted);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO shopify.city_corrections
                (id, tenant_id, synonym_id, origin, target, target_id, mirror_order_id, department_id, city_id, party_name, party_phone,
                 party_email, party_address, applied_at, updated_at)
            VALUES (@id, @tenant, @synonym, 'Correction', 'Order', @order, @mirror, @department, @city, 'Laura Gómez', '+573001112233',
                    'laura@verde.co', 'Cra 5 # 10-20', now(), now())
            """,
            holder,
            transaction))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("synonym", synonymId);
            insert.Parameters.AddWithValue("order", orderId);
            insert.Parameters.AddWithValue("mirror", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("department", valle);
            insert.Parameters.AddWithValue("city", calima);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
        await applying;

        Assert.Equal(
            $"Recorrect|{orderId}",
            await ScalarAsync<string>(
                connectionString,
                "SELECT kind || '|' || target_id FROM shopify.city_correction_jobs WHERE synonym_id = @synonym",
                ("synonym", synonymId)));
    }

    // N1 de punta a punta, contra Customers real: el primer intento completa al cliente en Cali y el proceso cae antes de guardar
    // en Shopify. El operador corrige el sinónimo a otra ciudad del Valle. El reintento pide esa ciudad, Customers reconoce su
    // propia escritura (la versión siguiente, en una ciudad que este trabajo pidió) y responde AlreadyApplied con Cali: la huella
    // guarda Cali, la recorrección se siembra en el mismo guardado y, al correr, lleva al cliente a la ciudad nueva.
    [Fact]
    public async Task ACustomerRetryAfterTheSynonymMovedKeepsTheWrittenCityAndRecorrectsIt()
    {
        var crash = new System.Runtime.CompilerServices.StrongBox<bool>(true);
        await using var shop = await CalliShop.StartAsync(services =>
        {
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(IShopifyCityCorrectionWriter));
            services.Remove(real);
            services.AddScoped<IShopifyCityCorrectionWriter>(provider => new CrashAfterCustomersWriter(
                crash, (IShopifyCityCorrectionWriter)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!)));
        });
        var job = await shop.SeedResolveCustomerAsync();

        await shop.DrainCorrectionsAsync();
        Assert.Equal("Pending|none|1", await shop.JobAsync(job));
        Assert.Equal($"Complete|{shop.CaliId}|2", await shop.CustomerAsync());

        var otherCity = await ScalarAsync<Guid>(
            shop.ConnectionString,
            """
            SELECT c.id FROM geography.cities c
            WHERE c.department_id = (SELECT department_id FROM geography.cities WHERE id = @cali) AND c.id <> @cali
            ORDER BY c.divipola_code LIMIT 1
            """,
            ("cali", shop.CaliId));
        await ExecuteAsync(
            shop.ConnectionString,
            "UPDATE geography.city_synonyms SET city_id = @city WHERE id = @id",
            ("city", otherCity),
            ("id", shop.SynonymId));
        await ExecuteAsync(
            shop.ConnectionString,
            "UPDATE shopify.city_correction_jobs SET lease_until = now() - interval '1 second' WHERE id = @id",
            ("id", job));

        await shop.DrainCorrectionsAsync();

        Assert.Equal("Applied|none|2", await shop.JobAsync(job));
        Assert.Equal(
            $"{shop.CaliId}|2",
            await ScalarAsync<string>(
                shop.ConnectionString,
                "SELECT city_id || '|' || customer_version FROM shopify.city_corrections WHERE target = 'Customer' AND target_id = @customer",
                ("customer", shop.CustomerId)));
        Assert.Equal(
            "Recorrect|Pending",
            await ScalarAsync<string>(
                shop.ConnectionString,
                "SELECT kind || '|' || status FROM shopify.city_correction_jobs WHERE id <> @id",
                ("id", job)));

        await shop.DrainCorrectionsAsync();

        Assert.Equal($"Complete|{otherCity}|3", await shop.CustomerAsync());
        Assert.Equal(
            $"{otherCity}|{shop.CaliId}|3",
            await ScalarAsync<string>(
                shop.ConnectionString,
                "SELECT city_id || '|' || previous_city_id || '|' || customer_version FROM shopify.city_corrections WHERE target = 'Customer'"));
    }

    // N2: otra réplica tiene tomada la fila del trabajo (lo está terminando). El reclamo la salta en vez de esperarla: si esperara,
    // podría agotar el tiempo del comando fuera del try de cada trabajo y cortar la pasada entera.
    [Fact]
    public async Task AClaimSkipsAJobAnotherReplicaIsFinishingInsteadOfWaiting()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var job = ShopifyCityCorrectionJob.Resolve(
            Guid.CreateVersion7(), Guid.CreateVersion7(), ShopifyCityCorrectionTarget.Order, Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), null, DateTimeOffset.UtcNow);
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            seed.ServiceProvider.GetRequiredService<IShopifyCityCorrectionJobRepository>().Add(job);
            await seed.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM shopify.city_correction_jobs WHERE id = @id FOR UPDATE", holder, transaction))
        {
            lockRow.Parameters.AddWithValue("id", job.Id);
            await lockRow.ExecuteScalarAsync(Ct);
        }

        int? whileHeld;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            whileHeld = await scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionJobQueue>()
                .TryClaimAsync(job.Id, DateTimeOffset.UtcNow, Ct)
                .WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }

        await transaction.CommitAsync(Ct);
        int? afterwards;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            afterwards = await scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionJobQueue>()
                .TryClaimAsync(job.Id, DateTimeOffset.UtcNow, Ct);
        }

        Assert.Equal(((int?)null, (int?)1), (whileHeld, afterwards));
    }

    // Hasta 10 s a que alguien espere un candado de aviso: es la señal de que el evento quedó detrás del candado.
    private static async Task WaitForAnAdvisoryLockWaiterAsync(string connectionString)
    {
        for (var poll = 0; poll < 100; poll++)
        {
            if (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted") > 0)
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail("Nobody waited for the synonym lock.");
    }

    /// <summary>El adaptador real, salvo que la primera vez que completa un cliente cae justo después de que Customers confirmó:
    /// el proceso que se muere entre la escritura y el guardado de Shopify.</summary>
    private sealed class CrashAfterCustomersWriter(
        System.Runtime.CompilerServices.StrongBox<bool> crash, IShopifyCityCorrectionWriter inner) : IShopifyCityCorrectionWriter
    {
        public Task<ShopifyCityWriteResult> AttachShippingAsync(
            Guid tenantId, Guid orderId, ShopifyPartyDraft party, string notesLinePrefix, CancellationToken cancellationToken) =>
            inner.AttachShippingAsync(tenantId, orderId, party, notesLinePrefix, cancellationToken);

        public Task<ShopifyCityWriteResult> CorrectShippingCityAsync(
            Guid tenantId, Guid orderId, ShopifyShippingFingerprint expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken) =>
            inner.CorrectShippingCityAsync(tenantId, orderId, expected, departmentId, cityId, cancellationToken);

        public async Task<ShopifyCityWriteResult> CompleteCustomerAsync(
            Guid tenantId,
            Guid customerId,
            long expectedVersion,
            ShopifyCompleteCustomer data,
            IReadOnlyCollection<Guid> requestedCityIds,
            CancellationToken cancellationToken)
        {
            var result = await inner.CompleteCustomerAsync(tenantId, customerId, expectedVersion, data, requestedCityIds, cancellationToken);
            if (crash.Value)
            {
                crash.Value = false;
                throw new InvalidOperationException("The process died after Customers committed.");
            }

            return result;
        }

        public Task<ShopifyCityWriteResult> CorrectCustomerCityAsync(
            Guid tenantId,
            Guid customerId,
            long expectedVersion,
            Guid previousCityId,
            Guid cityId,
            IReadOnlyCollection<Guid> requestedCityIds,
            CancellationToken cancellationToken) =>
            inner.CorrectCustomerCityAsync(tenantId, customerId, expectedVersion, previousCityId, cityId, requestedCityIds, cancellationToken);

        public Task<ShopifyOrderCityState?> ReadOrderCityStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            inner.ReadOrderCityStateAsync(tenantId, orderId, cancellationToken);
    }

    /// <summary>Un escritor que falla como Quotations caído (con la causa de fondo envuelta): para todos los pedidos, o sólo para
    /// el de <c>failing</c> y delega el resto en el adaptador real.</summary>
    private sealed class QuotationsDownWriter(
        System.Runtime.CompilerServices.StrongBox<Guid>? failing, IShopifyCityCorrectionWriter? inner = null) : IShopifyCityCorrectionWriter
    {
        public Task<ShopifyCityWriteResult> AttachShippingAsync(
            Guid tenantId, Guid orderId, ShopifyPartyDraft party, string notesLinePrefix, CancellationToken cancellationToken) =>
            Fails(orderId) ? throw Down() : inner!.AttachShippingAsync(tenantId, orderId, party, notesLinePrefix, cancellationToken);

        public Task<ShopifyCityWriteResult> CorrectShippingCityAsync(
            Guid tenantId, Guid orderId, ShopifyShippingFingerprint expected, Guid departmentId, Guid cityId, CancellationToken cancellationToken) =>
            Fails(orderId) ? throw Down() : inner!.CorrectShippingCityAsync(tenantId, orderId, expected, departmentId, cityId, cancellationToken);

        public Task<ShopifyCityWriteResult> CompleteCustomerAsync(
            Guid tenantId,
            Guid customerId,
            long expectedVersion,
            ShopifyCompleteCustomer data,
            IReadOnlyCollection<Guid> requestedCityIds,
            CancellationToken cancellationToken) =>
            throw Down();

        public Task<ShopifyCityWriteResult> CorrectCustomerCityAsync(
            Guid tenantId,
            Guid customerId,
            long expectedVersion,
            Guid previousCityId,
            Guid cityId,
            IReadOnlyCollection<Guid> requestedCityIds,
            CancellationToken cancellationToken) =>
            throw Down();

        public Task<ShopifyOrderCityState?> ReadOrderCityStateAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw Down();

        private static InvalidOperationException Down() =>
            new("Quotations is down.", new TimeoutException("The database did not answer."));

        private bool Fails(Guid orderId) => failing is null || failing.Value == orderId;
    }

    /// <summary>Una tienda lista con un pedido «CALLI» del Valle ya importado por el webhook (sin parte de envío) y el sinónimo
    /// CALLI → Cali escrito por SQL, como lo deja confirmarlo en la consola del operador. Interna y no privada: la reusa
    /// <c>CityCorrectionInsightsTests</c>, que necesita huellas reales de pedido y de cliente.</summary>
    internal sealed class CalliShop : IAsyncDisposable
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

        /// <summary>El cliente que la importación creó incompleto porque la ciudad era lo único que le faltaba.</summary>
        public Guid CustomerId { get; private set; }

        public Guid SiblingMirrorId { get; private set; }

        public Guid SiblingOrderId { get; private set; }

        /// <summary>Con <paramref name="withSibling"/>, otro pedido «CALLI» del mismo comprador, importado también antes de confirmar
        /// el sinónimo (después, la importación ya casaría CALLI y le pondría la parte).</summary>
        public static async Task<CalliShop> StartAsync(
            Action<IServiceCollection>? configure = null, bool withSibling = false, Guid? operatorTenantId = null)
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString())
            {
                ConfigureTestServices = configure,
                OperatorTenantId = operatorTenantId,
            };
            var world = await ShopifyWorld.ArrangeAsync(factory, database.GetConnectionString());
            await world.ConfigureStoreAsync(factory);
            var shop = new CalliShop(database, factory, world);
            (shop.MirrorId, shop.OrderId) = await shop.ImportCalliAsync(5550000000001, "#1001", "wh-calli");
            if (withSibling)
            {
                (shop.SiblingMirrorId, shop.SiblingOrderId) = await shop.ImportCalliAsync(5550000000002, "#1002", "wh-calli-2");
            }

            shop.CustomerId = await ScalarAsync<Guid>(
                shop.ConnectionString, "SELECT qep_customer_id FROM shopify.orders WHERE id = @id", ("id", shop.MirrorId));
            await shop.ConfirmCalliAsync();
            return shop;
        }

        public async Task<Guid> SeedResolveAsync(Guid? orderId = null, Guid? mirrorId = null)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var job = ShopifyCityCorrectionJob.Resolve(
                World.TenantId, SynonymId, ShopifyCityCorrectionTarget.Order, orderId ?? OrderId, mirrorId ?? MirrorId, OperatorId,
                Guid.CreateVersion7(), DateTimeOffset.UtcNow);
            scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionJobRepository>().Add(job);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
            return job.Id;
        }

        public async Task<Guid> SeedResolveCustomerAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var job = ShopifyCityCorrectionJob.Resolve(
                World.TenantId, SynonymId, ShopifyCityCorrectionTarget.Customer, CustomerId, MirrorId, OperatorId, Guid.CreateVersion7(),
                DateTimeOffset.UtcNow);
            scope.ServiceProvider.GetRequiredService<IShopifyCityCorrectionJobRepository>().Add(job);
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
            return job.Id;
        }

        /// <summary><c>completeness|city_id|version</c> del cliente de la importación.</summary>
        public Task<string> CustomerAsync() =>
            ScalarAsync<string>(
                ConnectionString,
                "SELECT completeness || '|' || coalesce(city_id::text, 'none') || '|' || version FROM customers.customers WHERE id = @id",
                ("id", CustomerId));

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

        private async Task<(Guid MirrorId, Guid OrderId)> ImportCalliAsync(long shopifyOrderId, string name, string webhookId)
        {
            var order = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
            order["id"] = shopifyOrderId;
            order["admin_graphql_api_id"] = $"gid://shopify/Order/{shopifyOrderId}";
            order["name"] = name;
            var address = order["shipping_address"]!.AsObject();
            address["city"] = "CALLI";
            address["province"] = "Valle del Cauca";
            address["province_code"] = "VAC";
            using var webhooks = Factory.CreateClient();
            var response = await PostWebhookAsync(webhooks, "orders/create", webhookId, Encoding.UTF8.GetBytes(order.ToJsonString()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await ActivatorUtilities.CreateInstance<ShopifyWebhookDeliveryWorker>(Factory.Services).DrainAsync(Ct);

            var imported = (await ScalarAsync<string>(
                ConnectionString,
                "SELECT id || '|' || qep_order_id || '|' || import_state FROM shopify.orders WHERE shopify_order_id = @id",
                ("id", shopifyOrderId)))
                .Split('|');
            Assert.Equal("Imported", imported[2]);
            return (Guid.Parse(imported[0]), Guid.Parse(imported[1]));
        }

        private async Task ConfirmCalliAsync()
        {
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
