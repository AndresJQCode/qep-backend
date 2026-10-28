using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public sealed record PasswordResetInput(string Token, string Password);

/// <summary>
/// Sin <c>OverridePropertyName</c> (D10): el error de la contraseña llega como "Password". Se valida
/// ANTES de consumir el token, así una contraseña corta no lo quema.
/// </summary>
public sealed class PasswordResetValidator : AbstractValidator<PasswordResetInput>
{
    public PasswordResetValidator()
    {
        RuleFor(input => input.Token).NotEmpty();
        RuleFor(input => input.Password).QepPassword();
    }
}

public interface IPasswordReset
{
    /// <summary>
    /// Un email mal formado o sin cuenta no hace nada (el endpoint responde 202 igual). Con cuenta:
    /// emite el token <c>reset</c>, encola <see cref="IdentityIntegrationEvents.PasswordResetRequested"/>
    /// y audita <c>identity.password.reset.requested</c>.
    /// </summary>
    Task RequestAsync(string email, string correlationId, CancellationToken cancellationToken);

    /// <summary>
    /// Valida, consume el token (atómico, D6), crea o reemplaza la credencial, verifica el email,
    /// audita <c>identity.password.reset.completed</c> y revoca todas las sesiones del usuario. No
    /// abre sesión ni activa al usuario: el login lo hace.
    /// </summary>
    /// <exception cref="IdentityDomainException"><c>identity.email_token.invalid</c>.</exception>
    Task ResetAsync(string rawToken, string password, CancellationToken cancellationToken);
}

public sealed class PasswordResetService(
    IValidator<PasswordResetInput> validator,
    IUserRepository userRepository,
    IPasswordCredentialRepository credentialRepository,
    IEmailTokenRepository tokenRepository,
    IEmailTokenIssuer tokenIssuer,
    IPasswordHasher passwordHasher,
    IIdentityOutbox outbox,
    IIdentityAuditRecorder auditRecorder,
    IIdentityUnitOfWork unitOfWork,
    ISessionService sessionService,
    IClock clock)
    : IPasswordReset
{
    public async Task RequestAsync(string email, string correlationId, CancellationToken cancellationToken)
    {
        string normalized;
        try
        {
            normalized = User.NormalizeEmail(email);
        }
        catch (IdentityDomainException)
        {
            return;
        }

        var user = await userRepository.FindByEmailAsync(normalized, cancellationToken);
        if (user is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var token = await tokenIssuer.IssueAsync(user.Id.Value, EmailTokenPurpose.Reset, cancellationToken);
        outbox.Add(
            IdentityIntegrationEvents.PasswordResetRequested,
            new EmailTokenIssuedPayload(user.Id.Value, token),
            correlationId,
            now);
        auditRecorder.Record(
            user.Id.Value,
            "identity.password.reset.requested",
            "user",
            user.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetAsync(string rawToken, string password, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(new PasswordResetInput(rawToken, password), cancellationToken);

        var now = clock.UtcNow;

        // Sin transacción abierta: la sentencia se confirma sola y deja pasar a un único request.
        var userId = await tokenRepository.TryConsumeAsync(
            EmailTokenCodec.Hash(rawToken),
            EmailTokenPurpose.Reset,
            now,
            cancellationToken);
        if (userId is null)
        {
            throw Invalid();
        }

        var user = await userRepository.FindByIdAsync(userId.Value, cancellationToken)
            ?? throw Invalid();

        var hash = passwordHasher.Hash(password);
        var credential = await credentialRepository.FindByUserAsync(user.Id.Value, cancellationToken);
        if (credential is null)
        {
            credentialRepository.Add(PasswordCredential.Create(user.Id, hash, now));
        }
        else
        {
            credential.Replace(hash, now);
        }

        // Llegar al link prueba que controla el correo.
        user.MarkEmailVerified(now);
        auditRecorder.Record(
            user.Id.Value,
            "identity.password.reset.completed",
            "user",
            user.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await sessionService.RevokeAllForUserAsync(user.Id.Value, "password_reset", cancellationToken);
    }

    private static IdentityDomainException Invalid() =>
        new(EmailVerificationService.InvalidTokenCode, "The link is invalid or has expired.");
}
