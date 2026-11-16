using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Fase 4b (H16): la cola de las encuestas. El reclamo toma las filas con <c>FOR UPDATE SKIP LOCKED</c> (por
/// <c>IX_ticket_surveys_pending</c>) y les pone <c>locked_until</c> en la misma transacción, que se confirma antes de devolver: otro worker
/// (otra réplica) salta la fila bloqueada mientras dura la transacción, y no la toma después hasta que el reclamo vence. <c>SELECT *</c>
/// sin componer, como <c>ConversationRunRepository.ClaimAbandonedAsync</c>: EF no envuelve el <c>FOR UPDATE</c> en una subconsulta y
/// trae todas las columnas mapeadas.
/// </summary>
internal sealed class SupportSurveyRepository(SupportDbContext dbContext) : ISupportSurveyRepository
{
    public async Task<IReadOnlyList<Guid>> ClaimAsync(
        int take, DateTimeOffset now, DateTimeOffset lockedUntil, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var at = now.ToUniversalTime();
        var pending = nameof(SupportSurveyStatus.Pending);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await dbContext.TicketSurveys.FromSql($"""
            SELECT * FROM support.ticket_surveys
            WHERE status = {pending} AND next_attempt_at <= {at} AND (locked_until IS NULL OR locked_until <= {at})
            ORDER BY next_attempt_at, id
            LIMIT {take}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        foreach (var survey in claimed)
        {
            survey.Claim(lockedUntil.ToUniversalTime(), at);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed.Select(survey => survey.Id).ToList();
    }

    public async Task<bool> RenewClaimAsync(
        Guid surveyId, DateTimeOffset claimedUntil, DateTimeOffset renewedUntil, CancellationToken cancellationToken)
    {
        DateTimeOffset? claimed = claimedUntil.ToUniversalTime();
        DateTimeOffset? renewed = renewedUntil.ToUniversalTime();
        var updated = await dbContext.TicketSurveys
            .Where(survey => survey.Id == surveyId
                && survey.Status == SupportSurveyStatus.Pending
                && survey.LockedUntil == claimed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(survey => survey.LockedUntil, renewed), cancellationToken);
        return updated == 1;
    }

    public Task<SupportTicketSurvey?> FindAsync(Guid surveyId, CancellationToken cancellationToken) =>
        dbContext.TicketSurveys.FirstOrDefaultAsync(survey => survey.Id == surveyId, cancellationToken);
}
