using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;

namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>Spec 2026-10-25 D-E5: las unidades del procesador. <see cref="Add"/> usa el <see cref="AutomationsDbContext"/> del scope de
/// la unidad, asÃ­ que la fila se confirma o se deshace junto con la corrida.</summary>
internal sealed class RunUnitRepository(AutomationsDbContext context) : IRunUnitRepository
{
    public void Add(RunUnitEntry entry) =>
        context.RunUnits.Add(new RunUnitRecord
        {
            TenantId = entry.TenantId,
            RunId = entry.RunId,
            BaseStep = entry.BaseStep,
            Trigger = entry.Trigger,
            InboundMessageId = entry.InboundMessageId,
            Outcome = entry.Outcome,
            StartedAt = entry.StartedAt,
            EndedAt = entry.EndedAt,
            StepsJson = entry.StepsJson,
            ClosedStatus = entry.ClosedStatus,
            ClosedReason = entry.ClosedReason,
        });

    public async Task<IReadOnlyList<RunUnitEntry>> ListAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken) =>
        await context.RunUnits.AsNoTracking()
            .Where(unit => unit.TenantId == tenantId && unit.RunId == runId)
            .OrderBy(unit => unit.BaseStep).ThenBy(unit => unit.StartedAt).ThenBy(unit => unit.Id)
            .Select(unit => new RunUnitEntry(
                unit.TenantId, unit.RunId, unit.BaseStep, unit.Trigger, unit.InboundMessageId, unit.Outcome, unit.StartedAt, unit.EndedAt,
                unit.StepsJson, unit.ClosedStatus, unit.ClosedReason))
            .ToListAsync(cancellationToken);

    // Como HttpCallLog.PurgeBeforeAsync: por la llave primaria, todos los tenants a la vez. La selección del lote recorre
    // IX_run_units_started_at: sin él, cada barrido sin filas vencidas leería toda la tabla de una base compartida.
    public async Task<int> PurgeBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);
        // Npgsql sÃ³lo escribe un timestamptz desde un DateTimeOffset en UTC.
        cutoff = cutoff.ToUniversalTime();
        return await context.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM automations.run_units
             WHERE id IN (SELECT id FROM automations.run_units WHERE started_at < {cutoff} LIMIT {batch})
            """, cancellationToken);
    }
}
