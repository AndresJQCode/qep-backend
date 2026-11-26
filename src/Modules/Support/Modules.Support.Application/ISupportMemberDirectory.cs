namespace Modules.Support.Application;

/// <summary><c>CanManage</c>: la membresía está activa y sus roles conceden <c>support.ticket.manage</c>. <c>CanUseTasks</c> (spec
/// 2026-11-22): activa y sus roles conceden <c>support.task.use</c>; sólo lo llena <c>FindAsync</c> y <c>ListTaskMembersAsync</c>.</summary>
public sealed record SupportMemberRef(Guid MemberId, string DisplayName, bool IsActive, bool CanManage, bool CanUseTasks = false);

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

    /// <summary>Las membresías activas cuyos roles conceden <c>support.task.use</c> o <c>support.ticket.manage</c>: el conjunto exacto de
    /// <c>GET /tasks/assignees</c> y el que acepta el responsable de una tarea. Sin orden: lo ordena quien lo muestra.</summary>
    Task<IReadOnlyList<SupportMemberRef>> ListTaskMembersAsync(Guid tenantId, CancellationToken cancellationToken);
}
