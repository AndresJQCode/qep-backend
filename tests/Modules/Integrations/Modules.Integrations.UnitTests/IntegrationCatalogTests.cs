using BuildingBlocks.Domain;
using Modules.Integrations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Catálogo» y «Visibilidad para un tenant»: la lista cerrada de la versión 1, la
/// forma de claves y campos, y quién ve qué proveedor.
/// </summary>
public sealed class IntegrationCatalogTests
{
    private static readonly IntegrationProvider WithoutConsumers = new(
        "orphan", "Sin consumidor", IntegrationCategory.Ai, [],
        [new FieldDefinition("apiKey", "Clave", FieldKind.Secret, required: true, maxLength: 64, pattern: null, invalidMessage: "Revisa la clave.")],
        maxConnections: 1);

    [Fact]
    public void TheFirstCatalogHasOnlyZenvia() =>
        Assert.Equal(["zenvia"], IntegrationProviders.All.Select(provider => provider.Key));

    [Fact]
    public void ZenviaDeclaresWhatTheSpecSays()
    {
        var zenvia = IntegrationProviders.Zenvia;

        Assert.Equal("Zenvia (WhatsApp)", zenvia.DisplayName);
        Assert.Equal(IntegrationCategory.Messaging, zenvia.Category);
        Assert.Equal([TenantModuleKeys.Quotations], zenvia.ConsumingModules);
        Assert.Equal(20, zenvia.MaxConnections);
        Assert.Equal(["apiToken", "fromNumber"], zenvia.Fields.Select(field => field.Key));

        var apiToken = zenvia.FindField(ZenviaFieldKeys.ApiToken);
        Assert.NotNull(apiToken);
        Assert.Equal(FieldKind.Secret, apiToken.Kind);
        Assert.True(apiToken.Required);
        Assert.Equal(512, apiToken.MaxLength);
        Assert.True(apiToken.IsSecret);

        var fromNumber = zenvia.FindField(ZenviaFieldKeys.FromNumber);
        Assert.NotNull(fromNumber);
        Assert.Equal(FieldKind.Phone, fromNumber.Kind);
        Assert.True(fromNumber.Required);
        Assert.Equal(15, fromNumber.MaxLength);

        Assert.Equal(["fromNumber"], zenvia.PublicFields.Select(field => field.Key));
        Assert.Equal(["apiToken"], zenvia.SecretFields.Select(field => field.Key));
    }

    [Fact]
    public void ParseAndFindAreOrdinal()
    {
        Assert.Same(IntegrationProviders.Zenvia, IntegrationProviders.Parse("zenvia"));
        Assert.Same(IntegrationProviders.Zenvia, IntegrationProviders.Find("zenvia"));
        Assert.Null(IntegrationProviders.Find("Zenvia"));
        Assert.Null(IntegrationProviders.Find(null));
        Assert.Throws<ArgumentException>(() => IntegrationProviders.Parse("whatsapp"));
    }

    [Fact]
    public void AProviderIsVisibleWhenOneOfItsModulesIsEnabled() =>
        Assert.True(IntegrationProviders.Zenvia.IsVisibleFor(TenantModuleSet.FromStored(TenantModuleKeys.All)));

    [Fact]
    public void AProviderIsHiddenWhenItsModulesAreOff() =>
        Assert.False(IntegrationProviders.Zenvia.IsVisibleFor(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Quotations]))));

    // Contratado pero apagado por una dependencia: cuenta lo efectivo, como en TenantModuleGuard.
    [Fact]
    public void AContractedButIneffectiveModuleDoesNotMakeItVisible() =>
        Assert.False(IntegrationProviders.Zenvia.IsVisibleFor(TenantModuleSet.FromStored([TenantModuleKeys.Quotations])));

    [Fact]
    public void TheDevelopmentStubSeesEverything() =>
        Assert.True(IntegrationProviders.Zenvia.IsVisibleFor(null));

    [Fact]
    public void AProviderWithoutConsumersIsNeverVisible()
    {
        Assert.False(WithoutConsumers.IsVisibleFor(null));
        Assert.False(WithoutConsumers.IsVisibleFor(TenantModuleSet.FromStored(TenantModuleKeys.All)));
    }

    [Theory]
    [InlineData("z")]
    [InlineData("Zenvia")]
    [InlineData("zen_via")]
    [InlineData("a23456789012345678901234567890123")]
    [InlineData("zenvia\n")]
    public void AProviderKeyOutsideItsShapeIsAProgrammingError(string key) =>
        Assert.Throws<ArgumentException>(() => new IntegrationProvider(
            key, "X", IntegrationCategory.Messaging, [TenantModuleKeys.Quotations], WithoutConsumers.Fields, 1));

    [Theory]
    [InlineData("a")]
    [InlineData("1abc")]
    [InlineData("api-token")]
    [InlineData("api_token")]
    [InlineData("apiToken\n")]
    public void AFieldKeyOutsideItsShapeIsAProgrammingError(string key) =>
        Assert.Throws<ArgumentException>(() =>
            new FieldDefinition(key, "X", FieldKind.Text, required: false, maxLength: 10, pattern: null, invalidMessage: "X"));

    [Theory]
    [InlineData("573001234567", true)]
    [InlineData("5730012345", true)]
    [InlineData("+573001234567", false)]
    [InlineData("57300 12345", false)]
    [InlineData("573001234", false)]
    [InlineData("57300\u00001234567", false)]
    [InlineData("573001234567\n", false)]
    public void TheSenderNumberIsE164WithoutPlus(string value, bool valid) =>
        Assert.Equal(valid, IntegrationProviders.Zenvia.FindField(ZenviaFieldKeys.FromNumber)!.HasValidShape(value));

    [Theory]
    [InlineData("abc-DEF_123.xyz", true)]
    [InlineData("con espacio", false)]
    [InlineData("tab\tdentro", false)]
    [InlineData("ñandú", false)]
    [InlineData("token\n", false)]
    public void TheApiTokenIsVisibleAsciiWithoutSpaces(string value, bool valid) =>
        Assert.Equal(valid, IntegrationProviders.Zenvia.FindField(ZenviaFieldKeys.ApiToken)!.HasValidShape(value));

    // Review Focus 1: un \0 llega a PostgreSQL como un 500; el catálogo lo rechaza antes, aunque el
    // campo no tenga patrón.
    [Fact]
    public void AControlCharacterIsNeverAValidShape()
    {
        var free = new FieldDefinition("note", "Nota", FieldKind.Text, required: false, maxLength: 40, pattern: null, invalidMessage: "X");

        Assert.True(free.HasValidShape("texto libre"));
        Assert.False(free.HasValidShape("a\u0000b"));
        Assert.False(free.HasValidShape("a\nb"));
    }

    // Un surrogate suelto no es UTF-8 válido: Npgsql/jsonb lo rechazan con un 500.
    // Es un Fact y no un Theory: xUnit serializa los datos y cambia un surrogate suelto por U+FFFD.
    [Fact]
    public void ALoneSurrogateIsNeverAValidShape()
    {
        var free = new FieldDefinition("note", "Nota", FieldKind.Text, required: false, maxLength: 40, pattern: null, invalidMessage: "X");

        Assert.False(free.HasValidShape("a\uD800b"));
        Assert.False(free.HasValidShape("ab\uD800"));
        Assert.False(free.HasValidShape("a\uDC00b"));
        Assert.False(free.HasValidShape("\uDE00\uD83D"));
        Assert.True(free.HasValidShape("emoji 😀 válido"));
    }

    [Fact]
    public void TooLongIsMeasuredInCharacters()
    {
        var fromNumber = IntegrationProviders.Zenvia.FindField(ZenviaFieldKeys.FromNumber)!;

        Assert.False(fromNumber.IsTooLong("123456789012345"));
        Assert.True(fromNumber.IsTooLong("1234567890123456"));
    }

    [Fact]
    public void TheDomainExceptionCarriesItsCodeAndOptionalFieldErrors()
    {
        DomainException plain = new IntegrationsDomainException(IntegrationsErrorCodes.NameTaken, "taken");
        var withFields = new IntegrationsDomainException(
            IntegrationsErrorCodes.CredentialsRejected,
            "rejected",
            new Dictionary<string, string[]> { ["secrets.apiToken"] = ["mensaje"] });

        Assert.Equal(IntegrationsErrorCodes.NameTaken, plain.Code);
        Assert.Empty(((IHasFieldErrors)plain).FieldErrors);
        Assert.Equal(["secrets.apiToken"], withFields.FieldErrors.Keys);
    }
}
