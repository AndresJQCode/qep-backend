namespace Modules.Support.Application;

// Spec 2026-11-14 (C1): tres segmentos, module.resource.action, como los demás permisos. Leer y gestionar tickets son del
// trabajo diario; configurar (tipos de ticket y umbral de «por vencer») es de quien administra.
public static class SupportPermissions
{
    public const string Read = "support.ticket.read";

    public const string Manage = "support.ticket.manage";

    public const string SettingsManage = "support.settings.manage";

    // Spec 2026-11-22 (tareas internas, §5.6): crear tareas y trabajar las propias. Lo llevan los cuatro roles de fábrica; no da
    // acceso a ningún ticket de cliente.
    public const string TaskUse = "support.task.use";
}
