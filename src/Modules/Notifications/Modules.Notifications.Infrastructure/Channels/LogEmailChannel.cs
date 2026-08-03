using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Channels;

// Development email channel: records the message instead of sending it, so the full
// notification flow is exercised without an external provider (ADR 0018).
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
