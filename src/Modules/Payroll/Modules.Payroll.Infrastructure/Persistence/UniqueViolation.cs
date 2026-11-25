using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Modules.Payroll.Infrastructure.Persistence;

/// <summary>
/// La violación de un índice único concreto. Se discrimina por nombre y no sólo por <c>23505</c> (precedente
/// <c>TenancyUnitOfWork</c>): cada índice es una deduplicación distinta, y otra violación sí es un error.
/// </summary>
internal static class UniqueViolation
{
    public static bool Is(DbUpdateException exception, params string[] constraintNames) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && constraintNames.Contains(postgres.ConstraintName, StringComparer.Ordinal);

    /// <summary>La restricción de exclusión (<c>23P01</c>) de las vacaciones: dos de un mismo empleado que comparten un día.</summary>
    public static bool IsExclusion(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } postgres
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
