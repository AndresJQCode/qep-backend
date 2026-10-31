namespace Modules.ServiceContracts.Application;

/// <summary><c>CanManage</c>: la membresía está activa y sus roles conceden <c>billing.service.manage</c>.</summary>
public sealed record ServiceContractsMemberRef(Guid MemberId, string DisplayName, string? Email, bool IsActive, bool CanManage);

/// <summary>Miembros del equipo vistos desde ServiceContracts (spec §3.3); adaptador en Bootstrapper sobre Tenancy + Identity + Authorization.</summary>
public interface IServiceContractsMemberDirectory
{
    Task<IReadOnlyList<ServiceContractsMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>Las membresías activas cuyos roles conceden <c>billing.service.manage</c>: el conjunto exacto de <c>GET /assignees</c> y los destinatarios de reserva del email diario.</summary>
    Task<IReadOnlyList<ServiceContractsMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// El encargado que nombra una fila de la importación por su correo (§3.2b): un miembro activo con
    /// <c>billing.service.manage</c> con ese correo, sin mayúsculas, o <c>null</c> (<c>assignee_invalid</c>).
    /// </summary>
    Task<ServiceContractsMemberRef?> FindManagerByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken);
}
