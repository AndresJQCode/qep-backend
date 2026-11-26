using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Soporte fase 3 (plan F23): el correo de apertura de un ticket sobre <see cref="IEmailChannel"/> y
/// <see cref="SupportOpeningEmailTemplate"/> de Notifications, que Soporte no ve. Calco de <see cref="SupportReplyEmailSender"/>: cualquier
/// excepción del canal es <c>send_failed</c> y el ticket ya quedó radicado. Nunca loguea el PIN ni el destinatario: sólo el tipo de la
/// excepción.
/// </summary>
internal sealed partial class SupportOpeningEmailSender(IEmailChannel channel, ILogger<SupportOpeningEmailSender> logger)
    : ISupportOpeningEmail
{
    // Sólo el tipo de la excepción: el mensaje del proveedor puede traer la dirección del destinatario.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Support opening email failed with {ExceptionType}; it is reported as send_failed.")]
    private static partial void LogSendFailed(ILogger logger, string exceptionType);

    public async Task<SupportChannelSendResult> SendAsync(SupportOpeningEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        try
        {
            await channel.SendAsync(
                SupportOpeningEmailTemplate.Render(new SupportOpeningEmailInput(
                    email.ToAddress, email.TenantName, email.TicketNumber, email.TypeName, email.DueOn, email.ConsultUrl, email.Pin,
                    email.Language)),
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
