namespace Modules.Pricing.Application;

// Tres segmentos, module.resource.action, igual que todos los permisos ya registrados
// (CatalogPermissions, CustomersPermissions).
public static class PricingPermissions
{
    public const string PriceListRead = "pricing.price_list.read";

    public const string PriceListManage = "pricing.price_list.manage";
}
