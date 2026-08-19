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
}
