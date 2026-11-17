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
}
