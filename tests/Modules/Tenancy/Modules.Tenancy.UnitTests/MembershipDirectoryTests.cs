using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §4: los permisos miran también el tenant, en cada request.</summary>
public sealed class MembershipDirectoryTests
{
    private static readonly Guid User = Guid.CreateVersion7();
    private static readonly TenantId Tenant = TenantId.New();

    private static MembershipDirectory Directory(TenantStatus? tenantStatus) =>
        new(new InMemoryMembershipRepository(
                Membership.CreateActive(MembershipId.New(), User, Tenant, ["admin"], "registration", DateTimeOffset.UnixEpoch)),
            new FixedTenantDirectory(tenantStatus));

    [Fact]
    public async Task AnActiveTenantKeepsTheRoles() =>
        Assert.Equal(["admin"], await Directory(TenantStatus.Active)
            .FindActiveRolesAsync(User, Tenant.Value, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Provisioning)]
    [InlineData(null)]   // sin fila: fail closed, como el enmascarado del camino real
    public async Task AnyOtherTenantStateResolvesNoRoles(TenantStatus? status) =>
        Assert.Null(await Directory(status).FindActiveRolesAsync(User, Tenant.Value, TestContext.Current.CancellationToken));
}
