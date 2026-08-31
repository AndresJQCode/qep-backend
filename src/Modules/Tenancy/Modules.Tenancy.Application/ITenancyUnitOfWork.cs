namespace Modules.Tenancy.Application;

public interface ITenancyUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Abre la transacción del comando y toma el lock de ciclo de vida del usuario con ese
    /// correo (<c>UserLifecycleLockKey</c>, BuildingBlocks), el mismo que Identity sostiene
    /// mientras decide si borra un usuario huérfano. Sin él, invitar entre la verificación de
    /// huellas del worker y su DELETE deja una membresía apuntando a un usuario inexistente:
    /// no hay FK entre esquemas que lo frene. El SQL vive en Infrastructure porque Application
    /// no referencia EF ni Npgsql.
    /// </summary>
    Task<IUserLifecycleScope> BeginUserLifecycleScopeAsync(
        string email,
        CancellationToken cancellationToken);
}

/// <summary>Se libera al commitear o, si el handler falla, al disponerlo (rollback).</summary>
public interface IUserLifecycleScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
