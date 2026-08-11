namespace Modules.Identity.Application;

/// <summary>
/// Consulta de sólo lectura de atributos básicos de usuario para otros módulos (por ejemplo
/// Notifications resolviendo el email de un usuario invitado). Expone sólo claims básicos no
/// autoritativos, según la frontera Identity/Authorization (ADR 0015).
/// </summary>
public interface IUserDirectory
{
    Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken);
}
