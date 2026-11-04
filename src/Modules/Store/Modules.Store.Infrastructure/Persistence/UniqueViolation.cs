using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Modules.Store.Infrastructure.Persistence;

/// <summary>
/// La violación de un índice o clave única concreta. Se discrimina por nombre y no sólo por <c>23505</c> (precedente
/// <c>TenancyUnitOfWork</c>): cada índice es una deduplicación distinta y devolver el código equivocado manda a corregir lo
/// equivocado.
/// </summary>
internal static class UniqueViolation
{
    public static bool Is(DbUpdateException exception, params string[] constraintNames) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && constraintNames.Contains(postgres.ConstraintName, StringComparer.Ordinal);
}
