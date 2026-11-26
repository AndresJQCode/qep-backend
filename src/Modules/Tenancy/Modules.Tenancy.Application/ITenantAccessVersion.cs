namespace Modules.Tenancy.Application;

/// <summary>
/// La versión de acceso de un tenant (spec 2026-11-13, D-5): un contador que invalida de una vez todas las entradas de la caché
/// de permisos del tenant. La sube todo caso de uso que cambia lo que alguien puede hacer ahí —roles, membresías, aceptar una
/// invitación, módulos y estado del tenant—, antes del guardado y otra vez después: el primer bump descarta lo que había, el
/// segundo lo que un request en carrera construyó con la versión del medio.
/// </summary>
public interface ITenantAccessVersion
{
    /// <summary>
    /// Sube la versión del tenant. Nunca lanza: sin éxito lo loguea en <c>Error</c> con el tenant, y la caché sirve lo que tenía
    /// hasta que su entrada venza (60 s). Con la caché apagada no hace nada.
    /// </summary>
    Task BumpAsync(Guid tenantId, CancellationToken cancellationToken);
}
