namespace Modules.Billing.Application;

/// <summary><c>CanManage</c>: la membresía está activa y sus roles conceden <c>billing.service.manage</c>.</summary>
public sealed record BillingMemberRef(Guid MemberId, string DisplayName, string? Email, bool IsActive, bool CanManage);

/// <summary>Miembros del equipo vistos desde Billing (spec §3.3); adaptador en Bootstrapper sobre Tenancy + Identity + Authorization.</summary>
public interface IBillingMemberDirectory
{
    Task<IReadOnlyList<BillingMemberRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>Las membresías activas cuyos roles conceden <c>billing.service.manage</c>: el conjunto exacto de <c>GET /assignees</c> y los destinatarios de reserva del email diario.</summary>
    Task<IReadOnlyList<BillingMemberRef>> ListManagersAsync(Guid tenantId, CancellationToken cancellationToken);
}
