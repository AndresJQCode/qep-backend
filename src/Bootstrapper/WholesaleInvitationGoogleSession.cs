using BuildingBlocks.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Bootstrapper;

/// <summary>
/// El paso de Identity de <c>POST /api/v1/wholesale-invitations/{token}/session</c> con Google (D-4), sacado del endpoint para
/// probarlo sin contenedores. La sesión se abre para exactamente el usuario que se encontró o se aprovisionó:
/// <list type="number">
/// <item>Primero se vincula (<see cref="IProviderLinking"/>): si el subject ya tiene usuario, o el correo verificado ya tiene uno,
/// ése es el usuario y no se aprovisiona nada.</item>
/// <item>Sólo si ninguno de los dos tiene usuario (<c>invitation_required</c>) se aprovisiona por el correo invitado y se vuelve
/// a vincular, que ahora encuentra a ese usuario por el correo.</item>
/// <item>El usuario de la sesión tiene que ser el del correo invitado: un subject ya vinculado a otra cuenta es
/// <c>wholesale.invitation.email_mismatch</c>, sin crear nada.</item>
/// </list>
/// <para>Antes se aprovisionaba primero y se ignoraba el id: con el subject vinculado a otra cuenta quedaba un usuario sin
/// membresía y la sesión se abría para la otra. Identity guarda con su propio DbContext, fuera de la transacción del lock de
/// Tenancy, así que lo aprovisionado no se puede deshacer con un rollback: la atomicidad sale del orden, que aprovisiona sólo
/// cuando ya se sabe que ese usuario es el que se va a usar. Lo único que puede dejarlo suelto es una falla de la base entre
/// aprovisionar y vincular, el mismo caso que invitar a un miembro (<c>InviteMember</c>).</para>
/// </summary>
public sealed class WholesaleInvitationGoogleSession(
    ITenancyUnitOfWork tenancyUnitOfWork,
    IIdentityProvisioning identityProvisioning,
    IProviderLinking providerLinking,
    IUserDirectory userDirectory)
{
    // El único proveedor externo (ADR 0014), igual que en AuthSessionEndpoints.
    public const string Provider = "google";

    private const string NoUserForIdentity = "invitation_required";

    /// <param name="invitedEmail">El correo de la invitación.</param>
    /// <param name="verifiedEmail">El correo verificado del token de Google, que quien llama ya comparó con el invitado.</param>
    /// <returns>El usuario de la sesión, o la denegación de Identity (un 403 genérico).</returns>
    public async Task<ProviderLinkOutcome> OpenAsync(
        string invitedEmail,
        string subject,
        string verifiedEmail,
        CancellationToken cancellationToken)
    {
        await using var lifecycle = await tenancyUnitOfWork.BeginUserLifecycleScopeAsync(invitedEmail, cancellationToken);

        var outcome = await providerLinking.LinkAndActivateAsync(Provider, subject, verifiedEmail, true, cancellationToken);
        if (outcome.DenialReason == NoUserForIdentity)
        {
            var provisioned = await identityProvisioning.GetOrProvisionInvitedUserAsync(invitedEmail, cancellationToken);
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

        var userEmail = await userDirectory.GetEmailAsync(outcome.UserId!.Value, cancellationToken);
        if (!string.Equals(userEmail?.Trim(), invitedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw EmailMismatch();
        }

        await lifecycle.CommitAsync(cancellationToken);
        return outcome;
    }

    public static RequestForbiddenException EmailMismatch() =>
        new(WholesaleErrorCodes.InvitationEmailMismatch, "The invitation was sent to another email address.");
}
