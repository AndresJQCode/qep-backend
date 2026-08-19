using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Companies.Application;

namespace Modules.Companies.Infrastructure.Persistence;

internal sealed class CompaniesUnitOfWork(CompaniesDbContext dbContext) : ICompaniesUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Esta unidad de trabajo ya no traduce violaciones de unicidad, y no por descuido: EMP-08
        // borro IX_companies_tenant_account_number, que era el unico indice unico del esquema. La
        // regla que quedo —que una empresa no repita la misma cuenta— la hace cumplir
        // Company.Create/Update en memoria, antes de llegar a PostgreSQL.
        //
        // El dia que vuelva a haber un indice unico aca (el gate del modulo tiene abierto si el
        // NIT lo es), la rama que se agregue discrimina por **nombre de indice** y no solo por
        // SqlState 23505: ese codigo solo dice que se violo alguno, y responder con el codigo de
        // dominio de otro campo manda al llamador a corregir el equivocado. Esa es la leccion con
        // la que se cerro SDD-CT-06, y sigue valiendo aunque hoy no haya indice que traducir.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The company changed while the update was being committed.",
                exception);
        }
    }
}
