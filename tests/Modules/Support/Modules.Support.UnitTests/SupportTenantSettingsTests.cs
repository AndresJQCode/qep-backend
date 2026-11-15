using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Pre-flight R3: el umbral de «por vencer» del tenant [owner 2026-11-14], 0 a 30 días hábiles, 3 por defecto.
public sealed class SupportTenantSettingsTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheRangeIsZeroToThirtyAndTheDefaultIsThree()
    {
        Assert.Equal(3, SupportTenantSettings.DefaultDueSoonBusinessDays);
        Assert.Equal(0, SupportTenantSettings.MinDueSoonBusinessDays);
        Assert.Equal(30, SupportTenantSettings.MaxDueSoonBusinessDays);
    }

    [Fact]
    public void ANewRowHasTheDefaultAndVersionOne()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.Equal(TenantId, settings.TenantId);
        Assert.Equal(3, settings.DueSoonBusinessDays);
        Assert.Equal(1L, settings.Version);
        Assert.Equal(Now, settings.UpdatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(30)]
    public void ChangingTheThresholdBumpsTheVersion(int businessDays)
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        settings.ChangeDueSoon(businessDays, Now.AddHours(1));

        Assert.Equal(businessDays, settings.DueSoonBusinessDays);
        Assert.Equal(2L, settings.Version);
        Assert.Equal(Now.AddHours(1), settings.UpdatedAt);
    }

    [Fact]
    public void TheSameThresholdIsANoOp()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        settings.ChangeDueSoon(3, Now.AddHours(1));

        Assert.Equal(1L, settings.Version);
        Assert.Equal(Now, settings.UpdatedAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void AThresholdOutOfRangeIsRejected(int businessDays)
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ChangeDueSoon(businessDays, Now.AddHours(1)));
        Assert.Equal(3, settings.DueSoonBusinessDays);
        Assert.Equal(1L, settings.Version);
    }
}
