using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>D33 y P9 del plan: todo se agrega en la base. Las tarjetas salen con ROW_NUMBER por etapa para no traer la columna
/// entera; usa <c>IX_deals_tenant_stage</c>.</summary>
internal sealed class PipelineBoardQueries(PipelineDbContext dbContext) : IPipelineBoardQueries
{
    public async Task<IReadOnlyList<BoardAggregateRow>> AggregateAsync(
        Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, CancellationToken cancellationToken)
    {
        var deals = dbContext.Deals.AsNoTracking()
            .Where(deal => deal.TenantId == tenantId && (deal.ClosedAt == null || deal.ClosedAt >= closedSince));
        if (ownerMemberId is { } owner)
        {
            deals = deals.Where(deal => deal.OwnerMemberId == owner);
        }

        var counts = await deals
            .GroupBy(deal => new { deal.StageId, deal.Currency })
            .Select(group => new { group.Key.StageId, group.Key.Currency, Count = group.Count() })
            .ToListAsync(cancellationToken);
        // D34: las sumas, sólo con los negocios que tienen monto, filtrados ANTES de agrupar. No sirve sumar el decimal? y esperar
        // null: EF lo traduce como COALESCE(SUM(amount), 0), y una columna de sólo-sin-monto saldría con un total en cero.
        var sums = await deals
            .Where(deal => deal.Amount != null)
            .GroupBy(deal => new { deal.StageId, deal.Currency })
            .Select(group => new { group.Key.StageId, group.Key.Currency, Amount = group.Sum(deal => deal.Amount!.Value) })
            .ToListAsync(cancellationToken);
        var amounts = sums.ToDictionary(row => (row.StageId, row.Currency), row => row.Amount);
        return [.. counts.Select(row => new BoardAggregateRow(
            row.StageId, row.Currency, row.Count, amounts.TryGetValue((row.StageId, row.Currency), out var amount) ? amount : null))];
    }

    public async Task<IReadOnlyList<BoardCardRow>> ListCardsAsync(
        Guid tenantId, Guid? ownerMemberId, DateTimeOffset closedSince, int perStage, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<CardRow>($"""
            SELECT d.id AS "Id", d.stage_id AS "StageId", d.title AS "Title", d.customer_id AS "CustomerId",
                   d.owner_member_id AS "OwnerMemberId", d.amount AS "Amount", d.currency AS "Currency",
                   d.stage_entered_at AS "StageEnteredAt", d.last_changed_at AS "LastChangedAt",
                   q.quotation_number AS "PrimaryQuotationNumber", d.version AS "Version"
            FROM (
                SELECT deals.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY deals.stage_id
                           ORDER BY CASE WHEN deals.closed_at IS NULL THEN deals.stage_entered_at END ASC NULLS LAST,
                                    deals.closed_at DESC NULLS LAST,
                                    deals.id) AS position
                FROM pipeline.deals
                WHERE deals.tenant_id = {tenantId}
                  AND (deals.closed_at IS NULL OR deals.closed_at >= {closedSince})
                  AND (CAST({ownerMemberId} AS uuid) IS NULL OR deals.owner_member_id = {ownerMemberId})
            ) AS d
            LEFT JOIN pipeline.deal_quotations AS q ON q.tenant_id = d.tenant_id AND q.quotation_id = d.primary_quotation_id
            WHERE d.position <= {perStage}
            ORDER BY d.stage_id, d.position
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new BoardCardRow(
            row.Id, row.StageId, row.Title, row.CustomerId, row.OwnerMemberId, row.Amount, row.Currency,
            row.StageEnteredAt, row.LastChangedAt, row.PrimaryQuotationNumber, row.Version))];
    }

    // Clase con setters y no un record: EF no ve a través del constructor de un record en SqlQuery (QuotationsReportSource).
    private sealed class CardRow
    {
        public Guid Id { get; init; }

        public Guid StageId { get; init; }

        public string Title { get; init; } = string.Empty;

        public Guid CustomerId { get; init; }

        public Guid OwnerMemberId { get; init; }

        public decimal? Amount { get; init; }

        public string Currency { get; init; } = string.Empty;

        public DateTimeOffset StageEnteredAt { get; init; }

        public DateTimeOffset LastChangedAt { get; init; }

        public string? PrimaryQuotationNumber { get; init; }

        public long Version { get; init; }
    }
}
