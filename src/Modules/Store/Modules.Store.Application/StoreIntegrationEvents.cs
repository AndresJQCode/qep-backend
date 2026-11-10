using System.Diagnostics.CodeAnalysis;

namespace Modules.Store.Application;

/// <summary>
/// Los eventos que Store publica en el outbox de plataforma. Store es dueño del contrato (nombre y payload); quien lo consume lo lee
/// por nombre con su propio record, y <c>BuyerParityTests</c> (Bootstrapper.UnitTests) los mantiene iguales.
/// </summary>
public static class StoreIntegrationEvents
{
    /// <summary>El pedido pidió crear la cuenta de comprador; lo consume Buyers (<c>BuyerSignupRequestedWorker</c>).</summary>
    public const string BuyerSignupRequested = "store.buyer-signup-requested.v1";
}

/// <summary>
/// El alta de cuenta pedida desde el checkout. Commitea con la solicitud: es idempotente por <paramref name="OrderId"/>.
/// Lleva <paramref name="PasswordHash"/> y <b>nunca</b> la contraseña en claro.
/// </summary>
/// <param name="StoreHost">El host normalizado de la tienda: el de los enlaces del correo y el del consentimiento.</param>
/// <param name="VisitorIp">La IP del consentimiento, la misma que guarda la solicitud.</param>
/// <param name="Email">El email de facturación ya normalizado (<see cref="StoreBuyerSignup.TryNormalizeEmail"/>): el de ingreso.</param>
public sealed record StoreBuyerSignupRequestedPayload(
    Guid OrderId,
    Guid TenantId,
    Guid ClientRequestId,
    string StoreHost,
    string StoreName,
    string VisitorIp,
    string Email,
    string PasswordHash,
    PublicBillingInput Billing,
    PublicShippingInput? Shipping);

public static class StoreBuyerSignup
{
    // Copia de BuyerEmail.MaxLength.
    private const int EmailMaxLength = 254;

    /// <summary>
    /// La regla de email de la cuenta de comprador (trim, minúsculas invariantes, máx. 254, con arroba), copiada de
    /// <c>BuyerEmail.TryNormalize</c> porque Store no ve Buyers. <c>BuyerParityTests</c> compara las dos.
    /// </summary>
    public static bool TryNormalizeEmail(string? raw, [NotNullWhen(true)] out string normalized)
    {
        normalized = string.Empty;
        if (raw is null)
        {
            return false;
        }

        var candidate = raw.Trim().ToLowerInvariant();
        if (candidate.Length == 0 || candidate.Length > EmailMaxLength || !candidate.Contains('@'))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
