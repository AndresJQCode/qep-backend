using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class LegalParameterRepository(HrDbContext dbContext) : ILegalParameterRepository
{
    public async Task<IReadOnlyList<LegalParameterValue>> ListValuesAsync(CancellationToken cancellationToken) =>
        await dbContext.LegalParameters.ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken) =>
        await dbContext.LegalParameterBrackets.ToArrayAsync(cancellationToken);

    public void Add(LegalParameterValue value) => dbContext.LegalParameters.Add(value);

    public void Remove(LegalParameterValue value) => dbContext.LegalParameters.Remove(value);

    public void AddBrackets(IEnumerable<LegalParameterBracket> brackets) => dbContext.LegalParameterBrackets.AddRange(brackets);

    public void RemoveBrackets(IEnumerable<LegalParameterBracket> brackets) =>
        dbContext.LegalParameterBrackets.RemoveRange(brackets);

    public void AddChange(LegalParameterChange change) => dbContext.LegalParameterChanges.Add(change);

    public async Task<(IReadOnlyList<LegalParameterChange> Items, int Total)> ListChangesAsync(
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var total = await dbContext.LegalParameterChanges.CountAsync(cancellationToken);
        var items = await dbContext.LegalParameterChanges
            .AsNoTracking()
            .OrderByDescending(change => change.ChangedAt)
            .ThenByDescending(change => change.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
        return (items, total);
    }
}
