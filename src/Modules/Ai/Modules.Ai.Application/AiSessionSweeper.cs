using BuildingBlocks.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Los barridos sin entrante, con el mismo orden de candados que el procesador (transacción de Ai, candado de la conversación):
/// <list type="bullet">
/// <item>D-A19: una sesión viva sin moverse en <see cref="AiSessionTimings.IdleAfter"/> termina <c>Abandoned</c> y la conversación vuelve a
/// <c>None</c>, para que la siguiente del cliente arranque.</item>
/// <item>§6.8: con el módulo <c>ai</c> apagado, toda sesión viva del tenant termina <c>Interrupted</c> y su conversación pasa a la bandeja sin
/// asignar, para que ninguna quede «atendida por un bot mudo». Las reservas de conexión no se tocan.</item>
/// </list>
/// Devuelve cuántas sesiones cerró; una conversación con el candado tomado se salta y queda para la vuelta siguiente.
/// </summary>
public sealed class AiSessionSweeper(
    IAiSessionUnitOfWork unitOfWork,
    IAgentSessionRepository sessions,
    IAiConversations conversations,
    IAiModuleStatus modules,
    AiSessionTimings timings,
    IClock clock)
{
    public const int BatchSize = 100;

    public async Task<int> SweepIdleAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow - timings.IdleAfter;
        var closed = 0;
        foreach (var candidate in await sessions.ListIdleAsync(cutoff, BatchSize, cancellationToken))
        {
            closed += await CloseAsync(candidate, idleBefore: cutoff, cancellationToken) ? 1 : 0;
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
                closed += await CloseAsync(candidate, idleBefore: null, cancellationToken) ? 1 : 0;
            }
        }

        return closed;
    }

    /// <param name="idleBefore">Con valor, el barrido de abandonadas: la sesión sólo se cierra si sigue sin moverse desde entonces.</param>
    private async Task<bool> CloseAsync(AgentSessionRef candidate, DateTimeOffset? idleBefore, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await unitOfWork.TryLockConversationAsync(candidate.ConversationId, cancellationToken))
        {
            return false;
        }

        var session = await sessions.FindLiveForUpdateAsync(candidate.TenantId, candidate.ConversationId, cancellationToken);
        if (session is null || (idleBefore is { } cutoff && session.UpdatedAt >= cutoff))
        {
            return false;
        }

        var now = clock.UtcNow;
        var conversation = await conversations.GetAsync(candidate.TenantId, candidate.ConversationId, cancellationToken);
        if (idleBefore is not null)
        {
            session.Finish(AgentSessionStatus.Abandoned, AgentSessionEndReasons.Abandoned, null, now);
            if (conversation is not null && conversation.IsAttendedBy(session.AgentId))
            {
                await conversations.EndAsync(candidate.TenantId, candidate.ConversationId, cancellationToken);
            }
        }
        else
        {
            session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.ModuleDisabled, null, now);
            await AiHandoffs.ModuleDisabledAsync(conversations, sessions, candidate.TenantId, conversation, session, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
