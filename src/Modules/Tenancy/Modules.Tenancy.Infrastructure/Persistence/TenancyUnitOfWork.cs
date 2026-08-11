using Microsoft.EntityFrameworkCore;
using Npgsql;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

internal sealed class TenancyUnitOfWork(TenancyDbContext dbContext) : ITenancyUnitOfWork
{
    /// <summary>unique_violation de PostgreSQL.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Lo crea la migración inicial (20260705161840_InitialPlatform) sobre tenancy.tenants.
    /// Se hace match por nombre para que una colisión en memberships —que tiene su propio
    /// índice único— no se etiquete mal como un slug ya tomado.
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
        // SDD-CT-06. Registrarse con un slug que alguien ya tomó es un error normal de usuario, no
        // una falla, pero la DbUpdateException cruda no coincide con ninguna rama de
        // ApiExceptionHandler y salía como 500 server.unexpected. Se traduce acá, en Infrastructure,
        // porque es la única capa que puede saber de EF y Npgsql: Modules.Tenancy.Application no
        // referencia ninguno de los dos, y ArchitectureTests lo hace cumplir. La excepción interna
        // se descarta a propósito — un 422 es un resultado esperado, no hay incidente que rastrear.
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
