namespace Modules.Notifications.Application;

public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody);

/// <summary>
/// Entrega un email por el proveedor configurado. Las implementaciones son adaptadores
/// (Infobip en producción, un canal de log en desarrollo) y no deben filtrar detalles del
/// proveedor a los llamadores (ADR 0018).
/// </summary>
public interface IEmailChannel
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
