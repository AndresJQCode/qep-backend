namespace Modules.Loans.Application;

/// <summary><c>CanManage</c>: la membresía está activa y sus roles conceden <c>loans.loan.manage</c>.</summary>
public sealed record LoansMemberRef(Guid MemberId, string DisplayName, bool IsActive, bool CanManage);

/// <summary>Miembros del equipo vistos desde Loans; adaptador en Bootstrapper sobre Tenancy + Authorization.</summary>
public interface ILoansMemberDirectory
{
    Task<IReadOnlyList<LoansMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>Las membresías activas cuyos roles conceden <c>loans.loan.manage</c>: el conjunto exacto de <c>GET /assignees</c> y el que acepta el alta de un préstamo.</summary>
    Task<IReadOnlyList<LoansMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken);
}