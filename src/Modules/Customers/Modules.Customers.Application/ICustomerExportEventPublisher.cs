namespace Modules.Customers.Application;

/// <summary>
/// Encola el aviso de que una exportacion quedo lista, para que Notifications le mande el correo a
/// quien la pidio.
///
/// Va por outbox y no por una llamada directa al modulo de notificaciones por dos razones: el aviso
/// commitea en la misma transaccion que la auditoria —o se registran los dos o ninguno— y un fallo
/// del proveedor de correo no puede tumbar el request que ya subio el archivo.
///
/// El enlace ya prefirmado viaja en el evento. Asi Notifications no necesita conocer
/// <c>Modules.Storage</c> y el worker que lo consume queda con las mismas dependencias que el de
/// invitaciones.
/// </summary>
public interface ICustomerExportEventPublisher
{
    void Publish(
        Guid tenantId,
        Guid subjectId,
        string downloadUrl,
        string fileName,
        int customerCount,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt);
}
