using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

/// <summary>
/// El único lugar donde un proveedor externo se vincula a un usuario existente (login y registro
/// por Google). Existe por una regla de seguridad: si el usuario nunca verificó su email, quien
/// pudo haberlo pre-registrado con contraseña no es necesariamente su dueño. Cuando el proveedor
/// prueba que el email es del que entra, la credencial de contraseña se borra y todos los tokens
/// de email sin usar se invalidan; sin eso el atacante conserva el acceso.
/// </summary>
public interface IVerifiedProviderLinker
{
    /// <summary>
    /// Vincula el proveedor. Si <see cref="User.EmailVerifiedAt"/> era null, borra la credencial de
    /// contraseña e invalida TODOS los tokens no usados; después marca el email como verificado. No
    /// guarda: los cambios entran en la unidad de trabajo del llamador.
    /// </summary>
    Task LinkAsync(User user, string provider, string subject, CancellationToken cancellationToken);
}

public sealed class VerifiedProviderLinker(
    IPasswordCredentialRepository credentials,
    IEmailTokenRepository tokens,
    IClock clock) : IVerifiedProviderLinker
{
    public async Task LinkAsync(
        User user,
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        user.LinkProvider(provider, subject, now);

        if (user.EmailVerifiedAt is null)
        {
            var credential = await credentials.FindByUserAsync(user.Id.Value, cancellationToken);
            if (credential is not null)
            {
                credentials.Remove(credential);
            }

            await tokens.InvalidateUnusedAsync(user.Id.Value, null, now, cancellationToken);
            user.MarkEmailVerified(now);
        }
    }
}
