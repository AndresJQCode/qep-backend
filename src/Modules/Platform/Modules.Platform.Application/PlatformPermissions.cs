namespace Modules.Platform.Application;

// Tres segmentos, module.resource.action, igual que el resto de los permisos registrados.
//
// El log de fallas no reusa un permiso existente a proposito. Sus filas llevan la traza de la
// excepcion y el mensaje crudo de cualquier modulo --lo que un 500 arrastre adentro-- asi que no
// se puede colgar de un permiso de lectura de negocio: quien puede leer productos no tiene por
// que ver el stack trace de un fallo de facturacion. Es de administracion, y solo `admin` lo
// tiene.
//
// El purgado va aparte de la lectura por la misma razon que `advisorship.roles.manage` va aparte
// de `advisorship.manage`: borrar en lote no es leer, y es irreversible.
public static class PlatformPermissions
{
    public const string RequestLogRead = "platform.request_log.read";
    public const string RequestLogPurge = "platform.request_log.purge";
}
