using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-10-25 D-E5 y D-E14, plan D-P4: arma la fila de <c>run_units</c> de una unidad del procesador y la agrega al contexto, para
/// que viaje en el único <c>SaveChanges</c> de la unidad (si la unidad se deshace, la fila se va con ella). Nunca lanza: un fallo al
/// armarla no puede deshacer la unidad, que repetiría envíos y llamadas HTTP; queda un log con ids y el nombre del error, sin la
/// excepción (su mensaje podría traer un valor del cliente) y la unidad sigue sin fila.
/// </summary>
public sealed partial class RunUnitWriter(IRunUnitRepository units, IClock clock, ILogger<RunUnitWriter> logger)
{
    private const string NoSteps = "[]";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run unit of run {RunId} of tenant {TenantId} was not recorded: {ErrorType}")]
    private static partial void LogNotRecorded(ILogger logger, Guid runId, Guid tenantId, string errorType);

    /// <summary>La unidad que corrió el motor. <paramref name="startedAt"/> es el reloj al empezar la unidad; el fin, el de ahora.</summary>
    public void Write(
        ConversationRun run, int baseStep, string trigger, Guid? inboundMessageId, string outcome, DateTimeOffset startedAt, RunUnitLog? log,
        IReadOnlySet<string> attachmentVariables)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            var steps = RunUnitJson.Serialize(log?.Steps ?? [], attachmentVariables);
            units.Add(new RunUnitEntry(run.TenantId, run.Id, baseStep, trigger, inboundMessageId, outcome, startedAt, clock.UtcNow, steps, null, null));
        }
        catch (Exception exception)
        {
            LogNotRecorded(logger, run.Id, run.TenantId, exception.GetType().Name);
        }
    }

    /// <summary>D-E14: una corrida que cerró alguien que no es el motor, sin pasos. Plan D-P4: <c>interrupted</c> para <c>Interrupted</c>,
    /// <c>ended</c> para <c>Completed</c> y <c>Abandoned</c>.</summary>
    public void WriteClosed(ConversationRun run, Guid? inboundMessageId, string closedReason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            var outcome = run.Status == RunStatus.Interrupted ? RunOutcomes.Interrupted : RunOutcomes.Ended;
            units.Add(new RunUnitEntry(
                run.TenantId, run.Id, run.StepNumber, RunUnitTriggers.Closed, inboundMessageId, outcome, at, clock.UtcNow, NoSteps,
                run.Status.ToString(), closedReason));
        }
        catch (Exception exception)
        {
            LogNotRecorded(logger, run.Id, run.TenantId, exception.GetType().Name);
        }
    }
}
