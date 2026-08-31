namespace BuildingBlocks.Application;

/// <summary>
/// Clave del advisory lock de PostgreSQL que serializa el ciclo de vida de un usuario entre
/// módulos: Identity lo toma antes de borrar un usuario huérfano (<c>OrphanUserCleanupWorker</c>)
/// y Tenancy antes de invitarlo (<c>InviteMemberHandler</c>). Sin él, una invitación puede
/// insertar una membresía entre la verificación de huellas del worker y su DELETE, y quedar
/// apuntando a un usuario que ya no existe — no hay FK entre esquemas que lo impida.
/// </summary>
/// <remarks>
/// Vive en BuildingBlocks porque los dos caminos están en módulos distintos, con DbContexts
/// distintos, y lo único que comparten es la base: el lock es <c>hashtext</c> de esta cadena,
/// así que los dos lados tienen que normalizar igual. Es la misma normalización que
/// <c>User.NormalizeEmail</c> (trim + minúsculas), sin su validación.
/// </remarks>
public static class UserLifecycleLockKey
{
    public static string For(string email) => email.Trim().ToLowerInvariant();
}
