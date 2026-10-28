namespace Modules.Identity.Application;

/// <summary>Nombres de los eventos de integración que Identity publica en el Outbox de plataforma.</summary>
public static class IdentityIntegrationEvents
{
    public const string EmailVerificationRequested = "identity.email-verification-requested.v1";

    public const string PasswordResetRequested = "identity.password-reset-requested.v1";

    public const string ExistingAccountNotice = "identity.existing-account-notice.v1";
}

/// <summary>Payload de los eventos que llevan un token crudo de un solo uso.</summary>
public sealed record EmailTokenIssuedPayload(Guid UserId, string Token);

public sealed record ExistingAccountNoticePayload(Guid UserId);
