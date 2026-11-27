using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

/// <summary>Spec 2026-11-26 §5.1: the setting aggregate (connection rule, de-duplication, version). Pure, no database.</summary>
public sealed class BirthdayNoticeSettingTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Manager = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid OtherManager = Guid.Parse("01900000-0000-7000-8000-0000000000a2");
    private static readonly Guid Connection = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    private static BirthdayNoticeValues Values(
        IReadOnlyList<Guid>? managers = null,
        bool whatsApp = false,
        Guid? connection = null,
        bool email = true) =>
        new(managers ?? [Manager], BirthdayNoticeSetting.DefaultLeadDays, whatsApp, connection, email);

    [Fact]
    public void CreateStartsAtVersion1()
    {
        var setting = BirthdayNoticeSetting.Create(Tenant, Values(), Manager, Now);

        Assert.Equal(1, setting.Version);
        Assert.Equal(Tenant, setting.TenantId);
        Assert.Equal(Manager, setting.UpdatedByMemberId);
        Assert.Equal(Now, setting.CreatedAt);
        Assert.Equal(Now, setting.UpdatedAt);
    }

    [Fact]
    public void UpdateBumpsVersion()
    {
        var setting = BirthdayNoticeSetting.Create(Tenant, Values(), Manager, Now);
        var later = Now.AddHours(1);

        setting.Update(Values(managers: [OtherManager]), OtherManager, later);

        Assert.Equal(2, setting.Version);
        Assert.Equal([OtherManager], setting.ManagerMemberIds);
        Assert.Equal(OtherManager, setting.UpdatedByMemberId);
        Assert.Equal(Now, setting.CreatedAt);
        Assert.Equal(later, setting.UpdatedAt);
    }

    [Fact]
    public void WhatsAppEnabledWithoutConnectionThrows()
    {
        var error = Assert.Throws<HrDomainException>(
            () => BirthdayNoticeSetting.Create(Tenant, Values(whatsApp: true, connection: null), Manager, Now));

        Assert.Equal("hr.birthday_notice.connection_required", error.Code);
        Assert.Contains("whatsAppConnectionId", error.FieldErrors.Keys);
    }

    [Fact]
    public void WhatsAppDisabledClearsConnection()
    {
        var setting = BirthdayNoticeSetting.Create(Tenant, Values(whatsApp: false, connection: Connection), Manager, Now);

        Assert.Null(setting.WhatsAppConnectionId);
    }

    [Fact]
    public void DuplicateManagersAreStoredOnce()
    {
        var setting = BirthdayNoticeSetting.Create(Tenant, Values(managers: [Manager, OtherManager, Manager]), Manager, Now);

        Assert.Equal([Manager, OtherManager], setting.ManagerMemberIds);
    }
}
