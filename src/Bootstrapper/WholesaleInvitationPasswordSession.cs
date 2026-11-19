using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// El paso de Identity de <c>POST /api/v1/wholesale-invitations/{token}/session-with-password</c> (D-4, spec 2026-11-19 §4),
/// sacado del endpoint para probarlo sin contenedores. El link ya no prueba el buzón: identifica la invitación y nada más.
/// <list type="number">
/// <item>Ruling W17: la contraseña se valida con las reglas del registro (<see cref="NewPasswordValidator"/>, 422
/// <c>errors.Password</c>) <b>antes</b> de tocar Identity, así una contraseña débil nunca deja un usuario creado y sin
/// membresía.</item>
/// <item>Bajo el lock de ciclo de vida del correo: si ese correo ya tiene usuario <b>con contraseña</b>, se verifica la
/// contraseña y nada más (un inicio de sesión).</item>
/// <item>Sin usuario o sin contraseña, siempre hacen falta la versión de los términos y un código válido del correo
/// (<see cref="IEmailChallenges"/>). Sin esto, el link más el correo de otra persona bastarían para fijarle una contraseña a su
/// cuenta. La versión se mira antes de gastar el código; un código errado o ausente es 401
/// <c>identity.credentials.invalid</c>, sin crear ni cambiar nada.</item>
/// <item>Después se aprovisiona, se crea la credencial y se registran los términos. <see cref="ITermsAcceptances.RecordAsync"/>
/// se confirma en su propia conexión y tiene FK a <c>identity.users</c>: va <b>después</b> de que el usuario quedó guardado
/// (<see cref="IIdentityProvisioning"/> y <see cref="IPasswordCredentials"/> guardan en el acto).</item>
/// </list>
/// <para>Si algo falla después de gastar el código, el usuario ya existe con su contraseña y el reintento es un inicio de sesión
/// normal; los términos que falten los pide <c>accept</c> antes de crear el tenant.</para>
/// </summary>
public sealed class WholesaleInvitationPasswordSession(
    ITenancyUnitOfWork tenancyUnitOfWork,
    IIdentityProvisioning identityProvisioning,
    IPasswordCredentials passwordCredentials,
    IUserDirectory userDirectory,
    IEmailChallenges emailChallenges,
    ITermsAcceptances termsAcceptances,
    TermsOptions termsOptions,
    IValidator<NewPasswordInput> passwordValidator)
{
    /// <param name="email">El correo que escribió el distribuidor; puede no ser el invitado (spec 2026-11-19 §6).</param>
    /// <param name="code">El código que llegó a ese correo; sólo se mira si la cuenta no tiene contraseña.</param>
    /// <param name="termsVersion">La versión de los términos que aceptó; sólo se mira si la cuenta no tiene contraseña.</param>
    public async Task<AuthenticatedUser> OpenAsync(
        string email,
        string password,
        string? code,
        string? termsVersion,
        CancellationToken cancellationToken)
    {
        await passwordValidator.ValidateAndThrowAsync(new NewPasswordInput(password), cancellationToken);

        await using var lifecycle = await tenancyUnitOfWork.BeginUserLifecycleScopeAsync(email, cancellationToken);

        var existing = await userDirectory.FindUserIdByEmailAsync(email, cancellationToken);
        if (existing is { } userWithAccount && await passwordCredentials.HasPasswordAsync(userWithAccount, cancellationToken))
        {
            var signedIn = await passwordCredentials.AcceptInvitationAsync(userWithAccount, password, cancellationToken);
            await lifecycle.CommitAsync(cancellationToken);
            return signedIn;
        }

        EnsureCurrentTermsVersion(termsVersion);
        if (string.IsNullOrWhiteSpace(code) || !await emailChallenges.TryConsumeAsync(email, code, cancellationToken))
        {
            throw new RequestUnauthorizedException(PasswordLoginService.InvalidCredentialsCode, "Invalid credentials.");
        }

        var userId = await identityProvisioning.GetOrProvisionInvitedUserAsync(email, cancellationToken);
        var user = await passwordCredentials.AcceptInvitationAsync(userId, password, cancellationToken);
        await termsAcceptances.RecordAsync(userId, termsVersion!, cancellationToken);
        await lifecycle.CommitAsync(cancellationToken);
        return user;
    }

    /// <summary>
    /// Lo mismo que exige <see cref="ITermsAcceptances.RecordAsync"/>, mirado antes de gastar el código: una versión vieja en la
    /// pantalla no debe dejar el código usado y la cuenta a medias. Sin versión en el cuerpo, 422 con el campo; sin versión
    /// configurada, falla cerrado.
    /// </summary>
    private void EnsureCurrentTermsVersion(string? termsVersion)
    {
        if (string.IsNullOrWhiteSpace(termsVersion))
        {
            throw new ValidationException(
                [new ValidationFailure("TermsVersion", "The terms version is required.") { ErrorCode = "NotEmptyValidator" }]);
        }

        var current = termsOptions.CurrentVersion?.Trim();
        if (string.IsNullOrEmpty(current))
        {
            throw new IdentityDomainException(TermsAcceptanceService.UnavailableCode, "No terms version is configured.");
        }

        if (!string.Equals(termsVersion.Trim(), current, StringComparison.Ordinal))
        {
            throw new IdentityDomainException(
                TermsAcceptanceService.VersionMismatchCode, "The accepted terms version is not the current one.");
        }
    }
}
