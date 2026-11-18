using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-11-17 (mayorista), D-5: prende los módulos que le falten a un tenant porque lo pidió otro módulo (vincular un
/// tenant existente como distribuidor), no un operador. Es una escritura del sistema: no revisa permisos; quien llama ya
/// decidió que el actor puede. Mismo cuerpo que la consola (<see cref="ChangeTenantModulesHandler"/>): candado del tenant,
/// <see cref="TenantModuleChangeBatch"/> con motivo <c>contract</c>, historial con <c>note</c>, auditoría
/// <c>tenancy.tenant_modules.changed</c> y la versión de acceso subida antes y después del commit.
/// </summary>
public interface ITenantModuleGrants
{
    /// <returns>Las claves que prendió, en el orden de <see cref="TenantModuleKeys.All"/>; vacía, y sin escribir nada, si ya
    /// estaban todas activas.</returns>
    /// <exception cref="TenantDomainException"><c>tenancy.modules.inconsistent_dependencies</c> si el lote deja un módulo
    /// activo sin sus dependencias: quien llama pide el cierre completo.</exception>
    Task<IReadOnlyList<TenantModuleKey>> EnsureActiveAsync(
        TenantId tenantId,
        IReadOnlyList<TenantModuleKey> keys,
        Guid actorUserId,
        string note,
        CancellationToken cancellationToken);
}
