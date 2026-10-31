using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;
using Npgsql;

namespace Modules.ApiAccess.Infrastructure.Persistence;

internal sealed class ApiAccessUnitOfWork(ApiAccessDbContext dbContext) : IApiAccessUnitOfWork
{
    // Se discrimina por nombre de índice y no solo por SqlState: 23505 dice que se violó algún
    // índice único, no cuál.
    private const string ActiveNameIndex = "IX_api_keys_tenant_name_active";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Va antes que la rama de DbUpdateException porque DbUpdateConcurrencyException hereda de ella.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The API key changed while the update was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, ActiveNameIndex))
        {
            // Traducido aquí y no en Application, que no referencia Npgsql.
            throw new ApiAccessDomainException(
                ApiAccessErrorCodes.KeyNameTaken,
                "Ya tienes una clave activa con ese nombre. Elige otro o revoca la anterior.");
        }
    }

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(postgres.ConstraintName, indexName, StringComparison.Ordinal);
}
