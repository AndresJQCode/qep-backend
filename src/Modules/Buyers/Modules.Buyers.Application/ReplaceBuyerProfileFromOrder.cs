using BuildingBlocks.Application;

namespace Modules.Buyers.Application;

/// <summary>
/// "Actualizar mis datos guardados" desde el pedido: reemplaza el perfil del comprador de la sesión con los bloques del pedido. Corre
/// después del commit del pedido (Store lo llama por <c>IStoreBuyerAccounts</c>), así que nunca lo hace fallar. Una sesión que no
/// sirve (ausente, desconocida, vencida, de otro host o de una cuenta borrada) devuelve <c>false</c> y no toca nada. No cambia el
/// email de ingreso, y el perfil no se juzga contra la tienda: el pedido ya lo hizo.
/// </summary>
public sealed class ReplaceBuyerProfileFromOrder(
    BuyerSessions sessions,
    IBuyerAccountRepository accounts,
    BuyerProfileWriter writer,
    IBuyersUnitOfWork unitOfWork,
    IClock clock)
{
    /// <param name="host">El host de la tienda ya normalizado: la sesión tiene que ser de ese host.</param>
    public async Task<bool> ReplaceAsync(
        string host, string? sessionToken, BuyerProfileDto profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var session = await sessions.TryResolveAsync(host, sessionToken, cancellationToken);
        if (session is null)
        {
            return false;
        }

        var account = await accounts.FindAsync(session.AccountId, cancellationToken);
        if (account is null)
        {
            return false;
        }

        await writer.UpsertAsync(account.Id, profile, clock.UtcNow, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
