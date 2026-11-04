using System.Net;
using System.Text.Json.Nodes;
using Modules.Storage.Domain;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// HTTP contract A de punta a punta: ETag, If-Match, los códigos <c>store.settings.*</c>, las monedas con su cuenta, los colores, el
/// banner, los países de venta y la publicación. Cada prueba abre su base: son lentas y se corren una vez, al final (ruling R16).
/// </summary>
public sealed class StoreSettingsApiTests
{
    private static readonly Guid Tenant = StoreSettingsFixture.Tenant;

    private static readonly string[] LowercaseCountries = ["co", "es"];

    private static readonly string[] UnknownCountries = ["XX"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(node => (string)node!).ToArray();

    [Fact]
    public async Task ANeverSavedStoreReadsAsDefaultsWithVersionZero()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.GetAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"0\"", response.Headers.ETag!.Tag);
        var settings = await StoreSettingsFixture.JsonAsync(response);
        Assert.Equal(0, (int)settings["version"]!);
        Assert.Null(settings["primaryColor"]);
        Assert.Null(settings["resolvedColors"]);
        Assert.Null(settings["colorChecks"]);
        Assert.Equal("RetailOnly", (string)settings["pricingMode"]!);
        Assert.Equal(
            ["SlugRequired", "DataPolicyRequired", "CurrencyRequired", "SalesCountryRequired"],
            Strings(settings["publishBlockers"]));
        Assert.Empty(settings["currencies"]!.AsArray());
        Assert.Null(settings["primaryCurrency"]);
    }

    [Fact]
    public async Task SavingWithIfMatchZeroCreatesTheStore()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.PutAsync(fixture.Body(), ifMatch: "0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
        var settings = await StoreSettingsFixture.JsonAsync(response);
        Assert.Equal(1, (int)settings["version"]!);
        Assert.Equal("https://tienda-demo.lulocrm.com", (string)settings["storeUrl"]!);
        Assert.Empty(settings["publishBlockers"]!.AsArray());
    }

    [Fact]
    public async Task SavingWithoutIfMatchIs428AndWithAStaleOneIs412()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var missing = await fixture.PutAsync(fixture.Body(), ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        await fixture.SaveAsync(fixture.Body());
        var stale = await fixture.PutAsync(fixture.Body(), ifMatch: "0");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await StoreSettingsFixture.CodeAsync(stale));
    }

    [Fact]
    public async Task ASlugTakenByAnotherTenantIs409()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        await SeedTenantAsync(fixture.Factory, OtherTenantId);
        var other = fixture.ClientFor(OtherTenantId, "store.settings.manage", "companies.company.manage", "catalog.product.manage");
        var otherCompany = await CreateCompanyAsync(other, Guid.Parse(OtherTenantId), "Otra SAS", ("Bancolombia", "Z-COP-9", "COP"));
        await CreateProductAsync(
            other, Guid.Parse(OtherTenantId), "OTRA-1", "Otra", new Dictionary<string, decimal> { ["COP"] = 1000m });
        await fixture.SaveAsync(fixture.Body());

        var body = fixture.Body(b => b["currencies"] = new[]
        {
            fixture.CurrencyEntry("COP", "Bancolombia", "Z-COP-9", companyId: otherCompany.Id),
        });
        var response = await fixture.PutAsync(body, ifMatch: "0", client: other, tenantId: OtherTenantId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("store.settings.slug_taken", await StoreSettingsFixture.CodeAsync(response));
    }

    [Theory]
    [InlineData("tienda", "store.settings.slug_reserved")]
    [InlineData("QEP-API", "store.settings.slug_reserved")]
    [InlineData("a", "store.settings.slug_invalid")]
    [InlineData("con espacio", "store.settings.slug_invalid")]
    public async Task AnInvalidOrReservedSlugIs422(string slug, string code)
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.PutAsync(fixture.Body(b => b["slug"] = slug));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(code, await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task ChangingTheSlugMovesTheHostname()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        await fixture.SaveAsync(fixture.Body(b => b["slug"] = "viejo"));
        Assert.Equal(["viejo.lulocrm.com"], await fixture.SubdomainHostnamesAsync(Tenant));

        await fixture.SaveAsync(fixture.Body(b => b["slug"] = "nuevo"), ifMatch: "1");

        Assert.Equal(["nuevo.lulocrm.com"], await fixture.SubdomainHostnamesAsync(Tenant));
    }

    [Fact]
    public async Task TwoCurrenciesWithTheirAccountsRoundTrip()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var body = fixture.Body(b =>
        {
            b["currencies"] = new[]
            {
                fixture.CurrencyEntry("COP", "Bancolombia", "A-COP-1"),
                fixture.CurrencyEntry("EUR", "Davivienda", "B-EUR-1"),
            };
            b["primaryCurrency"] = "COP";
        });

        await fixture.SaveAsync(body);
        var read = await StoreSettingsFixture.JsonAsync(await fixture.GetAsync());

        var currencies = read["currencies"]!.AsArray();
        Assert.Equal(["COP", "EUR"], currencies.Select(c => (string)c!["currency"]!).ToArray());
        Assert.Equal("A-COP-1", (string)currencies[0]!["billingAccount"]!["accountNumber"]!);
        Assert.Equal("EUR", (string)currencies[1]!["billingAccount"]!["currency"]!);
        Assert.Equal("COP", (string)read["primaryCurrency"]!);
    }

    [Fact]
    public async Task AnAccountInAnotherCurrencyThanItsEntryIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var body = fixture.Body(b =>
        {
            b["currencies"] = new[] { fixture.CurrencyEntry("EUR", "Bancolombia", "A-COP-1", accountCurrency: "COP") };
            b["primaryCurrency"] = "EUR";
        });

        var response = await fixture.PutAsync(body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.currency_mismatch", await StoreSettingsFixture.CodeAsync(response));
        Assert.Equal(0, (int)(await StoreSettingsFixture.JsonAsync(await fixture.GetAsync()))["version"]!);
    }

    [Fact]
    public async Task ARepeatedCurrencyIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var body = fixture.Body(b => b["currencies"] = new[]
        {
            fixture.CurrencyEntry("COP", "Bancolombia", "A-COP-1"),
            fixture.CurrencyEntry("cop", "Bancolombia", "A-COP-1"),
        });

        var response = await fixture.PutAsync(body);

        Assert.Equal("store.settings.currency_duplicate", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task APrimaryCurrencyOutsideTheListIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.PutAsync(fixture.Body(b => b["primaryCurrency"] = "EUR"));

        Assert.Equal("store.settings.primary_currency_invalid", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task ACurrencyWithoutPricedProductsIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var company = await CreateCompanyAsync(fixture.Admin, Tenant, "Con dólares", ("Bancolombia", "U-USD-1", "USD"));
        var body = fixture.Body(b =>
        {
            b["currencies"] = new[] { fixture.CurrencyEntry("USD", "Bancolombia", "U-USD-1", companyId: company.Id) };
            b["primaryCurrency"] = "USD";
        });

        var response = await fixture.PutAsync(body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.currency_invalid", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task TheSecondEntrysAccountIsCheckedToo()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var body = fixture.Body(b => b["currencies"] = new[]
        {
            fixture.CurrencyEntry("COP", "Bancolombia", "A-COP-1"),
            fixture.CurrencyEntry("EUR", "Davivienda", "NO-EXISTE"),
        });

        var response = await fixture.PutAsync(body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.StartsWith("quotation.billing.", await StoreSettingsFixture.CodeAsync(response), StringComparison.Ordinal);
        Assert.Equal(0, (int)(await StoreSettingsFixture.JsonAsync(await fixture.GetAsync()))["version"]!);
    }

    [Fact]
    public async Task PublishingWithoutCurrenciesIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        await fixture.SaveAsync(fixture.Body(b =>
        {
            b["currencies"] = Array.Empty<object>();
            b["primaryCurrency"] = null;
        }));

        var response = await fixture.PostAsync("/publish", ifMatch: "1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.currency_required", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task ColorsRoundTripWithTheirResolvedValuesAndChecks()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var saved = await fixture.SaveAsync(fixture.Body(b => b["primaryColor"] = "#ffd400"));

        Assert.Equal("#FFD400", (string)saved["primaryColor"]!);
        Assert.Null(saved["onPrimaryColor"]);
        var resolved = saved["resolvedColors"]!;
        Assert.Equal("#FFD400", (string)resolved["primary"]!);
        Assert.Equal("#111111", (string)resolved["onPrimary"]!);
        Assert.NotEqual("#FFD400", (string)resolved["accentText"]!);
        var checks = saved["colorChecks"]!;
        Assert.False((bool)checks["onPrimaryLowContrast"]!);
        Assert.False((bool)checks["accentTextLowContrast"]!);
        Assert.True((double)checks["onPrimaryContrast"]! >= 4.5);
        Assert.True((double)checks["accentTextContrast"]! >= 4.5);
    }

    [Fact]
    public async Task ALowContrastOverrideIsSavedAndFlagged()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var saved = await fixture.SaveAsync(fixture.Body(b => b["onPrimaryColor"] = "#FFFFFF"));

        Assert.Equal("#FFFFFF", (string)saved["onPrimaryColor"]!);
        Assert.True((bool)saved["colorChecks"]!["onPrimaryLowContrast"]!);
    }

    [Theory]
    [InlineData("primaryColor", "blue")]
    [InlineData("onPrimaryColor", "white")]
    [InlineData("accentTextColor", "#12345")]
    public async Task AMalformedColorIs422(string field, string value)
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.PutAsync(fixture.Body(b => b[field] = value));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.color_invalid", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task AMissingPrimaryColorIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.PutAsync(fixture.Body(b => b["primaryColor"] = null));

        Assert.Equal("store.settings.color_invalid", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task ThePreviewEqualsTheSavedResult()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var preview = await StoreSettingsFixture.JsonAsync(
            await fixture.PostAsync("/colors/preview", ifMatch: null, new { primaryColor = "#FFD400" }));
        var saved = await fixture.SaveAsync(fixture.Body());

        Assert.Equal(saved["resolvedColors"]!.ToJsonString(), preview["resolvedColors"]!.ToJsonString());
        Assert.Equal(saved["colorChecks"]!.ToJsonString(), preview["colorChecks"]!.ToJsonString());
    }

    [Fact]
    public async Task ThePreviewWritesNothing()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var saved = await fixture.SaveAsync(fixture.Body());
        var auditBefore = await CountAuditEntriesAsync(fixture.Factory, "store.settings.updated", Tenant.ToString());

        var response = await fixture.PostAsync("/colors/preview", ifMatch: null, new { primaryColor = "#1F4FD8", onPrimaryColor = "#000000" });
        var after = await StoreSettingsFixture.JsonAsync(await fixture.GetAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((int)saved["version"]!, (int)after["version"]!);
        Assert.Equal((string?)saved["updatedAt"], (string?)after["updatedAt"]);
        Assert.Equal("#FFD400", (string)after["primaryColor"]!);
        Assert.Equal(auditBefore, await CountAuditEntriesAsync(fixture.Factory, "store.settings.updated", Tenant.ToString()));
    }

    [Fact]
    public async Task APreviewWithAMalformedColorIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.PostAsync(
            "/colors/preview", ifMatch: null, new { primaryColor = "#FFD400", onPrimaryColor = "white" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.color_invalid", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task APreviewWithLowContrastIsFlaggedNot422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.PostAsync(
            "/colors/preview", ifMatch: null, new { primaryColor = "#FFD400", onPrimaryColor = "#FFFFFF" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await StoreSettingsFixture.JsonAsync(response);
        Assert.True((bool)preview["colorChecks"]!["onPrimaryLowContrast"]!);
        Assert.Equal("#FFFFFF", (string)preview["resolvedColors"]!["onPrimary"]!);
    }

    [Fact]
    public async Task ThePreviewNeedsTheStorePermission()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var without = fixture.ClientFor(TenantId, "companies.company.read");

        var denied = await fixture.PostAsync("/colors/preview", ifMatch: null, new { primaryColor = "#FFD400" }, without);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await using var storeOff = await StoreSettingsFixture.OpenAsync(
            Modules.Tenancy.Domain.TenantModuleKeys.All.Where(key => key != Modules.Tenancy.Domain.TenantModuleKeys.Store));
        var moduleOff = await storeOff.PostAsync("/colors/preview", ifMatch: null, new { primaryColor = "#FFD400" });
        Assert.Equal(HttpStatusCode.Forbidden, moduleOff.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", await StoreSettingsFixture.CodeAsync(moduleOff));
    }

    [Fact]
    public async Task BillingAccountsAreListedAcrossCompaniesByCurrency()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        await CreateCompanyAsync(fixture.Admin, Tenant, "Alfa SAS", ("Bancolombia", "ALFA-EUR", "EUR"), ("Bancolombia", "ALFA-COP", "COP"));

        var response = await fixture.GetAsync("/settings/billing-accounts?currency=eur");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await StoreSettingsFixture.JsonAsync(response))["items"]!.AsArray();
        Assert.Equal(["Alfa SAS", "Origen Botánico SAS"], items.Select(i => (string)i!["companyName"]!).ToArray());
        Assert.All(items, item => Assert.Equal("EUR", (string)item!["currency"]!));
        Assert.Equal(["ALFA-EUR", "B-EUR-1"], items.Select(i => (string)i!["accountNumber"]!).ToArray());

        var all = (await StoreSettingsFixture.JsonAsync(await fixture.GetAsync("/settings/billing-accounts")))["items"]!.AsArray();
        Assert.Equal(4, all.Count);

        var malformed = await fixture.GetAsync("/settings/billing-accounts?currency=EURO");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        Assert.Equal("store.settings.currency_invalid", await StoreSettingsFixture.CodeAsync(malformed));
    }

    [Fact]
    public async Task BillingAccountsSkipInactiveCompanies()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var inactive = await CreateCompanyAsync(fixture.Admin, Tenant, "Cerrada SAS", ("Bancolombia", "CERRADA-COP", "COP"));
        var deactivated = await fixture.Admin.PostAsync(
            $"/api/v1/tenants/{TenantId}/companies/{inactive.Id}/deactivate", content: null, Ct);
        deactivated.EnsureSuccessStatusCode();

        var items = (await StoreSettingsFixture.JsonAsync(await fixture.GetAsync("/settings/billing-accounts?currency=COP")))["items"]!.AsArray();

        Assert.Equal(["A-COP-1"], items.Select(i => (string)i!["accountNumber"]!).ToArray());
    }

    [Fact]
    public async Task BillingAccountsNeedTheStorePermission()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var without = fixture.ClientFor(TenantId, "companies.company.read", "companies.company.manage");

        var response = await fixture.GetAsync("/settings/billing-accounts", without);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ABannerWithAnotherOwnerTypeIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var productImage = await fixture.SeedImageAsync(FileOwnerType.Product, published: true);

        var response = await fixture.PutAsync(fixture.Body(b => b["bannerFileId"] = productImage));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.banner_invalid", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task ABannerThatIsNotPublicIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var unpublished = await fixture.SeedImageAsync(FileOwnerType.Store, published: false);

        var response = await fixture.PutAsync(fixture.Body(b => b["bannerFileId"] = unpublished));

        Assert.Equal("store.settings.banner_invalid", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task APublicStoreBannerSavesWithItsUrl()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        var banner = await fixture.SeedImageAsync(FileOwnerType.Store, published: true);

        var saved = await fixture.SaveAsync(fixture.Body(b =>
        {
            b["bannerFileId"] = banner;
            b["bannerText"] = "Envíos a todo el país";
        }));

        Assert.Equal(banner, (Guid)saved["banner"]!["imageFileId"]!);
        Assert.StartsWith(InMemoryPublicStorage.BaseUrl, (string)saved["banner"]!["imageUrl"]!, StringComparison.Ordinal);
        Assert.Equal("Envíos a todo el país", (string)saved["banner"]!["text"]!);
    }

    [Fact]
    public async Task SalesCountriesRoundTripUppercase()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        await fixture.SaveAsync(fixture.Body(b => b["salesCountries"] = LowercaseCountries));
        var read = await StoreSettingsFixture.JsonAsync(await fixture.GetAsync());

        Assert.Equal(["CO", "ES"], Strings(read["salesCountries"]));
    }

    [Fact]
    public async Task AnUnknownSalesCountryIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var response = await fixture.PutAsync(fixture.Body(b => b["salesCountries"] = UnknownCountries));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.sales_country_invalid", await StoreSettingsFixture.CodeAsync(response));
        Assert.Equal(0, (int)(await StoreSettingsFixture.JsonAsync(await fixture.GetAsync()))["version"]!);
    }

    [Fact]
    public async Task PublishingWithoutSalesCountriesIs422()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        await fixture.SaveAsync(fixture.Body(b => b["salesCountries"] = Array.Empty<string>()));

        var response = await fixture.PostAsync("/publish", ifMatch: "1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.sales_country_required", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task PublishingWithABlockerIs422WithItsCode()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        await fixture.SaveAsync(fixture.Body(b => b["dataPolicyUrl"] = null));

        var response = await fixture.PostAsync("/publish", ifMatch: "1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.settings.data_policy_required", await StoreSettingsFixture.CodeAsync(response));
    }

    [Fact]
    public async Task PublishThenUnpublishAuditsBoth()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();
        await fixture.SaveAsync(fixture.Body());

        var published = await StoreSettingsFixture.JsonAsync(await fixture.PostAsync("/publish", ifMatch: "1"));
        Assert.True((bool)published["isPublished"]!);
        Assert.NotNull(published["publishedAt"]);
        var unpublished = await StoreSettingsFixture.JsonAsync(await fixture.PostAsync("/unpublish", ifMatch: "2"));
        Assert.False((bool)unpublished["isPublished"]!);

        Assert.Equal(1, await CountAuditEntriesAsync(fixture.Factory, "store.settings.published", TenantId));
        Assert.Equal(1, await CountAuditEntriesAsync(fixture.Factory, "store.settings.unpublished", TenantId));
        Assert.Equal(1, await CountAuditEntriesAsync(fixture.Factory, "store.settings.updated", TenantId));
    }

    [Fact]
    public async Task TheEffectiveNameFallsBackToTheTenantName()
    {
        await using var fixture = await StoreSettingsFixture.OpenAsync();

        var withoutName = await fixture.SaveAsync(fixture.Body());
        Assert.Null(withoutName["name"]);
        Assert.Equal("Store API Tests", (string)withoutName["effectiveName"]!);

        var named = await fixture.SaveAsync(fixture.Body(b => b["name"] = "Mi tienda"), ifMatch: "1");
        Assert.Equal("Mi tienda", (string)named["name"]!);
        Assert.Equal("Mi tienda", (string)named["effectiveName"]!);
    }
}
