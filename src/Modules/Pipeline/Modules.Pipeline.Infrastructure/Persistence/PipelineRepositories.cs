using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Infrastructure.Persistence;

internal sealed class DealRepository(PipelineDbContext dbContext) : IDealRepository
{
    public Task<Deal?> FindAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        dbContext.Deals.SingleOrDefaultAsync(deal => deal.TenantId == tenantId && deal.Id == dealId, cancellationToken);

    public async Task<IReadOnlyList<Deal>> ListOpenByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        await dbContext.Deals
            .Where(deal => deal.TenantId == tenantId && deal.CustomerId == customerId && deal.ClosedAt == null)
            .OrderByDescending(deal => deal.LastChangedAt)
            .ToListAsync(cancellationToken);

    public Task<Deal?> FindLastClosedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        dbContext.Deals.AsNoTracking()
            .Where(deal => deal.TenantId == tenantId && deal.CustomerId == customerId && deal.ClosedAt != null)
            .OrderByDescending(deal => deal.ClosedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlySet<Guid>> CustomersWithOpenDealsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = customerIds.ToArray();
        var found = await dbContext.Deals.AsNoTracking()
            .Where(deal => deal.TenantId == tenantId && deal.ClosedAt == null && ids.Contains(deal.CustomerId))
            .Select(deal => deal.CustomerId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return found.ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> StagesWithDealsAsync(Guid tenantId, Guid pipelineId, CancellationToken cancellationToken)
    {
        var found = await dbContext.Deals.AsNoTracking()
            .Where(deal => deal.TenantId == tenantId && deal.PipelineId == pipelineId)
            .Select(deal => deal.StageId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return found.ToHashSet();
    }

    public Task<Guid?> FindLastOpenStageIdAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        dbContext.History.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.DealId == dealId)
            .OrderByDescending(entry => entry.Ordinal)
            .Select(entry => entry.FromStageId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DealStageHistoryEntry>> ListHistoryAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        await dbContext.History.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.DealId == dealId)
            .OrderBy(entry => entry.Ordinal)
            .ToListAsync(cancellationToken);

    public void Add(Deal deal) => dbContext.Deals.Add(deal);
}

internal sealed class DealQuotationRepository(PipelineDbContext dbContext) : IDealQuotationRepository
{
    public Task<DealQuotation?> FindAsync(Guid tenantId, Guid quotationId, CancellationToken cancellationToken) =>
        dbContext.DealQuotations.SingleOrDefaultAsync(
            quotation => quotation.TenantId == tenantId && quotation.QuotationId == quotationId, cancellationToken);

    public async Task<IReadOnlyList<DealQuotation>> ListByDealAsync(Guid tenantId, Guid dealId, CancellationToken cancellationToken) =>
        await dbContext.DealQuotations.AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId && quotation.DealId == dealId)
            .OrderByDescending(quotation => quotation.UpdatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DealQuotation>> ListUnlinkedByCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        await dbContext.DealQuotations.AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId && quotation.CustomerId == customerId && quotation.DealId == null)
            .OrderByDescending(quotation => quotation.UpdatedAt)
            .ToListAsync(cancellationToken);

    public void Add(DealQuotation quotation) => dbContext.DealQuotations.Add(quotation);
}

internal sealed class PipelineReasonRepository(PipelineDbContext dbContext) : IPipelineReasonRepository
{
    public async Task<IReadOnlyList<PipelineReason>> ListAsync(Guid tenantId, ReasonKind kind, CancellationToken cancellationToken) =>
        await dbContext.Reasons
            .Where(reason => reason.TenantId == tenantId && reason.Kind == kind)
            .OrderBy(reason => reason.Position)
            .ToListAsync(cancellationToken);

    public Task<PipelineReason?> FindActiveAsync(Guid tenantId, ReasonKind kind, Guid reasonId, CancellationToken cancellationToken) =>
        dbContext.Reasons.AsNoTracking().SingleOrDefaultAsync(
            reason => reason.TenantId == tenantId && reason.Kind == kind && reason.Id == reasonId && reason.IsActive, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken)
    {
        if (reasonIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = reasonIds.ToArray();
        return await dbContext.Reasons.AsNoTracking()
            .Where(reason => reason.TenantId == tenantId && ids.Contains(reason.Id))
            .ToDictionaryAsync(reason => reason.Id, reason => reason.Name, cancellationToken);
    }

    public void Add(PipelineReason reason) => dbContext.Reasons.Add(reason);
}

internal sealed class TriageDecisionRepository(PipelineDbContext dbContext) : ITriageDecisionRepository
{
    public async Task<IReadOnlyDictionary<Guid, TriageDecision>> FindLatestAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken)
    {
        if (conversationIds.Count == 0)
        {
            return new Dictionary<Guid, TriageDecision>();
        }

        var ids = conversationIds.ToArray();
        var rows = await dbContext.TriageDecisions.AsNoTracking()
            .Where(decision => decision.TenantId == tenantId && ids.Contains(decision.ConversationId))
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(decision => decision.ConversationId)
            .ToDictionary(group => group.Key, group => group.MaxBy(decision => decision.DecidedAgainstInboundAt)!);
    }

    public void Add(TriageDecision decision) => dbContext.TriageDecisions.Add(decision);
}
