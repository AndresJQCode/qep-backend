using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-30 §6.3.2: del resultado de Meta al error que ve el integrador. La liquidación lanza <c>MessageRejected</c> tanto
/// para un rechazo como para un envío sin confirmar, así que se decide por el resultado y no por lo lanzado. Nada de Meta (título,
/// código) llega al mensaje: lo lee un integrador.</summary>
public static class ExternalSendFailure
{
    public static readonly TimeSpan MetaRetryAfter = TimeSpan.FromSeconds(60);

    public static Exception Map(SendResult result, MessagingDomainException thrown)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(thrown);

        if (thrown.Code == MessagingErrorCodes.ConnectionUnavailable)
        {
            return thrown;
        }

        if (result.Outcome != SendOutcome.GraphError)
        {
            return new UpstreamServiceException(
                MessagingErrorCodes.ExternalSendUnconfirmed,
                "WhatsApp no confirmó el envío. Revisa antes de reintentar: el mensaje pudo haber salido.");
        }

        if (result.Code == MetaErrorCodes.MarketingOptOut)
        {
            return new MessagingDomainException(
                MessagingErrorCodes.ExternalRecipientOptedOut,
                "La persona dejó de recibir mensajes de marketing por WhatsApp.");
        }

        if (result.Code is { } code && CampaignFailureCodes.IsRetriable(code))
        {
            return new RequestRateLimitedException(
                ExternalApiErrorCodes.RateLimited,
                "WhatsApp limitó el envío. Intenta de nuevo en un momento.",
                MetaRetryAfter);
        }

        return new MessagingDomainException(
            MessagingErrorCodes.ExternalMessageRejected,
            "WhatsApp rechazó el mensaje.");
    }
}
