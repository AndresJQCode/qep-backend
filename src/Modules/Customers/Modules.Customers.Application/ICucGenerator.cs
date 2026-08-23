namespace Modules.Customers.Application;

/// <summary>
/// Emite el proximo numero de consecutivo del CUC del tenant.
///
/// Es un puerto y no un metodo estatico porque el consecutivo tiene que ser **atomico entre
/// transacciones concurrentes**: dos altas simultaneas que lean el maximo y sumen uno emiten el
/// mismo numero, y el unico arbitro real es la base. La implementacion vive en Infrastructure.
///
/// **Un unico contador por tenant**, no por clasificacion ni por departamento — decision de
/// negocio confirmada: dos clientes del mismo tenant con clasificaciones distintas siguen
/// compartiendo el mismo consecutivo.
///
/// Devuelve el numero crudo, no el CUC ya formado: el formato final (prefijo de clasificacion +
/// codigo de departamento + consecutivo con seis digitos) lo arma <see cref="CucFormatter"/>, que
/// es la pieza reutilizable y testeable sin base de datos. Este puerto solo resuelve el problema
/// de concurrencia.
///
/// `SDD-OD-06` sigue abierta sobre donde vive la emision del CUC —aca o en un modulo
/// `identifiers` propio—. Esta interfaz es justamente la costura por la que se muda el dia que se
/// decida: cambia el adaptador registrado, no los handlers.
/// </summary>
public interface ICucGenerator
{
    Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// La version en lote de <see cref="NextAsync"/>: reserva <paramref name="count"/> numeros
    /// consecutivos de una sola vez y devuelve el **primero** del bloque — si el contador estaba en
    /// 10 y se piden 5, devuelve 10 y deja el contador en 15; los cinco clientes usan
    /// 10, 11, 12, 13 y 14, en el mismo orden en que aparecen en el archivo.
    ///
    /// La usa la importacion masiva (Fase 5) para no hacer un round-trip separado por fila: una
    /// carga de 500 clientes reserva el bloque entero con una sola sentencia, no 500.
    /// </summary>
    Task<long> NextBatchAsync(Guid tenantId, int count, CancellationToken cancellationToken);
}
