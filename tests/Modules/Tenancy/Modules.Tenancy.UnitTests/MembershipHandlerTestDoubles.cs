using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

// Dobles de los puertos que usan los handlers del roster. A mano y sin librería de mocking, como
// el resto del repositorio. Lo que un handler del roster no usa lanza, para que una prueba no
// pase por un camino que no creía estar ejerciendo.

internal sealed class InMemoryMembershipRepository(params Membership[] memberships)
    : IMembershipRepository
{
    private readonly List<Membership> _memberships = [.. memberships];

    /// <summary>Lo que queda guardado, para afirmar qué borró una purga.</summary>
    public IReadOnlyList<Membership> All => _memberships;

    public Task<IReadOnlyList<Membership>> ListByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>(
            _memberships.Where(membership => membership.UserId == userId).ToList());

    public void Remove(Membership membership) => _memberships.Remove(membership);

    public Task<IReadOnlyList<Membership>> ListByTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>(
            _memberships.Where(membership => membership.TenantId == tenantId).ToList());

    public Task<IReadOnlyList<Membership>> ListByIdsAsync(
        TenantId tenantId, IReadOnlyCollection<MembershipId> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>(
            _memberships
                .Where(membership => membership.TenantId == tenantId && ids.Contains(membership.Id))
                .ToList());

    public Task<Membership?> FindByIdAsync(
        MembershipId id, TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.SingleOrDefault(
            membership => membership.Id == id && membership.TenantId == tenantId));

    public Task<bool> IsAdvisorCodeTakenAsync(
        TenantId tenantId,
        int advisorCode,
        MembershipId? exceptMembershipId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.Any(membership =>
            membership.TenantId == tenantId &&
            membership.AdvisorCode == advisorCode &&
            membership.Id != exceptMembershipId));

    public void Add(Membership membership) => _memberships.Add(membership);

    public Task<Membership?> FindByUserAndTenantAsync(
        Guid userId, TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.SingleOrDefault(
            membership => membership.UserId == userId && membership.TenantId == tenantId));

    public Task<Membership?> FindByInvitationTokenHashAsync(
        string tokenHash, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Membership>> ListInvitedByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TenantId>> ListActiveTenantsByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ActiveTenantSummary>> ListActiveTenantSummariesByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Membership>> ListActiveExcludingAsync(
        TenantId tenantId, MembershipId excludeId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class StubUserDirectory(IReadOnlyDictionary<Guid, string> emails) : IUserDirectory
{
    public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(emails.TryGetValue(userId, out var email) ? email : null);

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class RosterReadExecutionContext(TenantId tenantId, params string[] extraPermissions) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = tenantId;

    public bool HasPermission(string permission) =>
        permission == TenancyPermissions.AdvisorshipRead || extraPermissions.Contains(permission);
}

/// <summary>The HR fallback with nothing to lend: handlers that only need the reader to exist.</summary>
internal sealed class NoHrPhones : IMemberPhoneDirectory
{
    public Task<IReadOnlyDictionary<Guid, string>> FindHrPhonesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> memberIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

/// <summary>
/// Anota cada subida de la versión de acceso del tenant (spec 2026-11-13, D-5) en la misma lista que la unidad de trabajo,
/// para afirmar el orden: antes del guardado y otra vez después.
/// </summary>
internal sealed class RecordingTenantAccessVersion(List<string> steps) : ITenantAccessVersion
{
    public Task BumpAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        steps.Add($"bump:{tenantId}");
        return Task.CompletedTask;
    }
}
