using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public interface IUserRepository
{
    /// <param name="normalizedEmail">Email ya normalizado con <see cref="User.NormalizeEmail"/>.</param>
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<User?> FindByProviderAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);

    void Add(User user);

    /// <summary>
    /// Borrado físico. Sólo lo usa <c>OrphanUserCleanupWorker</c>, después de que ningún
    /// módulo declaró retener al usuario; ningún caso de uso de request lo llama. Los vínculos
    /// de proveedor y las preferencias caen por cascada; las sesiones no tienen FK y las borra
    /// el mismo worker.
    /// </summary>
    void Remove(User user);
}
