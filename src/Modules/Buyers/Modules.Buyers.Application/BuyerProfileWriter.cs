using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

/// <summary>
/// Crea o reemplaza el perfil guardado de una cuenta, en la unidad de trabajo del llamador. Lo comparten registro, perfil y el alta
/// desde el pedido.
/// </summary>
public sealed class BuyerProfileWriter(IBuyerProfileRepository profiles)
{
    public async Task UpsertAsync(Guid accountId, BuyerProfileDto profile, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // El único punto por el que pasa todo perfil guardado (registro, PUT /profile, pedido): los códigos se normalizan acá.
        var data = profile.WithNormalizedCodes().ToData();
        var existing = await profiles.FindAsync(accountId, cancellationToken);
        if (existing is null)
        {
            profiles.Add(BuyerProfile.Create(accountId, data, now));
            return;
        }

        existing.Replace(data, now);
    }

    public async Task ClearAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var existing = await profiles.FindAsync(accountId, cancellationToken);
        if (existing is not null)
        {
            profiles.Remove(existing);
        }
    }
}
