using BuildingBlocks.Domain.Localization;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

// Spec 2026-11-22 (i18n): el idioma del tenant es el respaldo de quien no eligió uno.
public sealed class TenantLanguageTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewTenantSpeaksLatinAmericanSpanish()
    {
        var tenant = CreateTenant();

        Assert.Equal("es-419", tenant.Language);
    }

    [Theory]
    [InlineData("es-ES", "es-ES")]
    [InlineData(" es-es ", "es-ES")]
    [InlineData("ES-419", "es-419")]
    public void UpdateSettingsChangesTheLanguageNormalizedAndTracksIt(string requested, string stored)
    {
        var tenant = CreateTenant();
        if (stored == "es-419")
        {
            Change(tenant, "es-ES");
            tenant.PullDomainEvents();
        }

        var changed = Change(tenant, requested);

        Assert.True(changed);
        Assert.Equal(stored, tenant.Language);
        var settingsUpdated = Assert.IsType<TenantSettingsUpdatedDomainEvent>(Assert.Single(tenant.DomainEvents));
        Assert.Equal(["language"], settingsUpdated.ChangedFields);
    }

    [Fact]
    public void ANullLanguageKeepsTheStoredOne()
    {
        var tenant = CreateTenant();
        Change(tenant, "es-ES");

        var changed = Change(tenant, null);

        Assert.False(changed);
        Assert.Equal("es-ES", tenant.Language);
    }

    [Theory]
    [InlineData("")]
    [InlineData("es")]
    [InlineData("es-CO")]
    [InlineData("en-US")]
    public void AnUnsupportedLanguageThrowsAndChangesNothing(string language)
    {
        var tenant = CreateTenant();

        var exception = Assert.Throws<TenantDomainException>(() => Change(tenant, language));

        Assert.Equal("tenancy.settings.language.invalid", exception.Code);
        Assert.Equal(SupportedLanguages.Default, tenant.Language);
        Assert.Equal(1, tenant.Version);
    }

    // Quien registra el tenant puede elegir el idioma desde el alta (spec 2026-11-22, i18n).
    [Theory]
    [InlineData(null, "es-419")]
    [InlineData("es-es", "es-ES")]
    public void CreateTakesTheLanguageOfTheRegistration(string? requested, string stored)
    {
        var tenant = Tenant.Create(
            TenantId.New(), "qcode-demo", "QCode Demo", "es-CO", "America/Bogota", "yyyy-MM-dd",
            MembershipId.New(), CreatedAt, language: requested);

        Assert.Equal(stored, tenant.Language);
    }

    [Fact]
    public void ARegistrationWithAnUnsupportedLanguageFailsBeforeCreatingAnything()
    {
        var exception = Assert.Throws<TenantDomainException>(() =>
            Tenant.EnsureValidRegistration("acme", "Acme", "es-CO", "America/Bogota", "dd/MM/yyyy", language: "en-US"));

        Assert.Equal("tenancy.settings.language.invalid", exception.Code);
    }

    private static bool Change(Tenant tenant, string? language) =>
        tenant.UpdateSettings(
            tenant.DisplayName, tenant.DefaultCulture, tenant.TimeZone, tenant.DateFormat,
            tenant.DefaultCurrency, tenant.NumberFormat, tenant.Country, tenant.PersonTypeRequirement,
            CreatedAt.AddMinutes(5), language: language);

    private static Tenant CreateTenant() =>
        Tenant.Create(
            TenantId.New(), "qcode-demo", "QCode Demo", "es-CO", "America/Bogota", "yyyy-MM-dd",
            MembershipId.New(), CreatedAt);
}
