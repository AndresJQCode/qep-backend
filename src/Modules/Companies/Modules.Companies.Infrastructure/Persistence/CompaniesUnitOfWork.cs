using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Npgsql;

namespace Modules.Companies.Infrastructure.Persistence;

internal sealed class CompaniesUnitOfWork(CompaniesDbContext dbContext) : ICompaniesUnitOfWork
{
    // Discriminar por nombre de indice y no solo por SqlState es deliberado: 23505 solo dice que
    // se violo algun indice unico, y responder companies.company.account_number_taken por otro
    // mandaria al llamador a corregir el campo equivocado. Esa es la leccion con la que se cerro
    // SDD-CT-06.
    //
    // Hoy hay un solo indice unico en este esquema. La constante y la comparacion por nombre
    // existen igual: el dia que el gate cierre "el NIT tambien es unico", ese indice llega con su
    // **propia rama y su propio codigo**, y colapsarlas con un `or` seria repetir el defecto.
    private const string AccountNumberIndex = "IX_companies_tenant_account_number";

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Va antes que la rama de DbUpdateException porque DbUpdateConcurrencyException hereda de
        // ella: al reves, el filtro de indice unico la dejaria pasar sin traducir y saldria como
        // 500. Mismo patron que TenancyUnitOfWork y CatalogUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The company changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres &&
                  postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                  string.Equals(
                      postgres.ConstraintName,
                      AccountNumberIndex,
                      StringComparison.Ordinal))
        {
            // Traducido aca y no en Application, que no referencia Npgsql y se mantiene asi
            // gracias a CompaniesLayerTests.
            throw new CompaniesDomainException(
                "companies.company.account_number_taken",
                "Another company in this tenant already uses that account number.");
        }
    }
}
