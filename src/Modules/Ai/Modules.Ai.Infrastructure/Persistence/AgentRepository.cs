using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Persistence;

internal sealed class AgentRepository(AiDbContext dbContext) : IAgentRepository
{
    public Task<Agent?> FindAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken) =>
        WithChildren().SingleOrDefaultAsync(agent => agent.TenantId == tenantId && agent.Id == agentId, cancellationToken);

    public async Task<IReadOnlyList<Agent>> ListAsync(Guid tenantId, string? search, CancellationToken cancellationToken)
    {
        var query = WithChildren().AsNoTracking().Where(agent => agent.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILIKE con el patrón escapado: un «%» o un «_» del buscador se busca tal cual.
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(agent => EF.Functions.ILike(agent.Name, pattern, @"\"));
        }

        return await query.OrderBy(agent => agent.Name).ThenBy(agent => agent.Id).ToListAsync(cancellationToken);
    }

    public Task<bool> NameExistsAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var lowered = name.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862
        return dbContext.Agents.AsNoTracking().AnyAsync(
            agent => agent.TenantId == tenantId && agent.Name.ToLower() == lowered && (exceptId == null || agent.Id != exceptId),
            cancellationToken);
#pragma warning restore CA1304, CA1311, CA1862
    }

    public void Add(Agent agent) => dbContext.Agents.Add(agent);

    public void Remove(Agent agent) => dbContext.Agents.Remove(agent);

    private IQueryable<Agent> WithChildren() =>
        dbContext.Agents.Include(agent => agent.Connections).Include(agent => agent.Versions).AsSplitQuery();

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
