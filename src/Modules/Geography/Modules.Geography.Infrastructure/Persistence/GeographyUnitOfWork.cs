using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Npgsql;

namespace Modules.Geography.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice, no sólo por SqlState (CLAUDE.md): dos operadores que confirman el mismo
/// texto a la vez chocan con <see cref="GeographyDbContext.SynonymTextActiveIndex"/>, y el perdedor recibe un 409 que le dice
/// que el sinónimo ya existe. Traducido acá y no en Application, que no referencia EF Core ni Npgsql.
/// </summary>
internal sealed class GeographyUnitOfWork(GeographyDbContext dbContext) : IGeographyUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The city synonym changed while the operation was being committed.", exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, GeographyDbContext.SynonymTextActiveIndex))
        {
            throw new ResourceConflictException(
                "geography.synonym.already_exists",
                "There is already a synonym for that text in the department.",
                exception);
        }
    }

    public async Task<IGeographyTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new GeographyTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    public Task LockCityTextAsync(DepartmentId departmentId, string normalizedText, CancellationToken cancellationToken)
    {
        // Fuera de una transacción el candado se soltaría al terminar la sentencia y no serializaría nada.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockCityTextAsync requires an open transaction (BeginTransactionAsync).");
        }

        // El mismo texto que department_id::text || ':' || normalized_text en SQL (un Guid se formatea como el uuid de
        // Postgres): un candado tomado desde SQL crudo con esa expresión es el mismo candado.
        var key = $"{departmentId.Value}:{normalizedText}";
        return dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    private sealed class GeographyTransaction(IDbContextTransaction transaction) : IGeographyTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        // Disponer sin commit hace rollback, y el rollback es lo que suelta el candado del texto.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
