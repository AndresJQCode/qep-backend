namespace Modules.Store.Application;

/// <summary>Miembros del equipo vistos desde la tienda; el adaptador vive en Bootstrapper sobre Tenancy.</summary>
public interface IStoreMemberDirectory
{
    /// <summary>La membresía activa de quien llama y su usuario. Sin membresía activa en el tenant, 403 <c>authorization.denied</c>.</summary>
    Task<(Guid MembershipId, Guid UserId)> ResolveCallerAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>El nombre visible de cada miembro (también de uno ya inactivo o quitado); un id desconocido no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
