namespace Modules.Authorization.Application;

/// <summary>
/// La plantilla con la que se siembra un rol en cada tenant (spec 2026-11-25, roles por tenant).
/// </summary>
/// <remarks>
/// Es otro tipo que <see cref="RoleDefinition"/> a propósito: una plantilla no resuelve permisos ni entra a ningún
/// catálogo. Lo que concede permisos es la fila de <c>authorization.roles</c> que se crea a partir de ella, y esa fila
/// es del tenant: la edita o la borra. Por eso un permiso nuevo en una plantilla sólo llega a los tenants nuevos
/// (decisión 2 del owner); los que ya existen lo marcan en la pantalla de roles.
///
/// Toda plantilla tiene que ser un subconjunto de <c>admin</c>: el catálogo de permisos sólo ofrece lo que concede
/// algún rol de sistema, y lo demás no se podría marcar ni guardar.
/// </remarks>
public sealed record RoleTemplate(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyCollection<string> Permissions);

/// <summary>
/// Las claves de los roles que se siembran en cada tenant.
/// </summary>
/// <remarks>
/// No están reservadas: cada una es una fila del tenant, que puede borrarla y crear otra con la misma clave. Ningún
/// código decide por ellas; no cambies un valor, porque las membresías guardadas nombran el rol por su clave.
/// </remarks>
public static class RoleTemplateKeys
{
    public const string Advisor = "advisor";
    public const string Billing = "billing";

    /// <summary>Punto de venta (spec 2026-10-07).</summary>
    public const string Cashier = "cashier";

    /// <summary>Agenda (spec 2026-11-21, D-13).</summary>
    public const string SchedulingManager = "scheduling_manager";

    public const string SchedulingProvider = "scheduling_provider";
}
