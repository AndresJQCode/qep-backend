namespace Modules.Store.Application;

/// <summary>
/// Lo que el pedido necesita de las cuentas de comprador, sin ver Buyers (D2): el adaptador vive en Bootstrapper.
/// </summary>
public interface IStoreBuyerAccounts
{
    /// <summary>El hash con el hasher de las cuentas; se calcula antes de guardar nada para que la contraseña nunca se persista.</summary>
    string HashPassword(string password);

    /// <summary>
    /// Reemplaza el perfil guardado del comprador de la sesión con los bloques del pedido (<c>shippingSameAsBilling</c> = sin envío).
    /// <c>false</c> si la sesión no sirve en ese host: ausente, vencida, de otro host o de una cuenta borrada.
    /// </summary>
    /// <param name="host">El host de la tienda ya normalizado.</param>
    Task<bool> ReplaceProfileAsync(
        string host,
        string? sessionToken,
        PublicBillingInput billing,
        PublicShippingInput? shipping,
        CancellationToken cancellationToken);
}
