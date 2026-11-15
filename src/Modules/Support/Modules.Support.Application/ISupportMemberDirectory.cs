namespace Modules.Support.Application;

/// <summary><c>CanManage</c>: la membresía está activa y sus roles conceden <c>support.ticket.manage</c>.</summary>
public sealed record SupportMemberRef(Guid MemberId, string DisplayName, bool IsActive, bool CanManage);

/// <summary>Miembros del equipo vistos desde Soporte (copia de <c>ILoansMemberDirectory</c>); adaptador en Bootstrapper sobre Tenancy
/// y Authorization.</summary>
public interface ISupportMemberDirectory
{
    /// <summary>También los quitados o suspendidos: la traza los nombra aunque ya no estén.</summary>
    Task<IReadOnlyList<SupportMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>Las membresías activas cuyos roles conceden <c>support.ticket.manage</c>: el conjunto exacto de <c>GET /assignees</c>
    /// y el que acepta un responsable.</summary>
    Task<IReadOnlyList<SupportMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken);
}
