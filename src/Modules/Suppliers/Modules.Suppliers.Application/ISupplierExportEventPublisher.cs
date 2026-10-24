namespace Modules.Suppliers.Application;

/// <summary>
/// Encola el aviso de que una exportación quedó lista para que Notifications mande el correo.
/// Va por outbox: commitea en la misma transacción que la auditoría y un fallo del proveedor de
/// correo no tumba el request que ya subió el archivo. El enlace prefirmado viaja en el evento.
/// </summary>
public interface ISupplierExportEventPublisher
{
    void Publish(
        Guid tenantId,
        Guid subjectId,
        string downloadUrl,
        string fileName,
        int supplierCount,
        DateTimeOffset expiresAt,
        DateTimeOffset occurredAt);
}
