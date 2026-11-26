using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// La prueba de buzón de la invitación (spec 2026-11-24): un secreto aleatorio, distinto del token, que sólo viaja en el link del
/// correo. Quien lo devuelve demuestra que controla el buzón invitado. En la base queda sólo su SHA-256, como el token.
/// </summary>
public static class DistributorEmailProofs
{
    /// <summary>La prueba en claro, para el evento del correo, y su hash, para el dominio.</summary>
    public static (string Proof, string Hash) Issue()
    {
        var proof = InvitationTokens.Generate();
        return (proof, InvitationTokens.HashOf(proof));
    }

    /// <summary>
    /// Si <paramref name="emailProof"/> es la prueba vigente de la invitación y <paramref name="email"/> el correo invitado.
    /// Sin prueba, no.
    /// </summary>
    public static bool Proves(Distributor distributor, string? emailProof, string? email) =>
        !string.IsNullOrWhiteSpace(emailProof)
        && distributor.ProvesInvitedMailbox(InvitationTokens.HashOf(emailProof), email);
}
