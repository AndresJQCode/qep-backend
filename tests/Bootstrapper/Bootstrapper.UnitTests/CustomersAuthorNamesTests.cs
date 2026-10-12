using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El nombre del autor de una nota de cliente (spec 2026-10-11 §4.2): la nota guarda el usuario, así que se busca su
/// membresía en el tenant. Quien no aparece no entra al diccionario, y Customers lo muestra como «Usuario eliminado».
/// </summary>
public sealed class CustomersAuthorNamesTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static Membership MemberOf(TenantId tenant, string displayName)
    {
        var userId = Guid.CreateVersion7();
        return Membership.Invite(
            MembershipId.New(), userId, tenant, displayName, ["advisor"], "invitation",
            $"token-{userId:N}", $"hash-{userId:N}", Now.AddHours(-1), Membership.DefaultInvitationTimeToLive);
    }

    [Fact]
    public async Task TheDisplayNameOfTheMembershipWinsOverTheEmail()
    {
        var member = MemberOf(Tenant, "Ana Gómez");
        var names = new CustomersAuthorNames(
            new InMemoryMembershipRepository(member),
            new StubUserDirectory(new Dictionary<Guid, string> { [member.UserId] = "ana@example.com" }));

        var result = await names.FindAsync(Tenant.Value, [member.UserId], TestContext.Current.CancellationToken);

        Assert.Equal("Ana Gómez", result[member.UserId]);
    }

    [Fact]
    public async Task AMembershipOfAnotherTenantDoesNotLendItsName()
    {
        var foreign = MemberOf(new TenantId(Guid.CreateVersion7()), "Marta Ruiz");
        var names = new CustomersAuthorNames(
            new InMemoryMembershipRepository(foreign), new StubUserDirectory(new Dictionary<Guid, string>()));

        var result = await names.FindAsync(Tenant.Value, [foreign.UserId], TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AUserWithNoMembershipFallsBackToTheEmailOrIsMissing()
    {
        var withEmail = Guid.CreateVersion7();
        var ghost = Guid.CreateVersion7();
        var names = new CustomersAuthorNames(
            new InMemoryMembershipRepository(),
            new StubUserDirectory(new Dictionary<Guid, string> { [withEmail] = "beto@example.com" }));

        var result = await names.FindAsync(Tenant.Value, [withEmail, ghost, withEmail], TestContext.Current.CancellationToken);

        Assert.Equal("beto@example.com", Assert.Single(result).Value);
        Assert.DoesNotContain(ghost, result.Keys);
    }

    [Fact]
    public async Task NoAuthorsMeansNoLookups()
    {
        var names = new CustomersAuthorNames(new InMemoryMembershipRepository(), new StubUserDirectory(new Dictionary<Guid, string>()));

        Assert.Empty(await names.FindAsync(Tenant.Value, [], TestContext.Current.CancellationToken));
    }
}
