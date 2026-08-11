using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Npgsql;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class CatalogUnitOfWork(CatalogDbContext dbContext) : ICatalogUnitOfWork
{
    // Discriminating by index name and not by SqlState alone is deliberate: 23505 only says
    // that some unique index was violated, and answering catalog.product.code_taken for a
    // different one would send the caller to fix the wrong field. That is the lesson
    // SDD-CT-06 was closed on, where another unique index reported the wrong domain code.
    private const string ProductCodeIndex = "IX_products_tenant_code";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      ProductCodeIndex,
                      StringComparison.Ordinal))
        {
            // Translated here and not in Application, which does not reference Npgsql and is
            // kept that way by CatalogLayerTests.
            throw new CatalogDomainException(
                "catalog.product.code_taken",
                "Another product in this tenant already uses that code.");
        }
    }
}
