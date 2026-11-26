using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Lo que las lecturas de tandas calculan de un pedido que quedó por confirmar y cuya factura ya existe (decidido el 2026-11-25): con la
/// de flete todavía <c>Pending</c> no se lee facturado, y el detalle avisa si el pedido no lleva la marca.
/// </summary>
public sealed class SiigoInvoiceBatchReadTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheDetailShowsFreightPendingWhenTheOrderInvoiceExistsAndTheFreightDidNotLeave()
    {
        var detail = await new Harness(Resolved(), Freight()).DetailAsync();

        var item = Assert.Single(detail.Items);
        Assert.Equal(("Failed", "siigo.invoice.freight_pending"), (item.Status, item.ErrorCode));
        Assert.Equal((0, 1), (detail.Invoiced, detail.Failed));
    }

    [Fact]
    public async Task TheListCountsThatOrderAsFailed()
    {
        var page = await new Harness(Resolved(), Freight()).ListAsync();

        var counts = Assert.Single(page.Items).StatusCounts;
        Assert.Equal(1, counts.Single(count => count.Status == "Failed").Count);
        Assert.Equal(0, counts.Single(count => count.Status == "Invoiced").Count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheDetailSaysWhetherAResolvedOrderCarriesTheMark(bool marked, bool orderMarkFailed)
    {
        var harness = new Harness(Resolved()) { Marked = marked };

        var item = Assert.Single((await harness.DetailAsync()).Items);

        Assert.Equal(("Invoiced", orderMarkFailed), (item.Status, item.OrderMarkFailed));
    }

    [Fact]
    public async Task TheDetailAlsoWarnsOfTheMarkWhenTheFreightIsPending()
    {
        var item = Assert.Single((await new Harness(Resolved(), Freight()) { Marked = false }.DetailAsync()).Items);

        Assert.True(item.OrderMarkFailed);
    }

    // El worker puede estar marcándolo: ni se pregunta ni se avisa.
    [Fact]
    public async Task AQueuedOrderIsNotAskedForItsMark()
    {
        var harness = new Harness(Resolved()) { Marked = false, Queued = true };

        var item = Assert.Single((await harness.DetailAsync()).Items);

        Assert.False(item.OrderMarkFailed);
        Assert.Equal(0, harness.MarkReads);
    }

    [Fact]
    public async Task AnOrderStillToConfirmIsNotAskedForItsMark()
    {
        var harness = new Harness(Unanswered()) { Marked = false };

        var item = Assert.Single((await harness.DetailAsync()).Items);

        Assert.Equal(("ToConfirm", false), (item.Status, item.OrderMarkFailed));
        Assert.Equal(0, harness.MarkReads);
    }

    [Fact]
    public async Task AStoredInvoicedItemWithARejectedFreightReadsFailedInTheDetailAndTheList()
    {
        var harness = new Harness(Resolved(), Rejected(Freight())) { StoredInvoiced = true };

        var item = Assert.Single((await harness.DetailAsync()).Items);
        var counts = Assert.Single((await harness.ListAsync()).Items).StatusCounts;

        Assert.Equal(("Failed", "siigo.invoice.freight_not_created"), (item.Status, item.ErrorCode));
        Assert.Equal(1, counts.Single(count => count.Status == "Failed").Count);
        Assert.Equal(0, counts.Single(count => count.Status == "Invoiced").Count);
    }

    // La de flete de un intento anterior del pedido no dice nada de la factura de ahora.
    [Fact]
    public async Task AFreightOlderThanTheOrderInvoiceIsIgnored()
    {
        var older = Rejected(Row(SiigoInvoiceKind.Freight, Now.AddDays(-1)));

        var item = Assert.Single((await new Harness(Resolved(), older).DetailAsync()).Items);

        Assert.Equal("Invoiced", item.Status);
    }

    [Fact]
    public async Task TheNewestFreightOfTheOrderInvoiceDecides()
    {
        var first = Rejected(Freight());
        var newest = Row(SiigoInvoiceKind.Freight, Now.AddMinutes(-20));
        newest.BeginSend(Now.AddMinutes(-20));
        newest.MarkCreated("id-2", "FV-1-10", 10m, null, Now.AddMinutes(-20));

        var item = Assert.Single((await new Harness(Resolved(), newest, first).DetailAsync()).Items);

        Assert.Equal("Invoiced", item.Status);
    }

    // Spec 2026-11-25 §1: se leía fallido por la de flete; anulada la del pedido, dice la anulación y no vuelve a leerse facturado.
    [Fact]
    public async Task AStoredInvoicedItemWhoseInvoiceWasAnnulledReadsFailedInTheDetailAndTheList()
    {
        var harness = new Harness(Annulled(), Rejected(Freight())) { StoredInvoiced = true, Marked = false };

        var detail = await harness.DetailAsync();
        var counts = Assert.Single((await harness.ListAsync()).Items).StatusCounts;

        var item = Assert.Single(detail.Items);
        Assert.Equal(("Failed", "siigo.invoice.annulled_in_siigo"), (item.Status, item.ErrorCode));
        Assert.Null(item.InvoiceNumber);
        Assert.False(item.OrderMarkFailed);
        Assert.Equal((false, false), (item.CanResend, item.CanResolve));
        Assert.Equal((0, 1), (detail.Invoiced, detail.Failed));
        Assert.Equal(1, counts.Single(count => count.Status == "Failed").Count);
        Assert.Equal(0, counts.Single(count => count.Status == "Invoiced").Count);
        Assert.Equal(0, harness.MarkReads);
    }

    // El pedido se volvió a facturar: el ítem de antes sigue enlazado a su fila anulada y no toma nada de las nuevas.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnItemOfAnAnnulledInvoiceStaysAnnulledAfterTheOrderIsInvoicedAgain(bool storedInvoiced)
    {
        var again = Row(SiigoInvoiceKind.Order, Now.AddMinutes(-2));
        again.BeginSend(Now.AddMinutes(-2));
        again.MarkCreated("id-3", "FV-1-11", 100m, null, Now.AddMinutes(-2));
        var freight = Row(SiigoInvoiceKind.Freight, Now.AddMinutes(-2));
        var harness = new Harness(Annulled(), again, freight) { StoredInvoiced = storedInvoiced };

        var item = Assert.Single((await harness.DetailAsync()).Items);

        Assert.Equal(("Failed", "siigo.invoice.annulled_in_siigo"), (item.Status, item.ErrorCode));
        Assert.Null(item.InvoiceNumber);
    }

    private static SiigoInvoice Annulled()
    {
        var row = Unanswered();
        row.MarkCreated("id-1", "FV-1-9", 100m, null, Now.AddMinutes(-5));
        row.MarkAnnulled(false, Now.AddMinutes(-3));
        return row;
    }

    private static SiigoInvoice Rejected(SiigoInvoice freight)
    {
        freight.MarkRejected("siigo.invoice.rejected", "invalid_reference", null, Now.AddMinutes(-20));
        return freight;
    }

    private static SiigoInvoice Row(SiigoInvoiceKind kind, DateTimeOffset? createdAt = null) =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), TenantId, Guid.CreateVersion7(), OrderId, kind, """{"a":1}""", 7, Guid.CreateVersion7(),
            createdAt ?? Now.AddHours(-1));

    private static SiigoInvoice Unanswered()
    {
        var row = Row(SiigoInvoiceKind.Order);
        row.BeginSend(Now.AddMinutes(-30));
        row.MarkUnanswered(SiigoErrorCodes.InvoiceTimeout, null, Now.AddMinutes(-30));
        return row;
    }

    private static SiigoInvoice Resolved()
    {
        var row = Unanswered();
        row.ResolveExists("FV-1-9", Now.AddMinutes(-5));
        return row;
    }

    private static SiigoInvoice Freight() => Row(SiigoInvoiceKind.Freight);

    // Una tanda de un pedido que quedó por confirmar, enlazada a la primera fila.
    private sealed class Harness : ISiigoInvoiceBatchRepository, ISiigoInvoiceRepository, ISiigoOrderSource, IExecutionContext,
        ITenantModules, IClock
    {
        private readonly SiigoInvoice[] _rows;
        private readonly SiigoInvoiceBatch _batch;

        public Harness(params SiigoInvoice[] rows)
        {
            _rows = rows;
            _batch = SiigoInvoiceBatch.Create(Guid.CreateVersion7(), TenantId, SubjectId, [OrderId], Now.AddMinutes(-40));
        }

        /// <summary>El ítem se guardó facturado; si no, por confirmar.</summary>
        public bool StoredInvoiced { get; init; }

        // Se cierra en la primera lectura, cuando ya se sabe cómo.
        private SiigoInvoiceBatch Settled()
        {
            if (StoredInvoiced)
            {
                _batch.Items[0].MarkInvoiced("FV-1-9", Now.AddMinutes(-30), _rows[0].Id);
            }
            else
            {
                _batch.Items[0].MarkToConfirm(_rows[0].Id, SiigoErrorCodes.InvoiceTimeout, Now.AddMinutes(-30));
            }

            return _batch;
        }

        public bool Marked { get; init; } = true;

        public bool Queued { get; init; }

        public int MarkReads { get; private set; }

        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public DateTimeOffset UtcNow => Now;

        TenantId IExecutionContext.TenantId => new(SiigoInvoiceBatchReadTests.TenantId);

        public bool HasPermission(string permission) => permission == SiigoPermissions.InvoiceIssue;

        public Task<SiigoInvoiceBatchDto> DetailAsync() =>
            new GetSiigoInvoiceBatchHandler(this, this, this, this, this, this, NullLogger<GetSiigoInvoiceBatchHandler>.Instance)
                .HandleAsync(new GetSiigoInvoiceBatchQuery(TenantId, _batch.Id), TestContext.Current.CancellationToken);

        public Task<SiigoInvoiceBatchPageDto> ListAsync() =>
            new ListSiigoInvoiceBatchesHandler(this, this, this, this, this, new ListSiigoInvoiceBatchesValidator())
                .HandleAsync(new ListSiigoInvoiceBatchesQuery(TenantId, null, null), TestContext.Current.CancellationToken);

        Task<TenantModuleSet?> ITenantModules.FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);

        Task<SiigoInvoiceBatch?> ISiigoInvoiceBatchRepository.FindAsync(
            Guid tenantId, Guid batchId, CancellationToken cancellationToken) => Task.FromResult<SiigoInvoiceBatch?>(Settled());

        public Task<(IReadOnlyList<SiigoInvoiceBatch> Items, int Total)> SearchAsync(
            Guid tenantId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<(IReadOnlyList<SiigoInvoiceBatch>, int)>(([Settled()], 1));

        public Task<IReadOnlyList<Guid>> ListOpenOrderIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(Queued ? [OrderId] : []);

        public Task<Guid?> FindOpenBatchIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        void ISiigoInvoiceBatchRepository.Add(SiigoInvoiceBatch batch) => throw new NotSupportedException();

        public Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>(
                [.. _rows.Where(row => row.TenantId == tenantId && invoiceIds.Contains(row.Id))]);

        public Task<IReadOnlyList<SiigoInvoice>> ListFreightAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>(
                [
                    .. _rows.Where(row => row.TenantId == tenantId && orderIds.Contains(row.OrderId)
                        && row.Kind == SiigoInvoiceKind.Freight),
                ]);

        public Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        void ISiigoInvoiceRepository.Add(SiigoInvoice invoice) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SiigoOrderLabel>> ListLabelsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SiigoOrderLabel>>(new Dictionary<Guid, SiigoOrderLabel>());

        public Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken)
        {
            MarkReads++;
            return Task.FromResult(Marked);
        }

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
    }
}
