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

    public async Task<IReadOnlyList<DateTimeOffset>> ListQuestionTimesAsync(
        Guid tenantId, Guid userId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await (
                from message in dbContext.CopilotMessages.AsNoTracking()
                join thread in dbContext.CopilotThreads.AsNoTracking() on message.ThreadId equals thread.Id
                where thread.TenantId == tenantId && thread.UserId == userId
                    && message.Role == CopilotMessageRole.User && message.CreatedAt >= since
                select message.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>SQL directo en una transacción propia: los <c>tool_calls</c> no tienen FK al hilo (cuelgan del trabajo), así que el cascade de
    /// los mensajes no los alcanza.</summary>
    public async Task DeleteThreadAsync(Guid tenantId, Guid threadId, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
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
}
