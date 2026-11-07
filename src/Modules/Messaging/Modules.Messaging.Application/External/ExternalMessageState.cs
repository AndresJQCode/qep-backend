using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-01 parte 2: el estado de un mensaje como lo lee el integrador. Un <c>Failed</c> cierto lleva el texto de la burbuja;
/// un envío sin confirmar (<see cref="MessageFailureReasons.Unconfirmed"/>) sale como <c>Unconfirmed</c> con su propio texto, porque el
/// de la burbuja dice «Intenta de nuevo» y el mensaje pudo haber salido.
/// </summary>
public sealed record ExternalMessageState(string Status, int? FailureCode, string? FailureReason)
{
    private const string UnconfirmedReason =
        "WhatsApp no confirmó el envío: el mensaje pudo haber salido. Revisa la conversación antes de mandar otro.";

    public static ExternalMessageState Describe(MessageStatus status, int? failureCode)
    {
        if (status != MessageStatus.Failed)
        {
            return new ExternalMessageState(status.ToString(), null, null);
        }

        return failureCode == MessageFailureReasons.Unconfirmed
            ? new ExternalMessageState("Unconfirmed", MessageFailureReasons.Unconfirmed, UnconfirmedReason)
            : new ExternalMessageState(nameof(MessageStatus.Failed), failureCode, MessageFailureReasons.For(failureCode));
    }
}
