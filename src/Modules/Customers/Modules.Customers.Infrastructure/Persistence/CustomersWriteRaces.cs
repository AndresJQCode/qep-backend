using BuildingBlocks.Application;
using Npgsql;

namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>
/// Un deadlock (<c>40P01</c>) o una falla de serialización (<c>40001</c>) es una carrera con otra escritura, no un error del
/// servidor ni un choque con datos que ya existen: responde 412 <c>concurrency.conflict</c> (el frontend vuelve a pedir la
/// vista previa), no 500 ni 409. La fusión (spec 2026-10-24 §3.2) es la que más la arriesga: toca filas de diez tablas de
/// seis esquemas en una sola transacción.
///
/// <para>Se busca en toda la cadena de <see cref="Exception.InnerException"/>: la estrategia de ejecución de Npgsql envuelve
/// un error transitorio en un <see cref="InvalidOperationException"/> («likely due to a transient failure»), y
/// <c>SaveChanges</c> además lo envuelve en un <c>DbUpdateException</c>. La usan <see cref="CustomersUnitOfWork"/>,
/// <see cref="CustomerMergeStore"/> y <c>Bootstrapper.CustomerMergeReferences</c> (el SQL de los otros módulos, que no pasa
/// por el unit of work). Vive en Infrastructure porque Application no referencia Npgsql (<c>CustomersLayerTests</c>).</para>
/// </summary>
public static class CustomersWriteRaces
{
    public static bool IsRace(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }

    public static RequestConcurrencyException ToConflict(Exception exception) =>
        new("concurrency.conflict", "Another write raced with this one. Reload and try again.", exception);

    /// <summary>Corre <paramref name="write"/> y traduce la carrera; cualquier otro error sale tal cual.</summary>
    public static async Task RunAsync(Func<Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        try
        {
            await write();
        }
        catch (Exception exception) when (IsRace(exception))
        {
            throw ToConflict(exception);
        }
    }
}
