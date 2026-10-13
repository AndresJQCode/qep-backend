using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using Npgsql;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>P4 del plan: el historial pendiente de cada negocio rastreado entra en el mismo SaveChanges (D19). La versión vieja es
/// <c>concurrency.conflict</c> (412), venga del token de <c>deals</c> o del índice único del ordinal del historial. Borrar una etapa que ganó un negocio entre la lectura y el commit choca con
/// <c>FK_deals_stages_stage_id</c> y sale <c>pipeline.stage.has_deals</c> (422), discriminando por nombre de restricción y no sólo
/// por SqlState (CLAUDE.md). Igual con D38: la segunda decisión de triage contra el mismo entrante choca con
/// <c>IX_triage_decisions_conversation_inbound</c> y sale <c>pipeline.triage.already_decided</c> (422); como todo va en un solo
/// SaveChanges, el negocio que esa decisión creaba no queda. Traducido acá y no en Application, que no referencia Npgsql
/// (PipelineLayerTests).</summary>
internal sealed class PipelineUnitOfWork(PipelineDbContext dbContext) : IPipelineUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<Deal>().ToArray())
        {
            dbContext.History.AddRange(entry.Entity.PullHistory());
        }

        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Antes que la rama de DbUpdateException: DbUpdateConcurrencyException hereda de ella.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The deal or the pipeline changed while the operation was being committed.", exception);
        }
        // P4: el mismo conflicto de versión, visto desde el historial (otra escritura ya usó este ordinal).
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, PipelineDbContext.DealHistoryOrdinalIndex, StringComparison.Ordinal))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The deal changed while the operation was being committed.", exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation &&
                  string.Equals(postgres.ConstraintName, PipelineDbContext.DealStageForeignKey, StringComparison.Ordinal))
        {
            throw new PipelineDomainException(
                PipelineErrorCodes.StageHasDeals, "A stage with deals cannot be removed; move its deals first.", exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(postgres.ConstraintName, PipelineDbContext.TriageDecisionInboundIndex, StringComparison.Ordinal))
        {
            throw new PipelineDomainException(
                PipelineErrorCodes.TriageAlreadyDecided, "Another advisor already decided this conversation for its last message.", exception);
        }
    }
}
