namespace Modules.Payroll.Application;

/// <summary>Políticas que aceptan más de un permiso. Viven fuera de <see cref="PayrollPermissions"/> para que el descubrimiento de
/// permisos no las cuente como permisos: no se asignan a un rol, se satisfacen con uno de los que listan.</summary>
public static class PayrollPolicies
{
    /// <summary>Leer las empresas y la configuración de nómina: <see cref="PayrollPermissions.PeriodRead"/> o
    /// <see cref="PayrollPermissions.SettingsManage"/>. Así un rol con sólo configurar usa las pantallas de configuración, y no ve
    /// períodos, liquidaciones, rastro ni Excel.</summary>
    public const string SettingsView = "payroll.policies.settings_view";
}
