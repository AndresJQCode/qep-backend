using BuildingBlocks.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Los barridos sin entrante, con el mismo orden de candados que el procesador (transacción de Ai, candado de la conversación):
/// <list type="bullet">
/// <item>D-A19: una sesión viva sin moverse en <see cref="AiSessionTimings.IdleAfter"/> termina <c>Abandoned</c> y la conversación vuelve a
/// <c>None</c>, para que la siguiente del cliente arranque. Spec 2026-11-17 D-B11: la de un bloque de chatbot usa sus propios
/// <c>idle_minutes</c> y vuelve al flujo por <c>idle</c>.</item>
/// <item>§6.8: con el módulo <c>ai</c> apagado, toda sesión viva del tenant termina <c>Interrupted</c> y su conversación pasa a la bandeja sin
/// asignar, para que ninguna quede «atendida por un bot mudo». La de un bloque vuelve al flujo por <c>error</c> (D-B16). Las reservas de
/// conexión no se tocan.</item>
/// <item>Spec 2026-11-17 §6.5: las delegaciones de Mensajería que quedaron vivas sin conversación que las apunte pasan a
/// <c>Interrupted</c>.</item>
/// </list>
/// Devuelve cuántas sesiones cerró; una conversación con el candado tomado se salta y queda para la vuelta siguiente.
/// </summary>
public sealed class AiSessionSweeper(
    IAiSessionUnitOfWork unitOfWork,
    IAgentSessionRepository sessions,
    IAiConversations conversations,
    IAiModuleStatus modules,
    IAiDelegationSweep delegations,
    AiSessionTimings timings,
    IClock clock)
{
    public const int BatchSize = 100;

    public async Task<int> SweepIdleAsync(CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var candidate in await sessions.ListIdleAsync(clock.UtcNow, timings.IdleAfter, BatchSize, cancellationToken))
        {
            closed += await CloseAsync(candidate, idle: true, cancellationToken) ? 1 : 0;
        }

        return closed;
    }

    public async Task<int> SweepDisabledModuleAsync(CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var tenantId in await sessions.ListTenantsWithLiveSessionsAsync(cancellationToken))
        {
            if (await modules.IsEnabledAsync(tenantId, cancellationToken))
            {
                continue;
            }

            foreach (var candidate in await sessions.ListLiveAsync(tenantId, BatchSize, cancellationToken))
            {
                closed += await CloseAsync(candidate, idle: false, cancellationToken) ? 1 : 0;
            }
        }

        return closed;
    }

    /// <summary>Spec 2026-11-17 §6.5: cuántas delegaciones huérfanas interrumpió.</summary>
    public Task<int> SweepOrphanDelegationsAsync(CancellationToken cancellationToken) =>
        delegations.InterruptOrphansAsync(clock.UtcNow, BatchSize, cancellationToken);

    /// <param name="idle">El barrido de inactividad: la sesión sólo se cierra si, con el candado, sigue sin moverse desde su propio corte.</param>
    private async Task<bool> CloseAsync(AgentSessionRef candidate, bool idle, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await unitOfWork.TryLockConversationAsync(candidate.ConversationId, cancellationToken))
        {
            return false;
        }

        var now = clock.UtcNow;
        var session = await sessions.FindLiveForUpdateAsync(candidate.TenantId, candidate.ConversationId, cancellationToken);
        if (session is null || (idle && session.UpdatedAt >= now - IdleOf(session)))
        {
            return false;
        }

        var conversation = await conversations.GetAsync(candidate.TenantId, candidate.ConversationId, cancellationToken);
        if (!idle)
        {
            await AiHandoffs.EndForDisabledModuleAsync(conversations, sessions, candidate.TenantId, conversation, session, now, cancellationToken);
        }
        else if (session.IsBlock)
        {
            // D-B11: el bloque decide qué hacer con un cliente que no volvió; si una persona ya la tomó, la sesión queda Interrupted.
            await AgentBlockReturn.EndAsync(
                conversations, candidate.TenantId, session, new AgentBlockResult(AgentBlockOutputs.Idle, AgentBlockReasons.Idle), now, cancellationToken);
        }
        else
        {
            session.Finish(AgentSessionStatus.Abandoned, AgentSessionEndReasons.Abandoned, null, now);
            if (conversation is not null && conversation.IsAttendedBy(session.AgentId))
            {
                await conversations.EndAsync(candidate.TenantId, candidate.ConversationId, cancellationToken);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>D-B11: los minutos del bloque, o el corte de siempre.</summary>
    private TimeSpan IdleOf(AgentSession session) =>
        session.IdleMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : timings.IdleAfter;
}
