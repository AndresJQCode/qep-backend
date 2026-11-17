using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Persistence;

internal sealed class AgentSessionRepository(AiDbContext dbContext) : IAgentSessionRepository
{
    // FOR UPDATE: la sesión viva no se cruza con otra unidad aunque el candado de la conversación ya la proteja; la red, como en Automations.
    public async Task<AgentSession?> FindLiveForUpdateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Sessions
            .FromSql($"""SELECT * FROM ai.sessions WHERE tenant_id = {tenantId} AND conversation_id = {conversationId} AND status = 'Live' FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task<AgentSession?> FindLatestAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Sessions.AsNoTracking()
            .Where(session => session.TenantId == tenantId && session.ConversationId == conversationId)
            .OrderByDescending(session => session.StartedAt).ThenByDescending(session => session.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AgentSession?> FindAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        dbContext.Sessions.AsNoTracking().SingleOrDefaultAsync(session => session.TenantId == tenantId && session.Id == sessionId, cancellationToken);

    public void Add(AgentSession session) => dbContext.Sessions.Add(session);

    public void AddTurn(AgentTurn turn) => dbContext.Turns.Add(turn);

    public async Task<IReadOnlyList<AgentTurn>> ListTurnsAsync(Guid sessionId, int upToSeq, CancellationToken cancellationToken) =>
        await dbContext.Turns.AsNoTracking()
            .Where(turn => turn.SessionId == sessionId && turn.Seq <= upToSeq)
            .OrderBy(turn => turn.Seq)
            .ToListAsync(cancellationToken);

    public Task<bool> HasCustomerTurnAfterAsync(Guid sessionId, int seq, CancellationToken cancellationToken) =>
        dbContext.Turns.AsNoTracking().AnyAsync(
            turn => turn.SessionId == sessionId && turn.Seq > seq && turn.Role == AgentTurnRoleKind.Customer, cancellationToken);

    // Revisión final, hallazgo 3: FOR UPDATE. El reclamo del worker no toma el candado de la conversación; sin el candado de fila, el worker
    // podía reclamar el trabajo (y leer los turnos hasta el up_to_seq viejo) mientras esta unidad le subía up_to_seq, y el turno nuevo del
    // cliente quedaba sin trabajo que lo respondiera. Con la fila tomada, el reclamo (SKIP LOCKED) la salta hasta el commit; si el reclamo
    // ganó, la fila ya no es Pending al releerla y la unidad crea un trabajo nuevo.
    public async Task<ModelJob?> FindPendingJobAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.ModelJobs
            .FromSql($"""SELECT * FROM ai.model_jobs WHERE session_id = {sessionId} AND status = 'Pending' FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    // Spec 2026-11-17 D-B11: el corte es por sesión (idle_minutes del bloque, o el de siempre). Recorre sólo las vivas (IX_sessions_live_updated);
    // el barrido lo vuelve a mirar con el candado de la conversación.
    public async Task<IReadOnlyList<AgentSessionRef>> ListIdleAsync(
        DateTimeOffset now, TimeSpan defaultIdle, int batch, CancellationToken cancellationToken) =>
        await dbContext.Database.SqlQuery<AgentSessionRef>(
            $"""
            SELECT tenant_id AS "TenantId", conversation_id AS "ConversationId"
              FROM ai.sessions
             WHERE status = 'Live'
               AND updated_at < {now} - COALESCE(make_interval(mins => idle_minutes), {defaultIdle})
             ORDER BY updated_at
             LIMIT {batch}
            """).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentSessionRef>> ListLiveAsync(Guid tenantId, int batch, CancellationToken cancellationToken) =>
        await dbContext.Sessions.AsNoTracking()
            .Where(session => session.TenantId == tenantId && session.Status == AgentSessionStatus.Live)
            .OrderBy(session => session.StartedAt)
            .Take(batch)
            .Select(session => new AgentSessionRef(session.TenantId, session.ConversationId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListTenantsWithLiveSessionsAsync(CancellationToken cancellationToken) =>
        await dbContext.Sessions.AsNoTracking()
            .Where(session => session.Status == AgentSessionStatus.Live)
            .Select(session => session.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

    public Task<bool> HasLiveSessionsAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken) =>
        dbContext.Sessions.AsNoTracking().AnyAsync(
            session => session.TenantId == tenantId && session.AgentId == agentId && session.Status == AgentSessionStatus.Live, cancellationToken);

    // Por ai.agent_connections + el estado del agente, que se sincronizan con messaging.connection_bots en la misma transacción (§4.3).
    public Task<Agent?> FindServingAgentAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        WithVersions()
            .Where(agent => agent.TenantId == tenantId && agent.Status == AgentStatus.Active
                && agent.Connections.Any(connection => connection.ConnectionId == connectionId))
            .OrderBy(agent => agent.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Agent?> FindAgentAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken) =>
        WithVersions().SingleOrDefaultAsync(agent => agent.TenantId == tenantId && agent.Id == agentId, cancellationToken);

    private IQueryable<Agent> WithVersions() =>
        dbContext.Agents.AsNoTracking().Include(agent => agent.Versions).Include(agent => agent.Connections).AsSplitQuery();
}

/// <summary>
/// Spec 2026-10-29 §6.2 paso 1 y §6.3 paso 4: la transacción de Ai con el candado de la conversación de <see cref="ConversationBotLock"/>
/// (el mismo de Automations). Con la transacción de la unidad abierta, guardar es confirmar y soltar el candado; sin ella, sólo guardar.
/// </summary>
internal sealed class AiSessionUnitOfWork(AiDbContext dbContext) : IAiSessionUnitOfWork
{
    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken) =>
        await dbContext.Database.BeginTransactionAsync(cancellationToken);

    public async Task<bool> TryLockConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("TryLockConversationAsync exige la transacción de BeginTransactionAsync.");
        }

        var taken = await dbContext.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock({ConversationBotLock.Namespace}, hashtext({conversationId.ToString()})) AS \"Value\"")
            .ToListAsync(cancellationToken);
        return taken.Single();
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (dbContext.Database.CurrentTransaction is { } transaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The agent session changed while it was being saved.", exception);
        }
        catch (Exception exception) when (AiUnitOfWork.IsWriteRace(exception))
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The agent session changed while it was being saved.", exception);
        }
    }
}
