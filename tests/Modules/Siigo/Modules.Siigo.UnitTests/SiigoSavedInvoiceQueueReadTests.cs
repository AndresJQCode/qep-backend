using System.Reflection;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Audit.Domain;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Sellar y resolver preguntan por la tanda abierta <b>después</b> del commit: si esa lectura falla, la operación ya está
/// guardada (hasta un sellado ante la DIAN) y repetirla respondería 422. La respuesta sale igual, tratando el pedido como en cola.</summary>
public sealed class SiigoSavedInvoiceQueueReadTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<Exception> ReadFailures =>
    [
        new InvalidOperationException("The database is not reachable."),
        // Un plazo de la base, no la cancelación de quien llama.
        new OperationCanceledException(),
    ];

    [Theory]
    [MemberData(nameof(ReadFailures), DisableDiscoveryEnumeration = true)]
    public async Task AStampSavedBeforeTheQueueReadFailsIsAnsweredAsQueued(Exception failure)
    {
        var harness = new Harness(Created(), new FailingBatches(failure));

        var stamped = await harness.Stamp(TestContext.Current.CancellationToken);

        Assert.Equal(("Stamped", false, false), (stamped.Status, stamped.CanResend, stamped.OrderMarkFailed));
        Assert.Equal(1, harness.UnitOfWork.Commits);
        // En cola no se pregunta por la marca: el worker puede estar marcando el pedido.
        Assert.Equal(0, harness.Orders.MarkReads);
        Assert.Equal(LogLevel.Warning, Assert.Single(harness.StampLog.Levels));
    }

    [Fact]
    public async Task AStampWhoseQueueReadAnswersStillWarnsAboutTheMissingMark()
    {
        var harness = new Harness(Created(), new FailingBatches(null));

        var stamped = await harness.Stamp(TestContext.Current.CancellationToken);

        Assert.True(stamped.OrderMarkFailed);
        Assert.Empty(harness.StampLog.Levels);
    }

    [Fact]
    public async Task AnExistsResolutionSavedBeforeTheQueueReadFailsIsAnsweredAsQueued()
    {
        var harness = new Harness(Unanswered(), new FailingBatches(new InvalidOperationException("down")));

        var resolved = await harness.Resolve("exists", "FV-1-777", TestContext.Current.CancellationToken);

        Assert.Equal(("Created", false, false), (resolved.Status, resolved.CanResend, resolved.OrderMarkFailed));
        Assert.Equal(1, harness.UnitOfWork.Commits);
        Assert.Equal(LogLevel.Warning, Assert.Single(harness.ResolveLog.Levels));
    }

    // «No existe» pregunta dos veces: dentro de la transacción (la guarda) y, ya guardado, para la respuesta.
    [Fact]
    public async Task AMissingResolutionSavedBeforeTheQueueReadFailsIsAnswered()
    {
        var harness = new Harness(Unanswered(), new FailingBatches(new InvalidOperationException("down"), answeredReads: 1));

        var resolved = await harness.Resolve("missing", null, TestContext.Current.CancellationToken);

        Assert.Equal("Failed", resolved.Status);
        Assert.Equal(1, harness.UnitOfWork.Commits);
    }

    // La guarda va antes del commit: si no se puede preguntar, no se guarda nada y el error sube.
    [Fact]
    public async Task AMissingResolutionWhoseGuardCannotBeReadSavesNothing()
    {
        var harness = new Harness(Unanswered(), new FailingBatches(new InvalidOperationException("down")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Resolve("missing", null, TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.UnitOfWork.Commits);
    }

    // Quien llama ya se fue: no hay a quién responder, así que la cancelación sube como en el resto de las lecturas del módulo.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancellationOfTheCallerStillPropagates(bool resolve)
    {
        using var caller = new CancellationTokenSource();
        var batches = new FailingBatches(new OperationCanceledException(caller.Token), onRead: caller.Cancel);
        var harness = new Harness(resolve ? Unanswered() : Created(), batches);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolve ? harness.Resolve("exists", "FV-1-777", caller.Token) : harness.Stamp(caller.Token));

        Assert.Equal(1, harness.UnitOfWork.Commits);
    }

    private static SiigoInvoice Pending() =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), TenantId, ConnectionId, OrderId, SiigoInvoiceKind.Order, """{"a":1}""", 7, Guid.CreateVersion7(),
            Now.AddHours(-1));

    private static SiigoInvoice Created()
    {
        var row = Pending();
        row.MarkCreated("siigo-1", "FV-1-123", 100m, null, Now.AddMinutes(-30));
        return row;
    }

    private static SiigoInvoice Unanswered()
    {
        var row = Pending();
        row.BeginSend(Now.AddMinutes(-30));
        row.MarkUnanswered(SiigoErrorCodes.InvoiceTimeout, null, Now.AddMinutes(-30));
        return row;
    }

    private sealed class Harness(SiigoInvoice row, FailingBatches batches)
    {
        public Orders Orders { get; } = new();

        public UnitOfWork UnitOfWork { get; } = new();

        public RecordingLogger<StampSiigoInvoiceHandler> StampLog { get; } = new();

        public RecordingLogger<ResolveSiigoInvoiceHandler> ResolveLog { get; } = new();

        public Task<SiigoInvoiceDto> Stamp(CancellationToken cancellationToken) =>
            new StampSiigoInvoiceHandler(
                new ExecutionContext(),
                new AllModules(),
                new ActiveConnections(),
                StampingApi.Create(),
                new Invoices(row),
                UnitOfWork,
                new FixedClock(),
                new StampSiigoInvoiceValidator(),
                batches,
                Orders,
                StampLog).HandleAsync(new StampSiigoInvoiceCommand(TenantId, OrderId, null), cancellationToken);

        public Task<SiigoInvoiceDto> Resolve(string outcome, string? number, CancellationToken cancellationToken) =>
            new ResolveSiigoInvoiceHandler(
                new ExecutionContext(),
                new AllModules(),
                new FixedMembership(),
                Orders,
                new Invoices(row),
                UnitOfWork,
                new SilentAudit(),
                new FixedClock(),
                new ResolveSiigoInvoiceValidator(),
                batches,
                ResolveLog).HandleAsync(
                    new ResolveSiigoInvoiceCommand(TenantId, OrderId, null, outcome, number), cancellationToken);
    }

    /// <param name="failure">Lo que lanza la lectura de la tanda abierta; <c>null</c> contesta que ninguna tiene el pedido.</param>
    /// <param name="answeredReads">Cuántas lecturas contesta bien antes de empezar a fallar.</param>
    private sealed class FailingBatches(Exception? failure, int answeredReads = 0, Action? onRead = null) : ISiigoInvoiceBatchRepository
    {
        private int _reads;

        public Task<Guid?> FindOpenBatchIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
        {
            onRead?.Invoke();
            return failure is null || _reads++ < answeredReads
                ? Task.FromResult<Guid?>(null)
                : Task.FromException<Guid?>(failure);
        }

        public void Add(SiigoInvoiceBatch batch) => throw new NotSupportedException();

        public Task<SiigoInvoiceBatch?> FindAsync(Guid tenantId, Guid batchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListOpenOrderIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<SiigoInvoiceBatch> Items, int Total)> SearchAsync(
            Guid tenantId, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>El pedido nunca confirma la marca.</summary>
    private sealed class Orders : ISiigoOrderSource
    {
        public int MarkReads { get; private set; }

        public Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken)
        {
            MarkReads++;
            return Task.FromResult(false);
        }

        public Task MarkInvoicedAsync(
            Guid tenantId, Guid orderId, string number, string siigoInvoiceId, Guid memberId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<SiigoInvoiceInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SiigoOrderLabel>> ListLabelsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ExecutionContext : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = new(SiigoSavedInvoiceQueueReadTests.TenantId);

        public bool HasPermission(string permission) => permission == SiigoPermissions.InvoiceIssue;
    }

    private sealed class AllModules : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }

    private sealed class FixedMembership : IMembershipDirectory
    {
        public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(Guid.CreateVersion7());

        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ActiveConnections : ISiigoConnectionDirectory
    {
        public Task<SiigoConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(new SiigoConnectionResolution(new SiigoCredentials(connectionId, "user", "key"), null));

        public Task<SiigoConnectionInfo?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SiigoConnectionResolution> ResolveForSetupAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiigoConnectionInfo>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Invoices(SiigoInvoice row) : ISiigoInvoiceRepository
    {
        public Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>([row]);

        public Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(SiigoInvoice invoice) => throw new NotSupportedException();
    }

    private sealed class UnitOfWork : ISiigoUnitOfWork
    {
        public int Commits { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<ISiigoTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ISiigoTransaction>(new Transaction(this));

        public Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class Transaction(UnitOfWork owner) : ISiigoTransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken)
            {
                owner.Commits++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SilentAudit : ISiigoAuditRecorder
    {
        public void Record(
            Guid tenantId, Guid actorId, AuditActorType actorType, string action, string resourceType, string resourceId,
            string changedFieldsJson, DateTimeOffset occurredAt)
        {
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    /// <summary>Un <see cref="ISiigoApi"/> que sólo sabe sellar, y la DIAN acepta.</summary>
    public class StampingApi : DispatchProxy
    {
        public static ISiigoApi Create() => DispatchProxy.Create<ISiigoApi, StampingApi>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ISiigoApi.StampInvoiceAsync)
                ? Task.FromResult<SiigoStampOutcome>(new SiigoStampOutcome.Stamped("cufe"))
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
