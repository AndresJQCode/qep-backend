using Microsoft.EntityFrameworkCore;
using Npgsql;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenancyUnitOfWork(TenancyDbContext dbContext) : ITenancyUnitOfWork
{
    /// <summary>PostgreSQL unique_violation.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Created by the initial migration (20260705161840_InitialPlatform) over tenancy.tenants.
    /// Matched by name so a collision on memberships — which has its own unique index — is not
    /// mislabelled as a taken slug.
    /// </summary>
    private const string TenantSlugIndex = "IX_tenants_slug";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "Tenant settings changed while the update was being committed.",
                exception);
        }
        // SDD-CT-06. Registering with a slug someone already took is a normal user error, not
        // a fault, but the raw DbUpdateException matches no branch in ApiExceptionHandler and
        // used to surface as 500 server.unexpected. Translated here, in Infrastructure, because
        // this is the only layer that may know about EF and Npgsql: Modules.Tenancy.Application
        // references neither, and ArchitectureTests enforces that. The inner exception is
        // dropped on purpose — a 422 is an expected outcome, so there is no incident to trace.
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
                  {
                      SqlState: UniqueViolation,
                      ConstraintName: TenantSlugIndex,
                  })
        {
            throw new TenantDomainException(
                "tenancy.slug.taken",
                "Tenant slug is already in use.");
        }
    }
}
