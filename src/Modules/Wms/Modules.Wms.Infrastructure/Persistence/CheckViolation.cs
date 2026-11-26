using Npgsql;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// La violación de un <c>CHECK</c> concreto, por nombre. Recibe <see cref="PostgresException"/> y no <c>DbUpdateException</c> porque
/// el upsert del ledger corre por <c>ExecuteSqlAsync</c>/<c>SqlQuery</c>: la excepción llega sin envolver y una rama en
/// <c>SaveChangesAsync</c> nunca se ejecutaría para este SQL (S26).
/// </summary>
internal static class CheckViolation
{
    public static bool Is(PostgresException exception, params string[] constraintNames) =>
        exception.SqlState == PostgresErrorCodes.CheckViolation
        && constraintNames.Contains(exception.ConstraintName, StringComparer.Ordinal);
}
