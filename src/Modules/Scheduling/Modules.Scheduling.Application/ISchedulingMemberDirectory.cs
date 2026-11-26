namespace Modules.Scheduling.Application;

public sealed record SchedulingMemberRef(Guid MemberId, string DisplayName, bool IsActive);

/// <summary>Puerto hacia las membresías del tenant (Tenancy); el adaptador vive en Bootstrapper. Calco de <c>IHrMemberDirectory</c>.</summary>
public interface ISchedulingMemberDirectory
{
    /// <summary>Las membresías del tenant entre las pedidas, activas o no: una de otro tenant o inexistente no está en el resultado.</summary>
    Task<IReadOnlyList<SchedulingMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>La membresía del tenant de un usuario (<c>Membership.UserId</c>): es de quien se resuelve el alcance de recursos.</summary>
    Task<SchedulingMemberRef?> FindBySubjectAsync(Guid tenantId, Guid subjectId, CancellationToken cancellationToken);
}
