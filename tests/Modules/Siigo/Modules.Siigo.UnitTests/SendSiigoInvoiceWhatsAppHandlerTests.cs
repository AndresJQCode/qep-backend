using System.Reflection;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Audit.Domain;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Enviar la factura por WhatsApp (spec 2026-11-21): el caso de uso sólo orquesta (autoriza, busca la fila, pide el PDF a Siigo, lo
/// entrega por el puerto y audita) y la lista marca <c>canSendWhatsApp</c>. El canal, el teléfono y la plantilla son de Quotations.
/// </summary>
public sealed class SendSiigoInvoiceWhatsAppHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D];

    [Fact]
    public async Task ASuccessfulSendLoadsThePdfSendsItAndAudits()
    {
        var row = CreatedRow();
        var harness = new Harness([row]);

        var result = await harness.Send(row.Id);

        Assert.True(result.Sent);
        Assert.Equal([row.SiigoInvoiceId!], harness.Api.RequestedPdfs);
        var sent = Assert.Single(harness.Delivery.Sent);
        Assert.Equal((TenantId, OrderId, "FV-1-123", 288490.5m), (sent.TenantId, sent.OrderId, sent.InvoiceNumber, sent.InvoiceTotal));
        Assert.Equal(Pdf, sent.Pdf);
        var audit = Assert.Single(harness.Audit.Entries);
        Assert.Equal(
            (TenantId, SubjectId, AuditActorType.Human, SiigoAuditActions.InvoiceWhatsAppSent, SiigoAuditActions.InvoiceResourceType, row.Id.ToString()),
            (audit.TenantId, audit.ActorId, audit.ActorType, audit.Action, audit.ResourceType, audit.ResourceId));
        Assert.Equal("siigo.invoice.whatsapp_sent", SiigoAuditActions.InvoiceWhatsAppSent);
        Assert.Equal(1, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AnInvoiceOfAnotherOrderIsNotFoundAndNothingIsCalled()
    {
        var harness = new Harness([CreatedRow(orderId: Guid.CreateVersion7())]);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => harness.Send(harness.Rows[0].Id));

        Assert.Equal(SiigoErrorCodes.InvoiceNotFound, error.Code);
        harness.AssertNothingCalled();
    }

    [Fact]
    public async Task AnInvoiceWithoutPdfIsNotFoundAndNothingIsCalled()
    {
        var pending = PendingRow();
        var harness = new Harness([pending]);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => harness.Send(pending.Id));

        Assert.Equal(SiigoErrorCodes.InvoiceNotFound, error.Code);
        harness.AssertNothingCalled();
    }

    [Fact]
    public async Task WithoutThePermissionItIs403NotNotFound()
    {
        var row = CreatedRow();
        var harness = new Harness([row], hasPermission: false);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => harness.Send(row.Id));

        Assert.Equal("authorization.denied", error.Code);
        harness.AssertNothingCalled();
    }

    [Fact]
    public async Task ASendFailureIsNotAuditedAsSent()
    {
        var row = CreatedRow();
        var failure = new InvalidOperationException("invoice.whatsapp.send_failed");
        var harness = new Harness([row], deliveryFailure: failure);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Send(row.Id));

        Assert.Same(failure, error);
        Assert.Empty(harness.Audit.Entries);
        Assert.Equal(0, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task TheAuditCarriesNeitherPhoneNorContent()
    {
        var row = CreatedRow();
        var harness = new Harness([row]);

        await harness.Send(row.Id);

        var json = Assert.Single(harness.Audit.Entries).ChangedFieldsJson;
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["errorCode", "fromStatus", "kind", "number", "orderId", "outcome", "status"],
            [.. document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)]);
        Assert.DoesNotContain(Convert.ToBase64String(Pdf), json, StringComparison.Ordinal);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheListMarksCanSendWhatsAppOnlyWhenThePdfExistsAndDeliveryCan()
    {
        var withPdf = CreatedRow();
        var withoutPdf = PendingRow(SiigoInvoiceKind.Freight);

        var allowed = await new Harness([withPdf, withoutPdf], canSend: true).List();
        var denied = await new Harness([withPdf, withoutPdf], canSend: false).List();

        Assert.Equal(
            [(SiigoInvoiceKind.Order.ToString(), true), (SiigoInvoiceKind.Freight.ToString(), false)],
            [.. allowed.Select(dto => (dto.Kind, dto.CanSendWhatsApp))]);
        Assert.All(denied, dto => Assert.False(dto.CanSendWhatsApp));
    }

    [Fact]
    public async Task TheListDoesNotAskDeliveryWhenNoRowHasPdf()
    {
        var harness = new Harness([PendingRow()], canSend: true);

        var list = await harness.List();

        Assert.All(list, dto => Assert.False(dto.CanSendWhatsApp));
        Assert.Equal(0, harness.Delivery.CanSendCalls);
    }

    // La pregunta es una pista opcional: que falle no puede tumbar la lista, que trae las filas que la pantalla tiene que mostrar.
    [Fact]
    public async Task AFailingAvailabilityCheckStillListsEveryRowWithoutTheHint()
    {
        var harness = new Harness([CreatedRow(), PendingRow(SiigoInvoiceKind.Freight)], canSendFailure: new InvalidOperationException("db down"));

        var list = await harness.List();

        Assert.Equal(2, list.Count);
        Assert.All(list, dto => Assert.False(dto.CanSendWhatsApp));
        Assert.Equal(1, harness.Delivery.CanSendCalls);
    }

    [Fact]
    public async Task ACancelledAvailabilityCheckStillPropagates()
    {
        var harness = new Harness([CreatedRow()], canSendFailure: new OperationCanceledException());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.List(cancelled.Token));
    }

    // Un timeout del HttpClient llega como TaskCanceledException aunque nadie haya cancelado la petición: es una falla, no una cancelación.
    [Fact]
    public async Task ATimeoutThatIsNotACancellationOfTheRequestFallsBackToNoHint()
    {
        var harness = new Harness([CreatedRow()], canSendFailure: new TaskCanceledException("timeout"));

        var list = await harness.List();

        Assert.All(list, dto => Assert.False(dto.CanSendWhatsApp));
    }

    [Fact]
    public async Task TheListAsksDeliveryOnceForTheWholeRequest()
    {
        var harness = new Harness([CreatedRow(), CreatedRow(SiigoInvoiceKind.Freight)], canSend: true);

        await harness.List();

        Assert.Equal(1, harness.Delivery.CanSendCalls);
    }

    private static SiigoInvoice PendingRow(SiigoInvoiceKind kind = SiigoInvoiceKind.Order, Guid? orderId = null) =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), TenantId, ConnectionId, orderId ?? OrderId, kind, """{"a":1}""", 7, SubjectId, Now);

    private static SiigoInvoice CreatedRow(SiigoInvoiceKind kind = SiigoInvoiceKind.Order, Guid? orderId = null)
    {
        var row = PendingRow(kind, orderId);
        row.MarkCreated($"siigo-{Guid.CreateVersion7():N}", "FV-1-123", 288490.5m, null, Now);
        return row;
    }

    private sealed class Harness
    {
        public Harness(
            IEnumerable<SiigoInvoice> rows, bool hasPermission = true, Exception? deliveryFailure = null, bool canSend = true, Exception? canSendFailure = null)
        {
            Rows = [.. rows];
            Delivery = new RecordingDelivery(deliveryFailure, canSend, canSendFailure);
            Api = ApiProxy.Create(Pdf);
            Context = new ExecutionContext(SubjectId, TenantId, hasPermission);
        }

        public SiigoInvoice[] Rows { get; }

        public RecordingDelivery Delivery { get; }

        public ApiProxy Api { get; }

        public ExecutionContext Context { get; }

        public RecordingAudit Audit { get; } = new();

        public RecordingUnitOfWork UnitOfWork { get; } = new();

        public Task<SiigoInvoiceWhatsAppSent> Send(Guid invoiceId) =>
            new SendSiigoInvoiceWhatsAppHandler(
                Context, new AllModules(), new ActiveConnections(), Api.Instance, new StubInvoices(Rows), Delivery, UnitOfWork, Audit, new FixedClock(Now))
                .HandleAsync(new SendSiigoInvoiceWhatsAppCommand(TenantId, OrderId, invoiceId), TestContext.Current.CancellationToken);

        public async Task<IReadOnlyList<SiigoInvoiceDto>> List(CancellationToken? cancellationToken = null) =>
            await new GetSiigoInvoicesHandler(
                Context, new AllModules(), new StubInvoices(Rows), UnitOfWork, new FixedClock(Now), Delivery, new NoOpenBatches(),
                new MarkedOrders(), NullLogger<GetSiigoInvoicesHandler>.Instance)
                .HandleAsync(new GetSiigoInvoicesQuery(TenantId, OrderId), cancellationToken ?? TestContext.Current.CancellationToken);

        public void AssertNothingCalled()
        {
            Assert.Empty(Api.RequestedPdfs);
            Assert.Empty(Delivery.Sent);
            Assert.Empty(Audit.Entries);
            Assert.Equal(0, UnitOfWork.Saves);
        }
    }

    private sealed class ExecutionContext(Guid subjectId, Guid tenantId, bool hasPermission) : IExecutionContext
    {
        public Guid SubjectId { get; } = subjectId;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => hasPermission;
    }

    private sealed class AllModules : ITenantModules
    {
        // `null` = el tenant no tiene fila: el guard lo deja pasar (como el stub de desarrollo).
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
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

    private sealed class StubInvoices(SiigoInvoice[] rows) : ISiigoInvoiceRepository
    {
        public Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>([.. rows.Where(row => row.TenantId == tenantId && row.OrderId == orderId)]);

        public Task<IReadOnlyList<SiigoInvoice>> ListFreightAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>([.. rows.Where(row => row.TenantId == tenantId && invoiceIds.Contains(row.Id))]);

        public void Add(SiigoInvoice invoice) => throw new NotSupportedException();
    }

    /// <summary>Ninguna tanda tiene el pedido.</summary>
    private sealed class NoOpenBatches : ISiigoInvoiceBatchRepository
    {
        public Task<Guid?> FindOpenBatchIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);

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

    /// <summary>Todo pedido lleva la marca de su factura.</summary>
    private sealed class MarkedOrders : ISiigoOrderSource
    {
        public Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken) =>
            Task.FromResult(true);

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

    private sealed record SentInvoice(Guid TenantId, Guid OrderId, string InvoiceNumber, decimal? InvoiceTotal, byte[] Pdf);

    private sealed class RecordingDelivery(Exception? failure, bool canSend, Exception? canSendFailure = null) : ISiigoInvoiceWhatsApp
    {
        public List<SentInvoice> Sent { get; } = [];

        public int CanSendCalls { get; private set; }

        public Task SendAsync(
            Guid tenantId, Guid orderId, string invoiceNumber, decimal? invoiceTotal, ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                throw failure;
            }

            Sent.Add(new SentInvoice(tenantId, orderId, invoiceNumber, invoiceTotal, pdf.ToArray()));
            return Task.CompletedTask;
        }

        public Task<bool> CanSendAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
        {
            CanSendCalls++;
            if (canSendFailure is not null)
            {
                throw canSendFailure;
            }

            return Task.FromResult(canSend);
        }
    }

    private sealed record AuditEntry(
        Guid TenantId, Guid ActorId, AuditActorType ActorType, string Action, string ResourceType, string ResourceId, string ChangedFieldsJson);

    private sealed class RecordingAudit : ISiigoAuditRecorder
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Record(
            Guid tenantId, Guid actorId, AuditActorType actorType, string action, string resourceType, string resourceId,
            string changedFieldsJson, DateTimeOffset occurredAt) =>
            Entries.Add(new AuditEntry(tenantId, actorId, actorType, action, resourceType, resourceId, changedFieldsJson));
    }

    private sealed class RecordingUnitOfWork : ISiigoUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public Task<ISiigoTransaction> BeginTransactionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Un <see cref="ISiigoApi"/> que sólo sabe entregar el PDF; cualquier otra llamada es un error de la prueba.</summary>
    public class ApiProxy : DispatchProxy
    {
        private byte[] _pdf = [];

        public List<string> RequestedPdfs { get; } = [];

        public ISiigoApi Instance => (ISiigoApi)(object)this;

        public static ApiProxy Create(byte[] pdf)
        {
            var proxy = (ApiProxy)(object)DispatchProxy.Create<ISiigoApi, ApiProxy>();
            proxy._pdf = pdf;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ISiigoApi.GetInvoicePdfAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            RequestedPdfs.Add((string)args![1]!);
            return Task.FromResult(_pdf);
        }
    }
}
