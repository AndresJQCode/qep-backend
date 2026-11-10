namespace Modules.Store.Application;

/// <summary>Miembros del equipo vistos desde la tienda; el adaptador vive en Bootstrapper sobre Tenancy.</summary>
public interface IStoreMemberDirectory
{
    /// <summary>La membresía activa de quien llama y su usuario. Sin membresía activa en el tenant, 403 <c>authorization.denied</c>.</summary>
    Task<(Guid MembershipId, Guid UserId)> ResolveCallerAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>El nombre visible de cada miembro (también de uno ya inactivo o quitado); un id desconocido no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>Si la membresía es de ese tenant y está activa; una quitada, suspendida o de otro tenant no lo es.</summary>
    Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);

    /// <summary>La membresía del owner del tenant, el asesor de respaldo de la tienda. Sin tenant lanza <see cref="InvalidOperationException"/>: no hay tienda sin tenant.</summary>
    Task<Guid> FindOwnerMembershipIdAsync(Guid tenantId, CancellationToken cancellationToken);
}
