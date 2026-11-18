namespace Modules.Wholesale.Application;

/// <summary>
/// Spec 2026-11-17 (mayorista): permisos de listas de precios y distribuidores. Cada uno necesita además su
/// <c>PermissionDefinition</c> (con <c>RequiredModules: [wholesale]</c>) y su política en
/// <c>QepServiceCollectionExtensions</c>: sin la política el síntoma es 500, no 403.
/// </summary>
public static class WholesalePermissions
{
    /// <summary>Crear, editar, borrar y previsualizar listas de precios. No hay permiso de lectura aparte.</summary>
    public const string PriceListManage = "wholesale.price_list.manage";

    public const string DistributorRead = "wholesale.distributor.read";

    public const string DistributorManage = "wholesale.distributor.manage";
}
