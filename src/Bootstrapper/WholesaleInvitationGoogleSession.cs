using Modules.Identity.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// El paso de Identity de <c>POST /api/v1/wholesale-invitations/{token}/session</c> con Google (D-4), sacado del endpoint para
/// probarlo sin contenedores. Spec 2026-11-19: el link ya no exige el correo invitado; la cuenta es la del <b>correo verificado de
/// Google</b>, y si es otro reemplaza al invitado al aceptar (§6). La sesión se abre para exactamente el usuario que se encontró o
/// se aprovisionó:
/// <list type="number">
/// <item>Primero se vincula (<see cref="IProviderLinking"/>): si el subject ya tiene usuario, o el correo verificado ya tiene uno,
/// ése es el usuario y no se aprovisiona nada.</item>
/// <item>Sólo si ninguno de los dos tiene usuario (<c>invitation_required</c>) se aprovisiona por el correo verificado y se vuelve
/// a vincular, que ahora encuentra a ese usuario por el correo.</item>
/// </list>
/// <para>Identity guarda con su propio DbContext, fuera de la transacción del lock de Tenancy, así que lo aprovisionado no se
/// puede deshacer con un rollback: la atomicidad sale del orden, que aprovisiona sólo cuando ya se sabe que ese usuario es el que
/// se va a usar. El lock es por el correo verificado, el mismo que se aprovisiona.</para>
/// </summary>
public sealed class WholesaleInvitationGoogleSession(
    ITenancyUnitOfWork tenancyUnitOfWork,
    IIdentityProvisioning identityProvisioning,
    IProviderLinking providerLinking)
{
    // El único proveedor externo (ADR 0014), igual que en AuthSessionEndpoints.
    public const string Provider = "google";

    private const string NoUserForIdentity = "invitation_required";

    /// <param name="verifiedEmail">El correo del token de Google, que quien llama ya comprobó verificado.</param>
    /// <returns>El usuario de la sesión, o la denegación de Identity (un 403 genérico).</returns>
    public async Task<ProviderLinkOutcome> OpenAsync(
        string verifiedEmail,
        string subject,
        CancellationToken cancellationToken)
    {
        await using var lifecycle = await tenancyUnitOfWork.BeginUserLifecycleScopeAsync(verifiedEmail, cancellationToken);

        var outcome = await providerLinking.LinkAndActivateAsync(Provider, subject, verifiedEmail, true, cancellationToken);
        if (outcome.DenialReason == NoUserForIdentity)
        {
            var provisioned = await identityProvisioning.GetOrProvisionInvitedUserAsync(verifiedEmail, cancellationToken);
            outcome = await providerLinking.LinkAndActivateAsync(Provider, subject, verifiedEmail, true, cancellationToken);
            if (outcome.UserId != provisioned)
            {
                throw new InvalidOperationException("The provisioned user was not the one linked to the Google identity.");
            }
        }

        if (outcome.IsDenied)
        {
            return outcome;
        }

        await lifecycle.CommitAsync(cancellationToken);
        return outcome;
    }
}
