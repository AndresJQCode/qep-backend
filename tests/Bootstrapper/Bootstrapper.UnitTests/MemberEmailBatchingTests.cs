using Modules.Authorization.Application;
using Modules.Messaging.Application;
using Modules.Quotations.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Los adaptadores que nombran a varios miembros (<c>DisplayName ?? correo</c>) piden los correos de quienes no tienen nombre en
/// un solo viaje a Identity: con la base en otro nodo, un <c>GetEmailAsync</c> por miembro es una ida y vuelta de red por fila.
/// Cada prueba siembra tres miembros sin nombre y uno con nombre, y afirma el resultado y que hubo un lote y ninguna lectura de a uno.
/// </summary>
public sealed class MemberEmailBatchingTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Membership Unnamed() =>
        Membership.CreateActive(MembershipId.New(), Guid.CreateVersion7(), Tenant, ["advisor"], "signup", Now);

    private static Membership Named(string displayName)
    {
        var membership = Unnamed();
        membership.UpdateProfile(displayName, null, Now);
        return membership;
    }

    private static (Membership[] Members, StubUserDirectory Users) Roster()
    {
        Membership[] members = [Unnamed(), Unnamed(), Unnamed(), Named("Zoe Named")];
        var users = new StubUserDirectory(new Dictionary<Guid, string>
        {
            [members[0].UserId] = "ana@example.com",
            [members[1].UserId] = "beto@example.com",
            [members[2].UserId] = "carla@example.com",
        });
        return (members, users);
    }

    private static void AssertOneBatch(StubUserDirectory users)
    {
        Assert.Equal(1, users.GetEmailsCalls);
        Assert.Equal(0, users.GetEmailCalls);
    }

    [Fact]
    public async Task MessagingAssigneesResolveTheEmailsInOneBatch()
    {
        var (members, users) = Roster();
        var assignees = new MessagingAssignees(new InMemoryMembershipRepository(members), users, new GrantAllRoleCatalog());

        var result = await assignees.ListAsync(Tenant.Value, Ct);

        Assert.Equal(["ana@example.com", "beto@example.com", "carla@example.com", "Zoe Named"], result.Select(item => item.DisplayName));
        AssertOneBatch(users);
    }

    [Fact]
    public async Task AiQuotationAdvisorsResolveTheEmailsInOneBatch()
    {
        var (members, users) = Roster();
        var advisors = new AiQuotationAdvisors(new InMemoryMembershipRepository(members), users, new GrantAllRoleCatalog());

        var result = await advisors.ListAsync(Tenant.Value, Ct);

        Assert.Equal(["ana@example.com", "beto@example.com", "carla@example.com", "Zoe Named"], result.Select(item => item.Name));
        AssertOneBatch(users);
    }

    [Fact]
    public async Task ShopifyMemberOptionsResolveTheEmailsInOneBatch()
    {
        var (members, users) = Roster();
        var directory = new ShopifyMemberDirectory(new InMemoryMembershipRepository(members), users);

        var result = await directory.ListActiveAsync(Tenant.Value, Ct);

        Assert.Equal(["ana@example.com", "beto@example.com", "carla@example.com", "Zoe Named"], result.Select(item => item.Name));
        AssertOneBatch(users);
    }

    [Fact]
    public async Task IntegrationsConnectionAuthorNamesResolveTheEmailsInOneBatch()
    {
        var (members, users) = Roster();
        var names = new IntegrationsConnectionAuthorNames(new InMemoryMembershipRepository(members), users);

        var result = await names.FindAsync(Tenant.Value, members.Select(member => member.Id.Value).ToArray(), Ct);

        Assert.Equal("ana@example.com", result[members[0].Id.Value]);
        Assert.Equal("carla@example.com", result[members[2].Id.Value]);
        Assert.Equal("Zoe Named", result[members[3].Id.Value]);
        AssertOneBatch(users);
    }

    [Fact]
    public async Task CustomersAuthorNamesResolveTheEmailsInOneBatch()
    {
        var (members, users) = Roster();
        var withoutMembership = Guid.CreateVersion7();
        var names = new CustomersAuthorNames(new InMemoryMembershipRepository(members), users);

        var result = await names.FindAsync(
            Tenant.Value, [.. members.Select(member => member.UserId), withoutMembership], Ct);

        Assert.Equal("beto@example.com", result[members[1].UserId]);
        Assert.Equal("Zoe Named", result[members[3].UserId]);
        Assert.DoesNotContain(withoutMembership, result.Keys);
        AssertOneBatch(users);
    }

    [Fact]
    public async Task CopilotMemberNamesResolveTheEmailsInOneBatch()
    {
        var (members, users) = Roster();
        var names = new CopilotMemberNames(new InMemoryMembershipRepository(members), users);

        var result = await names.FindAsync(Tenant.Value, members.Select(member => member.UserId).ToArray(), Ct);

        Assert.Equal("carla@example.com", result[members[2].UserId]);
        Assert.Equal("Zoe Named", result[members[3].UserId]);
        Assert.Equal(4, result.Count);
        AssertOneBatch(users);
    }

    /// <summary>Todo rol concede los permisos que miran las listas de asesores: la prueba es sobre los correos, no sobre la regla.</summary>
    private sealed class GrantAllRoleCatalog : ITenantRoleCatalog
    {
        public Task<IReadOnlyCollection<string>> PermissionsForAsync(
            Guid tenantId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<string>>(
                [MessagingPermissions.ConversationManage, QuotationsPermissions.QuotationManage]);

        public Task<IReadOnlyCollection<TenantRoleDefinition>> ListRolesAsync(
            Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ContainsRoleAsync(Guid tenantId, string role, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
