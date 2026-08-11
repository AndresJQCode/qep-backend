namespace Modules.Identity.Application;

/// <summary>
/// Resuelve el subject de un proveedor externo al id interno de usuario, para usar en cada
/// request autenticado (la API es un resource server que valida el token del proveedor; el
/// <c>sub</c> del token es el subject del proveedor, nunca el id interno).
/// </summary>
public interface IProviderIdentityResolver
{
    Task<Guid?> ResolveUserIdAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken);
}
