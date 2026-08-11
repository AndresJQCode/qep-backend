namespace Modules.Identity.Application;

/// <summary>
/// Contrato publicado entre módulos del módulo Identity. Otros módulos dependen de esta
/// interfaz (no de los internos de Identity) para obtener un id interno de usuario a partir
/// de un email invitado. Devuelve un <see cref="Guid"/> pelado para que los llamadores
/// referencien usuarios sólo por id, según el ADR 0016.
/// </summary>
public interface IIdentityProvisioning
{
    /// <summary>
    /// Devuelve el id del usuario de <paramref name="email"/>, creando un usuario invitado
    /// si no existe. Es idempotente: llamadas repetidas para el mismo email devuelven el
    /// mismo id de usuario y nunca crean duplicados.
    /// </summary>
    Task<Guid> GetOrProvisionInvitedUserAsync(string email, CancellationToken cancellationToken);
}
