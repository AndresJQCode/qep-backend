using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests;

public sealed class OwnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public void TheSelfOwnerCannotBeDeactivated()
    {
        var self = Owner.CreateSelf(OwnerId.New(), Tenant, "Mi empresa", Now);

        var error = Assert.Throws<WmsDomainException>(() => self.Update("Mi empresa", null, null, false, Now));

        Assert.Equal(WmsErrorCodes.OwnerSelfImmutable, error.Code);
        Assert.Equal(OwnerKind.Self, self.Kind);
        Assert.True(self.IsActive);
    }

    [Fact]
    public void AClientOwnerCanBeDeactivated()
    {
        var client = Owner.CreateClient(OwnerId.New(), Tenant, "Cliente SAS", "900123", "a@b.co", Now);
        var before = client.Version;

        client.Update("Cliente SAS", "900123", "a@b.co", false, Now.AddHours(1));

        Assert.False(client.IsActive);
        Assert.Equal(OwnerKind.Client, client.Kind);
        Assert.Equal(before + 1, client.Version);
    }
}
