using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Pricing.Application;
using Modules.Pricing.Domain;
using Npgsql;

namespace Modules.Pricing.Infrastructure.Persistence;

internal sealed class PricingUnitOfWork(PricingDbContext dbContext) : IPricingUnitOfWork
{
    // Discriminar por nombre de indice y no solo por SqlState es deliberado: 23505 solo dice que
    // se violo algun indice unico, y responder el codigo del otro campo mandaria al llamador a
    // corregir el equivocado — la leccion de SDD-CT-06.
    private const string NameIndex = "IX_price_lists_tenant_name";

    private const string PrefixIndex = "IX_price_lists_tenant_prefix";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Va antes que las ramas de DbUpdateException porque DbUpdateConcurrencyException hereda
        // de ella: al reves, el filtro de indice unico la dejaria pasar sin traducir y saldria
        // como 500. Mismo patron que CustomersUnitOfWork y CatalogUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The price list changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, NameIndex))
        {
            // Traducido aca y no en Application, que no referencia Npgsql y se mantiene asi
            // gracias a PricingLayerTests.
            throw new PricingDomainException(
                "pricing.price_list.name_taken",
                "Another price list in this tenant already uses that name.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, PrefixIndex))
        {
            throw new PricingDomainException(
                "pricing.price_list.prefix_taken",
                "Another price list in this tenant already uses that prefix.");
        }
    }

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);
}
