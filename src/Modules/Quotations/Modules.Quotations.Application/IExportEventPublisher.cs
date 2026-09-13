using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Encola los avisos para Notifications. Por outbox y en la unidad de trabajo del módulo, igual
/// que <c>ICustomerExportEventPublisher</c>: el evento commitea en la misma transacción que el
/// cambio de estado del job (D10), así que nunca sale un correo de un job que no terminó.
/// </summary>
public interface IExportEventPublisher
{
    /// <summary><c>quotations.export-ready.v1</c>.</summary>
    void PublishReady(ExportJob job, ExportJobResult result, DateTimeOffset occurredAt);

    /// <summary><c>quotations.export-failed.v1</c>.</summary>
    void PublishFailed(ExportJob job, DateTimeOffset occurredAt);
}
