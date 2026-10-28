using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public interface IPasswordCredentials
{
    Task<bool> HasPasswordAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Without a credential: validates the password (<c>errors.Password</c>) and creates it. With one:
    /// verifies it through <see cref="PasswordCredentialVerifier"/> (which owns the re-hash) and never
    /// replaces it. In both cases the invitation link proves the mailbox, so the email is marked
    /// verified and the user activated.
    /// </summary>
    /// <exception cref="RequestUnauthorizedException"><c>identity.credentials.invalid</c>.</exception>
    Task<AuthenticatedUser> AcceptInvitationAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken);
}

public sealed class PasswordCredentialsService(
    IUserRepository users,
    IPasswordCredentialRepository credentials,
    IPasswordHasher hasher,
    PasswordCredentialVerifier verifier,
    IIdentityUnitOfWork unitOfWork,
    IValidator<NewPasswordInput> validator,
    IClock clock)
    : IPasswordCredentials
{
    public async Task<bool> HasPasswordAsync(Guid userId, CancellationToken cancellationToken) =>
        await credentials.FindByUserAsync(userId, cancellationToken) is not null;

    public async Task<AuthenticatedUser> AcceptInvitationAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken)
            ?? throw new RequestUnauthorizedException(
                PasswordLoginService.InvalidCredentialsCode,
                "Invalid credentials.");
        var now = clock.UtcNow;

        var credential = await credentials.FindByUserAsync(userId, cancellationToken);
        if (credential is null)
        {
            await validator.ValidateAndThrowAsync(new NewPasswordInput(password), cancellationToken);
            credentials.Add(PasswordCredential.Create(user.Id, hasher.Hash(password), now));
        }
        else if (!verifier.Verify(credential, password))
        {
            throw new RequestUnauthorizedException(
                PasswordLoginService.InvalidCredentialsCode,
                "Invalid credentials.");
        }

        user.MarkEmailVerified(now);
        user.Activate(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new AuthenticatedUser(user.Id.Value, user.Email);
    }
}
