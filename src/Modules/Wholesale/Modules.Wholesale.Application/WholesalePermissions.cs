namespace Modules.Wholesale.Application;

/// <summary>
/// Spec 2026-11-17 (mayorista): permisos de listas de precios y distribuidores. Cada uno necesita además su
/// <c>PermissionDefinition</c> (con <c>RequiredModules: [wholesale]</c>) y su política en
/// <c>QepServiceCollectionExtensions</c>: sin la política el síntoma es 500, no 403.
/// </summary>
public static class WholesalePermissions
{
    /// <summary>
    /// Spec 2026-11-22 (D-7, D-10): las listas pasaron a Catalog y su permiso es <c>catalog.price_list.manage</c>, que también cubre
    /// la vista previa del distribuidor y las categorías para elegir la visibilidad. El literal se repite porque Mayorista no
    /// referencia Catalog; la constante, la definición y la política son las de <c>CatalogPermissions.PriceListManage</c>.
    /// </summary>
    public const string PriceListManage = "catalog.price_list.manage";

    public const string DistributorRead = "wholesale.distributor.read";

    public const string DistributorManage = "wholesale.distributor.manage";
}
