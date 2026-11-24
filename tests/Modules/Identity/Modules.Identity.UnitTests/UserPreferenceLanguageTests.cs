using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

/// <summary>
/// Spec 2026-11-22 (i18n): el idioma es de la persona en cada tenant, igual que el tema. Guardado
/// <c>null</c> significa «el del tenant»; quién resuelve el efectivo es quien ve los dos.
/// </summary>
public sealed class UserPreferenceLanguageTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SomeoneWhoNeverChoseFollowsTheTenant()
    {
        var preference = UserPreference.CreateDefault(UserId.New(), Guid.NewGuid(), UpdatedAt);

        Assert.Null(preference.Language);
    }

    [Theory]
    [InlineData("es-ES", "es-ES")]
    [InlineData(" ES-es ", "es-ES")]
    [InlineData("es-419", "es-419")]
    [InlineData(null, null)]
    public void CreateKeepsTheLanguageNormalized(string? requested, string? stored)
    {
        var preference = UserPreference.Create(
            UserId.New(), Guid.NewGuid(), "botanical", "light", messageSound: true, UpdatedAt, requested);

        Assert.Equal(stored, preference.Language);
    }

    [Fact]
    public void ChangeCanGoBackToTheTenantLanguage()
    {
        var preference = UserPreference.Create(
            UserId.New(), Guid.NewGuid(), "botanical", "light", messageSound: true, UpdatedAt, "es-ES");

        preference.Change("botanical", "light", messageSound: true, UpdatedAt, language: null);

        Assert.Null(preference.Language);
    }

    [Theory]
    [InlineData("")]
    [InlineData("es")]
    [InlineData("es-CO")]
    [InlineData("en")]
    public void AnUnsupportedLanguageIsRejectedAndNothingChanges(string language)
    {
        var preference = UserPreference.Create(
            UserId.New(), Guid.NewGuid(), "botanical", "light", messageSound: true, UpdatedAt, "es-ES");

        var exception = Assert.Throws<IdentityDomainException>(() =>
            preference.Change("ocean", "dark", messageSound: false, UpdatedAt, language));

        Assert.Equal("identity.preference.language.invalid", exception.Code);
        Assert.Equal("botanical", preference.ColorScheme);
        Assert.Equal("es-ES", preference.Language);
    }
}
