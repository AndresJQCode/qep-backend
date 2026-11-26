using Bootstrapper.Copilot;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Un reenvío que no se puede hacer (conexión, permiso) no deja <c>Failed</c> un pedido cuya factura existe o puede existir:
/// por confirmar o en camino queda por confirmar, enlazado a su fila, y ya creada queda facturado. Con una fila que nunca salió y nadie
/// está mandando, sin fila, o con la del pedido creada y la de flete todavía <c>Pending</c>, sigue fallido.</summary>
public sealed class SiigoBatchItemInvokerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALostPermissionKeepsAnInvoiceThatMayExistToConfirm(bool unknown)
    {
        var row = unknown ? Unknown() : AwaitingConfirmation();

        var outcome = await InvokeAsync(permitted: false, conflict: null, row);

        Assert.Equal(new SiigoBatchItemOutcome.ToConfirm(row.Id, "siigo.invoice_batch.forbidden"), outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnavailableConnectionKeepsAnInvoiceThatMayExistToConfirm(bool unknown)
    {
        var row = unknown ? Unknown() : AwaitingConfirmation();

        var outcome = await InvokeAsync(permitted: true, SiigoErrorCodes.ConnectionUnavailable, row);

        Assert.Equal(new SiigoBatchItemOutcome.ToConfirm(row.Id, SiigoErrorCodes.ConnectionUnavailable), outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARowThatNeverLeftStaysFailed(bool permitted)
    {
        var row = Pending(Now.AddMinutes(-20));
        row.BeginSend(Now.AddMinutes(-20));
        row.MarkTransient(SiigoErrorCodes.InvoiceUnavailable, Now.AddMinutes(-20));

        var outcome = await InvokeAsync(permitted, SiigoErrorCodes.ConnectionUnavailable, row);

        Assert.Equal(
            new SiigoBatchItemOutcome.Failed(permitted ? SiigoErrorCodes.ConnectionUnavailable : SiigoErrorCodes.InvoiceBatchForbidden),
            outcome);
    }

    [Fact]
    public async Task AnOrderWithoutALiveInvoiceStaysFailed()
    {
        var rejected = Pending(Now.AddMinutes(-20));
        rejected.MarkRejected("siigo.invoice.rejected", "invalid_reference", null, Now.AddMinutes(-20));

        var outcome = await InvokeAsync(permitted: false, conflict: null, rejected);

        Assert.Equal(new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceBatchForbidden), outcome);
    }

    // Un envío en camino, o uno cortado hace menos de 8 min: no se sabe si Siigo la creará, aunque la fila nunca haya quedado sin
    // respuesta.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInvoiceBeingSentStaysToConfirm(bool permitted)
    {
        var row = Pending(Now.AddMinutes(-1));
        row.BeginSend(Now.AddMinutes(-1));

        var outcome = await InvokeAsync(permitted, SiigoErrorCodes.ConnectionUnavailable, row);

        Assert.Equal(
            new SiigoBatchItemOutcome.ToConfirm(
                row.Id, permitted ? SiigoErrorCodes.ConnectionUnavailable : SiigoErrorCodes.InvoiceBatchForbidden),
            outcome);
    }

    // La factura ya existe y no queda ninguna Pending por mandar: el reenvío sólo volvería a marcar el pedido y no usa la conexión, así
    // que lo que lo frena antes es que quien pidió la tanda ya no pueda facturar. Sin una lectura que confirme la marca, se avisa.
    [Theory]
    [InlineData(SiigoInvoiceStatus.Created, true, false)]
    [InlineData(SiigoInvoiceStatus.Created, false, true)]
    [InlineData(SiigoInvoiceStatus.Stamped, false, true)]
    [InlineData(SiigoInvoiceStatus.StampRejected, true, false)]
    public async Task AnInvoicedOrderIsInvoicedAndNotFailed(SiigoInvoiceStatus status, bool marked, bool orderMarkFailed)
    {
        var row = Invoiced(status);
        var orders = new OrderMarks(marked);

        var outcome = await InvokeAsync(permitted: false, conflict: null, orders, row);

        Assert.Equal(status, row.Status);
        Assert.Equal(new SiigoBatchItemOutcome.Invoiced("FV-1-9", row.Id, orderMarkFailed), outcome);
        Assert.Equal([(TenantId, OrderId, "FV-1-9")], orders.Reads);
    }

    [Fact]
    public async Task AnInvoicedOrderWhoseMarkCannotBeReadSaysTheMarkFailed()
    {
        var row = Invoiced(SiigoInvoiceStatus.Created);

        var outcome = await InvokeAsync(permitted: false, conflict: null, new OrderMarks(null), row);

        Assert.Equal(new SiigoBatchItemOutcome.Invoiced("FV-1-9", row.Id, OrderMarkFailed: true), outcome);
    }

    // Una de flete que ya no se reenvía (creada, rechazada, descartada o sin resolver) no cambia lo que pasó con el pedido.
    [Theory]
    [InlineData(SiigoInvoiceStatus.Created)]
    [InlineData(SiigoInvoiceStatus.Rejected)]
    [InlineData(SiigoInvoiceStatus.Failed)]
    [InlineData(SiigoInvoiceStatus.Unknown)]
    public async Task AFreightThatIsNotPendingLeavesTheInvoicedOrderInvoiced(SiigoInvoiceStatus freightStatus)
    {
        var order = Invoiced(SiigoInvoiceStatus.Created);
        var freight = Freight(freightStatus);

        var outcome = await InvokeAsync(permitted: false, conflict: null, order, freight);

        Assert.Equal(freightStatus, freight.Status);
        Assert.Equal(new SiigoBatchItemOutcome.Invoiced("FV-1-9", order.Id), outcome);
    }

    // La de flete sigue Pending: el reenvío era para mandarla y no se pudo. Decir «facturado» la dejaría sin mandar sin que nadie lo vea.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task APendingFreightThatCannotBeSentFailsAsFreightPending(bool permitted, bool unanswered)
    {
        var order = Invoiced(SiigoInvoiceStatus.Created);
        var freight = Pending(Now.AddMinutes(-20), SiigoInvoiceKind.Freight);
        if (unanswered)
        {
            freight.BeginSend(Now.AddMinutes(-20));
            freight.MarkUnanswered(SiigoErrorCodes.InvoiceTimeout, null, Now.AddMinutes(-20));
        }

        var outcome = await InvokeAsync(permitted, SiigoErrorCodes.ConnectionUnavailable, order, freight);

        Assert.Equal(new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceFreightPending), outcome);
    }

    // La emisión contestó: la del pedido existe y la de flete sigue Pending (nunca salió, o salió y no tuvo respuesta). «Facturado» la
    // dejaría sin mandar sin que nadie lo vea. El código dice que la del pedido existe y falta la de flete.
    [Theory]
    [InlineData(SiigoInvoiceStatus.Created, "waiting", false)]
    [InlineData(SiigoInvoiceStatus.Created, "sending", false)]
    [InlineData(SiigoInvoiceStatus.Created, "unanswered", false)]
    [InlineData(SiigoInvoiceStatus.Created, "never-left", false)]
    [InlineData(SiigoInvoiceStatus.Created, "waiting", true)]
    [InlineData(SiigoInvoiceStatus.Stamped, "unanswered", false)]
    [InlineData(SiigoInvoiceStatus.StampRejected, "unanswered", true)]
    public async Task AnIssueThatLeavesTheFreightPendingIsNotInvoiced(
        SiigoInvoiceStatus orderStatus, string freightCase, bool orderMarkFailed)
    {
        var order = Invoiced(orderStatus);
        var freight = PendingFreight(freightCase);

        var outcome = await IssueAsync(orderMarkFailed, order, freight);

        Assert.Equal(new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceFreightPending), outcome);
    }

    [Theory]
    [InlineData(SiigoInvoiceStatus.Created)]
    [InlineData(SiigoInvoiceStatus.Stamped)]
    [InlineData(SiigoInvoiceStatus.StampRejected)]
    [InlineData(SiigoInvoiceStatus.Rejected)]
    [InlineData(SiigoInvoiceStatus.Failed)]
    [InlineData(SiigoInvoiceStatus.Unknown)]
    public async Task AnIssueWithAFreightThatIsNotPendingIsInvoiced(SiigoInvoiceStatus freightStatus)
    {
        var order = Invoiced(SiigoInvoiceStatus.Created);
        var freight = freightStatus is SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected
            ? Invoiced(freightStatus, SiigoInvoiceKind.Freight)
            : Freight(freightStatus);

        var outcome = await IssueAsync(orderMarkFailed: false, order, freight);

        Assert.Equal(freightStatus, freight.Status);
        Assert.Equal(new SiigoBatchItemOutcome.Invoiced("FV-1-9", order.Id), outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIssueWithoutAFreightIsInvoicedWithItsMarkWarning(bool orderMarkFailed)
    {
        var order = Invoiced(SiigoInvoiceStatus.Created);

        var outcome = await IssueAsync(orderMarkFailed, order);

        Assert.Equal(new SiigoBatchItemOutcome.Invoiced("FV-1-9", order.Id, orderMarkFailed), outcome);
    }

    // Sin saber si la del pedido existe, la de flete que la espera no cambia nada: la decide una persona.
    [Fact]
    public async Task AnIssueThatLeavesTheOrderInvoiceOpenStaysToConfirm()
    {
        var order = AwaitingConfirmation();

        var outcome = await IssueAsync(orderMarkFailed: false, order, PendingFreight("waiting"));

        Assert.Equal(new SiigoBatchItemOutcome.ToConfirm(order.Id, SiigoErrorCodes.InvoiceTimeout), outcome);
    }

    private static SiigoInvoice PendingFreight(string freightCase)
    {
        var row = Pending(Now.AddMinutes(-20), SiigoInvoiceKind.Freight);
        switch (freightCase)
        {
            case "sending":
                row.BeginSend(Now.AddMinutes(-1));
                break;
            case "unanswered":
                row.BeginSend(Now.AddMinutes(-20));
                row.MarkUnanswered(SiigoErrorCodes.InvoiceTimeout, null, Now.AddMinutes(-20));
                break;
            case "never-left":
                row.BeginSend(Now.AddMinutes(-20));
                row.MarkTransient(SiigoErrorCodes.InvoiceUnavailable, Now.AddMinutes(-20));
                break;
        }

        return row;
    }

    private static SiigoInvoice Freight(SiigoInvoiceStatus status)
    {
        var row = Pending(status == SiigoInvoiceStatus.Unknown ? Now.AddHours(-25) : Now.AddMinutes(-20), SiigoInvoiceKind.Freight);
        switch (status)
        {
            case SiigoInvoiceStatus.Created:
                row.MarkCreated("id-2", "FV-1-10", 10m, null, Now.AddMinutes(-20));
                break;
            case SiigoInvoiceStatus.Rejected:
                row.MarkRejected("siigo.invoice.rejected", "invalid_reference", null, Now.AddMinutes(-20));
                break;
            case SiigoInvoiceStatus.Failed:
                row.MarkFailed("siigo.invoice.order_rejected", "Discarded.", Now.AddMinutes(-20));
                break;
            default:
                row.ExpireIfStale(Now);
                break;
        }

        return row;
    }

    private static SiigoInvoice Invoiced(SiigoInvoiceStatus status, SiigoInvoiceKind kind = SiigoInvoiceKind.Order)
    {
        var row = Pending(Now.AddMinutes(-20), kind);
        row.BeginSend(Now.AddMinutes(-20));
        row.MarkCreated("id-1", "FV-1-9", 100m, status == SiigoInvoiceStatus.Stamped ? "cufe" : null, Now.AddMinutes(-20));
        if (status == SiigoInvoiceStatus.StampRejected)
        {
            row.MarkStampRejected("dian.rejected", "Regla 90", Now.AddMinutes(-20));
        }

        return row;
    }

    private static SiigoInvoice Pending(DateTimeOffset createdAt, SiigoInvoiceKind kind = SiigoInvoiceKind.Order) =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), TenantId, Guid.CreateVersion7(), OrderId, kind, """{"a":1}""", 7, Guid.CreateVersion7(), createdAt);

    private static SiigoInvoice AwaitingConfirmation()
    {
        var row = Pending(Now.AddMinutes(-20));
        row.BeginSend(Now.AddMinutes(-20));
        row.MarkUnanswered(SiigoErrorCodes.InvoiceTimeout, null, Now.AddMinutes(-20));
        return row;
    }

    private static SiigoInvoice Unknown()
    {
        var row = Pending(Now.AddHours(-25));
        row.ExpireIfStale(Now);
        return row;
    }

    private static Task<SiigoBatchItemOutcome> InvokeAsync(bool permitted, string? conflict, params SiigoInvoice[] rows) =>
        InvokeAsync(permitted, conflict, new OrderMarks(true), rows);

    private static Task<SiigoBatchItemOutcome> InvokeAsync(
        bool permitted, string? conflict, OrderMarks orders, params SiigoInvoice[] rows) =>
        InvokeAsync(permitted, new ConflictingDispatcher(conflict ?? "unused"), orders, rows);

    /// <summary>La emisión corre y contesta con esas filas.</summary>
    private static Task<SiigoBatchItemOutcome> IssueAsync(bool orderMarkFailed, params SiigoInvoice[] rows) =>
        InvokeAsync(
            permitted: true,
            new IssuedDispatcher(
                [.. rows.Select(row => SiigoInvoiceDto.From(row, Now, orderMarkFailed && row.Kind == SiigoInvoiceKind.Order))]),
            new OrderMarks(!orderMarkFailed),
            rows);

    private static async Task<SiigoBatchItemOutcome> InvokeAsync(
        bool permitted, IRequestDispatcher dispatcher, OrderMarks orders, SiigoInvoice[] rows)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<DelegatedExecutionScope>();
        services.AddSingleton(dispatcher);
        services.AddSingleton<ISiigoInvoiceRepository>(new Invoices(rows));
        services.AddSingleton<ISiigoOrderSource>(orders);
        services.AddSingleton<IClock>(new FixedClock());
        await using var provider = services.BuildServiceProvider();
        var invoker = ActivatorUtilities.CreateInstance<SiigoBatchItemInvoker>(provider, new Permissions(permitted));

        return await invoker.InvokeAsync(TenantId, Guid.CreateVersion7(), OrderId, null, TestContext.Current.CancellationToken);
    }

    /// <param name="marked">Lo que contesta la lectura de la marca; <c>null</c> falla.</param>
    private sealed class OrderMarks(bool? marked) : ISiigoOrderSource
    {
        public List<(Guid TenantId, Guid OrderId, string Number)> Reads { get; } = [];

        public Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken)
        {
            Reads.Add((tenantId, orderId, number));
            return marked is { } value
                ? Task.FromResult(value)
                : Task.FromException<bool>(new InvalidOperationException("The order cannot be read."));
        }

        public Task<bool> UnmarkInvoicedAsync(
            Guid tenantId, Guid orderId, string number, Guid memberId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SiigoInvoiceInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkInvoicedAsync(
            Guid tenantId, Guid orderId, string number, string siigoInvoiceId, Guid memberId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SiigoOrderLabel>> ListLabelsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Permissions(bool permitted) : IAuthorizationService
    {
        public Task<AuthorizationDecision> AuthorizeAsync(Guid subjectId, Guid tenantId, string permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>?> ResolvePermissionsAsync(Guid subjectId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<string>?>(permitted ? [SiigoPermissions.InvoiceIssue] : []);
    }

    private sealed class ConflictingDispatcher(string code) : IRequestDispatcher
    {
        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            Task.FromException<TResponse>(new ResourceConflictException(code, "The connection cannot be used."));

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class IssuedDispatcher(IReadOnlyList<SiigoInvoiceDto> rows) : IRequestDispatcher
    {
        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)rows);

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Invoices(SiigoInvoice[] rows) : ISiigoInvoiceRepository
    {
        public Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>([.. rows.Where(row => row.TenantId == tenantId && row.OrderId == orderId)]);

        public Task<IReadOnlyList<SiigoInvoice>> ListFreightAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(SiigoInvoice invoice) => throw new NotSupportedException();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
