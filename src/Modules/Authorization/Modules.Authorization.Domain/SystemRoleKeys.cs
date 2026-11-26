namespace Modules.Authorization.Domain;

/// <summary>
/// Las claves de los roles que se versionan con el código: sólo <c>admin</c> (spec 2026-11-25, roles por tenant).
/// </summary>
/// <remarks>
/// Viven acá y no en el catálogo porque el dominio necesita rechazar una colisión al crear un rol de tenant, y el
/// dominio no puede depender de la composición. Repetir un string es el precio de que <see cref="Role"/> no conozca al
/// contenedor de dependencias.
///
/// El costo real de no tenerlas: un rol de tenant llamado <c>admin</c> deja el catálogo con dos definiciones para la
/// misma clave y a <c>PermissionsFor</c> eligiendo una en silencio.
///
/// Los demás roles que trae un tenant nuevo no son de sistema ni están reservados: son filas que se siembran a partir
/// de una plantilla (<c>RoleTemplateKeys</c> en Application) y que el tenant edita o borra.
/// </remarks>
public static class SystemRoleKeys
{
    public const string Admin = "admin";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Admin };

    public static bool IsReserved(string key) => All.Contains(key);
}
