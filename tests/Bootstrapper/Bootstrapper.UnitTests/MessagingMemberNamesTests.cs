using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-10-12, decisión del owner: <c>{asesor}</c> llega al cliente por WhatsApp, así que sale sólo con el
/// <c>DisplayName</c> de la membresía; nunca con el correo (a diferencia de <c>FindAsync</c>, que es para el equipo).</summary>
public sealed class MessagingMemberNamesTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static Membership NamedMember(TenantId tenant, string displayName)
    {
        var userId = Guid.CreateVersion7();
        return Membership.Invite(
            MembershipId.New(), userId, tenant, displayName, ["advisor"], "invitation",
            $"token-{userId:N}", $"hash-{userId:N}", Now.AddHours(-1), Membership.DefaultInvitationTimeToLive);
    }

    private static Membership UnnamedMember(TenantId tenant) =>
        Membership.CreateActive(MembershipId.New(), Guid.CreateVersion7(), tenant, ["advisor"], "signup", Now);

    [Fact]
    public async Task FindDisplayNameReturnsTheMembershipName()
    {
        var member = NamedMember(Tenant, "Ana Gómez");
        var names = new MessagingMemberNames(
            new InMemoryMembershipRepository(member),
            new StubUserDirectory(new Dictionary<Guid, string> { [member.UserId] = "ana@example.com" }));

        Assert.Equal("Ana Gómez", await names.FindDisplayNameAsync(Tenant.Value, member.Id.Value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindDisplayNameNeverFallsBackToTheEmail()
    {
        var member = UnnamedMember(Tenant);
        Assert.True(string.IsNullOrWhiteSpace(member.DisplayName));
        var names = new MessagingMemberNames(
            new InMemoryMembershipRepository(member),
            new StubUserDirectory(new Dictionary<Guid, string> { [member.UserId] = "ana@example.com" }));

        Assert.Null(await names.FindDisplayNameAsync(Tenant.Value, member.Id.Value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindDisplayNameIsNullForAnotherTenantsMembership()
    {
        var foreign = NamedMember(new TenantId(Guid.CreateVersion7()), "Marta Ruiz");
        var names = new MessagingMemberNames(
            new InMemoryMembershipRepository(foreign), new StubUserDirectory(new Dictionary<Guid, string>()));

        Assert.Null(await names.FindDisplayNameAsync(Tenant.Value, foreign.Id.Value, TestContext.Current.CancellationToken));
    }
}
