namespace Modules.Hr.Application;

/// <summary>Nombres de los eventos de integración que Hr escribe en <c>platform.outbox_messages</c> para otros módulos.</summary>
public static class HrIntegrationEvents
{
    /// <summary>
    /// Spec 2026-11-26 («PDF del registro eliminado»): Hr soltó PDF de un empleado (hoy, al eliminar un registro de cargo y lugar).
    /// Payload: <c>{ "tenantId": "&lt;guid&gt;", "fileIds": ["&lt;guid&gt;", …] }</c>. Lo consume Storage
    /// (<c>storage.hr-document-release</c>), que purga los que Hr ya no usa en ninguna tabla.
    /// </summary>
    public const string DocumentsReleased = "hr.employee.documents-released.v1";
}

/// <summary>
/// Encola el aviso <see cref="HrIntegrationEvents.DocumentsReleased"/>. No guarda: el aviso viaja con el <c>SaveChangesAsync</c> de
/// quien llama, en la misma transacción que el cambio que suelta el PDF, o no sale nada. Calco de <c>IChatbotMediaReleases</c>.
/// </summary>
public interface IHrDocumentReleases
{
    void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt);
}
