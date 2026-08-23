using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>
/// Emite el proximo numero de consecutivo del CUC del tenant con un <c>UPDATE ... RETURNING</c>
/// atomico. El formato final (prefijo de clasificacion + codigo de departamento + este numero con
/// seis digitos) lo arma <c>CucFormatter</c>, en Application — este tipo solo resuelve la
/// concurrencia del consecutivo, no sabe nada de prefijos ni de departamentos.
///
/// **Por que SQL crudo y no leer-sumar-escribir con EF:** entre el <c>SELECT</c> y el
/// <c>SaveChanges</c> de dos altas concurrentes cabe la otra, y las dos emiten el mismo numero.
/// El <c>UPDATE</c> toma el lock de fila, asi que la segunda espera y lee el valor ya
/// incrementado. <c>INSERT ... ON CONFLICT DO NOTHING</c> antes cubre al primer cliente del
/// tenant, cuando la fila del contador todavia no existe.
///
/// Corre en la conexion del <c>DbContext</c> del modulo, asi que participa de la transaccion que
/// abre <c>SaveChangesAsync</c> cuando hay una. Si el alta falla despues de emitir, el numero se
/// pierde: el consecutivo puede tener huecos y eso esta bien — lo que no puede es repetirse.
///
/// **Un unico contador por tenant**, no por clasificacion ni por departamento: dos clientes del
/// mismo tenant con clasificaciones distintas siguen consumiendo el mismo consecutivo — decision
/// de negocio confirmada, no una limitacion de este mecanismo.
/// </summary>
internal sealed class CucGenerator(CustomersDbContext dbContext) : ICucGenerator
{
    public async Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO customers.cuc_counters (tenant_id, next_value)
            VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            cancellationToken);

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE customers.cuc_counters
                SET next_value = next_value + 1
                WHERE tenant_id = {tenantId}
                RETURNING next_value - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken);

        // La fila existe: la acaba de asegurar el INSERT de arriba, en la misma conexion. Un
        // resultado vacio aca significa que alguien la borro entre las dos sentencias, y emitir un
        // numero inventado seria peor que fallar.
        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The CUC counter for tenant '{tenantId}' could not be read back.");
    }

    // Mismo mecanismo que NextAsync -- INSERT ... ON CONFLICT DO NOTHING para asegurar la fila,
    // UPDATE ... RETURNING para el lock -- pero suma `count` de una vez en vez de 1. Una
    // importacion de 500 filas reserva su bloque entero con esta unica sentencia, no con 500 viajes
    // a la base.
    public async Task<long> NextBatchAsync(Guid tenantId, int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, "The batch size must be positive.");
        }

        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO customers.cuc_counters (tenant_id, next_value)
            VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            cancellationToken);

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE customers.cuc_counters
                SET next_value = next_value + {count}
                WHERE tenant_id = {tenantId}
                RETURNING next_value - {count} AS "Value"
                """)
            .ToListAsync(cancellationToken);

        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The CUC counter for tenant '{tenantId}' could not be read back.");
    }
}
