namespace Modules.Identity.Application;

/// <summary>Nombres de los eventos de integración que Identity publica en el Outbox de plataforma.</summary>
public static class IdentityIntegrationEvents
{
    public const string EmailVerificationRequested = "identity.email-verification-requested.v1";

    public const string PasswordResetRequested = "identity.password-reset-requested.v1";

    public const string ExistingAccountNotice = "identity.existing-account-notice.v1";

    /// <summary>Spec 2026-11-19 (onboarding por link): el código para confirmar un correo que todavía no es de un usuario.</summary>
    public const string EmailChallengeRequested = "identity.email-challenge-requested.v1";
}

/// <summary>Payload de los eventos que llevan un token crudo de un solo uso.</summary>
public sealed record EmailTokenIssuedPayload(Guid UserId, string Token);

public sealed record ExistingAccountNoticePayload(Guid UserId);

/// <summary>
/// Payload de <see cref="IdentityIntegrationEvents.EmailChallengeRequested"/>. Lleva el correo porque no hay usuario del que
/// leerlo, y el código crudo, que sólo viaja aquí: en la base queda su hash.
/// </summary>
public sealed record EmailChallengeRequestedPayload(Guid ChallengeId, string Email, string Code, DateTimeOffset ExpiresAt);
