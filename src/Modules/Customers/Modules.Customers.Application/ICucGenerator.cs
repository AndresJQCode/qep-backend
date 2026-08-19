namespace Modules.Customers.Application;

/// <summary>
/// Emite el proximo CUC del tenant.
///
/// Es un puerto y no un metodo estatico porque el consecutivo tiene que ser **atomico entre
/// transacciones concurrentes**: dos altas simultaneas que lean el maximo y sumen uno emiten el
/// mismo codigo, y el unico arbitro real es la base. La implementacion vive en Infrastructure.
///
/// `SDD-OD-06` sigue abierta sobre donde vive la emision del CUC —aca o en un modulo
/// `identifiers` propio—. Esta interfaz es justamente la costura por la que se muda el dia que se
/// decida: cambia el adaptador registrado, no los handlers.
/// </summary>
public interface ICucGenerator
{
    Task<string> NextAsync(Guid tenantId, CancellationToken cancellationToken);
}
