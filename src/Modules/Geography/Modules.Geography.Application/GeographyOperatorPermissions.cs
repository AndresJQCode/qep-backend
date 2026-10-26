namespace Modules.Geography.Application;

/// <summary>
/// Spec 2026-10-26 §6: la consola de ciudades del operador. Núcleo (<c>RequiredModules: []</c>), categoría <c>Operator</c>, como
/// <c>OperatorPermissions</c> de Tenancy. Los lleva el rol de fábrica <c>admin</c>, que es global; el prefijo <c>operator.</c> es lo
/// que hace que <c>OperatorPermissionFilter</c> los deje vivos sólo en el tenant operador. La segunda capa de cada handler es
/// <c>OperatorAuthorization.EnsureAuthorized</c> (Tenancy), con <c>IOperatorTenant.IsOperator</c>.
/// </summary>
public static class GeographyOperatorPermissions
{
    /// <summary>Ver la bandeja de ciudades sin resolver, los sinónimos y el impacto de confirmarlos.</summary>
    public const string CitiesRead = "operator.cities.read";

    /// <summary>Confirmar, descartar, corregir y borrar sinónimos: cambia pedidos y clientes de todos los tenants.</summary>
    public const string CitiesManage = "operator.cities.manage";
}
