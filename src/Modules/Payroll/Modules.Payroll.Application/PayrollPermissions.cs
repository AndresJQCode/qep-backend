namespace Modules.Payroll.Application;

// Tres segmentos, module.resource.action, como los demás permisos. Los cuatro del spec 2026-11-21: todos ven plata, así que
// ninguno entra en un rol que no sea admin de fábrica. Cada escritura exige también `PeriodRead` (D-1), salvo la configuración.
public static class PayrollPermissions
{
    public const string PeriodRead = "payroll.period.read";

    public const string PeriodManage = "payroll.period.manage";

    public const string PeriodApprove = "payroll.period.approve";

    public const string SettingsManage = "payroll.settings.manage";
}
