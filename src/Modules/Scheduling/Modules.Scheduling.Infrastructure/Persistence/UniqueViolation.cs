using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Modules.Scheduling.Infrastructure.Persistence;

/// <summary>
/// La violación de una restricción concreta. Se discrimina por nombre y no sólo por el <c>SqlState</c> (precedente <c>HrUnitOfWork</c>):
/// cada índice es una deduplicación distinta, y devolver el código equivocado manda a corregir el campo equivocado.
/// </summary>
internal static class UniqueViolation
{
    public static bool Is(DbUpdateException exception, params string[] constraintNames) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && constraintNames.Contains(postgres.ConstraintName, StringComparer.Ordinal);

    /// <summary>La restricción de exclusión (<c>23P01</c>) de las sesiones: dos sesiones activas del mismo recurso que se solapan.</summary>
    public static bool IsExclusion(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } postgres
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
