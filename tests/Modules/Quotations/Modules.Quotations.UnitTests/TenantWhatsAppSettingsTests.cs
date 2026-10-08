using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La configuración de WhatsApp por tenant (spec 2026-10-07, «Domain»): tres modos que se cambian
/// en cualquier dirección sin perder la cuenta propia, un guardado = una versión, y la rotación de
/// llave que no cuenta como «la key cambió».
/// </summary>
public sealed class TenantWhatsAppSettingsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Created.AddHours(1);
    private static readonly DateTimeOffset Latest = Created.AddHours(2);
    private const string FromNumber = "573001234567";
    private const string TemplateId = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";

    private static ProtectedSecret Token(string keyId = "k1") => new(keyId, [1, 2, 3, 4]);

    private static TenantWhatsAppSettings OwnConfigured()
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);
        settings.Configure(
            WhatsAppMode.Own, WhatsAppProvider.Zenvia, Token(), null, FromNumber, TemplateId, Created);
        return settings;
    }

    [Fact]
    public void CreateEmptyIsSharedAtVersionOneWithoutCredentials()
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);

        Assert.Equal(TenantId, settings.TenantId);
        Assert.Equal(WhatsAppMode.Shared, settings.Mode);
        Assert.Equal(1, settings.Version);
        Assert.Null(settings.Provider);
        Assert.Null(settings.ApiToken);
        Assert.Null(settings.ApiTokenUpdatedAt);
        Assert.Null(settings.FromNumber);
        Assert.Null(settings.TemplateId);
    }

    public static TheoryData<WhatsAppProvider?, bool, string?, string?> IncompleteOwn => new()
    {
        { null, true, FromNumber, TemplateId },
        { WhatsAppProvider.Zenvia, false, FromNumber, TemplateId },
        { WhatsAppProvider.Zenvia, true, null, TemplateId },
        { WhatsAppProvider.Zenvia, true, FromNumber, null },
    };

    [Theory]
    [MemberData(nameof(IncompleteOwn))]
    public void OwnWithoutAnyOfTheFourPiecesIsIncomplete(
        WhatsAppProvider? provider, bool withToken, string? fromNumber, string? templateId)
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);

        var error = Assert.Throws<QuotationsDomainException>(() => settings.Configure(
            WhatsAppMode.Own, provider, withToken ? Token() : null, null, fromNumber, templateId, Later));

        Assert.Equal("quotation.whatsapp_settings.incomplete", error.Code);
        Assert.Equal(WhatsAppMode.Shared, settings.Mode);
        Assert.Equal(1, settings.Version);
    }

    [Theory]
    [InlineData(WhatsAppMode.Shared)]
    [InlineData(WhatsAppMode.Disabled)]
    public void LeavingOwnKeepsTokenNumberAndTemplate(WhatsAppMode target)
    {
        var settings = OwnConfigured();
        var token = settings.ApiToken;

        var changes = settings.Configure(target, null, null, null, null, null, Later);

        Assert.True(changes.ModeChanged);
        Assert.Equal(target, settings.Mode);
        Assert.Same(token, settings.ApiToken);
        Assert.Equal(WhatsAppProvider.Zenvia, settings.Provider);
        Assert.Equal(FromNumber, settings.FromNumber);
        Assert.Equal(TemplateId, settings.TemplateId);
    }

    [Fact]
    public void BackToOwnWithEverythingStoredNeedsNothingNew()
    {
        var settings = OwnConfigured();
        settings.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Later);

        var changes = settings.Configure(WhatsAppMode.Own, null, null, null, null, null, Latest);

        Assert.True(changes.ModeChanged);
        Assert.False(changes.ApiKeyReplaced);
        Assert.Equal(WhatsAppMode.Own, settings.Mode);
    }

    [Fact]
    public void AnIdenticalConfigureReportsNothingAndKeepsTheVersion()
    {
        var settings = OwnConfigured();
        var version = settings.Version;

        var changes = settings.Configure(
            WhatsAppMode.Own, WhatsAppProvider.Zenvia, null, null, FromNumber, TemplateId, Later);

        Assert.False(changes.Any);
        Assert.Equal(version, settings.Version);
        Assert.Equal(Created, settings.UpdatedAt);
    }

    [Fact]
    public void ANullNewTokenKeepsTheStoredOne()
    {
        var settings = OwnConfigured();
        var token = settings.ApiToken;

        settings.Configure(WhatsAppMode.Own, null, null, null, null, "11111111-2222-3333-4444-555555555555", Later);

        Assert.Same(token, settings.ApiToken);
        Assert.Equal(Created, settings.ApiTokenUpdatedAt);
    }

    // El dominio no ve el texto: un token nuevo siempre es reemplazo, aunque sea el mismo.
    [Fact]
    public void ANewTokenIsAReplacementAndMovesItsTimestamp()
    {
        var settings = OwnConfigured();
        var replacement = Token();

        var changes = settings.Configure(WhatsAppMode.Own, null, replacement, null, null, null, Later);

        Assert.True(changes.ApiKeyReplaced);
        Assert.False(changes.ModeChanged);
        Assert.Same(replacement, settings.ApiToken);
        Assert.Equal(Later, settings.ApiTokenUpdatedAt);
    }

    [Fact]
    public void ChangingTheModeReportsModeChanged()
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);

        var changes = settings.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Later);

        Assert.Equal(new WhatsAppSettingsChanges(true, false, false, false), changes);
        Assert.Equal(2, settings.Version);
        Assert.Equal(Later, settings.UpdatedAt);
    }

    [Fact]
    public void ARekeyedTokenIsKeyRotatedAndKeepsTheTimestamp()
    {
        var settings = OwnConfigured();
        var rekeyed = Token("k2");

        var changes = settings.Configure(WhatsAppMode.Own, null, null, rekeyed, null, null, Later);

        Assert.Equal(new WhatsAppSettingsChanges(false, false, false, true), changes);
        Assert.Same(rekeyed, settings.ApiToken);
        Assert.Equal(Created, settings.ApiTokenUpdatedAt);
    }

    // Un guardado es un incremento, por muchas clases de cambio que traiga.
    [Fact]
    public void ModeDetailsAndRekeyTogetherBumpTheVersionExactlyOnce()
    {
        var settings = OwnConfigured();
        var version = settings.Version;

        var changes = settings.Configure(
            WhatsAppMode.Disabled, null, null, Token("k2"), "573009876543", null, Later);

        Assert.True(changes.ModeChanged);
        Assert.True(changes.DetailsChanged);
        Assert.True(changes.KeyRotated);
        Assert.Equal(version + 1, settings.Version);
    }

    [Fact]
    public void ReprotectBumpsTheVersionOnceAndKeepsTheTimestamp()
    {
        var settings = OwnConfigured();
        var version = settings.Version;
        var rekeyed = Token("k2");

        Assert.True(settings.Reprotect(rekeyed, Later));

        Assert.Same(rekeyed, settings.ApiToken);
        Assert.Equal(version + 1, settings.Version);
        Assert.Equal(Created, settings.ApiTokenUpdatedAt);
        Assert.Equal(Later, settings.UpdatedAt);
    }

    [Fact]
    public void ReprotectWithoutATokenDoesNothing()
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Created);

        Assert.False(settings.Reprotect(Token("k2"), Later));
        Assert.Equal(1, settings.Version);
    }

    [Fact]
    public void ANewTokenAndARekeyedOneTogetherAreAProgrammingError()
    {
        var settings = OwnConfigured();

        Assert.Throws<ArgumentException>(() => settings.Configure(
            WhatsAppMode.Own, null, Token(), Token("k2"), null, null, Later));
    }

    [Fact]
    public void ProtectedSecretToStringDoesNotPrintTheBytes()
    {
        Assert.Equal("ProtectedSecret { KeyId = k1 }", Token().ToString());
    }
}
