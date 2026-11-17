using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Persistence;

internal sealed class CopilotRepository(AiDbContext dbContext) : ICopilotRepository
{
    public Task<CopilotSettings?> FindSettingsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.CopilotSettings.SingleOrDefaultAsync(settings => settings.TenantId == tenantId, cancellationToken);

    public void AddSettings(CopilotSettings settings) => dbContext.CopilotSettings.Add(settings);

    public Task<CopilotThread?> FindThreadAsync(Guid tenantId, Guid userId, Guid threadId, CancellationToken cancellationToken) =>
        dbContext.CopilotThreads
            .Include(thread => thread.Messages.OrderBy(message => message.Seq))
            .SingleOrDefaultAsync(
                thread => thread.TenantId == tenantId && thread.UserId == userId && thread.Id == threadId, cancellationToken);

    public void AddThread(CopilotThread thread) => dbContext.CopilotThreads.Add(thread);

    public async Task<CopilotTokenUsage> SumTokensAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var totals = await dbContext.UsageRecords.AsNoTracking()
            .Where(record => record.TenantId == tenantId && record.CopilotThreadId != null && record.CalledAt >= since)
            .GroupBy(_ => 1)
            .Select(group => new { Input = group.Sum(record => (long)record.InputTokens), Output = group.Sum(record => (long)record.OutputTokens) })
            .SingleOrDefaultAsync(cancellationToken);
        return totals is null ? new CopilotTokenUsage(0, 0) : new CopilotTokenUsage(totals.Input, totals.Output);
    }

    public async Task<IReadOnlyList<CopilotThread>> ListThreadsAsync(
        Guid tenantId, Guid userId, CopilotThreadCursor? after, int take, CancellationToken cancellationToken)
    {
        var threads = dbContext.CopilotThreads.AsNoTracking().Where(thread => thread.TenantId == tenantId && thread.UserId == userId);

        // Keyset (last_activity_at, id) < (@at, @id), el mismo orden de la página; el id desempata dos hilos del mismo instante (precedente
        // AgentActivityReader.ListSessionsAsync).
        if (after is { } cursor)
        {
            threads = threads.Where(thread => EF.Functions.LessThan(
                ValueTuple.Create(thread.LastActivityAt, thread.Id), ValueTuple.Create(cursor.LastActivityAt, cursor.Id)));
        }

        return await threads
            .OrderByDescending(thread => thread.LastActivityAt).ThenByDescending(thread => thread.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    // Revisión de la rama, hallazgo 4: del registro propio y no de los mensajes, que se van con el hilo. Por IX_copilot_questions_member.
    public async Task<IReadOnlyList<DateTimeOffset>> ListQuestionTimesAsync(
        Guid tenantId, Guid userId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await dbContext.CopilotQuestions.AsNoTracking()
            .Where(question => question.TenantId == tenantId && question.UserId == userId && question.AskedAt >= since)
            .Select(question => question.AskedAt)
            .ToListAsync(cancellationToken);

    public async Task<ICopilotAskingLock> LockAskingAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var owned = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({CopilotAskLock.Namespace}, hashtext({CopilotAskLock.KeyFor(tenantId, userId)}))",
                cancellationToken);
            return new AskingLock(owned);
        }
        catch
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }

            throw;
        }
    }

    public void AddQuestion(Guid tenantId, Guid userId, DateTimeOffset askedAt) =>
        dbContext.CopilotQuestions.Add(new CopilotQuestionRow { Id = Guid.CreateVersion7(), TenantId = tenantId, UserId = userId, AskedAt = askedAt });

    public Task<int> DeleteQuestionsBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        // Npgsql sólo escribe un timestamptz desde un DateTimeOffset en UTC.
        var cutoff = before.ToUniversalTime();
        return dbContext.Database.ExecuteSqlAsync($"DELETE FROM ai.copilot_questions WHERE asked_at < {cutoff}", cancellationToken);
    }

    public Task<CopilotThread?> ReadThreadAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken) =>
        dbContext.CopilotThreads.AsNoTracking()
            .Include(thread => thread.Messages.OrderBy(message => message.Seq))
            .SingleOrDefaultAsync(thread => thread.TenantId == tenantId && thread.Id == threadId, cancellationToken);

    public Task<CopilotThread?> FindThreadForJobAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken) =>
        dbContext.CopilotThreads
            .Include(thread => thread.Messages.OrderBy(message => message.Seq))
            .SingleOrDefaultAsync(thread => thread.TenantId == tenantId && thread.Id == threadId, cancellationToken);

    public Task SetProgressAsync(Guid tenantId, Guid threadId, Guid jobId, string progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var value = progress.Length <= CopilotThread.ProgressMaxLength ? progress : progress[..CopilotThread.ProgressMaxLength];
        return dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE ai.copilot_messages SET progress = {value}
             WHERE tenant_id = {tenantId} AND thread_id = {threadId} AND job_id = {jobId} AND status = 'Pending'
            """,
            cancellationToken);
    }

    /// <summary>SQL directo en una transacción propia: los <c>tool_calls</c> no tienen FK al hilo (cuelgan del trabajo), así que el cascade de
    /// los mensajes no los alcanza. Lo pendiente del contexto (la auditoría del borrado) se guarda adentro, antes del commit.</summary>
    public async Task DeleteThreadAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM ai.tool_calls
             WHERE tenant_id = {tenantId}
               AND job_id IN (SELECT id FROM ai.model_jobs WHERE tenant_id = {tenantId} AND copilot_thread_id = {threadId})
            """,
            cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"DELETE FROM ai.copilot_threads WHERE tenant_id = {tenantId} AND id = {threadId}", cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<CopilotMemberTokenUsage>> SumTokensByMemberAsync(
        Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await dbContext.UsageRecords.AsNoTracking()
            .Where(record => record.TenantId == tenantId && record.CopilotThreadId != null && record.MemberUserId != null && record.CalledAt >= since)
            .GroupBy(record => record.MemberUserId!.Value)
            .Select(group => new CopilotMemberTokenUsage(
                group.Key, group.Sum(record => (long)record.InputTokens), group.Sum(record => (long)record.OutputTokens)))
            .ToListAsync(cancellationToken);

    /// <summary>D-C8: la retención es por tenant; sin fila de configuración rige la de fábrica. Usa <c>IX_copilot_threads_activity</c>.</summary>
    public async Task<IReadOnlyList<CopilotExpiredThread>> ListExpiredThreadsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQuery<ExpiredThreadRow>(
                $"""
                SELECT t.tenant_id AS "TenantId", t.id AS "ThreadId"
                  FROM ai.copilot_threads t
                  LEFT JOIN ai.copilot_settings s ON s.tenant_id = t.tenant_id
                 WHERE t.last_activity_at < {now} - make_interval(days => COALESCE(s.retention_days, {CopilotSettings.DefaultRetentionDays}))
                 ORDER BY t.last_activity_at
                 LIMIT {take}
                """)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new CopilotExpiredThread(row.TenantId, row.ThreadId))];
    }

    public Task<int> DeleteToolCallsBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM ai.tool_calls tc
             USING ai.model_jobs j
             WHERE tc.job_id = j.id AND tc.tenant_id = j.tenant_id
               AND j.copilot_thread_id IS NOT NULL
               AND tc.created_at < {before}
            """,
            cancellationToken);

    /// <summary>La transacción que abrió el candado, si la abrió él: sólo confirma esa (una del llamador la confirma el llamador).</summary>
    private sealed class AskingLock(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? owned) : ICopilotAskingLock
    {
        public Task CommitAsync(CancellationToken cancellationToken) => owned?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class ExpiredThreadRow
    {
        public Guid TenantId { get; init; }

        public Guid ThreadId { get; init; }
    }
}
