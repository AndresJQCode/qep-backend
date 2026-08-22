using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Channels;

// Canal de email de desarrollo: registra el mensaje en vez de enviarlo, para ejercitar todo
// el flujo de notificación sin un proveedor externo (ADR 0018).
internal sealed partial class LogEmailChannel(ILogger<LogEmailChannel> logger) : IEmailChannel
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Email (dev channel) to {ToAddress}: {Subject}")]
    private static partial void LogEmail(ILogger logger, string toAddress, string subject);

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        LogEmail(logger, message.ToAddress, message.Subject);
        return Task.CompletedTask;
    }
}
