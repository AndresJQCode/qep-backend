namespace Modules.Support.Application;

/// <summary>Una respuesta de un ticket por correo (plan E15): al correo de la foto del solicitante, con el nombre del tenant, el
/// radicado y el asunto del ticket. Fase 4b (H10): <paramref name="Document"/> es el PDF de una respuesta formal, que va adjunto.</summary>
public sealed record SupportReplyEmail(
    string ToAddress, string TenantName, string TicketNumber, string TicketSubject, string Text, SupportEmailDocument? Document = null);

/// <summary>Fase 4b: el PDF de la respuesta formal como adjunto del correo, con el nombre que ve el solicitante.</summary>
public sealed record SupportEmailDocument(string FileName, ReadOnlyMemory<byte> Content);

/// <summary>El correo de Soporte. El adaptador de Bootstrapper usa el canal y las plantillas de Notifications; cualquier falla del canal
/// es <see cref="SupportChannelSendResult.Failed"/> con <c>send_failed</c>, nunca una excepción. Un éxito es
/// <see cref="SupportChannelSendResult.Sent"/> sin mensaje ni conversación. El correo no es idempotente: un reintento manda otro.</summary>
public interface ISupportReplyEmail
{
    Task<SupportChannelSendResult> SendAsync(SupportReplyEmail email, CancellationToken cancellationToken);
}
