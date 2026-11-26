using BuildingBlocks.Application;

namespace Modules.Ai.Application;

/// <summary>Plan fase 2, Parte C: cuánto viven los <c>tool_calls</c> de los trabajos del copiloto (<c>Ai:Copilot:ToolCallRetentionDays</c>,
/// 30 por defecto; D-C9 da por hecha la retención de P-5, que para los agentes sigue abierta).</summary>
public sealed record CopilotRetention(TimeSpan ToolCalls);

public sealed record CopilotRetentionSweep(int Threads, int ToolCalls);

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C8, D-C9 y D-C13, plan fase 2 C3.4: el barrido diario.
/// <list type="bullet">
/// <item>Un hilo sin actividad en la retención de su tenant (<c>retention_days</c>, 7..365, 90 por defecto) se borra como lo borraría su
/// dueño: con los <c>tool_calls</c> de sus trabajos y la entrada <c>ai.copilot.thread_deleted</c>, ésta con actor sistema, en la misma
/// transacción.</item>
/// <item>Los <c>tool_calls</c> del copiloto con más de <see cref="CopilotRetention.ToolCalls"/> se borran aunque el hilo siga vivo: los
/// resultados de herramientas no vuelven a viajar al modelo (D-C9) y traen datos personales.</item>
/// </list>
/// Un trabajo en curso de un hilo borrado se descarta en su commit (plan C2.3). Ningún texto de preguntas ni de respuestas sale de acá.
/// </summary>
public sealed class CopilotRetentionSweeper(ICopilotRepository repository, IAiAuditRecorder audit, CopilotRetention retention, IClock clock)
{
    public const int BatchSize = 100;

    /// <summary>Tope de vueltas por barrido: con un atraso enorme, el resto queda para el día siguiente en vez de ocupar el worker.</summary>
    public const int MaxBatches = 50;

    /// <summary>Hallazgo 4: el tope más largo mira un día (<see cref="CopilotLimits.Day"/>); el registro se guarda el doble.</summary>
    public static readonly TimeSpan QuestionLogRetention = CopilotLimits.Day * 2;

    public async Task<CopilotRetentionSweep> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var threads = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var expired = await repository.ListExpiredThreadsAsync(now, BatchSize, cancellationToken);
            foreach (var thread in expired)
            {
                // Hallazgo 8 de la revisión: cada pod barre. La auditoría va sólo con un borrado que ocurrió (DELETE … RETURNING), y un hilo
                // que otro pod tiene tomado se salta (SKIP LOCKED) en vez de esperarlo y auditar un borrado que no fue suyo.
                var deleted = await repository.DeleteThreadAsync(
                    thread.TenantId, thread.ThreadId,
                    () => audit.RecordCopilot(
                        thread.TenantId, null, AiAuditActions.CopilotThreadDeleted, AiAuditActions.ResourceCopilotThread, thread.ThreadId, now),
                    skipLocked: true, cancellationToken);
                threads += deleted ? 1 : 0;
            }

            if (expired.Count < BatchSize)
            {
                break;
            }
        }

        var toolCalls = await repository.DeleteToolCallsBeforeAsync(now - retention.ToolCalls, cancellationToken);
        // Hallazgo 4 de la revisión: el registro de preguntas sólo sirve para el tope del día; dos días de margen.
        await repository.DeleteQuestionsBeforeAsync(now - QuestionLogRetention, cancellationToken);
        return new CopilotRetentionSweep(threads, toolCalls);
    }
}
