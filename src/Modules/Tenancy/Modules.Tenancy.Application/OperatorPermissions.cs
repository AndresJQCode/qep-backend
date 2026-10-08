namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §2 (O6, D1). Núcleo (<c>RequiredModules: []</c>): el operador no se bloquea a sí
/// mismo apagando módulos. Los lleva el rol de fábrica <c>admin</c>, que es global; el filtro de
/// operador (<c>OperatorPermissionFilter</c>, Authorization) es lo que los deja vivos sólo en el
/// tenant operador. Sólo viven en roles de sistema (D11).
/// </summary>
public static class OperatorPermissions
{
    public const string TenantsRead = "operator.tenants.read";
    public const string ModulesManage = "operator.modules.manage";
    public const string TenantsManage = "operator.tenants.manage";
}
