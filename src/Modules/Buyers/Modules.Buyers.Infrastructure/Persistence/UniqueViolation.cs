using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Modules.Buyers.Infrastructure.Persistence;

/// <summary>
/// La violación de un índice único concreto. Se discrimina por nombre y no sólo por <c>23505</c>: cada índice es una
/// deduplicación distinta (precedente <c>TenancyUnitOfWork</c>).
/// </summary>
internal static class UniqueViolation
{
    public static bool Is(DbUpdateException exception, params string[] constraintNames) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && constraintNames.Contains(postgres.ConstraintName, StringComparer.Ordinal);
}
