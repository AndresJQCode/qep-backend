using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-11-10 §4.5: el banco de los dos handlers que ven la conversación de un contacto (decisión de triage y resumen
/// de negocios). Una persona (<see cref="Person"/>) con empresa (<see cref="Company"/>) y una conversación abierta de la persona.</summary>
internal sealed class ContactHandlersBed
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    public ContactHandlersBed()
    {
        Provisioner = new FakeProvisioner(SalesPipeline.CreateDefault(TenantId, Now));
        Customers.Refs[Person] = new PipelineCustomerRef(Person, "Ana Pérez", true);
        Customers.Refs[Company] = new PipelineCustomerRef(Company, "Acme (razón social)", true);
        Customers.Refs[Stranger] = new PipelineCustomerRef(Stranger, "Otro", true);
        Customers.Companies[Person] = new PipelineCompanyRef(Company, "Acme SAS");
        Conversations.Items[ConversationId] = new PipelineConversation(ConversationId, Person, true, Now.AddHours(-1), Now.AddHours(-1));
    }

    public Guid TenantId { get; } = Guid.CreateVersion7();

    public Guid Person { get; } = Guid.CreateVersion7();

    public Guid Company { get; } = Guid.CreateVersion7();

    public Guid Stranger { get; } = Guid.CreateVersion7();

    public Guid ConversationId { get; } = Guid.CreateVersion7();

    public Guid MemberId { get; } = Guid.CreateVersion7();

    public FakeProvisioner Provisioner { get; }

    public SalesPipeline Pipeline => Provisioner.Pipeline;

    public InMemoryDeals Deals { get; } = new();

    public CompanyAwareCustomers Customers { get; } = new();

    public FakeConversations Conversations { get; } = new();

    public RecordingDecisions Decisions { get; } = new();

    public RecordingPipelineAudit Audit { get; } = new();

    public NoOpUnitOfWork UnitOfWork { get; } = new();

    private FakeExecution Execution => new(TenantId);

    private static IClock ClockAtNow => new FixedClock(Now);

    private DealDetailBuilder DetailBuilder => new(
        Customers, new FakeMemberNames(), Conversations, Deals, new InMemoryDealQuotations(), new InMemoryDealOrders(), new FakeReasons(), ClockAtNow);

    public DecideTriageHandler Triage()
    {
        var execution = Execution;
        return new DecideTriageHandler(
            execution,
            new ModulesWithMessaging(),
            Conversations,
            Customers,
            Deals,
            Decisions,
            new FakeReasons(),
            new PipelineCaller(new FakeMembership(MemberId), execution),
            UnitOfWork,
            Audit,
            new DealCreator(execution, Provisioner, Deals, new FakeCurrency(), UnitOfWork, Audit, DetailBuilder),
            ClockAtNow,
            new DecideTriageValidator());
    }

    public GetCustomerDealsSummaryHandler Summary() =>
        new(
            Execution,
            new ModulesWithMessaging(),
            Provisioner,
            Deals,
            new InMemoryDealQuotations(),
            new FakeReasons(),
            Decisions,
            Customers,
            new FakeMemberNames(),
            Conversations,
            ClockAtNow);

    public Deal OpenDeal(Guid customerId, string title, DateTimeOffset? changedAt = null)
    {
        var deal = Deal.Create(TenantId, Pipeline, customerId, MemberId, title, null, "COP", null, DealSource.Manual, null, DealActor.ForUser(MemberId), changedAt ?? Now);
        Deals.Add(deal);
        return deal;
    }

    public Deal ClosedDeal(Guid customerId, string title, DateTimeOffset closedAt)
    {
        var deal = OpenDeal(customerId, title, closedAt.AddDays(-5));
        deal.Win(Pipeline, DealActor.ForUser(MemberId), closedAt);
        return deal;
    }
}

internal sealed class CompanyAwareCustomers : IPipelineCustomerDirectory
{
    public Dictionary<Guid, PipelineCustomerRef> Refs { get; } = [];

    /// <summary>Por id de la persona.</summary>
    public Dictionary<Guid, PipelineCompanyRef> Companies { get; } = [];

    public Task<IReadOnlyDictionary<Guid, PipelineCustomerRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PipelineCustomerRef>>(Refs.Where(pair => customerIds.Contains(pair.Key)).ToDictionary());

    public Task<Guid> ResolveSurvivorAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) => Task.FromResult(customerId);

    public Task<Guid?> FindCompanyIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies.TryGetValue(customerId, out var company) ? (Guid?)company.Id : null);

    public Task<PipelineCompanyRef?> FindCompanyAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies.GetValueOrDefault(customerId));
}

internal sealed class FakeConversations : IPipelineConversationDirectory
{
    public Dictionary<Guid, PipelineConversation> Items { get; } = [];

    public Dictionary<Guid, PipelineCustomerActivity> Activity { get; } = [];

    public Task<IReadOnlyDictionary<Guid, PipelineCustomerActivity>> FindActivityAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PipelineCustomerActivity>>(Activity.Where(pair => customerIds.Contains(pair.Key)).ToDictionary());

    public Task<PipelineConversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.GetValueOrDefault(conversationId));
}

internal sealed class RecordingDecisions : ITriageDecisionRepository
{
    public List<TriageDecision> Items { get; } = [];

    public Task<IReadOnlyDictionary<Guid, TriageDecision>> FindLatestAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, TriageDecision>>(
            Items.Where(decision => conversationIds.Contains(decision.ConversationId)).GroupBy(decision => decision.ConversationId)
                .ToDictionary(group => group.Key, group => group.OrderBy(decision => decision.DecidedAt).Last()));

    public void Add(TriageDecision decision) => Items.Add(decision);
}

internal sealed class NoOpUnitOfWork : IPipelineUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.FromResult(0);
    }
}

internal sealed class FakeReasons : IPipelineReasonRepository
{
    public Task<IReadOnlyList<PipelineReason>> ListAsync(Guid tenantId, ReasonKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PipelineReason?> FindActiveAsync(Guid tenantId, ReasonKind kind, Guid reasonId, CancellationToken cancellationToken) =>
        Task.FromResult<PipelineReason?>(null);

    public Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public void Add(PipelineReason reason) => throw new NotSupportedException();
}

internal sealed class FakeMemberNames : IPipelineMemberNames
{
    public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(memberIds.ToDictionary(id => id, _ => "Asesor"));
}

internal sealed class FakeExecution(Guid tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => true;
}

internal sealed class FakeMembership(Guid memberId) : IMembershipDirectory
{
    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(memberId);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class ModulesWithMessaging : ITenantModules
{
    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<TenantModuleSet?>(TenantModuleSet.FromStored([TenantModuleKeys.Customers, TenantModuleKeys.Pipeline, TenantModuleKeys.Messaging]));
}

internal sealed class FakeCurrency : ITenantDefaultCurrency
{
    public Task<string> GetAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult("COP");
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
