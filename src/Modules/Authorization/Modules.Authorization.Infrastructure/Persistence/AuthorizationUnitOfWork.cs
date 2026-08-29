using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;
using Npgsql;

namespace Modules.Authorization.Infrastructure.Persistence;

internal sealed class AuthorizationUnitOfWork(AuthorizationDbContext dbContext)
    : IAuthorizationUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Antes que DbUpdateException, de la que hereda: al reves, el filtro de abajo la
        // dejaria pasar sin traducir y saldria como 500. Mismo orden que CompaniesUnitOfWork.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The role changed while the update was being committed.",
                exception);
        }
        // Discrimina por **nombre de indice** y no solo por SqlState 23505: ese codigo dice
        // que se violo alguno, y responder con el codigo de dominio del campo equivocado
        // manda a corregir lo que no era. Es la leccion con la que se cerro SDD-CT-06.
        catch (DbUpdateException exception)
            when (IsUniqueViolationOf(exception, "IX_roles_tenant_key"))
        {
            throw new AuthorizationDomainException(
                "authorization.role.key_taken",
                "That role key is already in use in this organization.");
        }
    }

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        postgres.ConstraintName == indexName;
}
