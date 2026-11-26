using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// El paso de Identity de <c>POST /api/v1/wholesale-invitations/{token}/session-with-password</c> (D-4, spec 2026-11-19 §4),
/// sacado del endpoint para probarlo sin contenedores. El link por sí solo no prueba el buzón: el del correo sí puede, porque
/// lleva una prueba propia (spec 2026-11-24); el que se copia y el de WhatsApp, no. Quien lo decide es Wholesale
/// (<c>DistributorEmailProofs</c>) y aquí sólo llega el resultado, <c>emailProven</c>.
/// <list type="number">
/// <item>Ruling W17: la contraseña se valida con las reglas del registro (<see cref="NewPasswordValidator"/>, 422
/// <c>errors.Password</c>) <b>antes</b> de tocar Identity, así una contraseña débil nunca deja un usuario creado y sin
/// membresía.</item>
/// <item>Bajo el lock de ciclo de vida del correo: si ese correo ya tiene usuario <b>con contraseña</b>, es un inicio de sesión
/// y va por <see cref="IPasswordLogin"/>: la misma auditoría de intentos y el mismo 403 <c>identity.email.not_verified</c> con
/// el correo sin verificar. La prueba de buzón no cuenta aquí: ni cambia la contraseña ni marca el correo como
/// verificado.</item>
/// <item>Sin usuario o sin contraseña, hacen falta el buzón probado por el link <b>o</b> un código válido del correo
/// (<see cref="IEmailChallenges"/>), y siempre la versión de los términos. Sin esto, el link más el correo de otra persona
/// bastarían para fijarle una contraseña a su cuenta. Con el buzón probado no se gasta ningún código.</item>
/// <item>Revisión del bloque C: sin buzón probado ni código válido la respuesta no puede decir si el correo tiene cuenta o
/// contraseña. Pasa por el mismo login (que falla: no hay credencial), así que es el mismo 401
/// <c>identity.credentials.invalid</c>, con el mismo hash y la misma auditoría que una contraseña errada. Por lo mismo la versión
/// de los términos se mira <b>después</b> de la prueba o de gastar un código válido, que sólo tiene el dueño del buzón: mirarla
/// antes hacía que un código inventado sin términos diera 422 sólo para los correos sin contraseña. El costo es que una versión
/// vieja en la pantalla gasta el código (sin crear nada) y hay que pedir otro.</item>
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
    IPasswordLogin passwordLogin,
    IUserDirectory userDirectory,
    IEmailChallenges emailChallenges,
    ITermsAcceptances termsAcceptances,
    TermsOptions termsOptions,
    IValidator<NewPasswordInput> passwordValidator)
{
    /// <param name="email">El correo que escribió el distribuidor; puede no ser el invitado (spec 2026-11-19 §6).</param>
    /// <param name="code">El código que llegó a ese correo; sólo se mira si la cuenta no tiene contraseña.</param>
    /// <param name="emailProven">Si el link del correo ya probó que quien llama controla el buzón de <paramref name="email"/>
    /// (spec 2026-11-24); sólo se mira si la cuenta no tiene contraseña, y entonces el código no hace falta.</param>
    /// <param name="termsVersion">La versión de los términos que aceptó; sólo se mira si la cuenta no tiene contraseña.</param>
    /// <param name="correlationId">La del request, para el aviso de verificación que reenvía el login.</param>
    public async Task<AuthenticatedUser> OpenAsync(
        string email,
        string password,
        string? code,
        bool emailProven,
        string? termsVersion,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await passwordValidator.ValidateAndThrowAsync(new NewPasswordInput(password), cancellationToken);

        await using var lifecycle = await tenancyUnitOfWork.BeginUserLifecycleScopeAsync(email, cancellationToken);

        var existing = await userDirectory.FindUserIdByEmailAsync(email, cancellationToken);
        if (existing is { } userWithAccount && await passwordCredentials.HasPasswordAsync(userWithAccount, cancellationToken))
        {
            var signedIn = await passwordLogin.LoginAsync(email, password, correlationId, cancellationToken);
            await lifecycle.CommitAsync(cancellationToken);
            return signedIn;
        }

        if (!emailProven
            && (string.IsNullOrWhiteSpace(code) || !await emailChallenges.TryConsumeAsync(email, code, cancellationToken)))
        {
            // Sin credencial el login siempre falla: el mismo 401, hash y auditoría que una contraseña errada.
            await passwordLogin.LoginAsync(email, password, correlationId, cancellationToken);
            throw new RequestUnauthorizedException(PasswordLoginService.InvalidCredentialsCode, "Invalid credentials.");
        }

        EnsureCurrentTermsVersion(termsVersion);

        var userId = await identityProvisioning.GetOrProvisionInvitedUserAsync(email, cancellationToken);
        var user = await passwordCredentials.AcceptInvitationAsync(userId, password, cancellationToken);
        await termsAcceptances.RecordAsync(userId, termsVersion!, cancellationToken);
        await lifecycle.CommitAsync(cancellationToken);
        return user;
    }

    /// <summary>
    /// Lo mismo que exige <see cref="ITermsAcceptances.RecordAsync"/>, mirado antes de aprovisionar: una versión vieja en la
    /// pantalla no deja una cuenta con contraseña y sin términos. Sin versión en el cuerpo, 422 con el campo; sin versión
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
