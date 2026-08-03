namespace Modules.Identity.Application;

/// <summary>
/// Resolves an external provider subject to the internal user id, for use on every
/// authenticated request (the API is a resource server validating the provider's
/// token; the token's <c>sub</c> is the provider subject, never the internal id).
/// </summary>
public interface IProviderIdentityResolver
{
    Task<Guid?> ResolveUserIdAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken);
}
