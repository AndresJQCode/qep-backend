namespace Modules.Catalog.Application;

// Tres segmentos, module.resource.action, igual que todos los permisos ya registrados
// (TenancyPermissions, StoragePermissions).
//
// Los de tasas de impuesto —catalog.tax_rate.read y .manage— estuvieron acá una vez, registrados
// en roles y publicados en /authorization/catalog, sin una sola línea que los consumiera. Se
// quitaron en la corrección de la revisión de CAT-02 porque un permiso publicado antes que su
// funcionalidad le dice al frontend que existe algo que no existe, y le cuelga a admin un
// permiso de gestión sobre nada. Vuelven acá con CAT-03, en el mismo commit que sus endpoints.
//
// Van separados de los de producto porque cambiar una tasa mueve los totales de toda cotización
// —es high— y cargar un producto no. Por eso advisor recibe sólo la lectura.
public static class CatalogPermissions
{
    public const string ProductRead = "catalog.product.read";
    public const string ProductManage = "catalog.product.manage";
    public const string TaxRateRead = "catalog.tax_rate.read";
    public const string TaxRateManage = "catalog.tax_rate.manage";
}
