namespace Modules.Messaging.Application;

/// <summary>§8.2, paso 3: <c>INSERT … ON CONFLICT (body_sha256) DO NOTHING</c>. <c>false</c> si ya estaba.</summary>
public interface IWebhookDeliveries
{
    Task<bool> EnqueueAsync(byte[] body, DateTimeOffset receivedAt, CancellationToken cancellationToken);
}
