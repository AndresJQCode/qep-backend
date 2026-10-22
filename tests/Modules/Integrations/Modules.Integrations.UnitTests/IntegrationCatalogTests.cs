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

    // Spec 2026-10-09 §6.1: la fila literal del proveedor nuevo.
    [Fact]
    public void WhatsAppCloudIsInTheCatalogWithItsFieldsAndOnboarding()
    {
        var provider = IntegrationProviders.WhatsAppCloud;

        Assert.Equal("whatsapp-cloud", provider.Key);
        Assert.Equal("WhatsApp Business (Meta)", provider.DisplayName);
        Assert.Equal(IntegrationCategory.Messaging, provider.Category);
        Assert.Equal([TenantModuleKeys.Messaging], provider.ConsumingModules);
        Assert.Equal(5, provider.MaxConnections);
        Assert.Equal(ProviderOnboarding.MetaEmbeddedSignup, provider.Onboarding);
        Assert.Equal(
            ["displayPhoneNumber", "verifiedName", "phoneNumberId", "wabaId", "qualityRating", "accessToken", "registrationPin"],
            provider.Fields.Select(field => field.Key));
        Assert.Equal(["displayPhoneNumber", "verifiedName"], provider.CatalogFields.Select(field => field.Key));
        Assert.True(provider.FindField("accessToken")!.Internal);
        Assert.True(provider.FindField("accessToken")!.IsSecret);
        Assert.Equal(2048, provider.FindField("accessToken")!.MaxLength);
        Assert.True(provider.FindField("registrationPin")!.Internal);
        Assert.True(provider.FindField("registrationPin")!.IsSecret);
        Assert.Equal(@"^[0-9]{6}\z", provider.FindField("registrationPin")!.Pattern!.ToString());
        Assert.Null(provider.FindField("displayPhoneNumber")!.Pattern);
        Assert.Equal(
            [IntegrationProviders.Zenvia, IntegrationProviders.WhatsAppCloud, IntegrationProviders.Instagram, IntegrationProviders.Shopify, IntegrationProviders.ExternalApi],
            IntegrationProviders.All);
        Assert.Equal(ProviderOnboarding.Form, IntegrationProviders.Zenvia.Onboarding);
    }

    // Spec 2026-10-14 §6.1: la fila literal de Instagram. Lo consume messaging, se conecta con Instagram Login y los
    // campos los llena el backend (username y accountName los refresca el probador; igUserId, tokenExpiresAt y el
    // token son internos).
    [Fact]
    public void InstagramIsInTheCatalogWithItsFieldsAndOnboarding()
    {
        var provider = IntegrationProviders.Instagram;

        Assert.Equal("instagram", provider.Key);
        Assert.Equal("Instagram (Meta)", provider.DisplayName);
        Assert.Equal(IntegrationCategory.Messaging, provider.Category);
        Assert.Equal([TenantModuleKeys.Messaging], provider.ConsumingModules);
        Assert.Equal(5, provider.MaxConnections);
        Assert.Equal(ProviderOnboarding.InstagramLogin, provider.Onboarding);
        Assert.True(provider.IsBackendOwned);
        Assert.Equal(["username", "accountName", "igUserId", "tokenExpiresAt", "accessToken"], provider.Fields.Select(field => field.Key));
        Assert.Equal(["username", "accountName"], provider.CatalogFields.Select(field => field.Key));
        Assert.True(provider.FindField("igUserId")!.Internal);
        Assert.Equal(@"^[0-9]{1,64}\z", provider.FindField("igUserId")!.Pattern!.ToString());
        Assert.True(provider.FindField("tokenExpiresAt")!.Internal);
        Assert.True(provider.FindField("accessToken")!.Internal);
        Assert.True(provider.FindField("accessToken")!.IsSecret);
        Assert.Equal(2048, provider.FindField("accessToken")!.MaxLength);
        Assert.False(IntegrationProviders.Zenvia.IsBackendOwned);
        Assert.True(IntegrationProviders.WhatsAppCloud.IsBackendOwned);
    }

    // Spec 2026-10-15 §4.1: la fila literal de Shopify. Lo consume el módulo shopify, se conecta por OAuth y todos los
    // campos los escribe el backend (shopDomain y shopName se ven; shopId, scopes, webhookSubscriptionIds y el token no).
    [Fact]
    public void ShopifyIsInTheCatalogWithItsFieldsAndOnboarding()
    {
        var provider = IntegrationProviders.Shopify;

        Assert.Equal("shopify", provider.Key);
        Assert.Equal("Shopify", provider.DisplayName);
        Assert.Equal(IntegrationCategory.Commerce, provider.Category);
        Assert.Equal([TenantModuleKeys.Shopify], provider.ConsumingModules);
        Assert.Equal(IntegrationProviders.DefaultMaxConnections, provider.MaxConnections);
        Assert.Equal(ProviderOnboarding.ShopifyOAuth, provider.Onboarding);
        Assert.Equal(
            [ShopifyFieldKeys.ShopDomain, ShopifyFieldKeys.ShopName, ShopifyFieldKeys.ShopId, ShopifyFieldKeys.Scopes, ShopifyFieldKeys.WebhookSubscriptionIds, ShopifyFieldKeys.AccessToken],
            provider.Fields.Select(field => field.Key));
        Assert.Equal(["shopDomain", "shopName", "shopId", "scopes", "webhookSubscriptionIds", "accessToken"], provider.Fields.Select(field => field.Key));
        Assert.Equal(["shopDomain", "shopName"], provider.CatalogFields.Select(field => field.Key));
        Assert.Equal(["accessToken"], provider.SecretFields.Select(field => field.Key));
        Assert.True(provider.FindField("accessToken")!.Internal);
        Assert.Same(provider, IntegrationProviders.Parse("shopify"));
        Assert.Equal("app_uninstalled", ConnectionFailureCodes.AppUninstalled);
        Assert.Equal("webhooks_not_registered", ConnectionFailureCodes.WebhooksNotRegistered);
    }

    [Fact]
    public void ShopifyIsVisibleOnlyWithShopifyModule()
    {
        Assert.True(IntegrationProviders.Shopify.IsVisibleFor(TenantModuleSet.FromStored(TenantModuleKeys.All)));
        Assert.False(IntegrationProviders.Shopify.IsVisibleFor(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Shopify]))));
        // shopify depende de orders: contratado sin orders no está efectivo.
        Assert.False(IntegrationProviders.Shopify.IsVisibleFor(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Orders]))));
        Assert.False(IntegrationProviders.Shopify.IsVisibleFor(TenantModuleSet.FromStored([TenantModuleKeys.Quotations, TenantModuleKeys.Messaging])));
    }

    // La tienda la elige Shopify en la autorización: el formulario no crea ni edita más que el nombre.
    [Fact]
    public void ShopifyAllFieldsAreBackendOwned()
    {
        Assert.True(IntegrationProviders.Shopify.IsBackendOwned);
        Assert.All(IntegrationProviders.Shopify.Fields, field => Assert.False(field.Required));
        Assert.All(
            IntegrationProviders.Shopify.Fields.Where(field => field.Key is not ("shopDomain" or "shopName")),
            field => Assert.True(field.Internal));
    }

    // Spec 2026-10-21 D-H1: la fila literal de la API externa. La consume automations, se conecta por formulario y no pide
    // habilitación por tenant.
    [Fact]
    public void ExternalApiIsAFormProviderConsumedByAutomations()
    {
        var provider = IntegrationProviders.ExternalApi;

        Assert.Equal("external-api", provider.Key);
        Assert.Equal("API externa", provider.DisplayName);
        Assert.Equal(IntegrationCategory.Automation, provider.Category);
        Assert.Equal([TenantModuleKeys.Automations], provider.ConsumingModules);
        Assert.Equal(10, provider.MaxConnections);
        Assert.Equal(ProviderOnboarding.Form, provider.Onboarding);
        Assert.False(provider.IsBackendOwned);
        Assert.False(provider.RequiresTenantEnablement);
        Assert.Equal(["baseUrl", "authHeader", "testPath"], provider.PublicFields.Select(field => field.Key));
        Assert.Equal(["apiKey"], provider.SecretFields.Select(field => field.Key));
        Assert.Equal(FieldKind.Url, provider.FindField("baseUrl")!.Kind);
        Assert.True(provider.FindField("baseUrl")!.Required);
        Assert.Equal(300, provider.FindField("baseUrl")!.MaxLength);
        Assert.True(provider.FindField("apiKey")!.Required);
        Assert.Equal(512, provider.FindField("apiKey")!.MaxLength);
        Assert.False(provider.FindField("authHeader")!.Required);
        Assert.Equal(64, provider.FindField("authHeader")!.MaxLength);
        Assert.False(provider.FindField("testPath")!.Required);
        Assert.Equal(200, provider.FindField("testPath")!.MaxLength);
        Assert.All(provider.Fields, field => Assert.False(field.Internal));
        Assert.Same(provider, IntegrationProviders.Parse("external-api"));
    }

    [Theory]
    [InlineData("https://api-clasic.qcode.co", true)]
    [InlineData("https://api.x.co/v1", true)]
    [InlineData("http://api.x.co", false)]
    [InlineData("https://api.x.co/v1?x=1", false)]
    [InlineData("https://api.x.co/v1#frag", false)]
    [InlineData("https://api.x.co\n", false)]
    [InlineData("https://", false)]
    public void BaseUrlPatternRequiresHttpsWithoutQuery(string value, bool valid) =>
        Assert.Equal(valid, IntegrationProviders.ExternalApi.FindField("baseUrl")!.HasValidShape(value));

    [Theory]
    [InlineData("authHeader", "x-api-key", true)]
    [InlineData("authHeader", "X Token", false)]
    [InlineData("testPath", "/health", true)]
    [InlineData("testPath", "health", false)]
    [InlineData("testPath", "/health?x=1", false)]
    [InlineData("apiKey", "sk_live-1234", true)]
    [InlineData("apiKey", "with space", false)]
    public void TheOptionalAndSecretFieldsKeepTheirShape(string key, string value, bool valid) =>
        Assert.Equal(valid, IntegrationProviders.ExternalApi.FindField(key)!.HasValidShape(value));

    // Fallo P2-R30: un header reservado no puede llevar la clave (Host sería el SNI del TLS y la mandaría en claro; los de
    // conexión, contenido o proxy rompen el request o lo desvían). Sin distinguir mayúsculas.
    [Theory]
    [InlineData("Host")]
    [InlineData("host")]
    [InlineData("Connection")]
    [InlineData("keep-alive")]
    [InlineData("Transfer-Encoding")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Authorization")]
    [InlineData("proxy-connection")]
    [InlineData("Content-Type")]
    [InlineData("content-length")]
    [InlineData("Cookie")]
    [InlineData("USER-AGENT")]
    public void TheAuthHeaderCannotBeAReservedHeader(string name) =>
        Assert.False(IntegrationProviders.ExternalApi.FindField("authHeader")!.HasValidShape(name));

    [Theory]
    [InlineData("X-Token")]
    [InlineData("x-api-key")]
    [InlineData("Authorization")]
    [InlineData("Hosted-Key")]
    [InlineData("X-Content-Key")]
    [InlineData("Tenant")]
    public void TheAuthHeaderAcceptsCustomNames(string name) =>
        Assert.True(IntegrationProviders.ExternalApi.FindField("authHeader")!.HasValidShape(name));

    [Fact]
    public void ExternalApiIsVisibleOnlyWithAutomations()
    {
        Assert.True(IntegrationProviders.ExternalApi.IsVisibleFor(TenantModuleSet.FromStored(TenantModuleKeys.All)));
        Assert.False(IntegrationProviders.ExternalApi.IsVisibleFor(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Automations]))));
    }

    // Meta devuelve el número formateado: "+57 300 123 4567" tiene que pasar tal cual.
    [Fact]
    public void TheDisplayPhoneNumberAcceptsMetaFormatting() =>
        Assert.True(IntegrationProviders.WhatsAppCloud.FindField("displayPhoneNumber")!.HasValidShape("+57 300 123 4567"));

    [Fact]
    public void ZenviaDeclaresWhatTheSpecSays()
    {
        var zenvia = IntegrationProviders.Zenvia;

        Assert.Equal("Zenvia (WhatsApp)", zenvia.DisplayName);
        Assert.Equal(IntegrationCategory.Messaging, zenvia.Category);
        Assert.Equal([TenantModuleKeys.Quotations], zenvia.ConsumingModules);
        Assert.Equal(20, zenvia.MaxConnections);
        Assert.Equal(["apiToken", "fromNumber", "templateId", "orderTemplateId"], zenvia.Fields.Select(field => field.Key));

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

        // Spec 2026-10-21 D11: la plantilla va atada al número emisor, así que es de la conexión.
        var templateId = zenvia.FindField(ZenviaFieldKeys.TemplateId);
        Assert.NotNull(templateId);
        Assert.Equal(FieldKind.Text, templateId.Kind);
        Assert.True(templateId.Required);
        Assert.Equal(36, templateId.MaxLength);

        var orderTemplateId = zenvia.FindField(ZenviaFieldKeys.OrderTemplateId);
        Assert.NotNull(orderTemplateId);
        Assert.Equal(FieldKind.Text, orderTemplateId.Kind);
        Assert.False(orderTemplateId.Required);
        Assert.Equal(36, orderTemplateId.MaxLength);

        Assert.Equal(["fromNumber", "templateId", "orderTemplateId"], zenvia.PublicFields.Select(field => field.Key));
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
