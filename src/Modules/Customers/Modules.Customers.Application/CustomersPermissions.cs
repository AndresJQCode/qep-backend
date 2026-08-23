namespace Modules.Customers.Application;

// Tres segmentos, module.resource.action, igual que todos los permisos ya registrados
// (TenancyPermissions, StoragePermissions, CatalogPermissions, CompaniesPermissions).
//
// Los tres los declara `CLI-01`. `customers.import` es propio y no parte de `manage` porque cargar
// un Excel de mil clientes de una vez no es la misma autoridad que editar uno: el gate del modulo
// pide explicitamente que se mapeen a roles por separado.
public static class CustomersPermissions
{
    public const string CustomerRead = "customers.customer.read";

    public const string CustomerManage = "customers.customer.manage";

    public const string CustomerImport = "customers.customer.import";

    // El catalogo de clasificaciones de cliente (nombre + prefijo) vive en el mismo modulo que
    // Customer, pero es un recurso distinto con sus propios permisos, mismo criterio que
    // CatalogPermissions.TaxRateRead/TaxRateManage frente a ProductRead/ProductManage.
    public const string ClassificationRead = "customers.classification.read";

    public const string ClassificationManage = "customers.classification.manage";
}
