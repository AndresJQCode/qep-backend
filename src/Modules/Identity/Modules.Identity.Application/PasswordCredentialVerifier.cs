using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

/// <summary>
/// The single place of the rule "verify and re-hash if the hasher asks for it". Login and the
/// invitation acceptance both go through it, so the rule cannot drift between them.
/// </summary>
public sealed class PasswordCredentialVerifier(IPasswordHasher hasher, IClock clock)
{
    /// <summary>
    /// Always calls the hasher, even without a credential: a null hash runs against the dummy one, so
    /// "no credential" costs the same as "wrong password". A <c>SuccessRehashNeeded</c> outcome
    /// replaces the hash on the entity; this method does not save, the caller's unit of work does.
    /// </summary>
    /// <returns>True only with a credential and a Success / SuccessRehashNeeded outcome.</returns>
    public bool Verify(PasswordCredential? credential, string password)
    {
        var outcome = hasher.Verify(credential?.Hash, password);
        if (credential is null)
        {
            return false;
        }

        switch (outcome)
        {
            case PasswordVerificationOutcome.Success:
                return true;
            case PasswordVerificationOutcome.SuccessRehashNeeded:
                credential.Replace(hasher.Hash(password), clock.UtcNow);
                return true;
            default:
                return false;
        }
    }
}
