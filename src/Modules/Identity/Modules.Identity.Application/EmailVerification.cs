using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public sealed record AuthenticatedUser(Guid UserId, string Email);

public interface IEmailVerification
{
    /// <summary>
    /// Consume el token de verificación (atómico y fuera de la unidad de trabajo, D6), marca el email
    /// como verificado, activa al usuario y audita <c>identity.email.verified</c>.
    /// </summary>
    /// <exception cref="IdentityDomainException">
    /// <c>identity.email_token.invalid</c> si el token no existe, venció o ya se usó: los tres casos
    /// salen idénticos para no darle a quien adivina información sobre el token.
    /// </exception>
    Task<AuthenticatedUser> VerifyAsync(string rawToken, CancellationToken cancellationToken);
}

public sealed class EmailVerificationService(
    IEmailTokenRepository tokenRepository,
    IUserRepository userRepository,
    IIdentityAuditRecorder auditRecorder,
    IIdentityUnitOfWork unitOfWork,
    IClock clock)
    : IEmailVerification
{
    public const string InvalidTokenCode = "identity.email_token.invalid";

    public async Task<AuthenticatedUser> VerifyAsync(string rawToken, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // Sin transacción abierta: la sentencia se confirma sola y deja pasar a un único request.
        // El token crudo nunca se registra ni se audita; sólo viaja su hash.
        var userId = await tokenRepository.TryConsumeAsync(
            EmailTokenCodec.Hash(rawToken),
            EmailTokenPurpose.Verify,
            now,
            cancellationToken);
        if (userId is null)
        {
            throw Invalid();
        }

        // El usuario pudo borrarse (compensación de registro) entre el consumo y esta lectura:
        // se responde igual que un token inválido.
        var user = await userRepository.FindByIdAsync(userId.Value, cancellationToken)
            ?? throw Invalid();

        user.MarkEmailVerified(now);
        user.Activate(now);
        auditRecorder.Record(
            user.Id.Value,
            "identity.email.verified",
            "user",
            user.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new AuthenticatedUser(user.Id.Value, user.Email);
    }

    private static IdentityDomainException Invalid() =>
        new(InvalidTokenCode, "The verification link is invalid or has expired.");
}
