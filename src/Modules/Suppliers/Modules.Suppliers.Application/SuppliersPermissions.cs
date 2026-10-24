namespace Modules.Suppliers.Application;

// Tres segmentos, module.resource.action, como los demás permisos. Dos y no más (spec 2026-10-23 D-14): leer incluye
// exportar; gestionar incluye crear, editar, activar y desactivar.
public static class SuppliersPermissions
{
    public const string Read = "suppliers.supplier.read";

    public const string Manage = "suppliers.supplier.manage";
}
