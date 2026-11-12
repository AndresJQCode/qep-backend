using Modules.Buyers.Application;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>Buyers → Store: lo que el pedido necesita de las cuentas de comprador (D7), sin que Store vea Buyers.</summary>
internal sealed class StoreBuyerAccounts(
    IBuyerPasswordHasher hasher,
    ReplaceBuyerProfileFromOrder profiles,
    BuyerSessions sessions,
    IBuyerAccountRepository accounts) : IStoreBuyerAccounts
{
    public string HashPassword(string password) => hasher.Hash(password);

    public Task<bool> ReplaceProfileAsync(
        string host,
        string? sessionToken,
        PublicBillingInput billing,
        PublicShippingInput? shipping,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(billing);
        var profile = new BuyerProfileDto(
            new BuyerBillingDto(
                billing.Name, billing.IdentificationType, billing.IdentificationNumber, billing.Phone, billing.Email,
                billing.Address, billing.Country, billing.DepartmentId, billing.CityId, billing.CityName),
            shipping is null
                ? null
                : new BuyerShippingDto(
                    shipping.RecipientName, shipping.Phone, shipping.Address, shipping.Country, shipping.DepartmentId,
                    shipping.CityId, shipping.CityName),
            ShippingSameAsBilling: shipping is null);
        return profiles.ReplaceAsync(host, sessionToken, profile, cancellationToken);
    }

    // Como ReplaceBuyerProfileFromOrder: la sesión del host y luego la cuenta, que pudo borrarse con la sesión todavía viva.
    public async Task<Guid?> ResolveAccountIdAsync(string host, string? sessionToken, CancellationToken cancellationToken)
    {
        var session = await sessions.TryResolveAsync(host, sessionToken, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var account = await accounts.FindAsync(session.AccountId, cancellationToken);
        return account?.Id;
    }
}
