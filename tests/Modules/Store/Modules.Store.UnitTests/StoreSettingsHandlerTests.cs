using BuildingBlocks.Application;
using Modules.Store.Application;
using Modules.Store.Domain;
using static Modules.Store.UnitTests.SettingsRig;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

/// <summary>
/// El orden y los efectos de los manejadores de ajustes. Las rutas HTTP y la base las cubre la integración (que se corre al final);
/// acá se fija lo que sólo el manejador decide: qué se comprueba antes de qué, y que la tienda y el subdominio se guardan juntos.
/// </summary>
public sealed class StoreSettingsHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UpdateStoreSettingsCommand Update(StoreSettingsRequest body, long version = 0, Guid? tenant = null) =>
        new(tenant ?? TenantId, version, body);

    private static async Task<string> CodeOfAsync(Func<Task> act) =>
        Assert.IsType<StoreDomainException>(await Record.ExceptionAsync(act)).Code;

    [Fact]
    public async Task SavingAnActiveAdvisorStoresItAndReturnsItsName()
    {
        var rig = new SettingsRig();
        rig.Members.Active.Add(Member);
        rig.Members.Names[Member] = "Ana";

        var saved = await rig.UpdateHandler().HandleAsync(Update(Body(advisorMembershipId: Member)), Ct);

        Assert.Equal(Member, saved.AdvisorMembershipId);
        Assert.Equal("Ana", saved.AdvisorName);
        Assert.Contains("advisorMembershipId", Assert.Single(rig.Audit.Entries).ChangedFields);
    }

    [Fact]
    public async Task AnInactiveOrForeignAdvisorIs422AdvisorNotFound()
    {
        var rig = new SettingsRig();

        Assert.Equal(
            StoreErrorCodes.AdvisorNotFound,
            await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(Body(advisorMembershipId: OtherMember)), Ct)));
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task ClearingTheAdvisorAuditsTheField()
    {
        var rig = new SettingsRig(ConfiguredStore(Input(advisorMembershipId: Member)));

        var saved = await rig.UpdateHandler().HandleAsync(Update(Body(), version: 1), Ct);

        Assert.Null(saved.AdvisorMembershipId);
        Assert.Null(saved.AdvisorName);
        Assert.Contains("advisorMembershipId", Assert.Single(rig.Audit.Entries).ChangedFields);
    }

    [Fact]
    public async Task ANeverSavedStoreReadsAsDefaultsWithVersionZero()
    {
        var rig = new SettingsRig();

        var settings = await rig.GetHandler().HandleAsync(new GetStoreSettingsQuery(TenantId), Ct);

        Assert.Equal(0, settings.Version);
        Assert.Null(settings.PrimaryColor);
        Assert.Null(settings.ResolvedColors);
        Assert.Null(settings.ColorChecks);
        Assert.Equal("RetailOnly", settings.PricingMode);
        Assert.Equal(["SlugRequired", "CurrencyRequired", "SalesCountryRequired"], settings.PublishBlockers);
        Assert.Equal("Origen Botánico", settings.EffectiveName);
        Assert.Equal("https://cdn.test/logo.png", settings.LogoUrl);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task SavingANewStoreAddsItAndReplacesTheSubdomainInOneSave()
    {
        var rig = new SettingsRig();

        var saved = await rig.UpdateHandler().HandleAsync(Update(Body()), Ct);

        // La tienda y el subdominio entran al mismo contexto y se guardan una sola vez: sin esa unidad no hay garantía de una fila Subdomain.
        Assert.Equal(["stores.add", "domains.replace", "uow.save"], rig.Log.Entries);
        Assert.Equal([("acme", "lulocrm.com")], rig.Domains.Replaced);
        Assert.Equal(1, saved.Version);
        Assert.Equal("https://acme.lulocrm.com", saved.StoreUrl);
        Assert.Equal(["CO", "ES"], saved.SalesCountries);
        Assert.Equal("#FFD400", saved.ResolvedColors!.Primary);
        Assert.Equal("#111111", saved.ResolvedColors.OnPrimary);
        Assert.False(saved.ColorChecks!.OnPrimaryLowContrast);
        var audited = Assert.Single(rig.Audit.Entries);
        Assert.Equal(StoreAuditActions.SettingsUpdated, audited.Action);
    }

    [Fact]
    public async Task ChangingTheSlugMovesTheSubdomainAndKeepingItDoesNot()
    {
        var rig = new SettingsRig(ConfiguredStore());

        await rig.UpdateHandler().HandleAsync(Update(Body(slug: "acme"), version: 1), Ct);
        Assert.Empty(rig.Domains.Replaced);

        await rig.UpdateHandler().HandleAsync(Update(Body(slug: "nuevo"), version: 2), Ct);
        Assert.Equal([("nuevo", "lulocrm.com")], rig.Domains.Replaced);
        Assert.Equal(2, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AnIdenticalSaveWritesNothingAndKeepsTheVersion()
    {
        var rig = new SettingsRig(ConfiguredStore());
        var first = await rig.UpdateHandler().HandleAsync(Update(Body(name: "Nueva"), version: 1), Ct);
        var audited = rig.Audit.Entries.Count;
        var saves = rig.UnitOfWork.Saves;

        var again = await rig.UpdateHandler().HandleAsync(Update(Body(name: "Nueva"), version: first.Version), Ct);

        Assert.Equal(first.Version, again.Version);
        Assert.Equal(audited, rig.Audit.Entries.Count);
        Assert.Equal(saves, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AStaleOrMissingVersionIsAConcurrencyErrorAndSavesNothing()
    {
        var rig = new SettingsRig(ConfiguredStore());

        var stale = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => rig.UpdateHandler().HandleAsync(Update(Body(), version: 0), Ct));

        Assert.Equal("concurrency.conflict", stale.Code);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ASlugTakenByAnotherTenantIsAConflict()
    {
        var rig = new SettingsRig();
        rig.Stores.SlugsOfOtherTenants.Add("acme");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => rig.UpdateHandler().HandleAsync(Update(Body()), Ct));

        Assert.Equal(StoreErrorCodes.SlugTaken, error.Code);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    // Familia slug: el formato y las palabras reservadas se rechazan antes de cualquier consulta.
    [Theory]
    [InlineData("ab", StoreErrorCodes.SlugInvalid)]
    [InlineData("Con Espacio", StoreErrorCodes.SlugInvalid)]
    [InlineData("Tienda", StoreErrorCodes.SlugReserved)]
    [InlineData("qep-api", StoreErrorCodes.SlugReserved)]
    public async Task ABadSlugIsRejectedBeforeAnyLookup(string slug, string code)
    {
        var rig = new SettingsRig();

        Assert.Equal(code, await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(Body(slug: slug)), Ct)));
        Assert.Equal(0, rig.Catalog.CurrencyCalls);
        Assert.Empty(rig.Accounts.Validated);
    }

    // Familia colores: ausente o mal formado es color_invalid en cualquiera de los tres; el contraste bajo nunca se rechaza.
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("blue", null, null)]
    [InlineData("#FFD400", "white", null)]
    [InlineData("#FFD400", null, "#12345")]
    public async Task AMissingOrMalformedColorIsRejected(string? primary, string? onPrimary, string? accentText)
    {
        var rig = new SettingsRig();
        var body = Body(primaryColor: primary, onPrimaryColor: onPrimary) with { AccentTextColor = accentText };

        Assert.Equal(StoreErrorCodes.ColorInvalid, await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(body), Ct)));
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ALowContrastOverrideIsSavedAndFlagged()
    {
        var rig = new SettingsRig();

        var saved = await rig.UpdateHandler().HandleAsync(Update(Body(onPrimaryColor: "#FFFFFF")), Ct);

        Assert.Equal("#FFFFFF", saved.OnPrimaryColor);
        Assert.True(saved.ColorChecks!.OnPrimaryLowContrast);
    }

    [Fact]
    public async Task ThePuresChecksRunBeforeAnyLookup()
    {
        var rig = new SettingsRig();
        var mismatch = Body(currencies: [Entry("EUR", "B-222", accountCurrency: "COP")], primaryCurrency: "EUR");

        Assert.Equal(StoreErrorCodes.CurrencyMismatch, await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(mismatch), Ct)));
        Assert.Equal(StoreErrorCodes.SalesCountryInvalid, await CodeOfAsync(
            () => rig.UpdateHandler().HandleAsync(Update(Body(salesCountries: ["XX"])), Ct)));
        Assert.Equal(StoreErrorCodes.CurrencyDuplicate, await CodeOfAsync(
            () => rig.UpdateHandler().HandleAsync(Update(Body(currencies: [Entry("COP", "A"), Entry("COP", "B")])), Ct)));
        Assert.Equal(StoreErrorCodes.PrimaryCurrencyInvalid, await CodeOfAsync(
            () => rig.UpdateHandler().HandleAsync(Update(Body(primaryCurrency: "EUR")), Ct)));

        Assert.Equal(0, rig.Catalog.CurrencyCalls);
        Assert.Empty(rig.Accounts.Validated);
    }

    [Fact]
    public async Task ACurrencyWithoutPricedProductsIsRejectedBeforeItsAccountIsChecked()
    {
        var rig = new SettingsRig();
        rig.Catalog.CurrenciesWithPrices.Remove("EUR");
        var body = Body(currencies: [Entry("COP", "A-111"), Entry("EUR", "B-222")]);

        Assert.Equal(StoreErrorCodes.CurrencyInvalid, await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(body), Ct)));

        Assert.Equal(1, rig.Catalog.CurrencyCalls);
        Assert.Empty(rig.Accounts.Validated);
    }

    [Fact]
    public async Task EveryEntrysAccountIsCheckedInOrderAndTheSecondOneFailsTheSave()
    {
        var rig = new SettingsRig();
        rig.Accounts.UnknownNumbers.Add("B-999");
        var body = Body(currencies: [Entry("COP", "A-111"), Entry("EUR", "B-999")]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.UpdateHandler().HandleAsync(Update(body), Ct));

        Assert.Equal("quotation.billing.account_not_found", error.Message);
        Assert.Equal(["A-111", "B-999"], rig.Accounts.Validated.Select(call => call.Number));
        Assert.Equal(["COP", "EUR"], rig.Accounts.Validated.Select(call => call.Currency));
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task TwoCurrenciesKeepTheirOrderAndPrimary()
    {
        var rig = new SettingsRig();
        var body = Body(currencies: [Entry("EUR", "B-222"), Entry("COP", "A-111")], primaryCurrency: "cop");

        var saved = await rig.UpdateHandler().HandleAsync(Update(body), Ct);

        Assert.Equal(["EUR", "COP"], saved.Currencies.Select(currency => currency.Currency));
        Assert.Equal("COP", saved.PrimaryCurrency);
        Assert.Equal("B-222", saved.Currencies[0].BillingAccount.AccountNumber);
    }

    [Fact]
    public async Task ABannerThatIsNotAPublicStoreImageIsRejectedAndAPublicOneSaves()
    {
        var rig = new SettingsRig();
        var banner = Guid.NewGuid();

        Assert.Equal(
            StoreErrorCodes.BannerInvalid,
            await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(Body(bannerFileId: banner)), Ct)));

        rig.Files.PublicImages[banner] = "https://cdn.test/banner.png";
        var saved = await rig.UpdateHandler().HandleAsync(Update(Body(bannerFileId: banner)), Ct);

        Assert.Equal(new StoreBannerDto(banner, "https://cdn.test/banner.png", null), saved.Banner);
    }

    [Fact]
    public async Task AFeaturedCategoryThatDoesNotExistIsRejectedAndDeletedOnesDropOnRead()
    {
        var rig = new SettingsRig();
        var kept = Guid.NewGuid();
        var missing = Guid.NewGuid();
        rig.Catalog.Categories[kept] = "Camisetas";

        Assert.Equal(
            StoreErrorCodes.CategoryNotFound,
            await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(Body(featured: [kept, missing])), Ct)));

        var saved = await rig.UpdateHandler().HandleAsync(Update(Body(featured: [kept])), Ct);
        Assert.Equal([new StoreFeaturedCategoryDto(kept, "Camisetas")], saved.FeaturedCategories);

        // La categoría se borra después: el guardado sigue ahí y la lectura la descarta sin avisar.
        rig.Catalog.Categories.Clear();
        var read = await rig.GetHandler().HandleAsync(new GetStoreSettingsQuery(TenantId), Ct);
        Assert.Empty(read.FeaturedCategories);
    }

    [Theory]
    [InlineData("http://acme.test/politica")]
    [InlineData("acme.test/politica")]
    [InlineData("javascript:alert(1)")]
    public async Task AUrlThatIsNotHttpsIsRejected(string url)
    {
        var rig = new SettingsRig();

        Assert.Equal(
            StoreErrorCodes.UrlInvalid,
            await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(Body(instagramUrl: url)), Ct)));
        Assert.Equal(
            StoreErrorCodes.UrlInvalid,
            await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(Body(dataPolicyUrl: url)), Ct)));
    }

    [Fact]
    public async Task AnUnknownSalesCountryIsRejectedAndKnownOnesSaveUppercase()
    {
        var rig = new SettingsRig();

        Assert.Equal(
            StoreErrorCodes.SalesCountryInvalid,
            await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(Body(salesCountries: ["co", "XX"])), Ct)));
        Assert.Equal(0, rig.UnitOfWork.Saves);

        var saved = await rig.UpdateHandler().HandleAsync(Update(Body(salesCountries: ["us", "co"])), Ct);
        Assert.Equal(["US", "CO"], saved.SalesCountries);
    }

    // El registro de auditoría lleva nombres de campo, nunca valores: ni números de cuenta ni la URL de la política.
    [Fact]
    public async Task TheAuditNamesTheChangedFieldsAndNeverTheirValues()
    {
        var rig = new SettingsRig(ConfiguredStore());
        var body = Body(
            slug: "acme",
            currencies: [Entry("COP", "A-SECRET-111")],
            salesCountries: ["co"],
            dataPolicyUrl: "https://acme.test/otra-politica");

        await rig.UpdateHandler().HandleAsync(Update(body, version: 1), Ct);

        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Contains("currencies", entry.ChangedFields);
        Assert.Contains("dataPolicyUrl", entry.ChangedFields);
        Assert.Contains("primaryColor", entry.ChangedFields);
        Assert.DoesNotContain("slug", entry.ChangedFields);
        Assert.DoesNotContain("salesCountries", entry.ChangedFields);
        Assert.All(entry.ChangedFields, field => Assert.DoesNotContain("SECRET", field));
    }

    [Fact]
    public async Task APublishedStoreCannotDropItsLastCurrency()
    {
        var rig = new SettingsRig(ConfiguredStore());
        await rig.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 1, true), Ct);

        var body = Body(currencies: [], primaryCurrency: null);

        Assert.Equal(
            StoreErrorCodes.CurrencyRequired,
            await CodeOfAsync(() => rig.UpdateHandler().HandleAsync(Update(body, version: 2), Ct)));
    }

    // ----- Autorización: tenant de la ruta, permiso y módulo, en los cinco manejadores.

    [Fact]
    public async Task EveryHandlerDeniesAnotherTenantAMissingPermissionAndAModuleThatIsOff()
    {
        var other = Guid.NewGuid();
        var rigs = new (SettingsRig Rig, string Code)[]
        {
            (new SettingsRig(permissions: []), "authorization.denied"),
            (new SettingsRig(callerTenant: other), "authorization.denied"),
            (new SettingsRig(modules: FakeTenantModules.StoreOff()), "tenancy.module_not_enabled"),
        };

        foreach (var (rig, code) in rigs)
        {
            var calls = new Func<Task>[]
            {
                () => rig.GetHandler().HandleAsync(new GetStoreSettingsQuery(TenantId), Ct),
                () => rig.UpdateHandler().HandleAsync(Update(Body()), Ct),
                () => rig.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 0, true), Ct),
                () => rig.PreviewHandler().HandleAsync(new PreviewStoreColorsQuery(TenantId, "#FFD400", null, null), Ct),
                () => rig.ListAccountsHandler().HandleAsync(new ListStoreBillingAccountsQuery(TenantId, null), Ct),
            };

            foreach (var call in calls)
            {
                var denied = Assert.IsType<RequestForbiddenException>(await Record.ExceptionAsync(call));
                Assert.Equal(code, denied.Code);
            }

            Assert.Equal(0, rig.UnitOfWork.Saves);
            Assert.Empty(rig.Log.Entries);
        }
    }

    // ----- Publicar

    [Fact]
    public async Task PublishingWithABlockerIsRejectedWithItsCodeAndPersistsNothing()
    {
        var rig = new SettingsRig();

        Assert.Equal(
            StoreErrorCodes.SlugRequired,
            await CodeOfAsync(() => rig.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 0, true), Ct)));
        Assert.Empty(rig.Log.Entries);

        var noCurrency = new SettingsRig(ConfiguredStore(Input(currencies: [], primaryCurrency: null)));
        Assert.Equal(
            StoreErrorCodes.CurrencyRequired,
            await CodeOfAsync(() => noCurrency.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 1, true), Ct)));

        var noCountry = new SettingsRig(ConfiguredStore(Input(countries: [])));
        Assert.Equal(
            StoreErrorCodes.SalesCountryRequired,
            await CodeOfAsync(() => noCountry.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 1, true), Ct)));
    }

    [Fact]
    public async Task PublishingWithoutAPolicyIsAllowed()
    {
        // Decisión del owner (2026-11-05): la política de datos es opcional para publicar.
        var rig = new SettingsRig(ConfiguredStore(Input(policy: null)));

        var published = await rig.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 1, true), Ct);

        Assert.True(published.IsPublished);
        Assert.Null(published.DataPolicyUrl);
        Assert.Empty(published.PublishBlockers);
    }

    [Fact]
    public async Task PublishThenUnpublishAuditsBothAndRepeatingIsIdempotent()
    {
        var rig = new SettingsRig(ConfiguredStore());

        var published = await rig.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 1, true), Ct);
        Assert.True(published.IsPublished);
        Assert.Equal(2, published.Version);

        var again = await rig.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 2, true), Ct);
        Assert.Equal(2, again.Version);

        var unpublished = await rig.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 2, false), Ct);
        Assert.False(unpublished.IsPublished);
        Assert.Equal(3, unpublished.Version);

        Assert.Equal(
            [StoreAuditActions.Published, StoreAuditActions.Unpublished],
            rig.Audit.Entries.Select(entry => entry.Action));
        Assert.Equal(2, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task PublishingWithAStaleVersionIsAConcurrencyError()
    {
        var rig = new SettingsRig(ConfiguredStore());

        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => rig.PublishHandler().HandleAsync(new PublishStoreCommand(TenantId, 7, true), Ct));
    }

    // ----- Vista previa de colores

    [Fact]
    public async Task ThePreviewEqualsTheSavedResultAndWritesNothing()
    {
        var rig = new SettingsRig();

        var preview = await rig.PreviewHandler().HandleAsync(new PreviewStoreColorsQuery(TenantId, "#FFD400", null, null), Ct);
        var saved = await rig.UpdateHandler().HandleAsync(Update(Body(primaryColor: "#FFD400")), Ct);

        Assert.Equal(saved.ResolvedColors, preview.ResolvedColors);
        Assert.Equal(saved.ColorChecks, preview.ColorChecks);
        Assert.Equal(1, rig.UnitOfWork.Saves);
        Assert.Equal("#111111", preview.ResolvedColors.OnPrimary);
    }

    [Fact]
    public async Task APreviewFlagsLowContrastAndRejectsMalformedColors()
    {
        var rig = new SettingsRig();

        var preview = await rig.PreviewHandler().HandleAsync(new PreviewStoreColorsQuery(TenantId, "#FFD400", "#FFFFFF", null), Ct);
        Assert.True(preview.ColorChecks.OnPrimaryLowContrast);

        foreach (var (primary, onPrimary) in new (string?, string?)[] { (null, null), ("blue", null), ("#FFD400", "white") })
        {
            Assert.Equal(
                StoreErrorCodes.ColorInvalid,
                await CodeOfAsync(() => rig.PreviewHandler().HandleAsync(new PreviewStoreColorsQuery(TenantId, primary, onPrimary, null), Ct)));
        }

        Assert.Empty(rig.Log.Entries);
    }

    // ----- Cuentas de cobro

    [Fact]
    public async Task TheBillingAccountListNormalizesTheCurrencyAndRejectsAMalformedOne()
    {
        var rig = new SettingsRig();
        rig.Accounts.Options.Add(new StoreBillingAccountOption(CompanyId, "Origen", "Bancolombia", "A-111", "EUR"));

        var listed = await rig.ListAccountsHandler().HandleAsync(new ListStoreBillingAccountsQuery(TenantId, " eur "), Ct);

        Assert.Equal("EUR", rig.Accounts.LastListedCurrency);
        Assert.Single(listed.Items);

        await rig.ListAccountsHandler().HandleAsync(new ListStoreBillingAccountsQuery(TenantId, null), Ct);
        Assert.Null(rig.Accounts.LastListedCurrency);

        Assert.Equal(
            StoreErrorCodes.CurrencyInvalid,
            await CodeOfAsync(() => rig.ListAccountsHandler().HandleAsync(new ListStoreBillingAccountsQuery(TenantId, "EURO"), Ct)));
    }
}
