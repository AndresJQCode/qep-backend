namespace Modules.Tenancy.Application;

/// <summary>Un tenant que el usuario administra como owner: el id y el nombre que se muestran al elegir cuál vincular.</summary>
public sealed record OwnedTenant(Guid TenantId, string DisplayName);

/// <summary>
/// Spec 2026-11-17 (mayorista): los tenants que un usuario puede vincular como distribuidor al aceptar una invitación. Sólo
/// los que administra como owner, no los de cualquier membresía: vincular prende módulos y da de alta un proveedor en ese
/// tenant, y eso lo decide su dueño.
/// </summary>
public interface ITenantOwnership
{
    /// <returns>Los tenants <c>Active</c> cuyo <c>OwnerMembershipId</c> es una membresía <c>Active</c> de
    /// <paramref name="userId"/>, ordenados por nombre.</returns>
    Task<IReadOnlyList<OwnedTenant>> ListOwnedActiveTenantsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Spec 2026-11-19 (onboarding por link): los tenants que el usuario puede vincular. Además de los suyos como owner,
    /// los que administra como miembro <c>Active</c> con el rol de sistema <c>admin</c>; un rol personalizado no cuenta.
    /// </summary>
    /// <returns>Los tenants <c>Active</c> que cumplen, ordenados por nombre como
    /// <see cref="ListOwnedActiveTenantsAsync"/>.</returns>
    Task<IReadOnlyList<OwnedTenant>> ListAdministeredActiveTenantsAsync(Guid userId, CancellationToken cancellationToken);
}
