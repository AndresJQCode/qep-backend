using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Npgsql;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class QuotationsUnitOfWork(QuotationsDbContext dbContext) : IQuotationsUnitOfWork
{
    // Discriminar por nombre de indice y no solo por SqlState es deliberado -- la leccion de
    // SDD-CT-06: 23505 solo dice que se violo algun indice unico.
    private const string QuotationNumberIndex = "IX_quotations_tenant_number";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Antes que DbUpdateException porque DbUpdateConcurrencyException hereda de ella: al
        // reves, el filtro de indice unico la dejaria pasar sin traducir y saldria como 500.
        // Mismo patron que CatalogUnitOfWork/CustomersUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The quotation changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      QuotationNumberIndex,
                      StringComparison.Ordinal))
        {
            // No deberia alcanzarse en la practica -- el numero lo asigna
            // IQuotationNumberGenerator con un contador atomico por tenant -- pero traducido
            // igual: un 500 con el nombre de la constraint adentro no le dice nada al llamador.
            throw new QuotationsDomainException(
                "quotation.quotation.number_taken",
                "Another quotation in this tenant already uses that number.");
        }
    }
}
