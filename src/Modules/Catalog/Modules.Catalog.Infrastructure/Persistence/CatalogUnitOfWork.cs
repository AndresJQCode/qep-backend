using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Npgsql;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class CatalogUnitOfWork(CatalogDbContext dbContext) : ICatalogUnitOfWork
{
    // Discriminar por nombre de índice y no sólo por SqlState es deliberado: 23505 sólo dice
    // que se violó algún índice único, y responder catalog.product.code_taken para otro
    // mandaría al llamador a corregir el campo equivocado. Esa es la lección con la que se
    // cerró SDD-CT-06, donde otro índice único reportaba el código de dominio equivocado.
    private const string ProductCodeIndex = "IX_products_tenant_code";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Va antes que la rama de DbUpdateException porque DbUpdateConcurrencyException hereda
        // de ella: al revés, el filtro de índice único la dejaría pasar sin traducir y saldría
        // como 500. Mismo patrón que TenancyUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The product changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      ProductCodeIndex,
                      StringComparison.Ordinal))
        {
            // Traducido acá y no en Application, que no referencia Npgsql y se mantiene así
            // gracias a CatalogLayerTests.
            throw new CatalogDomainException(
                "catalog.product.code_taken",
                "Another product in this tenant already uses that code.");
        }
    }
}
