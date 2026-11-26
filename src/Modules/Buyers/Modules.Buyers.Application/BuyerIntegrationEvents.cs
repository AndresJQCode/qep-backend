namespace Modules.Buyers.Application;

/// <summary>Eventos de integración que publica el módulo. Los consumen los workers de Notifications.</summary>
public static class BuyerIntegrationEvents
{
    public const string EmailVerificationRequested = "buyers.email-verification-requested.v1";
    public const string ExistingAccountNotice = "buyers.existing-account-notice.v1";
    public const string PasswordResetRequested = "buyers.password-reset-requested.v1";
    public const string AccountDeleted = "buyers.account-deleted.v1";
}

/// <summary>
/// Lo que lleva cada correo del comprador. <paramref name="Token"/> es el valor en claro y sólo viaja en los correos que lo
/// necesitan (verificar, restablecer); en los demás va <c>null</c>.
/// </summary>
public sealed record BuyerEmailPayload(Guid AccountId, string Email, string StoreHost, string StoreName, string? Token);
