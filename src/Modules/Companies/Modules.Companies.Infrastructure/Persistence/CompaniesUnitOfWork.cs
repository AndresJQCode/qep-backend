using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Npgsql;

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
        // Va antes que la rama de DbUpdateException porque DbUpdateConcurrencyException hereda de
        // ella: al revés, el filtro de clave foránea la dejaría pasar sin traducir y saldría como
        // 500. Mismo patrón que CatalogUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The company changed while the update was being committed.",
                exception);
        }
        // El borrado de una empresa que alguien referencia. PostgreSQL es quien impone la regla
        // —cualquier clave foránea contra companies.companies(id) frena el DELETE—, y sin esta
        // traducción esa violación sale como 500 server.unexpected, que no le dice nada a quien
        // apretó "Eliminar". Es el mismo hallazgo que CAT-04 corrigió en Catalog.
        //
        // El filtro no discrimina por nombre de constraint, a diferencia de CatalogUnitOfWork, y
        // es deliberado: acá no se traduce **una** constraint conocida sino cualquiera que apunte
        // a la empresa, y esas van a llegar de módulos que todavía no existen. Lo que acota la
        // rama es la otra mitad de la condición: que lo que EF estaba guardando fuera un DELETE
        // de Company. Sin eso, una violación de clave foránea de otra operación se llevaría un
        // código que manda a mirar la entidad equivocada — la lección de SDD-CT-06.
        catch (DbUpdateException exception)
            when (IsForeignKeyViolation(exception) && IsDeletingACompany(exception))
        {
            throw new CompaniesDomainException(
                "companies.company.in_use",
                "The company cannot be deleted because another record references it.");
        }
    }

    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation;

    // El estado sigue siendo Deleted cuando SaveChanges falla: EF sólo lo pasa a Detached después
    // de un commit exitoso. Si eso cambiara, esta rama dejaría de entrar y el caso volvería a
    // salir como 500 — por eso la prueba de integración afirma sobre el código y no sólo sobre el
    // status.
    private static bool IsDeletingACompany(DbUpdateException exception) =>
        exception.Entries.Any(entry =>
            entry.Entity is Company && entry.State == EntityState.Deleted);
}
