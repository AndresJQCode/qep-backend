namespace Modules.Hr.Application;

public sealed record HrMemberRef(Guid MemberId, string DisplayName, bool IsActive);

/// <summary>Puerto hacia las membresías del tenant (Tenancy); el adaptador vive en Bootstrapper.</summary>
public interface IHrMemberDirectory
{
    /// <summary>Las membresías del tenant entre las pedidas, activas o no: una de otro tenant o inexistente no está en el resultado.</summary>
    Task<IReadOnlyList<HrMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>Las membresías activas, por nombre y luego por id.</summary>
    Task<IReadOnlyList<HrMemberRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>La membresía del tenant de un usuario (<c>Membership.UserId</c>): es el actor del historial de acciones.</summary>
    Task<HrMemberRef?> FindBySubjectAsync(Guid tenantId, Guid subjectId, CancellationToken cancellationToken);
}
