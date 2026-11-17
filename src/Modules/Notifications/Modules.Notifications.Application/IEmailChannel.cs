namespace Modules.Notifications.Application;

/// <summary>Un correo ya armado. <paramref name="Attachments"/> (Soporte fase 4b) son los archivos adjuntos; <c>null</c> o vacía, el correo
/// sale igual que antes.</summary>
public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>Soporte fase 4b (H10): un archivo adjunto de un correo, como el PDF de la respuesta formal de un ticket. Su contenido nunca va a
/// un log.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>
/// Entrega un email por el proveedor configurado. Las implementaciones son adaptadores
/// (Infobip en producción, un canal de log en desarrollo) y no deben filtrar detalles del
/// proveedor a los llamadores (ADR 0018).
/// </summary>
public interface IEmailChannel
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
