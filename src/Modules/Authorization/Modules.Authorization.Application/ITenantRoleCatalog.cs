namespace Modules.Authorization.Application;

/// <summary>Un rol tal como lo ve un tenant: los de sistema y los suyos.</summary>
/// <param name="IsSystem">
/// Si se versiona con el codigo. Un rol de sistema se muestra pero no se edita ni se borra,
/// y el front necesita saberlo sin reimplementar la lista de claves reservadas.
/// </param>
public sealed record TenantRoleDefinition(
    string Role,
    string DisplayName,
    string Description,
    string Category,
    string RiskLevel,
    IReadOnlyCollection<string> Permissions,
    bool IsSystem,
    long Version);

/// <summary>
/// El catalogo de roles acotado a un tenant: los de sistema mas los que el tenant definio.
/// </summary>
/// <remarks>
/// Distinto de <see cref="IRoleCatalog"/>, que sigue siendo el catalogo del codigo —estatico
/// y singleton—. Este es scoped y consulta la base, porque un rol custom cambia sin reiniciar.
///
/// Que sea viable depende de un hecho del sistema: los permisos se resuelven en CADA request
/// (`ExternalClaimsTransformation` -> `AuthorizationService.ResolvePermissionsAsync`), no
/// viajan cacheados en un token. Editar un rol tiene efecto en el request siguiente y no hay
/// sesion que invalidar.
/// </remarks>
public interface ITenantRoleCatalog
{
    /// <summary>Los permisos que conceden esos roles, unidos y sin repetir.</summary>
    /// <remarks>Un rol desconocido no concede nada y no es un error: deny por defecto.</remarks>
    Task<IReadOnlyCollection<string>> PermissionsForAsync(
        Guid tenantId,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<TenantRoleDefinition>> ListRolesAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<bool> ContainsRoleAsync(
        Guid tenantId,
        string role,
        CancellationToken cancellationToken);
}
