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

    public Task<IReadOnlyList<Membership>> ListByTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>(
            _memberships.Where(membership => membership.TenantId == tenantId).ToList());

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
        throw new NotSupportedException();

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

    public Task<IReadOnlyList<Membership>> ListByUserAsync(
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
}

internal sealed class RosterReadExecutionContext(TenantId tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = tenantId;

    public bool HasPermission(string permission) => permission == TenancyPermissions.AdvisorshipRead;
}
