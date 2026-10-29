namespace Modules.Billing.Application;

// Tres segmentos, module.resource.action, como los demás permisos. Dos y no más (spec cobros-recurrentes,
// owner 2026-10-29): leer incluye la lista «Por cobrar» y los encargados; gestionar incluye crear y editar
// servicios, marcar pagos, el perfil de cobro, la importación y los ajustes.
public static class BillingPermissions
{
    public const string Read = "billing.service.read";

    public const string Manage = "billing.service.manage";
}
