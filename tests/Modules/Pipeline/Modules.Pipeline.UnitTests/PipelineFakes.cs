using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Los dobles del <see cref="QuotationEventApplier"/>. <see cref="InMemoryDeals.Flush"/> hace lo que la unidad de trabajo:
/// guarda el historial pendiente de cada negocio.</summary>
internal sealed class ApplierBed
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    public ApplierBed()
    {
        Provisioner = new FakeProvisioner(SalesPipeline.CreateDefault(TenantId, Now));
        Applier = new QuotationEventApplier(Modules, Provisioner, Deals, Quotations, Orders, Audit);
    }

    public Guid TenantId { get; } = Guid.CreateVersion7();

    public Guid AdvisorId { get; } = Guid.CreateVersion7();

    public FakeTenantModules Modules { get; } = new();

    public FakeProvisioner Provisioner { get; }

    public SalesPipeline Pipeline => Provisioner.Pipeline;

    public InMemoryDeals Deals { get; } = new();

    public InMemoryDealQuotations Quotations { get; } = new();

    public InMemoryDealOrders Orders { get; } = new();

    public RecordingPipelineAudit Audit { get; } = new();

    public QuotationEventApplier Applier { get; }

    public QuotationFact Fact(Guid clientId, Guid quotationId, decimal total = 1_000m, string currency = "COP", Guid? orderId = null) =>
        new(TenantId, quotationId, "COT-0001", clientId, AdvisorId, total, currency, orderId);

    public OrderFact OrderFact(Guid clientId, Guid orderId, decimal total, string source = "Manual") =>
        new(TenantId, orderId, "PED-0001", clientId, AdvisorId, total, "COP", source, null);

    public Deal OpenDeal(Guid clientId, string title = "Manual")
    {
        var deal = Deal.Create(TenantId, Pipeline, clientId, AdvisorId, title, null, "COP", null, DealSource.Manual, null, DealActor.ForUser(AdvisorId), Now);
        Deals.Add(deal);
        Deals.Flush();
        return deal;
    }

    public PipelineStage Stage(string name) => Pipeline.OrderedStages.Single(stage => stage.Name == name);
}

internal sealed class FakeTenantModules : ITenantModules
{
    public bool PipelineEnabled { get; set; } = true;

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        TenantModuleKey[] keys = PipelineEnabled ? [TenantModuleKeys.Customers, TenantModuleKeys.Pipeline] : [TenantModuleKeys.Customers];
        return Task.FromResult<TenantModuleSet?>(TenantModuleSet.FromStored(keys));
    }
}

internal sealed class FakeProvisioner(SalesPipeline pipeline) : IPipelineProvisioner
{
    public SalesPipeline Pipeline { get; } = pipeline;

    public Task<SalesPipeline> EnsureAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(Pipeline);
}

internal sealed class InMemoryDeals : IDealRepository
{
    public List<Deal> Items { get; } = [];

    public List<DealStageHistoryEntry> History { get; } = [];

    public void Flush()
    {
        foreach (var deal in Items)
        {
            History.AddRange(deal.PullHistory());
        }
    }

    public Task<Deal?> FindAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(deal => deal.TenantId == tenantId && deal.Id == dealId));

    public Task<IReadOnlyList<Deal>> ListOpenByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Deal>>([.. Items.Where(deal => deal.TenantId == tenantId && deal.CustomerId == customerId && !deal.IsClosed)]);

    public Task<Deal?> FindLastClosedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Where(deal => deal.TenantId == tenantId && deal.CustomerId == customerId && deal.IsClosed).MaxBy(deal => deal.ClosedAt));

    public Task<IReadOnlySet<Guid>> CustomersWithOpenDealsAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(Items.Where(deal => !deal.IsClosed && customerIds.Contains(deal.CustomerId)).Select(deal => deal.CustomerId).ToHashSet());

    public Task<IReadOnlySet<Guid>> StagesWithDealsAsync(Guid tenantId, Guid pipelineId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(Items.Select(deal => deal.StageId).ToHashSet());

    // Por ordinal, como DealRepository (P4): el orden de la lista no es contrato.
    public Task<Guid?> FindLastOpenStageIdAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult(History.Where(row => row.DealId == dealId).MaxBy(row => row.Ordinal)?.FromStageId);

    public Task<IReadOnlyList<DealStageHistoryEntry>> ListHistoryAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealStageHistoryEntry>>([.. History.Where(row => row.DealId == dealId).OrderBy(row => row.Ordinal)]);

    public void Add(Deal deal) => Items.Add(deal);
}

internal sealed class InMemoryDealQuotations : IDealQuotationRepository
{
    public List<DealQuotation> Items { get; } = [];

    public Task<DealQuotation?> FindAsync(Guid tenantId, Guid quotationId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(quotation => quotation.TenantId == tenantId && quotation.QuotationId == quotationId));

    public Task<IReadOnlyList<DealQuotation>> ListByDealAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealQuotation>>([.. Items.Where(quotation => quotation.DealId == dealId)]);

    public Task<IReadOnlyList<DealQuotation>> ListUnlinkedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealQuotation>>([.. Items.Where(quotation => quotation.CustomerId == customerId && quotation.DealId is null)]);

    public void Add(DealQuotation quotation) => Items.Add(quotation);
}

internal sealed class InMemoryDealOrders : IDealOrderRepository
{
    public List<DealOrder> Items { get; } = [];

    public Task<DealOrder?> FindAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.SingleOrDefault(order => order.TenantId == tenantId && order.OrderId == orderId));

    public Task<IReadOnlyList<DealOrder>> ListByDealAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealOrder>>([.. Items.Where(order => order.TenantId == tenantId && order.DealId == dealId)]);

    public Task<IReadOnlyList<DealOrder>> ListUnlinkedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealOrder>>([.. Items.Where(order => order.TenantId == tenantId && order.CustomerId == customerId && order.DealId is null)]);

    public void Add(DealOrder order) => Items.Add(order);
}

internal sealed class RecordingPipelineAudit : IPipelineAuditRecorder
{
    public List<(string Action, Guid ResourceId, bool BySystem)> Entries { get; } = [];

    public void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset occurredAt) =>
        Entries.Add((action, resourceId, bySystem));
}
