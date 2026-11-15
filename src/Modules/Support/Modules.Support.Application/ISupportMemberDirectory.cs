namespace Modules.Support.Application;

/// <summary><c>CanManage</c>: la membresía está activa y sus roles conceden <c>support.ticket.manage</c>.</summary>
public sealed record SupportMemberRef(Guid MemberId, string DisplayName, bool IsActive, bool CanManage);

/// <summary>Miembros del equipo vistos desde Soporte (copia de <c>ILoansMemberDirectory</c>); adaptador en Bootstrapper sobre Tenancy
/// y Authorization.</summary>
public interface ISupportMemberDirectory
{
    /// <summary>También los quitados o suspendidos: la traza los nombra aunque ya no estén. Consulta los permisos de cada uno: es para
    /// validar un responsable, no para nombrar (para eso, <see cref="NamesAsync"/>).</summary>
    Task<IReadOnlyList<SupportMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>El nombre visible de cada miembro, también de uno quitado o suspendido; un id desconocido no aparece. Sólo lee la
    /// membresía, sin consultar permisos: es lo que el detalle y la bandeja necesitan para nombrar (revisión final, B4; como
    /// <c>IStoreMemberDirectory.NamesAsync</c>).</summary>
    Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>Las membresías activas cuyos roles conceden <c>support.ticket.manage</c>: el conjunto exacto de <c>GET /assignees</c>
    /// y el que acepta un responsable. Sin orden: lo ordena quien lo muestra (<see cref="ListSupportAssigneesHandler"/>).</summary>
    Task<IReadOnlyList<SupportMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken);
}
