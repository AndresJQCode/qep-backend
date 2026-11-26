using Bootstrapper;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: the member an escalation is for, read from Tenancy (state, name, phone) and Identity (email). The
/// phone is the effective one (own, else HR), already normalized; a membership that does not exist is <c>null</c>.
/// </summary>
public sealed class TaskNoticeRecipientsTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SuspendedMembershipIsInactive()
    {
        var member = Active("Ana Gómez");
        var owner = MembershipId.New();
        var tenant = Modules.Tenancy.Domain.Tenant.Create(
            Tenant, "acme", "Acme", "es-CO", "America/Bogota", "dd/MM/yyyy", owner, Now);
        member.Suspend(tenant, Now);

        var recipient = await Recipients(member, email: "ana@example.com").FindAsync(Tenant.Value, member.Id.Value, Ct);

        Assert.False(Assert.IsType<Modules.Notifications.Application.TaskNoticeRecipient>(recipient).IsActive);
    }

    [Fact]
    public async Task UsesTheEffectivePhone()
    {
        var own = Active("Ana Gómez");
        own.ChangeOwnPhone("+573001234567", Now);
        var fromHr = Active("Beto Ruiz");

        var recipients = Recipients([own, fromHr], "x@example.com", new Dictionary<Guid, string> { [fromHr.Id.Value] = "+573119998877" });

        Assert.Equal("+573001234567", (await recipients.FindAsync(Tenant.Value, own.Id.Value, Ct))!.PhoneE164);
        Assert.Equal("+573119998877", (await recipients.FindAsync(Tenant.Value, fromHr.Id.Value, Ct))!.PhoneE164);
    }

    [Fact]
    public async Task TheNameAndEmailComeFromTheMembershipAndTheUser()
    {
        var named = Active("Ana Gómez");
        var unnamed = Membership.CreateActive(MembershipId.New(), Guid.CreateVersion7(), Tenant, ["advisor"], "signup", Now);

        var recipients = Recipients([named, unnamed], "ana@example.com", new Dictionary<Guid, string>());

        var withName = (await recipients.FindAsync(Tenant.Value, named.Id.Value, Ct))!;
        Assert.True(withName.IsActive);
        Assert.Equal("Ana Gómez", withName.DisplayName);
        Assert.Equal("ana@example.com", withName.Email);
        Assert.Equal("ana@example.com", (await recipients.FindAsync(Tenant.Value, unnamed.Id.Value, Ct))!.DisplayName);
    }

    [Fact]
    public async Task UnknownMembershipIsNull()
    {
        var recipients = Recipients([Active("Ana Gómez")], "ana@example.com", new Dictionary<Guid, string>());

        Assert.Null(await recipients.FindAsync(Tenant.Value, Guid.CreateVersion7(), Ct));
        Assert.Null(await recipients.FindAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), Ct));
    }

    private static Membership Active(string name)
    {
        var member = Membership.CreateActive(MembershipId.New(), Guid.CreateVersion7(), Tenant, ["advisor"], "signup", Now);
        member.UpdateProfile(name, advisorCode: null, Now);
        return member;
    }

    private static TaskNoticeRecipients Recipients(Membership member, string email) =>
        Recipients([member], email, new Dictionary<Guid, string>());

    private static TaskNoticeRecipients Recipients(
        IReadOnlyList<Membership> members, string email, IReadOnlyDictionary<Guid, string> hrPhones) =>
        new(
            new InMemoryMembershipRepository([.. members]),
            new StubUserDirectory(members.ToDictionary(member => member.UserId, _ => email)),
            new MembershipPhoneReader(new StubPhoneDirectory(hrPhones)));

    private sealed class StubPhoneDirectory(IReadOnlyDictionary<Guid, string> phones) : IMemberPhoneDirectory
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindHrPhonesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                memberIds.Where(phones.ContainsKey).ToDictionary(id => id, id => phones[id]));
    }
}
