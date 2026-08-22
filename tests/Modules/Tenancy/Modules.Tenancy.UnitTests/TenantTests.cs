using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UpdateSettingsWithChangesIncrementsVersionAndRaisesEvent()
    {
        var tenant = CreateTenant();

        var changed = tenant.UpdateSettings(
            "QCode Enterprise",
            "en-US",
            "America/New_York",
            "MM/dd/yyyy",
            CreatedAt.AddMinutes(5));

        Assert.True(changed);
        Assert.Equal(2, tenant.Version);
        var domainEvent = Assert.Single(tenant.DomainEvents);
        var settingsUpdated = Assert.IsType<TenantSettingsUpdatedDomainEvent>(domainEvent);
        Assert.Equal(
            ["displayName", "defaultCulture", "timeZone", "dateFormat"],
            settingsUpdated.ChangedFields);
    }

    [Fact]
    public void UpdateSettingsWithoutChangesDoesNotRaiseEvent()
    {
        var tenant = CreateTenant();

        var changed = tenant.UpdateSettings(
            tenant.DisplayName,
            tenant.DefaultCulture,
            tenant.TimeZone,
            tenant.DateFormat,
            CreatedAt.AddMinutes(5));

        Assert.False(changed);
        Assert.Equal(1, tenant.Version);
        Assert.Empty(tenant.DomainEvents);
    }

    [Fact]
    public void CreateWithInvalidCultureThrowsDomainException()
    {
        var exception = Assert.Throws<TenantDomainException>(() =>
            Tenant.Create(
                TenantId.New(),
                "qcode-demo",
                "QCode Demo",
                "_",
                "America/Bogota",
                "yyyy-MM-dd",
                CreatedAt));

        Assert.Equal("tenancy.settings.culture.invalid", exception.Code);
    }

    private static Tenant CreateTenant() =>
        Tenant.Create(
            TenantId.New(),
            "qcode-demo",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            CreatedAt);
}
