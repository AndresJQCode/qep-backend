using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// La violación de una clave foránea concreta, por nombre. Reconoce <c>23503</c> <b>y</b> <c>23001</c>: borrar el padre de una FK
/// <c>ON DELETE RESTRICT</c> da <c>restrict_violation</c> (23001), no <c>foreign_key_violation</c>, y <see cref="UniqueViolation"/>
/// sólo mira <c>23505</c> (precedente <c>CustomersUnitOfWork.IsForeignKeyOrRestrictViolationOf</c>). WMS-D la reutiliza para sus
/// claves internas.
/// </summary>
internal static class ForeignKeyViolation
{
    public static bool Is(Exception exception, string constraintName)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var postgres = exception as PostgresException ?? (exception as DbUpdateException)?.InnerException as PostgresException;
        return postgres is { SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation }
            && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
    }
}
