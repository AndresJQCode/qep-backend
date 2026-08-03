namespace Modules.Notifications.Application;

public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody);

/// <summary>
/// Delivers an email through the configured provider. Implementations are adapters
/// (Infobip in production, a log channel in development) and must not leak provider
/// specifics to callers (ADR 0018).
/// </summary>
public interface IEmailChannel
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
