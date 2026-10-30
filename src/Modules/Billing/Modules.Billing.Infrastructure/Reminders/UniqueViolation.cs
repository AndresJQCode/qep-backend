using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Modules.Billing.Infrastructure.Reminders;

/// <summary>
/// La violación de un índice único concreto. Se discrimina por nombre y no sólo por <c>23505</c> (precedente
/// <c>TenancyUnitOfWork</c>): cada índice es una deduplicación distinta del worker, y otra violación sí es un error.
/// </summary>
internal static class UniqueViolation
{
    public static bool Is(DbUpdateException exception, params string[] constraintNames) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && constraintNames.Contains(postgres.ConstraintName, StringComparer.Ordinal);
}
