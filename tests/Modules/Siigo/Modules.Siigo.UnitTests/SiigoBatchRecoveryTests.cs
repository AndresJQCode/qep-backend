using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Batches;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Spec 2026-11-24 §4: el resultado de un ítem recuperado (lease vencido) o cortado por el apagado, leído de las filas del pedido. Con la
/// del pedido facturada y la de flete todavía <c>Pending</c> no sale facturado (decidido el 2026-11-25).
/// </summary>
public sealed class SiigoBatchRecoveryTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    // Nunca salió (esperaba a la del pedido), se cortó con el reclamo puesto o se mandó y no tuvo respuesta: el ítem dice siempre lo
    // mismo, que la del pedido existe y falta la de flete. El motivo de la fila de flete se ve en el pedido.
    [Theory]
    [InlineData(SiigoInvoiceStatus.Created, "waiting")]
    [InlineData(SiigoInvoiceStatus.Created, "claimed")]
    [InlineData(SiigoInvoiceStatus.Created, "unanswered")]
    [InlineData(SiigoInvoiceStatus.Created, "never-left")]
    [InlineData(SiigoInvoiceStatus.Stamped, "waiting")]
    [InlineData(SiigoInvoiceStatus.StampRejected, "claimed")]
    public async Task APendingFreightFailsAsFreightPending(SiigoInvoiceStatus orderStatus, string freightCase)
    {
        var freight = Pending(SiigoInvoiceKind.Freight);
        if (freightCase != "waiting")
        {
            freight.BeginSend(Now.AddMinutes(freightCase == "claimed" ? -1 : -20));
        }

        if (freightCase == "unanswered")
        {
            freight.MarkUnanswered(SiigoErrorCodes.InvoiceTimeout, null, Now.AddMinutes(-20));
        }
        else if (freightCase == "never-left")
        {
            freight.MarkTransient(SiigoErrorCodes.InvoiceUnavailable, Now.AddMinutes(-20));
        }

        var outcome = await RecoverAsync(new OrderMarks(true), Invoiced(orderStatus), freight);

        Assert.Equal(new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceFreightPending), outcome);
    }

    // Aunque el pedido tampoco lleve la marca: la tanda del reintento la vuelve a poner y las filas del pedido la avisan al leerse.
    [Fact]
    public async Task APendingFreightFailsAlsoWhenTheOrderIsNotMarked()
    {
        var outcome = await RecoverAsync(
            new OrderMarks(false), Invoiced(SiigoInvoiceStatus.Created), Pending(SiigoInvoiceKind.Freight));

        Assert.Equal(new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceFreightPending), outcome);
    }

    // Una de flete que no está Pending no cambia lo que pasó con el pedido.
    [Theory]
    [InlineData(SiigoInvoiceStatus.Created)]
    [InlineData(SiigoInvoiceStatus.Stamped)]
    [InlineData(SiigoInvoiceStatus.StampRejected)]
    [InlineData(SiigoInvoiceStatus.Rejected)]
    [InlineData(SiigoInvoiceStatus.Failed)]
    [InlineData(SiigoInvoiceStatus.Unknown)]
    public async Task AFreightThatIsNotPendingLeavesTheItemInvoiced(SiigoInvoiceStatus freightStatus)
    {
        var order = Invoiced(SiigoInvoiceStatus.Created);
        var freight = Freight(freightStatus);

        var outcome = await RecoverAsync(new OrderMarks(true), order, freight);

        Assert.Equal(freightStatus, freight.Status);
        Assert.Equal(new SiigoBatchItemOutcome.Invoiced("FV-1-9", order.Id), outcome);
    }

    [Fact]
    public async Task AnOpenOrderInvoiceStaysToConfirmWithAPendingFreight()
    {
        var order = Pending(SiigoInvoiceKind.Order);
        order.BeginSend(Now.AddMinutes(-20));

        var outcome = await RecoverAsync(new OrderMarks(true), order, Pending(SiigoInvoiceKind.Freight));

        Assert.Equal(new SiigoBatchItemOutcome.ToConfirm(order.Id, SiigoErrorCodes.InvoiceInterrupted), outcome);
    }

    private static SiigoInvoice Pending(SiigoInvoiceKind kind, DateTimeOffset? createdAt = null) =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), TenantId, Guid.CreateVersion7(), OrderId, kind, """{"a":1}""", 7, Guid.CreateVersion7(),
            createdAt ?? Now.AddMinutes(-20));

    private static SiigoInvoice Invoiced(SiigoInvoiceStatus status, SiigoInvoiceKind kind = SiigoInvoiceKind.Order)
    {
        var row = Pending(kind);
        row.BeginSend(Now.AddMinutes(-20));
        row.MarkCreated("id-1", "FV-1-9", 100m, status == SiigoInvoiceStatus.Stamped ? "cufe" : null, Now.AddMinutes(-20));
        if (status == SiigoInvoiceStatus.StampRejected)
        {
            row.MarkStampRejected("dian.rejected", "Regla 90", Now.AddMinutes(-20));
        }

        return row;
    }

    private static SiigoInvoice Freight(SiigoInvoiceStatus status)
    {
        if (status is SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected)
        {
            return Invoiced(status, SiigoInvoiceKind.Freight);
        }

        var row = Pending(
            SiigoInvoiceKind.Freight, status == SiigoInvoiceStatus.Unknown ? Now.AddHours(-25) : Now.AddMinutes(-20));
        switch (status)
        {
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

    private static async Task<SiigoBatchItemOutcome> RecoverAsync(OrderMarks orders, params SiigoInvoice[] rows)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISiigoInvoiceRepository>(new Invoices(rows));
        services.AddSingleton<ISiigoOrderSource>(orders);
        await using var provider = services.BuildServiceProvider();
        // La recuperación sólo usa el scope y el log: ni timbre, ni opciones, ni base.
        using var worker = new SiigoInvoiceBatchWorker(
            null!,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SiigoInvoiceBatchOptions()),
            TimeProvider.System,
            NullLogger<SiigoInvoiceBatchWorker>.Instance);

        return await worker.RecoverAsync(TenantId, OrderId, TestContext.Current.CancellationToken);
    }

    private sealed class OrderMarks(bool marked) : ISiigoOrderSource
    {
        public Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken) =>
            Task.FromResult(marked);

        public Task<SiigoInvoiceInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkInvoicedAsync(
            Guid tenantId, Guid orderId, string number, string siigoInvoiceId, Guid memberId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UnmarkInvoicedAsync(
            Guid tenantId, Guid orderId, string number, Guid memberId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SiigoOrderLabel>> ListLabelsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
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
}
