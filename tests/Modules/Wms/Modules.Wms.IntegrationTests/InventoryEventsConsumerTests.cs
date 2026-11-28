using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Wms.Application;
using Modules.Wms.Infrastructure.Messaging;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>Spec inventario básico §5.2: el consumidor <c>wms.inventory-events</c> con un aplicador grabador. Prueba el orden por
/// documento, la marca de arranque, la idempotencia, el payload malformado y el estacionamiento de un documento que no pasa.</summary>
public sealed class InventoryEventsConsumerTests
{
    private const string Created = "quotations.order.created.v1";

    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(PostgreSqlContainer Database, QepApiFactory Factory, RecordingApplier Applier, CapturedLogs Logs)> OpenWithRecorderAsync()
    {
        var applier = new RecordingApplier();
        var logs = new CapturedLogs();
        var (database, factory) = await OpenAsync(configureServices: services =>
        {
            services.AddScoped<IInventoryEventApplier>(_ => applier);
            services.AddLogging(builder => builder.AddProvider(logs));
        });
        return (database, factory, applier, logs);
    }

    private static async Task<Guid> PublishAsync(
        PostgreSqlContainer database,
        Guid documentId,
        long version,
        DateTimeOffset occurredAt,
        string eventName = Created,
        string? payloadOverride = null)
    {
        var id = Guid.CreateVersion7();
        var payload = payloadOverride
            ?? string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"tenantId":"{{Tenant}}","orderId":"{{documentId}}","version":{{version}},"occurredAt":"{{occurredAt:O}}","awaitingWholesaleApproval":false,"lines":[]}""");
        await ExecuteAsync(
            database,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @event, @payload::jsonb, @correlation, @occurredAt, 0)
            """,
            ("id", id),
            ("event", eventName),
            ("payload", payload),
            ("correlation", documentId.ToString("D", CultureInfo.InvariantCulture)),
            ("occurredAt", occurredAt));
        return id;
    }

    private static Task<int> ExpireClaimsAsync(PostgreSqlContainer database) =>
        ExecuteAsync(database, "UPDATE wms.inbox_messages SET claimed_until = now() - interval '1 second' WHERE processed_at IS NULL");

    private static WmsInventoryEventsProcessor Processor(QepApiFactory factory) =>
        factory.Services.GetRequiredService<WmsInventoryEventsProcessor>();

    private static DateTimeOffset Recent(int seconds = 0) => DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(seconds);

    [Fact]
    public async Task ALaterMessageOfTheSameDocumentWaitsForTheOlderOne()
    {
        var (database, factory, applier, _) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        var document = Guid.CreateVersion7();
        var first = await PublishAsync(database, document, 1, Recent());
        var second = await PublishAsync(database, document, 2, Recent(1));
        applier.FailOnce(first);
        var processor = Processor(factory);

        await processor.ProcessBatchAsync(Ct);
        Assert.Empty(applier.Applied);

        // El viejo sigue esperando su reintento: el nuevo no pasa por encima.
        await processor.ProcessBatchAsync(Ct);
        Assert.Empty(applier.Applied);

        await ExpireClaimsAsync(database);
        await processor.ProcessBatchAsync(Ct);
        Assert.Equal([first], applier.Applied.Select(message => message.MessageId));

        await processor.ProcessBatchAsync(Ct);
        Assert.Equal([first, second], applier.Applied.Select(message => message.MessageId));
    }

    [Fact]
    public async Task DifferentDocumentsAdvanceIndependently()
    {
        var (database, factory, applier, _) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        var stuck = await PublishAsync(database, Guid.CreateVersion7(), 1, Recent());
        var free = await PublishAsync(database, Guid.CreateVersion7(), 1, Recent(1));
        applier.FailAlways(stuck);

        await Processor(factory).ProcessBatchAsync(Ct);

        Assert.Equal([free], applier.Applied.Select(message => message.MessageId));
    }

    [Fact]
    public async Task MessagesBeforeTheStartMarkAreIgnored()
    {
        var (database, factory, applier, _) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        await PublishAsync(database, Guid.CreateVersion7(), 1, InventoryStartAt.AddDays(-1));

        var more = await Processor(factory).ProcessBatchAsync(Ct);

        Assert.False(more);
        Assert.Empty(applier.Applied);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.inbox_messages"));
    }

    [Fact]
    public async Task SameOccurredAtIsOrderedByVersion()
    {
        var (database, factory, applier, _) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        var document = Guid.CreateVersion7();
        var at = Recent();
        // Se escribe primero la versión 2: su Id de outbox es menor, y sólo la versión del payload desempata.
        var second = await PublishAsync(database, document, 2, at);
        var first = await PublishAsync(database, document, 1, at);
        var processor = Processor(factory);

        for (var pass = 0; pass < 3; pass++)
        {
            await processor.ProcessBatchAsync(Ct);
        }

        Assert.Equal([first, second], applier.Applied.Select(message => message.MessageId));
    }

    [Fact]
    public async Task WithinADocumentTheVersionOrdersBeforeTheClock()
    {
        var (database, factory, applier, _) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        var document = Guid.CreateVersion7();
        // La versión 1 quedó con una hora posterior a la de la 2 (reloj atrasado): la versión manda.
        var second = await PublishAsync(database, document, 2, Recent());
        var first = await PublishAsync(database, document, 1, Recent(30));
        var processor = Processor(factory);

        for (var pass = 0; pass < 3; pass++)
        {
            await processor.ProcessBatchAsync(Ct);
        }

        Assert.Equal([first, second], applier.Applied.Select(message => message.MessageId));
    }

    [Fact]
    public async Task AClaimHoldsForMinutesAndAFailureRetriesSoonerThanThat()
    {
        var (database, factory, applier, _) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        var failing = await PublishAsync(database, Guid.CreateVersion7(), 1, Recent());
        var passing = await PublishAsync(database, Guid.CreateVersion7(), 1, Recent(1));
        applier.FailAlways(failing);
        double heldSeconds = 0;
        applier.OnApply = async message =>
        {
            if (message.MessageId == passing)
            {
                heldSeconds = await ScalarAsync<double>(
                    database,
                    "SELECT extract(epoch FROM (claimed_until - now()))::float8 FROM wms.inbox_messages WHERE message_id = @id",
                    ("id", passing));
            }
        };

        await Processor(factory).ProcessBatchAsync(Ct);

        // Mientras corre, el reclamo dura más que cualquier espera de reintento (un asiento puede aprovisionar una bodega).
        Assert.InRange(heldSeconds, 100, 145);
        // Al fallar, el reintento vuelve a la curva de Purchases: 5 s el primero.
        var retryIn = await ScalarAsync<double>(
            database,
            "SELECT extract(epoch FROM (claimed_until - now()))::float8 FROM wms.inbox_messages WHERE message_id = @id",
            ("id", failing));
        Assert.InRange(retryIn, 0, 20);
    }

    [Fact]
    public async Task AProcessedMessageIsNotAppliedTwice()
    {
        var (database, factory, applier, _) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        var message = await PublishAsync(database, Guid.CreateVersion7(), 1, Recent());
        var processor = Processor(factory);

        await processor.ProcessBatchAsync(Ct);
        await processor.ProcessBatchAsync(Ct);

        Assert.Equal([message], applier.Applied.Select(applied => applied.MessageId));
        Assert.NotNull(await ScalarAsync<DateTime?>(
            database, "SELECT processed_at FROM wms.inbox_messages WHERE message_id = @id", ("id", message)));
    }

    [Fact]
    public async Task AMalformedPayloadIsMarkedProcessedAndLogged()
    {
        var (database, factory, applier, logs) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        var message = await PublishAsync(database, Guid.CreateVersion7(), 1, Recent(), payloadOverride: """{"unexpected":true}""");

        await Processor(factory).ProcessBatchAsync(Ct);

        Assert.Empty(applier.Applied);
        Assert.NotNull(await ScalarAsync<DateTime?>(
            database, "SELECT processed_at FROM wms.inbox_messages WHERE message_id = @id", ("id", message)));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("malformed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WholesaleCancelledIsNotConsumed()
    {
        var (database, factory, applier, _) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        await PublishAsync(database, Guid.CreateVersion7(), 1, Recent(), eventName: "quotations.order.wholesale-cancelled.v1");

        await Processor(factory).ProcessBatchAsync(Ct);

        Assert.Empty(applier.Applied);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.inbox_messages"));
    }

    [Fact]
    public async Task AFailingMessagePastTheThresholdParksItsDocumentAndLogsAnError()
    {
        var (database, factory, applier, logs) = await OpenWithRecorderAsync();
        await using var _db = database;
        using var _f = factory;
        var documentId = Guid.CreateVersion7();
        var stuck = await PublishAsync(database, documentId, 1, Recent());
        var free = await PublishAsync(database, Guid.CreateVersion7(), 1, Recent(1));
        applier.FailAlways(stuck);
        var processor = Processor(factory);

        for (var attempt = 0; attempt < ClaimedOutboxConsumer.ParkAfterAttempts; attempt++)
        {
            await processor.ProcessBatchAsync(Ct);
            await ExpireClaimsAsync(database);
        }

        Assert.Equal(
            "Parked",
            await ScalarAsync<string>(
                database,
                "SELECT status FROM wms.document_stock_states WHERE tenant_id = @t AND document_type = 'Order' AND document_id = @d",
                ("t", Tenant),
                ("d", documentId)));
        Assert.Null(await ScalarAsync<DateTime?>(
            database, "SELECT processed_at FROM wms.inbox_messages WHERE message_id = @id", ("id", stuck)));
        Assert.Equal([free], applier.Applied.Select(message => message.MessageId));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Error);
    }

    private sealed class RecordingApplier : IInventoryEventApplier
    {
        private readonly HashSet<Guid> _failOnce = [];
        private readonly HashSet<Guid> _failAlways = [];
        private readonly ConcurrentQueue<InventoryMessage> _applied = [];

        public IReadOnlyList<InventoryMessage> Applied => [.. _applied];

        public void FailOnce(Guid messageId) => _failOnce.Add(messageId);

        public void FailAlways(Guid messageId) => _failAlways.Add(messageId);

        public Func<InventoryMessage, Task>? OnApply { get; set; }

        public async Task ApplyAsync(InventoryMessage message, CancellationToken cancellationToken)
        {
            if (OnApply is not null)
            {
                await OnApply(message);
            }

            if (_failAlways.Contains(message.MessageId) || _failOnce.Remove(message.MessageId))
            {
                throw new InvalidOperationException("The applier failed on purpose.");
            }

            _applied.Enqueue(message);
        }
    }

    private sealed record CapturedEntry(LogLevel Level, string Message);

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedEntry> _entries = [];

        public IReadOnlyList<CapturedEntry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new Capture(_entries, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<CapturedEntry> entries, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category.Contains("Wms", StringComparison.Ordinal);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    entries.Enqueue(new CapturedEntry(logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
