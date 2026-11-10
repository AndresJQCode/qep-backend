using System.Text.Json;
using Modules.Buyers.Application;

namespace Modules.Buyers.Infrastructure.Signup;

/// <summary>
/// El alta pedida desde el checkout, leída con un record propio porque Buyers no ve Store. Mismos campos (en camelCase) que
/// <c>StoreBuyerSignupRequestedPayload</c>; <c>BuyerParityTests</c> (Bootstrapper.UnitTests) los compara campo a campo.
/// Lleva el hash, nunca la contraseña.
/// </summary>
public sealed record BuyerSignupRequestedMessage(
    Guid OrderId,
    Guid TenantId,
    Guid ClientRequestId,
    string StoreHost,
    string StoreName,
    string VisitorIp,
    string Email,
    string PasswordHash,
    BuyerBillingDto Billing,
    BuyerShippingDto? Shipping)
{
    /// <summary>Literal: es el nombre que declara <c>StoreIntegrationEvents.BuyerSignupRequested</c>.</summary>
    public const string EventName = "store.buyer-signup-requested.v1";

    /// <summary>Lanza <see cref="JsonException"/> si el JSON no se lee o le falta un dato sin el cual no hay alta posible.</summary>
    public static BuyerSignupRequestedMessage Parse(string payloadJson)
    {
        var message = JsonSerializer.Deserialize<BuyerSignupRequestedMessage>(payloadJson, JsonSerializerOptions.Web)
            ?? throw new JsonException("The signup message is empty.");
        if (message.OrderId == Guid.Empty
            || string.IsNullOrWhiteSpace(message.StoreHost)
            || string.IsNullOrWhiteSpace(message.StoreName)
            || string.IsNullOrWhiteSpace(message.VisitorIp)
            || string.IsNullOrWhiteSpace(message.PasswordHash)
            || message.Email is null
            || message.Billing is null)
        {
            throw new JsonException("The signup message lacks a required field.");
        }

        return message;
    }
}
