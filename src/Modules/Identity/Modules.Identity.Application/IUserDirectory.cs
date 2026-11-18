namespace Modules.Identity.Application;

/// <summary>
/// Consulta de sólo lectura de atributos básicos de usuario para otros módulos (por ejemplo
/// Notifications resolviendo el email de un usuario invitado). Expone sólo claims básicos no
/// autoritativos, según la frontera Identity/Authorization (ADR 0015).
/// </summary>
public interface IUserDirectory
{
    Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Los correos de varios usuarios en un solo viaje a la base; los ids sin usuario no vienen. Un roster de doce miembros
    /// resuelto de a uno son doce consultas en serie, y con la base en otro nodo cada una paga la red: por eso todo lector de
    /// una lista pasa por acá y no por <see cref="GetEmailAsync"/> en un bucle. Lo de por defecto itera, para que un doble de
    /// prueba que sólo conoce el método de a uno siga valiendo; <c>UserDirectory</c> lo reemplaza por una sola consulta.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, string>> GetEmailsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var emails = new Dictionary<Guid, string>();
        foreach (var userId in userIds.Distinct())
        {
            if (await GetEmailAsync(userId, cancellationToken) is { } email)
            {
                emails[userId] = email;
            }
        }

        return emails;
    }
}
