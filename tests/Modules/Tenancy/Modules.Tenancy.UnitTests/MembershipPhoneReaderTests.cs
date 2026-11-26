using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class MembershipPhoneReaderTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwnPhoneWins()
    {
        var membership = NewMembership("+573001112233");
        var directory = new FakeDirectory { [membership.Id.Value] = "+573009998877" };

        var phone = await new MembershipPhoneReader(directory).ForAsync(membership, Ct);

        Assert.Equal("+573001112233", phone.Phone);
        Assert.Equal(MemberPhoneSource.Member, phone.Source);
        Assert.Equal("Member", phone.SourceName);
    }

    [Fact]
    public async Task HrFillsWhenThereIsNoOwnPhone()
    {
        var membership = NewMembership(null);
        var directory = new FakeDirectory { [membership.Id.Value] = "+573009998877" };

        var phone = await new MembershipPhoneReader(directory).ForAsync(membership, Ct);

        Assert.Equal("+573009998877", phone.Phone);
        Assert.Equal(MemberPhoneSource.Hr, phone.Source);
        Assert.Equal("Hr", phone.SourceName);
    }

    [Fact]
    public async Task NeitherIsNone()
    {
        var membership = NewMembership(null);

        var phone = await new MembershipPhoneReader(new FakeDirectory()).ForAsync(membership, Ct);

        Assert.Equal(EffectivePhone.None, phone);
        Assert.Null(phone.SourceName);
    }

    [Fact]
    public async Task DoesNotAskHrWhenEveryMemberHasItsOwn()
    {
        var first = NewMembership("+573001112233");
        var second = NewMembership("+573001112244");
        var directory = new FakeDirectory();

        var phones = await new MembershipPhoneReader(directory).ForAsync([first, second], Ct);

        Assert.Equal(0, directory.Calls);
        Assert.Equal(MemberPhoneSource.Member, phones[first.Id].Source);
        Assert.Equal(MemberPhoneSource.Member, phones[second.Id].Source);
    }

    [Fact]
    public async Task OnlyAsksHrAboutTheMembersWithoutAnOwnPhone()
    {
        var own = NewMembership("+573001112233");
        var bare = NewMembership(null);
        var directory = new FakeDirectory { [bare.Id.Value] = "+573009998877" };

        var phones = await new MembershipPhoneReader(directory).ForAsync([own, bare], Ct);

        Assert.Equal(1, directory.Calls);
        Assert.Equal([bare.Id.Value], directory.Asked);
        Assert.Equal(MemberPhoneSource.Hr, phones[bare.Id].Source);
    }

    [Fact]
    public async Task ClearingTheOwnPhoneBringsBackHr()
    {
        var membership = NewMembership("+573001112233");
        var directory = new FakeDirectory { [membership.Id.Value] = "+573009998877" };
        var reader = new MembershipPhoneReader(directory);
        Assert.Equal(MemberPhoneSource.Member, (await reader.ForAsync(membership, Ct)).Source);

        membership.ChangeOwnPhone(null, At.AddHours(1));
        var after = await reader.ForAsync(membership, Ct);

        Assert.Equal("+573009998877", after.Phone);
        Assert.Equal(MemberPhoneSource.Hr, after.Source);
    }

    private static Membership NewMembership(string? ownPhone)
    {
        var membership = Membership.Invite(
            MembershipId.New(),
            Guid.CreateVersion7(),
            TenantId.New(),
            "Ana Pérez",
            ["advisor"],
            "invitation",
            "token",
            "token-hash",
            At,
            Membership.DefaultInvitationTimeToLive);
        if (ownPhone is not null)
        {
            membership.ChangeOwnPhone(ownPhone, At);
        }

        return membership;
    }

    // Keyed by membership id; counts the calls so "no own phone to fill" can be proven as zero.
    private sealed class FakeDirectory : Dictionary<Guid, string>, IMemberPhoneDirectory
    {
        public int Calls { get; private set; }

        public IReadOnlyCollection<Guid> Asked { get; private set; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> FindHrPhonesAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> memberIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            Asked = memberIds;
            IReadOnlyDictionary<Guid, string> found = memberIds
                .Where(ContainsKey)
                .ToDictionary(id => id, id => this[id]);
            return Task.FromResult(found);
        }
    }
}
