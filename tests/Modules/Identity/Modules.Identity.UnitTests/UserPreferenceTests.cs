using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

/// <summary>
/// ACC-03. La preferencia de apariencia es del usuario <b>en cada tenant</b> (SDD-OD-17),
/// así que la identidad de la entidad es el par y no el usuario solo.
/// </summary>
public sealed class UserPreferenceTests
{
    private static readonly DateTimeOffset UpdatedAt =
        new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateDefaultUsesBotanicalAndLight()
    {
        var preference = UserPreference.CreateDefault(UserId.New(), Guid.NewGuid(), UpdatedAt);

        // CA-ACC-03-01: el default reproduce lo que el producto ya muestra, así que quien
        // nunca elige no ve ningún cambio.
        Assert.Equal("botanical", preference.ColorScheme);
        Assert.Equal(ThemeMode.Light, preference.Mode);
    }

    [Fact]
    public void CreateNormalizesColorScheme()
    {
        var preference = UserPreference.Create(
            UserId.New(),
            Guid.NewGuid(),
            "  Botanical ",
            "dark",
            messageSound: true,
            UpdatedAt);

        Assert.Equal("botanical", preference.ColorScheme);
        Assert.Equal(ThemeMode.Dark, preference.Mode);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("")]
    [InlineData("LIGHTS")]
    public void CreateRejectsUnknownMode(string mode)
    {
        // CA-ACC-03-07. `mode` sí es un conjunto cerrado: son dos valores y el spec los fija.
        // "system" está fuera a propósito — es SDD-OD-20, todavía abierta.
        var exception = Assert.Throws<IdentityDomainException>(() =>
            UserPreference.Create(UserId.New(), Guid.NewGuid(), "botanical", mode, messageSound: true, UpdatedAt));

        Assert.Equal("identity.preference.mode.invalid", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Botánica")]
    [InlineData("scheme with spaces")]
    [InlineData("under_score")]
    [InlineData("way-too-long-color-scheme-identifier-abcdefghijklmnop")]
    public void CreateRejectsMalformedColorScheme(string colorScheme)
    {
        // CA-ACC-03-08: se valida la FORMA, no la pertenencia a un catálogo.
        var exception = Assert.Throws<IdentityDomainException>(() =>
            UserPreference.Create(UserId.New(), Guid.NewGuid(), colorScheme, "light", messageSound: true, UpdatedAt));

        Assert.Equal("identity.preference.scheme.invalid", exception.Code);
    }

    [Fact]
    public void CreateAcceptsWellFormedSchemeTheBackendDoesNotKnow()
    {
        // CA-ACC-03-09. El catálogo de esquemas es del frontend (ficha de `account`);
        // duplicarlo acá crearía dos autoridades sobre lo mismo, y agregar un esquema
        // pasaría a requerir un deploy del backend. Un id desconocido degrada al default
        // en el cliente, que ya está construido.
        var preference = UserPreference.Create(
            UserId.New(),
            Guid.NewGuid(),
            "midnight-2",
            "dark",
            messageSound: true,
            UpdatedAt);

        Assert.Equal("midnight-2", preference.ColorScheme);
    }

    [Fact]
    public void ChangeReplacesBothAxesAndMovesUpdatedAt()
    {
        var preference = UserPreference.CreateDefault(UserId.New(), Guid.NewGuid(), UpdatedAt);

        preference.Change("ocean", "dark", messageSound: true, UpdatedAt.AddHours(1));

        Assert.Equal("ocean", preference.ColorScheme);
        Assert.Equal(ThemeMode.Dark, preference.Mode);
        Assert.Equal(UpdatedAt.AddHours(1), preference.UpdatedAt);
    }

    [Fact]
    public void ChangeRejectsInvalidValuesAndLeavesThePreferenceUntouched()
    {
        var preference = UserPreference.CreateDefault(UserId.New(), Guid.NewGuid(), UpdatedAt);

        Assert.Throws<IdentityDomainException>(() =>
            preference.Change("ocean", "system", messageSound: true, UpdatedAt.AddHours(1)));

        // Una preferencia rechazada no puede quedar a medias: ni el esquema ni la marca de
        // tiempo se mueven si el modo no valida.
        Assert.Equal("botanical", preference.ColorScheme);
        Assert.Equal(ThemeMode.Light, preference.Mode);
        Assert.Equal(UpdatedAt, preference.UpdatedAt);
    }

    // Spec 2026-10-14 (Mensajería en tiempo real), decisión 7: el sonido de mensaje nuevo es una preferencia de la
    // persona en el tenant, al lado del tema, y quien nunca eligió lo tiene encendido.
    [Fact]
    public void CreateDefaultTurnsTheMessageSoundOn()
    {
        var preference = UserPreference.CreateDefault(UserId.New(), Guid.NewGuid(), UpdatedAt);

        Assert.True(preference.MessageSound);
    }

    [Fact]
    public void CreateAndChangeCarryTheMessageSound()
    {
        var preference = UserPreference.Create(UserId.New(), Guid.NewGuid(), "botanical", "light", messageSound: false, UpdatedAt);
        Assert.False(preference.MessageSound);

        preference.Change("botanical", "light", messageSound: true, UpdatedAt.AddHours(1));

        Assert.True(preference.MessageSound);
        Assert.Equal(UpdatedAt.AddHours(1), preference.UpdatedAt);
    }

    [Fact]
    public void AChangeWithAnInvalidModeLeavesTheMessageSoundUntouchedToo()
    {
        var preference = UserPreference.CreateDefault(UserId.New(), Guid.NewGuid(), UpdatedAt);

        Assert.Throws<IdentityDomainException>(() => preference.Change("ocean", "system", messageSound: false, UpdatedAt.AddHours(1)));

        Assert.True(preference.MessageSound);
    }
}
