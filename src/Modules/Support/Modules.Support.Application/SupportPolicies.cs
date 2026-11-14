namespace Modules.Support.Application;

/// <summary>Políticas que aceptan más de un permiso (plan D5 y D15). Viven fuera de <see cref="SupportPermissions"/> para que el
/// descubrimiento de permisos no las cuente como permisos: no se asignan a un rol, se satisfacen con uno de los que listan.</summary>
public static class SupportPolicies
{
    /// <summary>Ver tickets: <see cref="SupportPermissions.Read"/> o <see cref="SupportPermissions.Manage"/>. Quien tiene
    /// cualquiera de los dos ve todos los tickets del tenant (owner, spec §10): no hay «sólo los suyos».</summary>
    public const string TicketView = "support.ticket.view";

    /// <summary>Leer los ajustes de soporte: <see cref="SupportPermissions.Read"/>, <see cref="SupportPermissions.Manage"/> o
    /// <see cref="SupportPermissions.SettingsManage"/>. Así un rol con sólo configurar lee la versión que tiene que mandar al
    /// guardar.</summary>
    public const string SettingsView = "support.settings.view";
}
