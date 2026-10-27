using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public sealed record PasswordRegistrationInput(string Email, string Password);

/// <summary>
/// Sin <c>OverridePropertyName</c> (D10): los errores llegan con el nombre PascalCase de la
/// propiedad ("Email", "Password"), que es lo que el frontend lee.
/// </summary>
public sealed class PasswordRegistrationValidator : AbstractValidator<PasswordRegistrationInput>
{
    public const string InvalidEmailCode = "identity.email.invalid";

    public PasswordRegistrationValidator()
    {
        // La regla de formato del email es la del dominio: una sola definición de «válido».
        RuleFor(input => input.Email).Custom((email, context) =>
        {
            try
            {
                User.NormalizeEmail(email ?? string.Empty);
            }
            catch (IdentityDomainException exception)
            {
                context.AddFailure(new FluentValidation.Results.ValidationFailure(
                    context.PropertyPath, exception.Message)
                {
                    ErrorCode = InvalidEmailCode,
                });
            }
        });
        RuleFor(input => input.Password).QepPassword();
    }
}

/// <param name="CreatedUserId">Null cuando el email ya tenía cuenta y no se creó nada.</param>
public sealed record PasswordRegistrationOutcome(Guid? CreatedUserId)
{
    public bool IsExistingAccount => CreatedUserId is null;
}

public interface IPasswordRegistration
{
    /// <exception cref="ValidationException">El cuerpo no cumple las reglas; sale como 422 validation.failed.</exception>
    Task ValidateAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Hashea PRIMERO y después busca el email, para que el tiempo no delate si existe. Nuevo:
    /// crea el usuario activo con su credencial y audita <c>identity.password.registered</c>.
    /// Existente: no toca nada salvo encolar <see cref="IdentityIntegrationEvents.ExistingAccountNotice"/>.
    /// </summary>
    Task<PasswordRegistrationOutcome> ProvisionOwnerAsync(
        string email,
        string password,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Emite el token de verificación y encola <see cref="IdentityIntegrationEvents.EmailVerificationRequested"/>.</summary>
    Task RequestEmailVerificationAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class PasswordRegistrationService(
    IValidator<PasswordRegistrationInput> validator,
    IUserRepository userRepository,
    IPasswordCredentialRepository credentialRepository,
    IPasswordHasher passwordHasher,
    IEmailTokenIssuer tokenIssuer,
    IIdentityAuditRecorder auditRecorder,
    IIdentityOutbox outbox,
    IIdentityUnitOfWork unitOfWork,
    IClock clock)
    : IPasswordRegistration
{
    /// <summary>Lo tira <c>IdentityUnitOfWork</c> cuando pierde la carrera por <c>IX_users_email</c>.</summary>
    public const string EmailTakenCode = "identity.user.email_taken";

    public Task ValidateAsync(string email, string password, CancellationToken cancellationToken) =>
        validator.ValidateAndThrowAsync(new PasswordRegistrationInput(email, password), cancellationToken);

    public async Task<PasswordRegistrationOutcome> ProvisionOwnerAsync(
        string email,
        string password,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(email);

        // Antes de mirar si el email existe: ambas ramas pagan el mismo costo de hash.
        var hash = passwordHasher.Hash(password);

        var existing = await userRepository.FindByEmailAsync(normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            return await NotifyExistingAccountAsync(existing.Id.Value, correlationId, cancellationToken);
        }

        var now = clock.UtcNow;
        var user = User.CreateInvited(UserId.New(), normalizedEmail, now);
        user.Activate(now);
        userRepository.Add(user);
        credentialRepository.Add(PasswordCredential.Create(user.Id, hash, now));
        auditRecorder.Record(
            user.Id.Value,
            "identity.password.registered",
            "user",
            user.Id.ToString(),
            "success",
            now);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (IdentityDomainException exception) when (exception.Code == EmailTakenCode)
        {
            // Otro registro del mismo email ganó la carrera. La unidad de trabajo ya limpió el
            // tracker, así que esta lectura trae al ganador y nada de lo que se intentó guardar.
            var winner = await userRepository.FindByEmailAsync(normalizedEmail, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The unique email index rejected the insert but no user owns that email.");
            return await NotifyExistingAccountAsync(winner.Id.Value, correlationId, cancellationToken);
        }

        return new PasswordRegistrationOutcome(user.Id.Value);
    }

    public async Task RequestEmailVerificationAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var token = await tokenIssuer.IssueAsync(userId, EmailTokenPurpose.Verify, cancellationToken);
        outbox.Add(
            IdentityIntegrationEvents.EmailVerificationRequested,
            new EmailTokenIssuedPayload(userId, token),
            correlationId,
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<PasswordRegistrationOutcome> NotifyExistingAccountAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        outbox.Add(
            IdentityIntegrationEvents.ExistingAccountNotice,
            new ExistingAccountNoticePayload(userId),
            correlationId,
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new PasswordRegistrationOutcome(null);
    }
}
