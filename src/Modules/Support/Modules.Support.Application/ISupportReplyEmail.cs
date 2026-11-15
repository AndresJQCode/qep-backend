namespace Modules.Support.Application;

/// <summary>Una respuesta de un ticket por correo (plan E15): al correo de la foto del solicitante, con el nombre del tenant, el
/// radicado y el asunto del ticket.</summary>
public sealed record SupportReplyEmail(string ToAddress, string TenantName, string TicketNumber, string TicketSubject, string Text);

/// <summary>El correo de Soporte. El adaptador de Bootstrapper usa el canal y las plantillas de Notifications; cualquier falla del canal
/// es <see cref="SupportChannelSendResult.Failed"/> con <c>send_failed</c>, nunca una excepción. Un éxito es
/// <see cref="SupportChannelSendResult.Sent"/> sin mensaje ni conversación. El correo no es idempotente: un reintento manda otro.</summary>
public interface ISupportReplyEmail
{
    Task<SupportChannelSendResult> SendAsync(SupportReplyEmail email, CancellationToken cancellationToken);
}
