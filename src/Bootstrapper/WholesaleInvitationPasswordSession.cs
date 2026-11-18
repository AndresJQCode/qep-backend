using FluentValidation;
using Modules.Identity.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// El paso de Identity de <c>POST /api/v1/wholesale-invitations/{token}/session-with-password</c> (D-4), sacado del endpoint para
/// probarlo sin contenedores. Ruling W17: la contraseña se valida con las reglas del registro (<see cref="NewPasswordValidator"/>,
/// 422 <c>errors.Password</c>) <b>antes</b> de tocar Identity, así una contraseña débil nunca deja un usuario creado y sin
/// membresía (la limpieza de huérfanos sólo corre por eventos de membresía). Después, bajo el lock de ciclo de vida del usuario:
/// aprovisionar por el correo invitado, crear o verificar la credencial y confirmar.
/// <para>Se valida siempre, también si el usuario ya tiene contraseña: las reglas son sólo de largo y una credencial existente ya
/// las cumplió al crearse.</para>
/// </summary>
public sealed class WholesaleInvitationPasswordSession(
    ITenancyUnitOfWork tenancyUnitOfWork,
    IIdentityProvisioning identityProvisioning,
    IPasswordCredentials passwordCredentials,
    IValidator<NewPasswordInput> passwordValidator)
{
    /// <param name="invitedEmail">El correo de la invitación, nunca uno del cuerpo.</param>
    public async Task<AuthenticatedUser> OpenAsync(string invitedEmail, string password, CancellationToken cancellationToken)
    {
        await passwordValidator.ValidateAndThrowAsync(new NewPasswordInput(password), cancellationToken);

        await using var lifecycle = await tenancyUnitOfWork.BeginUserLifecycleScopeAsync(invitedEmail, cancellationToken);
        var userId = await identityProvisioning.GetOrProvisionInvitedUserAsync(invitedEmail, cancellationToken);
        var user = await passwordCredentials.AcceptInvitationAsync(userId, password, cancellationToken);
        await lifecycle.CommitAsync(cancellationToken);
        return user;
    }
}
