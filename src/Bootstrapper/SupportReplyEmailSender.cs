using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Soporte fase 2 (plan E15): el correo de respuesta de un ticket sobre <see cref="IEmailChannel"/> y
/// <see cref="SupportReplyEmailTemplate"/> de Notifications, que Soporte no ve. Igual que <see cref="ServiceContractsEmailSender"/>, cualquier
/// excepción del canal es <c>send_failed</c>: la respuesta queda <c>Failed</c> y visible, nunca un 500.
/// </summary>
internal sealed partial class SupportReplyEmailSender(IEmailChannel channel, ILogger<SupportReplyEmailSender> logger) : ISupportReplyEmail
{
    // Sólo el tipo de la excepción: el mensaje del proveedor puede traer la dirección del destinatario.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Support reply email failed with {ExceptionType}; it is reported as send_failed.")]
    private static partial void LogSendFailed(ILogger logger, string exceptionType);

    public async Task<SupportChannelSendResult> SendAsync(SupportReplyEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        try
        {
            await channel.SendAsync(
                SupportReplyEmailTemplate.Render(new SupportReplyEmailInput(
                    email.ToAddress, email.TenantName, email.TicketNumber, email.TicketSubject, email.Text)),
                cancellationToken);
            return new SupportChannelSendResult.Sent(null, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendFailed(logger, exception.GetType().Name);
            return new SupportChannelSendResult.Failed(null, SupportChannelReasons.SendFailed);
        }
    }
}
