using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public sealed record PasswordLoginInput(string Email, string Password);

/// <summary>
/// Only "not empty" on purpose: length or format rules would tell an attacker what a valid
/// credential looks like. The email format is not checked here either; a malformed one is treated
/// as an unknown account by the service.
/// </summary>
public sealed class PasswordLoginValidator : AbstractValidator<PasswordLoginInput>
{
    public PasswordLoginValidator()
    {
        RuleFor(input => input.Email).NotEmpty();
        // Not NotEmpty(): it also rejects whitespace-only, and QepPassword accepts such a password at
        // register/reset/accept (NIST), so the account would be locked out.
        RuleFor(input => input.Password).Must(password => !string.IsNullOrEmpty(password))
            .WithErrorCode("NotEmptyValidator")
            .WithMessage("'Password' must not be empty.");
    }
}

public interface IPasswordLogin
{
    /// <summary>
    /// Verifies the credentials. An unknown email, a missing credential and a wrong password are the
    /// same 401. A correct password on an unverified email is a 403 and re-issues the verify token.
    /// </summary>
    /// <exception cref="RequestUnauthorizedException"><c>identity.credentials.invalid</c>.</exception>
    /// <exception cref="RequestForbiddenException"><c>identity.email.not_verified</c>.</exception>
    Task<AuthenticatedUser> LoginAsync(
        string email,
        string password,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class PasswordLoginService(
    IUserRepository users,
    IPasswordCredentialRepository credentials,
    PasswordCredentialVerifier verifier,
    IEmailTokenIssuer tokens,
    IIdentityOutbox outbox,
    IIdentityAuditRecorder audit,
    IIdentityUnitOfWork unitOfWork,
    IValidator<PasswordLoginInput> validator,
    IClock clock)
    : IPasswordLogin
{
    public const string InvalidCredentialsCode = "identity.credentials.invalid";
    public const string EmailNotVerifiedCode = "identity.email.not_verified";

    public async Task<AuthenticatedUser> LoginAsync(
        string email,
        string password,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(new PasswordLoginInput(email, password), cancellationToken);

        var user = await FindUserAsync(email, cancellationToken);
        var credential = user is null
            ? null
            : await credentials.FindByUserAsync(user.Id.Value, cancellationToken);

        // Always verify, even without a user or a credential: the hasher runs against the dummy hash
        // so the response time does not tell the three failure causes apart.
        var verified = verifier.Verify(credential, password);
        var now = clock.UtcNow;

        if (user is null || !verified)
        {
            // Never the raw email, the password or the hash: for an unknown account the entry has no
            // actor and the resource is the literal "unknown".
            audit.Record(
                user?.Id.Value ?? Guid.Empty,
                "identity.password.login.failed",
                "user",
                user?.Id.ToString() ?? "unknown",
                "failure",
                now,
                user is null ? AuditActorType.System : AuditActorType.Human);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials();
        }

        if (user.EmailVerifiedAt is null)
        {
            // The password was right, so telling the owner to verify leaks nothing. Re-issuing the
            // token invalidates the previous one. A pending re-hash is saved with this too.
            var token = await tokens.IssueAsync(user.Id.Value, EmailTokenPurpose.Verify, cancellationToken);
            outbox.Add(
                IdentityIntegrationEvents.EmailVerificationRequested,
                new EmailTokenIssuedPayload(user.Id.Value, token),
                correlationId,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new RequestForbiddenException(EmailNotVerifiedCode, "The email is not verified.");
        }

        user.Activate(now);
        audit.Record(
            user.Id.Value,
            "identity.password.login.succeeded",
            "user",
            user.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new AuthenticatedUser(user.Id.Value, user.Email);
    }

    private async Task<User?> FindUserAsync(string email, CancellationToken cancellationToken)
    {
        string normalized;
        try
        {
            normalized = User.NormalizeEmail(email);
        }
        catch (IdentityDomainException)
        {
            // A malformed email cannot belong to any account: same outcome as an unknown one.
            return null;
        }

        return await users.FindByEmailAsync(normalized, cancellationToken);
    }

    private static RequestUnauthorizedException InvalidCredentials() =>
        new(InvalidCredentialsCode, "Invalid credentials.");
}
