namespace Modules.Wms.Application;

/// <summary>Lo que las tareas necesitan saber de las membresías (Tenancy). Wms no referencia sus tablas: el adaptador está en
/// Bootstrapper.</summary>
public interface IWmsMemberDirectory
{
    /// <summary>El nombre visible de membresías del tenant, en cualquier estado: la historia de una tarea sigue nombrando a quien ya
    /// no está. La que no tiene nombre no viene.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);

    /// <summary><c>true</c> sólo si la membresía está activa en el tenant y alguno de sus roles concede el permiso.</summary>
    Task<bool> CanPerformAsync(Guid tenantId, Guid memberId, string permission, CancellationToken ct);

    /// <summary>Las membresías activas del tenant que conceden el permiso.</summary>
    Task<IReadOnlyList<Guid>> ListWithPermissionAsync(Guid tenantId, string permission, CancellationToken ct);
}
