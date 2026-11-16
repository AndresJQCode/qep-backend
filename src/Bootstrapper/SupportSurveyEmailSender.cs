using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Soporte fase 4b (H16, H19): el correo de la encuesta sobre <see cref="IEmailChannel"/> y <see cref="SupportSurveyEmailTemplate"/> de
/// Notifications, que Soporte no ve. Igual que <see cref="SupportReplyEmailSender"/>, cualquier excepción del canal es <c>send_failed</c>: la
/// encuesta se reintenta, nunca un 500. El enlace lleva el token: ni él ni la dirección van al log.
/// </summary>
internal sealed partial class SupportSurveyEmailSender(IEmailChannel channel, ILogger<SupportSurveyEmailSender> logger) : ISupportSurveyEmail
{
    // Sólo el tipo de la excepción: el mensaje del proveedor puede traer la dirección del destinatario.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Support survey email failed with {ExceptionType}; it is reported as send_failed.")]
    private static partial void LogSendFailed(ILogger logger, string exceptionType);

    public async Task<SupportChannelSendResult> SendAsync(SupportSurveyEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        try
        {
            await channel.SendAsync(
                SupportSurveyEmailTemplate.Render(new SupportSurveyEmailInput(
                    email.ToAddress, email.TenantName, email.TicketNumber, email.TicketSubject, email.RatingUrl)),
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
