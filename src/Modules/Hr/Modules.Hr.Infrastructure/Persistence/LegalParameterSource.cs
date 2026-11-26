using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class LegalParameterSource(HrDbContext dbContext) : ILegalParameterSource
{
    public async Task<IReadOnlyList<LegalParameterValue>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.LegalParameters
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken) =>
        await dbContext.LegalParameterBrackets
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
}
