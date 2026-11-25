using System.Reflection;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Spec 2026-11-24 §3: qué deja <c>IssueSiigoInvoiceHandler</c> según lo que contesta Siigo al reenviar una <c>Pending</c>. Se siembran
/// las filas, así que va por el camino de reanudación y no se arma ninguna factura.
/// </summary>
public sealed class IssueSiigoInvoiceHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    // El caso de producción del 2026-11-24.
    [Fact]
    public async Task ATimeoutThenADocumentsServiceOnTheResendStaysPendingWithTheSameKey()
    {
        var row = PendingRow();
        var harness = new Harness([row], Transient("timeout"), Rejected("documents_service"));
        var second = Now.AddMinutes(1);

        await harness.Issue();
        harness.Clock.UtcNow = second;
        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal(second, row.LastUnansweredAt);
        Assert.Equal(("siigo.invoice.siigo_service", "documents_service"), (row.ErrorCode, row.ErrorDetail));
        Assert.Equal([row.IdempotencyKey, row.IdempotencyKey], harness.Api.Keys);
        Assert.Equal(["siigo.invoice.unanswered"], harness.Audit.Actions);
    }

    [Fact]
    public async Task ATimeoutThenADataRejectionIsNotRejected()
    {
        var row = PendingRow();
        var harness = new Harness([row], Transient("timeout"), Rejected("invalid_reference"));

        await harness.Issue();
        harness.Clock.UtcNow = Now.AddMinutes(1);
        var result = await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal("Pending", Assert.Single(result).Status);
        Assert.Equal(("siigo.invoice.rejected", "invalid_reference"), (row.ErrorCode, row.ErrorDetail));
        Assert.Equal(Now, row.LastUnansweredAt);
        Assert.Null(row.SendingSince);
        Assert.Equal(["siigo.invoice.unanswered", "siigo.invoice.unanswered"], harness.Audit.Actions);
    }

    [Fact]
    public async Task ADataRejectionOnTheFirstAttemptIsRejected()
    {
        var row = PendingRow();
        var harness = new Harness([row], Rejected("invalid_reference"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Rejected, row.Status);
        Assert.Null(row.UnansweredAt);
        Assert.Equal([SiigoAuditActions.InvoiceRejected], harness.Audit.Actions);
    }

    [Fact]
    public async Task AServiceOutageOnTheFirstAttemptAwaitsConfirmation()
    {
        var row = PendingRow();
        var harness = new Harness([row], Rejected("documents_service"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal(("siigo.invoice.siigo_service", "documents_service"), (row.ErrorCode, row.ErrorDetail));
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    [Fact]
    public async Task ATooManyRequestsAwaitsConfirmation()
    {
        var row = PendingRow();
        var harness = new Harness([row], Transient("http_429"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal("siigo.invoice.rate_limited", row.ErrorCode);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    // D-2 (owner, 2026-11-24): en estos dos la factura nunca salió de QEP.
    [Theory]
    [InlineData("partner_id_missing")]
    [InlineData("auth_without_token")]
    public async Task AMissingPartnerIdIsNotAnUnansweredAttempt(string reason)
    {
        var row = PendingRow();
        var harness = new Harness([row], Transient(reason));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Null(row.UnansweredAt);
        Assert.Null(row.SendingSince);
        Assert.Equal("siigo.invoice.unavailable", row.ErrorCode);
        Assert.Empty(harness.Audit.Actions);
    }

    [Fact]
    public async Task AResumeOfASendThatDiedIsMarkedUnansweredBeforeSendingAgain()
    {
        var row = PendingRow();
        row.BeginSend(Now.AddMinutes(-9));
        var harness = new Harness([row], Created());

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Created, row.Status);
        Assert.Equal(Now, row.UnansweredAt);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered, SiigoAuditActions.InvoiceCreated], harness.Audit.Actions);
    }

    // Una fila que la versión anterior dejó sin unanswered_at durante el despliegue: el rechazo del reenvío no la libera.
    [Fact]
    public async Task ADataRejectionOnARowThePreviousVersionLeftUnansweredStaysPending()
    {
        var row = PendingRow();
        row.BeginSend(Now.AddMinutes(-1));
        row.MarkTransient("siigo.invoice.timeout", Now.AddMinutes(-1));
        var harness = new Harness([row], Rejected("invalid_reference"));

        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, row.Status);
        Assert.Equal(Now.AddMinutes(-1), row.UnansweredAt);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered, SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    [Fact]
    public async Task TheFreightIsDiscardedOnlyWhenTheOrderInvoiceIsReallyRejected()
    {
        var unansweredOrder = PendingRow();
        unansweredOrder.BeginSend(Now.AddMinutes(-1));
        unansweredOrder.MarkUnanswered("siigo.invoice.timeout", null, Now.AddMinutes(-1));
        var waitingFreight = PendingRow(SiigoInvoiceKind.Freight);

        await new Harness([unansweredOrder, waitingFreight], Rejected("invalid_reference")).Issue();

        Assert.Equal(SiigoInvoiceStatus.Pending, unansweredOrder.Status);
        Assert.Equal(SiigoInvoiceStatus.Pending, waitingFreight.Status);

        var rejectedOrder = PendingRow();
        var discardedFreight = PendingRow(SiigoInvoiceKind.Freight);

        await new Harness([rejectedOrder, discardedFreight], Rejected("invalid_reference")).Issue();

        Assert.Equal(SiigoInvoiceStatus.Rejected, rejectedOrder.Status);
        Assert.Equal(SiigoInvoiceStatus.Failed, discardedFreight.Status);
        Assert.Equal("siigo.invoice.order_rejected", discardedFreight.ErrorCode);
    }

    // La de flete que esperaba nunca salió: si se queda con el reclamo, el reenvío la tomaría por un envío cortado.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFreightThatWaitedForTheOrderReleasesItsClaimAndIsNeverUnanswered(bool serviceError)
    {
        var order = PendingRow();
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        SiigoInvoiceOutcome first = serviceError ? Rejected("documents_service") : Transient("timeout");
        var harness = new Harness([order, freight], first, Created(), Created());

        await harness.Issue();

        Assert.Null(freight.SendingSince);
        Assert.Null(freight.UnansweredAt);

        harness.Clock.UtcNow = Now.AddMinutes(9);
        await harness.Issue();

        Assert.Equal(SiigoInvoiceStatus.Created, freight.Status);
        Assert.Null(freight.UnansweredAt);
        Assert.Equal(
            [order.IdempotencyKey, order.IdempotencyKey, freight.IdempotencyKey], harness.Api.Keys);
    }

    // Un redespliegue corta el envío de la del pedido con las dos filas reclamadas. La de flete sólo sale después de que la del pedido
    // existe, así que nunca salió: su reclamo vencido no es un envío sin respuesta.
    [Fact]
    public async Task AResumeAfterACutOrderSendLeavesTheFreightWithoutUnansweredAt()
    {
        var order = PendingRow();
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        order.BeginSend(Now.AddMinutes(-9));
        freight.BeginSend(Now.AddMinutes(-9));
        var harness = new Harness([order, freight], Transient("timeout"));

        await harness.Issue();

        Assert.Equal(Now, order.UnansweredAt);
        Assert.Null(freight.UnansweredAt);
        Assert.Null(freight.SendingSince);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    [Fact]
    public async Task AFreightWhoseOrderSendWasCutGoesOutWithoutEverHavingBeenUnanswered()
    {
        var order = PendingRow();
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        order.BeginSend(Now.AddMinutes(-9));
        freight.BeginSend(Now.AddMinutes(-9));

        await new Harness([order, freight], Created(), Created()).Issue();

        Assert.Equal(SiigoInvoiceStatus.Created, freight.Status);
        Assert.Null(freight.UnansweredAt);
    }

    // Con la del pedido ya creada, la de flete sí pudo salir: su reclamo vencido sigue siendo un envío sin respuesta.
    [Fact]
    public async Task ADeadFreightSendOfAnInvoicedOrderIsStillUnanswered()
    {
        var order = PendingRow();
        order.BeginSend(Now.AddMinutes(-9));
        order.MarkCreated("siigo-1", "FV-1-123", 100m, null, Now.AddMinutes(-9));
        var freight = PendingRow(SiigoInvoiceKind.Freight);
        freight.BeginSend(Now.AddMinutes(-9));
        var harness = new Harness([order, freight], Transient("timeout"));

        await harness.Issue();

        Assert.Equal(Now, freight.UnansweredAt);
        Assert.Equal([SiigoAuditActions.InvoiceUnanswered], harness.Audit.Actions);
    }

    // Si se declara que la del pedido no existe, la de flete nunca salió, tenga lo que tenga anotado (por ejemplo, el relleno de la
    // migración sobre un reclamo viejo). Viva, dejaría el pedido bloqueado.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaringTheOrderMissingDiscardsItsFreightEvenIfItIsMarkedUnanswered(bool expired)
    {
        var sentAt = expired ? Now.AddHours(-25) : Now.AddMinutes(-11);
        var order = Unanswered(SiigoInvoiceKind.Order, sentAt);
        var freight = Unanswered(SiigoInvoiceKind.Freight, sentAt);
        Assert.Equal(expired, order.ExpireIfStale(Now));
        Assert.Equal(expired, freight.ExpireIfStale(Now));

        await new Harness([order, freight]).Resolve("missing");

        Assert.Equal(SiigoInvoiceStatus.Failed, order.Status);
        Assert.Equal(SiigoInvoiceStatus.Failed, freight.Status);
        Assert.Equal("siigo.invoice.order_rejected", freight.ErrorCode);
    }

    [Fact]
    public async Task DeclaringThatTheOrderInvoiceExistsLeavesItsFreightAlone()
    {
        var order = Unanswered(SiigoInvoiceKind.Order, Now.AddMinutes(-11));
        var freight = Unanswered(SiigoInvoiceKind.Freight, Now.AddMinutes(-11));

        await new Harness([order, freight]).Resolve("exists", "FV-1-777");

        Assert.Equal(SiigoInvoiceStatus.Created, order.Status);
        Assert.Equal(SiigoInvoiceStatus.Pending, freight.Status);
    }

    [Fact]
    public async Task TheFreightOfAnInvoicedOrderIsNotDiscarded()
    {
        var order = PendingRow();
        order.BeginSend(Now.AddMinutes(-11));
        order.MarkCreated("siigo-1", "FV-1-123", 100m, null, Now.AddMinutes(-11));
        var freight = Unanswered(SiigoInvoiceKind.Freight, Now.AddMinutes(-11));
        var harness = new Harness([order, freight]);

        await Assert.ThrowsAsync<SiigoDomainException>(() => harness.Resolve("missing"));

        Assert.Equal(SiigoInvoiceStatus.Pending, freight.Status);
    }

    private static SiigoInvoice Unanswered(SiigoInvoiceKind kind, DateTimeOffset sentAt)
    {
        var row = PendingRow(kind, sentAt);
        row.BeginSend(sentAt);
        row.MarkUnanswered("siigo.invoice.timeout", null, sentAt);
        return row;
    }

    private static SiigoInvoice PendingRow(SiigoInvoiceKind kind = SiigoInvoiceKind.Order, DateTimeOffset? createdAt = null) =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), TenantId, ConnectionId, OrderId, kind, """{"a":1}""", 7, SubjectId, createdAt ?? Now.AddHours(-1));

    private static SiigoInvoiceOutcome.Transient Transient(string reason) => new(reason);

    private static SiigoInvoiceOutcome.Rejected Rejected(string code) => new([new SiigoError(code, "message")], null);

    private static SiigoInvoiceOutcome.Created Created() => new("siigo-1", "FV-1-123", 100m, null);

    private sealed class Harness
    {
        private readonly IssueSiigoInvoiceHandler _handler;
        private readonly ResolveSiigoInvoiceHandler _resolver;

        public Harness(SiigoInvoice[] rows, params SiigoInvoiceOutcome[] script)
        {
            Api = ApiProxy.Create(script);
            _resolver = new ResolveSiigoInvoiceHandler(
                new ExecutionContext(),
                new AllModules(),
                new FixedMembership(),
                new OrderSource(),
                new InMemoryInvoices([.. rows]),
                new FakeUnitOfWork(),
                Audit,
                Clock,
                new ResolveSiigoInvoiceValidator());
            _handler = new IssueSiigoInvoiceHandler(
                new ExecutionContext(),
                new AllModules(),
                new FixedMembership(),
                new OrderSource(),
                // El camino de reanudación no lee la configuración.
                null!,
                new ActiveConnections(),
                Api.Instance,
                new InMemoryInvoices([.. rows]),
                new FakeUnitOfWork(),
                Audit,
                Clock,
                new IssueSiigoInvoiceValidator());
        }

        public ApiProxy Api { get; }

        public RecordingAudit Audit { get; } = new();

        public MutableClock Clock { get; } = new() { UtcNow = Now };

        public Task<IReadOnlyList<SiigoInvoiceDto>> Issue() =>
            _handler.HandleAsync(new IssueSiigoInvoiceCommand(TenantId, OrderId, null), TestContext.Current.CancellationToken);

        /// <summary>Una persona resuelve la factura del pedido.</summary>
        public Task<SiigoInvoiceDto> Resolve(string outcome, string? number = null) =>
            _resolver.HandleAsync(
                new ResolveSiigoInvoiceCommand(TenantId, OrderId, null, outcome, number), TestContext.Current.CancellationToken);
    }

    private sealed class ExecutionContext : IExecutionContext
    {
        public Guid SubjectId => IssueSiigoInvoiceHandlerTests.SubjectId;

        public TenantId TenantId { get; } = new(IssueSiigoInvoiceHandlerTests.TenantId);

        public bool HasPermission(string permission) => permission == SiigoPermissions.InvoiceIssue;
    }

    private sealed class AllModules : ITenantModules
    {
        // `null` = el tenant no tiene fila: el guard lo deja pasar (como el stub de desarrollo).
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }

    private sealed class FixedMembership : IMembershipDirectory
    {
        private readonly Guid _membershipId = Guid.CreateVersion7();

        public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(_membershipId);

        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class OrderSource : ISiigoOrderSource
    {
        public Task<SiigoInvoiceInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(7);

        public Task MarkInvoicedAsync(
            Guid tenantId, Guid orderId, string number, string siigoInvoiceId, Guid memberId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SiigoOrderLabel>> ListLabelsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
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

    private sealed class InMemoryInvoices(List<SiigoInvoice> rows) : ISiigoInvoiceRepository
    {
        public Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>([.. rows.Where(row => row.TenantId == tenantId && row.OrderId == orderId)]);

        public Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(SiigoInvoice invoice) => throw new NotSupportedException();
    }

    private sealed class FakeUnitOfWork : ISiigoUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<ISiigoTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ISiigoTransaction>(new FakeTransaction());

        public Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken) => Task.CompletedTask;

        private sealed class FakeTransaction : ISiigoTransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAudit : ISiigoAuditRecorder
    {
        public List<string> Actions { get; } = [];

        public void Record(
            Guid tenantId, Guid actorId, AuditActorType actorType, string action, string resourceType, string resourceId,
            string changedFieldsJson, DateTimeOffset occurredAt) =>
            Actions.Add(action);
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    /// <summary>Un <see cref="ISiigoApi"/> que sólo sabe crear facturas: contesta el guion en orden y anota las claves que recibe.</summary>
    public class ApiProxy : DispatchProxy
    {
        private Queue<SiigoInvoiceOutcome> _script = new();

        public List<string> Keys { get; } = [];

        public ISiigoApi Instance => (ISiigoApi)(object)this;

        public static ApiProxy Create(IEnumerable<SiigoInvoiceOutcome> script)
        {
            var proxy = (ApiProxy)(object)DispatchProxy.Create<ISiigoApi, ApiProxy>();
            proxy._script = new Queue<SiigoInvoiceOutcome>(script);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ISiigoApi.CreateInvoiceAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            Keys.Add((string)args![2]!);
            return Task.FromResult(_script.Dequeue());
        }
    }
}
