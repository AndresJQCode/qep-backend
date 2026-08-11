namespace Modules.Catalog.Application;

// Tres segmentos, module.resource.action, igual que todos los permisos ya registrados
// (TenancyPermissions, StoragePermissions). Productos y tasas de impuesto van separados
// porque cambiar una tasa mueve los totales de toda cotización, y cargar un producto no.
public static class CatalogPermissions
{
    public const string ProductRead = "catalog.product.read";
    public const string ProductManage = "catalog.product.manage";
    public const string TaxRateRead = "catalog.tax_rate.read";
    public const string TaxRateManage = "catalog.tax_rate.manage";
}
