namespace Modules.Catalog.Application;

// Three segments, module.resource.action, matching every permission already registered
// (TenancyPermissions, StoragePermissions). Products and tax rates are split because
// changing a tax rate moves the totals of every quote, while loading a product does not.
public static class CatalogPermissions
{
    public const string ProductRead = "catalog.product.read";
    public const string ProductManage = "catalog.product.manage";
    public const string TaxRateRead = "catalog.tax_rate.read";
    public const string TaxRateManage = "catalog.tax_rate.manage";
}
