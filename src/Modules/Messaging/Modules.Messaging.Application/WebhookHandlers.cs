using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>§8.2 GET: devuelve el <c>hub.challenge</c> si el token coincide; <c>null</c> si no (el endpoint responde 403).</summary>
public sealed record VerifyWebhookQuery(string? Mode, string? Token, string? Challenge) : IQuery<string?>;

public sealed class VerifyWebhookHandler(IWebhookSignatureVerifier verifier) : IQueryHandler<VerifyWebhookQuery, string?>
{
    public Task<string?> HandleAsync(VerifyWebhookQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(
            verifier.IsConfigured
            && string.Equals(query.Mode, "subscribe", StringComparison.Ordinal)
            && verifier.VerifyToken(query.Token)
            && !string.IsNullOrEmpty(query.Challenge)
                ? query.Challenge
                : null);
}

/// <summary>§8.2 POST: firma → guardar deduplicado → 200. <c>true</c> si la entrega era nueva.
/// <see cref="ToString"/> no imprime el cuerpo (trae mensajes de personas).</summary>
public sealed record ReceiveWebhookCommand(byte[] Body, string? Signature) : ICommand<bool>
{
    public override string ToString() => $"ReceiveWebhookCommand {{ Bytes = {Body.Length} }}";
}

public sealed class ReceiveWebhookHandler(IWebhookSignatureVerifier verifier, IWebhookDeliveries deliveries, IClock clock)
    : ICommandHandler<ReceiveWebhookCommand, bool>
{
    public Task<bool> HandleAsync(ReceiveWebhookCommand command, CancellationToken cancellationToken)
    {
        // 401 antes de tocar la base (§11).
        if (!verifier.IsConfigured || !verifier.VerifyBody(command.Body, command.Signature))
        {
            throw new RequestUnauthorizedException(MessagingErrorCodes.WebhookSignatureInvalid, "The webhook signature is missing or invalid.");
        }

        return deliveries.EnqueueAsync(command.Body, clock.UtcNow, cancellationToken);
    }
}
