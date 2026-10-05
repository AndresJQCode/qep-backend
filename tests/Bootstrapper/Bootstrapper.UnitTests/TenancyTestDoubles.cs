using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

// Dobles de los puertos de Tenancy e Identity que usan los adaptadores del composition root. A mano y
// sin librería de mocking, como el resto del repositorio. Lo que ningún adaptador usa lanza, para que
// una prueba no pase por un camino que no creía estar ejerciendo.

/// <summary>Las membresías que siembra la prueba. Cuenta las dos lecturas por tenant, para que una
/// prueba afirme cuál de las dos usó el adaptador.</summary>
internal sealed class InMemoryMembershipRepository(params Membership[] memberships)
    : IMembershipRepository
{
    public int ListByTenantCalls { get; private set; }

    public int ListByIdsCalls { get; private set; }

    public Task<IReadOnlyList<Membership>> ListByTenantAsync(
        TenantId tenantId, CancellationToken cancellationToken)
    {
        ListByTenantCalls++;
        return Task.FromResult<IReadOnlyList<Membership>>(
            memberships.Where(membership => membership.TenantId == tenantId).ToList());
    }

    public Task<IReadOnlyList<Membership>> ListByIdsAsync(
        TenantId tenantId, IReadOnlyCollection<MembershipId> ids, CancellationToken cancellationToken)
    {
        ListByIdsCalls++;
        return Task.FromResult<IReadOnlyList<Membership>>(
            memberships
                .Where(membership => membership.TenantId == tenantId && ids.Contains(membership.Id))
                .ToList());
    }

    public Task<Membership?> FindByUserAndTenantAsync(
        Guid userId, TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Membership?> FindByIdAsync(
        MembershipId id, TenantId tenantId, CancellationToken cancellationToken) =>
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

    public Task<bool> IsAdvisorCodeTakenAsync(
        TenantId tenantId,
        int advisorCode,
        MembershipId? exceptMembershipId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Add(Membership membership) => throw new NotSupportedException();

    public void Remove(Membership membership) => throw new NotSupportedException();
}

internal sealed class StubUserDirectory(IReadOnlyDictionary<Guid, string> emails) : IUserDirectory
{
    public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(emails.TryGetValue(userId, out var email) ? email : null);
}
