namespace Modules.Messaging.Application;

/// <summary>§8.2, paso 3: <c>INSERT … ON CONFLICT (body_sha256) DO NOTHING</c>. <c>false</c> si ya estaba. <paramref name="provider"/>
/// (spec 2026-10-14 §6.2) dice con qué parser se procesa.</summary>
public interface IWebhookDeliveries
{
    Task<bool> EnqueueAsync(WebhookProvider provider, byte[] body, DateTimeOffset receivedAt, CancellationToken cancellationToken);
}
