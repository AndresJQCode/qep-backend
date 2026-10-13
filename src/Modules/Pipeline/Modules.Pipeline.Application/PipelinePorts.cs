using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Spec 2026-10-13 D4: el embudo del tenant, creado con sus semillas (D31) en el primer acceso. Idempotente y seguro con
/// dos peticiones a la vez (índice único por tenant). Devuelve el agregado con sus etapas, rastreado.</summary>
public interface IPipelineProvisioner
{
    Task<SalesPipeline> EnsureAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Todo método recibe <c>tenantId</c>: un id de otro tenant responde igual que uno inexistente.</summary>
public interface IDealRepository
{
    /// <summary>Con tracking.</summary>
    Task<Deal?> FindAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken);

    /// <summary>Los abiertos del cliente, con tracking, el último cambiado primero.</summary>
    Task<IReadOnlyList<Deal>> ListOpenByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>El cerrado más reciente del cliente (contexto de la ficha, D36). Sin tracking.</summary>
    Task<Deal?> FindLastClosedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>De los clientes pedidos, los que tienen al menos un negocio abierto.</summary>
    Task<IReadOnlySet<Guid>> CustomersWithOpenDealsAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>D12: las etapas con al menos un negocio, abierto o cerrado. Ésas no se borran.</summary>
    Task<IReadOnlySet<Guid>> StagesWithDealsAsync(Guid tenantId, Guid pipelineId, CancellationToken cancellationToken);

    /// <summary>D15: la etapa de origen de la fila de mayor <see cref="DealStageHistoryEntry.Ordinal"/>, que en un negocio cerrado
    /// es la de cierre. Por ordinal y no por <c>occurred_at</c>: crear y ganar en el mismo SaveChanges empatan en el instante (P4).</summary>
    Task<Guid?> FindLastOpenStageIdAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken);

    /// <summary>El historial del negocio, por <see cref="DealStageHistoryEntry.Ordinal"/> ascendente: el orden en que el dominio lo
    /// escribió.</summary>
    Task<IReadOnlyList<DealStageHistoryEntry>> ListHistoryAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken);

    void Add(Deal deal);
}

/// <summary>§4.4: la foto de las cotizaciones que llegaron por evento.</summary>
public interface IDealQuotationRepository
{
    /// <summary>Con tracking.</summary>
    Task<DealQuotation?> FindAsync(Guid tenantId, Guid quotationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DealQuotation>> ListByDealAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken);

    /// <summary>Las del cliente sin negocio, para el <c>link</c> del detalle.</summary>
    Task<IReadOnlyList<DealQuotation>> ListUnlinkedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    void Add(DealQuotation quotation);
}

public interface IPipelineReasonRepository
{
    /// <summary>Activos e inactivos, con tracking: el reemplazo del catálogo (P8) los necesita a todos.</summary>
    Task<IReadOnlyList<PipelineReason>> ListAsync(Guid tenantId, ReasonKind kind, CancellationToken cancellationToken);

    Task<PipelineReason?> FindActiveAsync(Guid tenantId, ReasonKind kind, Guid reasonId, CancellationToken cancellationToken);

    /// <summary>Los nombres, activos o no: el historial puede nombrar uno desactivado.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken);

    void Add(PipelineReason reason);
}

public interface ITriageDecisionRepository
{
    /// <summary>La última decisión de cada conversación pedida (vale la última, §5.1): la del entrante más reciente (D38).</summary>
    Task<IReadOnlyDictionary<Guid, TriageDecision>> FindLatestAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken);

    void Add(TriageDecision decision);
}

/// <summary>P4 del plan: antes de guardar, agrega el historial pendiente de cada negocio rastreado. Una versión vieja es
/// <c>concurrency.conflict</c> (412).</summary>
public interface IPipelineUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>§6.3: auditoría atómica propia (proyección de <c>audit.entries</c> en <c>PipelineDbContext</c>), en la misma
/// transacción que el cambio. Lo del worker va con <paramref name="bySystem"/> y actor vacío.</summary>
public interface IPipelineAuditRecorder
{
    void Record(Guid tenantId, Guid actorId, bool bySystem, string action, string resourceType, Guid resourceId, DateTimeOffset occurredAt);
}

public static class PipelineAuditActions
{
    public const string DealResource = "deal";
    public const string TriageResource = "triage_decision";
    public const string SettingsResource = "pipeline";

    public const string DealCreated = "pipeline.deal.created";
    public const string DealUpdated = "pipeline.deal.updated";
    public const string DealMoved = "pipeline.deal.moved";
    public const string DealWon = "pipeline.deal.won";
    public const string DealLost = "pipeline.deal.lost";
    public const string DealReopened = "pipeline.deal.reopened";
    public const string QuotationLinked = "pipeline.deal.quotation_linked";
    public const string QuotationUnlinked = "pipeline.deal.quotation_unlinked";
    public const string PrimaryQuotationSet = "pipeline.deal.primary_quotation_set";
    public const string TriageCreated = "pipeline.triage.created";
    public const string TriageDismissed = "pipeline.triage.dismissed";
    public const string StagesReplaced = "pipeline.stages.replaced";
    public const string LossReasonsReplaced = "pipeline.loss_reasons.replaced";
    public const string DismissReasonsReplaced = "pipeline.dismiss_reasons.replaced";
}
