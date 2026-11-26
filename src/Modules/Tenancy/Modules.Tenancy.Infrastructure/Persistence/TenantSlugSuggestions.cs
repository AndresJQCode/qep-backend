using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// <see cref="ITenantSlugSuggestions"/>: lee <c>tenancy.tenants.slug</c> sin tracking, candidato por candidato. El primero
/// casi siempre está libre, así que en el caso común es una sola consulta.
/// </summary>
internal sealed class TenantSlugSuggestions(TenancyDbContext dbContext) : ITenantSlugSuggestions
{
    public async Task<string> SuggestAsync(string displayName, CancellationToken cancellationToken)
    {
        var slug = TenantSlugs.Normalize(displayName);
        var candidate = slug;
        for (var number = 2; await IsUnavailableAsync(candidate, cancellationToken); number++)
        {
            candidate = TenantSlugs.WithSuffix(slug, number);
        }

        return candidate;
    }

    private async Task<bool> IsUnavailableAsync(string candidate, CancellationToken cancellationToken) =>
        TenantSlugReservations.IsReserved(candidate)
        || await dbContext.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Slug == candidate, cancellationToken);
}
