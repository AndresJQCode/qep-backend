namespace Modules.Identity.Application;

public sealed class ProviderIdentityResolver(IUserRepository userRepository)
    : IProviderIdentityResolver
{
    public async Task<Guid?> ResolveUserIdAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByProviderAsync(provider, subject, cancellationToken);
        return user?.Id.Value;
    }
}
