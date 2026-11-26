namespace Modules.Support.Application;

/// <summary>
/// Fase 4b (H12): genera el PDF de la respuesta formal contra <c>qcode-pdf</c> con la clave propia de Soporte (<c>Support:Pdf</c>,
/// nunca la de cotizaciones). Si el servicio no devuelve el PDF lanza <c>SupportDomainException</c> con
/// <c>support.reply.document_render_failed</c> (422), sin el cuerpo de la respuesta: puede repetir datos del solicitante.
/// </summary>
public interface ISupportFormalReplyRenderer
{
    Task<byte[]> RenderAsync(SupportFormalReplyDocument document, CancellationToken cancellationToken);
}
