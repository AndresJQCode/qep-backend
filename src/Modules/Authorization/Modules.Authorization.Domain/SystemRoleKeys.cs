namespace Modules.Authorization.Domain;

/// <summary>
/// Las claves de los roles que se versionan con el codigo.
/// </summary>
/// <remarks>
/// Viven aca y no en el catalogo porque el dominio necesita rechazar una colision al crear
/// un rol custom, y el dominio no puede depender de la composicion. Duplicar tres strings
/// es el precio de que <see cref="Role"/> no conozca al contenedor de dependencias.
///
/// El costo real de no tenerlas: un rol custom llamado `admin` deja el catalogo con dos
/// definiciones para la misma clave y a `PermissionsFor` eligiendo una en silencio.
/// </remarks>
public static class SystemRoleKeys
{
    public const string Admin = "admin";
    public const string Advisor = "advisor";
    public const string Billing = "billing";

    /// <summary>Rol de sistema del punto de venta (spec 2026-10-07). Reservado para que un custom
    /// no lo pise en silencio en TenantRoleCatalog.</summary>
    public const string Cashier = "cashier";

    /// <summary>Roles de sistema de la Agenda (spec 2026-11-21, D-13). Definidos en código, no se crean al activar el módulo.</summary>
    public const string SchedulingManager = "scheduling_manager";

    public const string SchedulingProvider = "scheduling_provider";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Admin, Advisor, Billing, Cashier, SchedulingManager, SchedulingProvider,
        };

    public static bool IsReserved(string key) => All.Contains(key);
}
