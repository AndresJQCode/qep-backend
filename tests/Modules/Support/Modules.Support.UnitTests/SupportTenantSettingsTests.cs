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

    [Fact]
    public void TheNextStepIsTrimmedAndAnEmptyOneIsNull()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        Assert.Null(settings.FormalNextStep);

        settings.ChangeFormalNextStep("  Puedes acudir a la Superintendencia.\n\tLínea 2  ", Now.AddHours(1));
        Assert.Equal("Puedes acudir a la Superintendencia.\n\tLínea 2", settings.FormalNextStep);
        Assert.Equal(2L, settings.Version);
        Assert.Equal(Now.AddHours(1), settings.UpdatedAt);

        settings.ChangeFormalNextStep("   ", Now.AddHours(2));
        Assert.Null(settings.FormalNextStep);
        Assert.Equal(3L, settings.Version);
    }

    [Fact]
    public void SettingTheSameNextStepDoesNotBumpTheVersion()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        settings.ChangeFormalNextStep("Texto", Now.AddHours(1));

        settings.ChangeFormalNextStep("  Texto ", Now.AddHours(2));
        settings.ChangeFormalNextStep("Texto", Now.AddHours(3));

        Assert.Equal(2L, settings.Version);
        Assert.Equal(Now.AddHours(1), settings.UpdatedAt);

        var empty = SupportTenantSettings.CreateDefault(TenantId, Now);
        empty.ChangeFormalNextStep(null, Now.AddHours(1));
        empty.ChangeFormalNextStep("", Now.AddHours(1));
        Assert.Equal(1L, empty.Version);
    }

    [Fact]
    public void ANextStepLongerThan2000Throws()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.Equal(2000, SupportTenantSettings.FormalNextStepMaxLength);
        settings.ChangeFormalNextStep($"  {new string('a', 2000)}  ", Now.AddHours(1));
        Assert.Throws<ArgumentException>(() => settings.ChangeFormalNextStep(new string('b', 2001), Now.AddHours(2)));
        Assert.Equal(new string('a', 2000), settings.FormalNextStep);
        Assert.Equal(2L, settings.Version);
    }

    /// <summary>La regla del validador repetida en el dominio: un camino que se salte el validador no puede guardar un carácter de
    /// control (un <c>\0</c> llegaría a Postgres como 22021).</summary>
    [Fact]
    public void ANextStepWithAControlCharacterThrows()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        settings.ChangeFormalNextStep("Paso uno.\r\n\tPaso dos.", Now.AddHours(1));
        Assert.Throws<ArgumentException>(() => settings.ChangeFormalNextStep("Con un \0 nulo", Now.AddHours(2)));
        Assert.Throws<ArgumentException>(() => settings.ChangeFormalNextStep("Con un \u0007 de control", Now.AddHours(2)));
        Assert.Throws<ArgumentException>(() => SupportFormalNextStep.Normalize("Con un \u001b escape"));
        Assert.Equal("Paso uno.\r\n\tPaso dos.", settings.FormalNextStep);
    }
}
