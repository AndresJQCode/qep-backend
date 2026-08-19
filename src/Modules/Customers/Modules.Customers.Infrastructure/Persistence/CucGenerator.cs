using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;

namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>
/// Emite el proximo CUC del tenant con un <c>UPDATE ... RETURNING</c> atomico.
///
/// El formato es <c>CUC-000142</c>: prefijo, guion y seis digitos con ceros a la izquierda. Sale
/// del consumidor, que ya lo genera asi en sus datos de prueba
/// (<c>generateCuc</c> en <c>features/customers/services/customers.fixtures.ts</c>) y lo pinta en
/// una columna de la grilla. Pasados los seis digitos el numero simplemente crece: recortar seria
/// emitir un codigo repetido, y el indice unico lo rechazaria.
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
/// </summary>
internal sealed class CucGenerator(CustomersDbContext dbContext) : ICucGenerator
{
    private const string Prefix = "CUC-";

    private const int PaddedDigits = 6;

    public async Task<string> NextAsync(Guid tenantId, CancellationToken cancellationToken)
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
        // CUC inventado seria peor que fallar.
        var next = emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The CUC counter for tenant '{tenantId}' could not be read back.");

        // "D6" con cultura invariante, no ToString() a secas: el separador de miles y los digitos
        // de una cultura arabe o hindi convertirian el mismo consecutivo en codigos distintos
        // segun donde corra el servidor, y el CUC es un identificador. Pasados los seis digitos el
        // formato simplemente crece — recortar seria emitir un codigo repetido.
        return $"{Prefix}{next.ToString($"D{PaddedDigits}", CultureInfo.InvariantCulture)}";
    }
}
