using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.StoreHandlersWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.1 y §7, plan Tarea 8: el BFF del asistente (<c>GET …/setup</c>) con dobles de los puertos.
/// Lo que se mira: los pasos y el abierto, la sugerencia de empresa y de IVA, la cobertura medida una sola vez, el asesor por
/// defecto y que Shopify caído no tumbe la lectura ni deje datos del comerciante en los logs.
/// </summary>
public sealed class ShopifySetupHandlerTests
{
    private const string ShopEmail = "dueno@mitienda.co";

    private const string ShopPhone = "+573001112233";

    private static readonly ShopifyShopProfile ColombianProfile = new(
        ShopEmail, null, "COP", true, new ShopifyShopAddress("Mi Tienda SAS", "Cra 1 # 2-3", null, "Medellín", "Antioquia", "ANT", "CO", ShopPhone));

    [Fact]
    public async Task AFreshlyLinkedStoreIsAllPending()
    {
        var bed = new SetupBed();

        var setup = await bed.GetAsync();

        Assert.Equal(ShopifySetupStepKeys.All, setup.Steps.Select(step => step.Key));
        Assert.Equal(("Pending", new[] { ShopifySetupMissing.CompanyMissing }), (setup.Steps[0].Status, setup.Steps[0].Missing.ToArray()));
        Assert.Equal(("Blocked", new[] { ShopifySetupStepKeys.Company }), (setup.Steps[1].Status, setup.Steps[1].BlockedBy.ToArray()));
        Assert.Equal(
            ("Blocked", new[] { ShopifySetupStepKeys.Company, ShopifySetupStepKeys.Advisor }),
            (setup.Steps[2].Status, setup.Steps[2].BlockedBy.ToArray()));
        Assert.Equal(("Blocked", new[] { ShopifySetupMissing.ScopeMissing }), (setup.Steps[3].Status, setup.Steps[3].Missing.ToArray()));
        Assert.Equal(("Pending", new[] { ShopifySetupMissing.HistoryNotRun }), (setup.Steps[4].Status, setup.Steps[4].Missing.ToArray()));
        Assert.Equal(ShopifySetupStepKeys.Company, setup.OpenStep);
        Assert.Null(setup.Company.Selected);
        Assert.NotNull(setup.Companies);
        Assert.Empty(setup.Companies);
        Assert.Empty(setup.Waiting);
        Assert.NotNull(setup.Advisor.Options);
        Assert.NotNull(setup.Buyers.DocumentSources);
        Assert.Null(setup.History.LastRun);
        Assert.Null(setup.Products.LastImport);
        // La lectura crea la tienda, como el GET de la tienda; settings trae la versión que el PUT necesita.
        Assert.True(bed.World.Database.Stores.ContainsKey(ConnectionId));
        Assert.Equal(bed.World.Database.Stores[ConnectionId].Version, setup.Store.Settings.Version);
        Assert.Equal(("Mi tienda", "Active"), (setup.Store.Name, setup.Store.ConnectionStatus));
    }

    [Fact]
    public async Task TheSuggestedCityIsResolvedInColombia()
    {
        var bed = new SetupBed(profile: ColombianProfile);
        var department = Guid.CreateVersion7();
        var city = Guid.CreateVersion7();
        bed.Geography.Cities[("ANTIOQUIA", "Medellín")] = new ShopifyCityRef(department, city);

        var setup = await bed.GetAsync();

        var suggested = setup.Company.Suggested;
        Assert.Equal((city, department, (string?)null), (suggested.CityId, suggested.DepartmentId, suggested.CityName));
        Assert.Equal(("Mi Tienda SAS", "CO", "COP"), (suggested.Name, suggested.Country, setup.Store.Currency));
    }

    [Fact]
    public async Task ASuggestionOutsideColombiaKeepsTheCityText()
    {
        var profile = ColombianProfile with
        {
            BillingAddress = ColombianProfile.BillingAddress! with { City = "Monterrey", Province = "Nuevo León", ProvinceCode = "NL", CountryCode = "MX" },
        };
        var bed = new SetupBed(profile: profile);

        var setup = await bed.GetAsync();

        Assert.Equal(((Guid?)null, "Monterrey", "MX"), (setup.Company.Suggested.CityId, setup.Company.Suggested.CityName, setup.Company.Suggested.Country));
        Assert.Empty(bed.Geography.Requests);
    }

    [Fact]
    public async Task WithoutReadProductsTheVatSuggestionIsNull()
    {
        var bed = new SetupBed();

        var setup = await bed.GetAsync();

        Assert.Null(setup.Company.SuggestedChargesVat);
        Assert.Equal((0, 0, 0), (bed.Source.ShopRequests, bed.Source.TaxableRequests, bed.Source.VariantKeyRequests));
        Assert.False(setup.Products.ScopeGranted);
    }

    [Fact]
    public async Task AShopWithoutProductsHasANullVatSuggestion()
    {
        var bed = new SetupBed(scopes: ["read_orders", "read_products"]);
        bed.Source.Shop = new ShopifyShopInfo("COP", true, 0);
        bed.Source.Taxable = true;

        var setup = await bed.GetAsync();

        Assert.Null(setup.Company.SuggestedChargesVat);
        Assert.Equal(0, bed.Source.TaxableRequests);
    }

    [Fact]
    public async Task ATaxableVariantSuggestsChargingVat()
    {
        var bed = new SetupBed(scopes: ["read_orders", "read_products"]);
        bed.Source.Shop = new ShopifyShopInfo("COP", true, 12);
        bed.Source.Taxable = true;

        var setup = await bed.GetAsync();

        Assert.True(setup.Company.SuggestedChargesVat);
    }

    [Fact]
    public async Task ADoneCompanyDoesNotAskShopifyForVat()
    {
        var bed = new SetupBed(scopes: ["read_orders", "read_products"]);
        bed.SeedStoreWithCompany();

        var setup = await bed.GetAsync();

        Assert.Equal("Done", setup.Steps[0].Status);
        Assert.Null(setup.Company.SuggestedChargesVat);
        Assert.Equal((0, 0), (bed.Source.ShopRequests, bed.Source.TaxableRequests));
        Assert.Equal(CompanyId, setup.Company.Selected!.Id);
        Assert.True(setup.Company.Selected.HasBankAccount);
        Assert.Equal("Blocked", setup.Steps[2].Status);
        Assert.Equal([ShopifySetupStepKeys.Advisor], setup.Steps[2].BlockedBy);
    }

    [Fact]
    public async Task ShopifyUnavailableStillAnswersTheSetup()
    {
        var bed = new SetupBed(scopes: ["read_orders", "read_products"]);
        var unavailable = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.Unavailable, null);
        bed.Source.ShopFailure = unavailable;
        bed.Source.Failure = unavailable;

        var setup = await bed.GetAsync();

        Assert.Null(setup.Company.SuggestedChargesVat);
        Assert.Null(setup.Products.Coverage);
        Assert.Equal(ShopifySetupStepKeys.All, setup.Steps.Select(step => step.Key));
        Assert.Null(bed.World.Database.Stores[ConnectionId].ProductsCoverage);
    }

    [Fact]
    public async Task TheCoverageIsMeasuredOnceWhenThereIsNoImport()
    {
        var bed = new SetupBed(scopes: ["read_orders", "read_products"]);
        bed.Source.VariantPages.Enqueue(new ShopifyVariantKeysPage([new ShopifyVariantKey(1, "SKU-NUEVO", "Medias")], null));

        var first = await bed.GetAsync();
        var second = await bed.GetAsync();

        Assert.Equal(1, bed.Source.VariantKeyRequests);
        Assert.Equal((1, 0, Now), (first.Products.Coverage!.ShopifyVariants, first.Products.Coverage.Matched, first.Products.Coverage.MeasuredAt));
        Assert.Equal(first.Products.Coverage.MeasuredAt, second.Products.Coverage!.MeasuredAt);
        Assert.Equal([new ShopifyMissingVariantDto("SKU-NUEVO", "Medias")], second.Products.Coverage.MissingSample);
    }

    [Fact]
    public async Task TheDefaultAdvisorIsTheCaller()
    {
        var bed = new SetupBed();
        bed.World.Members.Active.Add(CallerMembershipId);
        bed.World.Members.Names[CallerMembershipId] = "Quien configura";

        var setup = await bed.GetAsync();

        Assert.Null(setup.Advisor.SelectedMembershipId);
        Assert.Equal(CallerMembershipId, setup.Advisor.DefaultMembershipId);
        Assert.Equal([CallerMembershipId], setup.Advisor.Options.Where(option => option.IsMe).Select(option => option.MembershipId));
        Assert.Equal(2, setup.Advisor.Options.Count);
    }

    [Fact]
    public async Task WithoutRedirectUriTheReauthorizeUrlIsNull()
    {
        var bed = new SetupBed();

        var without = await bed.GetAsync();
        Assert.Null(without.Products.ReauthorizeUrl);
        Assert.Empty(bed.Reauthorization.Requests);

        var with = await bed.GetAsync("https://app.example.co/shopify/callback");
        Assert.NotNull(with.Products.ReauthorizeUrl);
        Assert.Equal([("mi-tienda.myshopify.com", "https://app.example.co/shopify/callback")], bed.Reauthorization.Requests);
    }

    [Fact]
    public async Task TheSetupNeverLogsTheShopEmailOrPhone()
    {
        var bed = new SetupBed(scopes: ["read_orders", "read_products"], profile: ColombianProfile);
        var unavailable = new ShopifyProductsSourceException(ShopifyProductsSourceFailure.Unavailable, null);
        bed.Source.ShopFailure = unavailable;
        bed.Source.Failure = unavailable;

        await bed.GetAsync();

        Assert.NotEmpty(bed.Logger.Entries);
        Assert.DoesNotContain(bed.Logger.Entries, entry =>
            entry.Message.Contains(ShopEmail, StringComparison.Ordinal) || entry.Message.Contains(ShopPhone, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitingAndStatsComeFromTheMirror()
    {
        var bed = new SetupBed();
        bed.World.SeedStore();
        bed.World.SeedWaitingRow();
        bed.World.Mirrors.AttributeKeys.Add("Cédula");

        var setup = await bed.GetAsync();

        var group = Assert.Single(setup.Waiting);
        Assert.Equal((ShopifyWaitReasons.ProductUnmatched, 1), (group.Reason, group.Count));
        Assert.Contains("Cédula", setup.Buyers.DocumentSources);
        Assert.Equal(0, setup.Stats.ImportedLast7Days);
    }

    [Fact]
    public async Task ReadPermissionOnlyIsDenied()
    {
        var bed = new SetupBed(permissions: [ShopifyPermissions.ConnectionRead]);

        await Assert.ThrowsAsync<BuildingBlocks.Application.RequestForbiddenException>(() => bed.GetAsync());
    }
}

/// <summary>El asistente sobre <see cref="StoreHandlersWorld"/>: la conexión con sus scopes y su perfil, y los dobles de Shopify,
/// Companies, Geography y la importación de productos.</summary>
internal sealed class SetupBed
{
    public SetupBed(IReadOnlyCollection<string>? scopes = null, ShopifyShopProfile? profile = null, string[]? permissions = null)
    {
        World = permissions is null ? new StoreHandlersWorld() : new StoreHandlersWorld(permissions);
        World.Connections.Connections["mi-tienda.myshopify.com"] = World.Connection with
        {
            Scopes = scopes ?? ["read_orders", "read_customers"],
            Profile = profile,
        };
    }

    public StoreHandlersWorld World { get; }

    public FakeSetupCompanies Companies { get; } = new();

    public FakeProductsSource Source { get; } = new();

    public FakeProductLinks Links { get; } = new();

    public FakeProductLookup Products { get; } = new();

    public FakeProductImports Imports { get; } = new();

    public FakeGeographyLookup Geography { get; } = new();

    public ShopifyProductImportHandlersTests.FakeReauthorization Reauthorization { get; } = new();

    public RecordingLogger<GetShopifySetupHandler> Logger { get; } = new();

    /// <summary>La tienda con la empresa activa del tenant (con NIT y la cuenta guardada), sin asesor.</summary>
    public ShopifyStore SeedStoreWithCompany()
    {
        var store = World.SeedStore();
        store.AssignBilling(new ShopifyBillingAccount(CompanyId, "Bancolombia", "123-456", "COP"), CallerMembershipId, Now);
        Companies.Active.Add(new ShopifySetupCompanyRef(
            CompanyId, "Origen Botánico SAS", "901.234.567-8", "NIT", "CO", true, [new ShopifySetupBankAccount("Bancolombia", "123-456", "COP")]));
        return store;
    }

    public Task<ShopifySetupDto> GetAsync(string? redirectUri = null) =>
        new GetShopifySetupHandler(
                World.Context,
                new ShopifyEnabledModules(),
                World.Connections,
                World.TenantConnections,
                World.Stores,
                World.UnitOfWork,
                Companies,
                World.Members,
                World.References,
                World.Memberships,
                Geography,
                Source,
                Reauthorization,
                Imports,
                new ShopifyProductCoverageCalculator(Source, Links, Products, World.Clock),
                World.Mirrors,
                Links,
                World.Clock,
                Logger)
            .HandleAsync(new GetShopifySetupQuery(TenantId, ConnectionId, redirectUri), TestContext.Current.CancellationToken);
}

/// <summary>Las empresas activas del tenant en memoria. <see cref="CreateAsync"/> anota el borrador y agrega la empresa con
/// <see cref="NextId"/>; <see cref="CreateFailure"/> la hace fallar como lo haría Companies.</summary>
internal sealed class FakeSetupCompanies : IShopifySetupCompanies
{
    public List<ShopifySetupCompanyRef> Active { get; } = [];

    public List<ShopifySetupCompanyDraft> Created { get; } = [];

    public Guid NextId { get; set; } = Guid.Parse("01900000-0000-7000-8000-0000000000d1");

    public Exception? CreateFailure { get; set; }

    public Task<IReadOnlyList<ShopifySetupCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShopifySetupCompanyRef>>(
            tenantId == StoreHandlersWorld.TenantId ? [.. Active.OrderBy(company => company.Name, StringComparer.Ordinal)] : []);

    public Task<ShopifySetupCompanyRef?> FindActiveAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        Task.FromResult(tenantId == StoreHandlersWorld.TenantId ? Active.FirstOrDefault(company => company.Id == companyId) : null);

    public Task<ShopifySetupCompanyRef> CreateAsync(ShopifySetupCompanyDraft draft, CancellationToken cancellationToken)
    {
        Created.Add(draft);
        if (CreateFailure is { } failure)
        {
            return Task.FromException<ShopifySetupCompanyRef>(failure);
        }

        var company = new ShopifySetupCompanyRef(
            NextId, draft.Name, draft.TaxId, draft.DocumentType ?? "NIT", draft.Country, draft.ChargesVat,
            draft.BankAccount is { } account ? [account] : []);
        Active.Add(company);
        return Task.FromResult(company);
    }
}
