using BuildingBlocks.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

/// <summary>§6: arma <see cref="DealDetailDto"/> con una llamada a cada directorio; la parte de tarjeta (etapa, dueño, días,
/// estancado) sale de <see cref="DealCardMapper"/>, igual que en el tablero y la ficha. Público sólo para que Infrastructure lo
/// registre; no es API para otros módulos.</summary>
public sealed class DealDetailBuilder(
    IPipelineCustomerDirectory customers,
    IPipelineMemberNames members,
    IPipelineConversationDirectory conversations,
    IDealRepository deals,
    IDealQuotationRepository quotations,
    IPipelineReasonRepository reasons,
    IClock clock)
{
    public async Task<DealDetailDto> BuildAsync(SalesPipeline pipeline, Deal deal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(deal);
        var tenantId = deal.TenantId;
        var history = await deals.ListHistoryAsync(tenantId, deal.Id, cancellationToken);
        var linked = await quotations.ListByDealAsync(tenantId, deal.Id, cancellationToken);
        var unlinked = await quotations.ListUnlinkedByCustomerAsync(tenantId, deal.CustomerId, cancellationToken);
        var customer = (await customers.FindAsync(tenantId, [deal.CustomerId], cancellationToken)).GetValueOrDefault(deal.CustomerId);
        var memberIds = history
            .Where(row => row.ActorMemberId is not null)
            .Select(row => row.ActorMemberId!.Value)
            .Append(deal.OwnerMemberId)
            .Distinct()
            .ToArray();
        var names = await members.FindAsync(tenantId, memberIds, cancellationToken);
        var activity = (await conversations.FindActivityAsync(tenantId, [deal.CustomerId], cancellationToken)).GetValueOrDefault(deal.CustomerId);
        var reasonName = deal.LossReasonId is { } reasonId
            ? (await reasons.FindNamesAsync(tenantId, [reasonId], cancellationToken)).GetValueOrDefault(reasonId)
            : null;
        var card = DealCardMapper.Describe(
            pipeline, deal.StageId, deal.StageEnteredAt, deal.LastChangedAt, deal.OwnerMemberId, names, activity?.LastMessageAt, clock.UtcNow);

        return new DealDetailDto(
            deal.Id,
            deal.Title,
            deal.CustomerId,
            DealCardMapper.CustomerName(customer),
            customer?.IsComplete ?? false,
            deal.OwnerMemberId,
            card.OwnerName,
            deal.Amount,
            deal.Currency,
            deal.PrimaryQuotationId is null,
            deal.ExpectedCloseDate,
            deal.StageId,
            card.StageName,
            card.StageKind.ToString(),
            card.DaysInStage,
            card.IsStalled,
            deal.Source.ToString(),
            deal.OriginConversationId,
            deal.LossReasonId,
            reasonName,
            deal.LossNote,
            deal.ClosedAt,
            deal.PrimaryQuotationId,
            [.. linked.Select(quotation => ToDto(quotation, deal.PrimaryQuotationId))],
            [.. unlinked.Select(quotation => ToDto(quotation, null))],
            activity?.ConversationIds ?? [],
            [.. history.Select(row => ToDto(row, pipeline, names))],
            deal.CreatedAt,
            deal.UpdatedAt,
            deal.Version);
    }

    internal static DealQuotationDto ToDto(DealQuotation quotation, Guid? primaryQuotationId) =>
        new(quotation.QuotationId, quotation.QuotationNumber, quotation.Total, quotation.Currency, quotation.Status.ToString(),
            quotation.QuotationId == primaryQuotationId, quotation.OrderId, quotation.OrderCancelledAt is not null);

    private static StageHistoryDto ToDto(DealStageHistoryEntry row, SalesPipeline pipeline, IReadOnlyDictionary<Guid, string> names) =>
        new(row.FromStageId,
            row.FromStageId is { } from ? pipeline.FindStage(from)?.Name : null,
            row.ToStageId,
            pipeline.FindStage(row.ToStageId)?.Name,
            row.OccurredAt,
            row.ActorKind.ToString(),
            row.ActorMemberId,
            row.ActorMemberId is { } member ? DealCardMapper.MemberName(names, member) : null,
            row.Amount,
            row.Currency,
            row.Probability);
}
