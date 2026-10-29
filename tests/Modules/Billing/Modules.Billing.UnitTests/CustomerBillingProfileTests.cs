using Modules.Billing.Domain;

namespace Modules.Billing.UnitTests;

public sealed class CustomerBillingProfileTests
{
    [Fact]
    public void ChangeBumpsVersionAndUpdatedAt()
    {
        var created = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        var profile = CustomerBillingProfile.Create(Guid.NewGuid(), Guid.NewGuid(), BillingChannel.WhatsApp, Guid.NewGuid(), created);
        var version = profile.Version;
        var assignee = Guid.NewGuid();

        profile.Change(BillingChannel.Email, assignee, created.AddHours(1));

        Assert.Equal(BillingChannel.Email, profile.Channel);
        Assert.Equal(assignee, profile.AssigneeMemberId);
        Assert.Equal(version + 1, profile.Version);
        Assert.Equal(created.AddHours(1), profile.UpdatedAt);
        Assert.Equal(created, profile.CreatedAt);
    }
}
