using BuildingBlocks.Application;
using BuildingBlocks.Domain.Pricing;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>
/// Fase 3 (Task 4): lo que el distribuidor ve del catálogo del mayorista. Toda lectura entre tenants pasa por la relación
/// <c>Active</c> y el tenant mayorista activo con el módulo prendido; el precio es el de lista con la regla del mayorista.
/// </summary>
public sealed class WholesaleOffersTests
{
    private readonly DistributorWorld _world = new();
    private readonly FakeTenancy _tenancy = new();
    private readonly FakeCatalogReader _catalog = new();
    private readonly FakeTenantPriceRounding _rounding = new(new PriceRounding(0, PriceRoundingMode.Nearest));
    private readonly Guid _buyer = Guid.CreateVersion7();

    public WholesaleOffersTests()
    {
        _tenancy.AddTenant(
            _world.Tenant,
            "Mayorista Central",
            Guid.CreateVersion7(),
            TenantStatus.Active,
            TenantModuleKeys.Catalog,
            TenantModuleKeys.Customers,
            TenantModuleKeys.Companies,
            TenantModuleKeys.Quotations,
            TenantModuleKeys.Orders,
            TenantModuleKeys.Wholesale);
    }

    private static CancellationToken Ct => DistributorWorld.Ct;

    private WholesaleOffers Offers() =>
        new(_world.Distributors, _world.PriceLists, new FakeTenantDefaultCurrency("COP"), _rounding, _catalog, _tenancy, _tenancy);

    private Distributor SeedActive() => _world.SeedActive(distributorTenant: _buyer);

    private PriceList UseCategories(Distributor distributor, decimal discount, params Guid[] categories)
    {
        var list = _world.PriceLists.Seed(PriceList.Create(
            _world.Tenant, "Hogar " + Guid.CreateVersion7(), discount, PriceListVisibility.Categories, categories,
            DistributorWorld.Now));
        distributor.ChangeTerms(list.Id, distributor.AdvisorMemberId, distributor.BillingCompanyId, DistributorWorld.Now);
        return list;
    }

    [Fact]
    public async Task WithoutRelationshipIsRelationshipInactive()
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Offers().ResolveTermsAsync(_world.Tenant, _buyer, Ct));

        Assert.Equal(WholesaleErrorCodes.RelationshipInactive, error.Code);
    }

    [Fact]
    public async Task ABlockedRelationshipIsRelationshipInactive()
    {
        SeedActive().Block(DistributorWorld.Now);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Offers().SearchAsync(_world.Tenant, _buyer, null, 1, 50, Ct));

        Assert.Equal(WholesaleErrorCodes.RelationshipInactive, error.Code);
    }

    [Fact]
    public async Task AnInvitedRelationshipIsRelationshipInactive()
    {
        // Una invitada no tiene tenant del distribuidor: aunque alguien adivine el id reservado, no hay relación.
        var invited = _world.SeedInvited();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Offers().ResolveTermsAsync(_world.Tenant, invited.Invitation.ReservedTenantId, Ct));

        Assert.Equal(WholesaleErrorCodes.RelationshipInactive, error.Code);
    }

    [Fact]
    public async Task AWholesalerWithoutTheModuleIsRelationshipInactive()
    {
        SeedActive();
        _tenancy.Tenants[_world.Tenant].Modules.Remove(TenantModuleKeys.Wholesale);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Offers().ResolveTermsAsync(_world.Tenant, _buyer, Ct));

        Assert.Equal(WholesaleErrorCodes.RelationshipInactive, error.Code);
    }

    [Fact]
    public async Task AnInactiveWholesalerTenantIsRelationshipInactive()
    {
        SeedActive();
        var entry = _tenancy.Tenants[_world.Tenant];
        _tenancy.Tenants[_world.Tenant] = entry with { Status = TenantStatus.Suspended };

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Offers().ResolveTermsAsync(_world.Tenant, _buyer, Ct));

        Assert.Equal(WholesaleErrorCodes.RelationshipInactive, error.Code);
    }

    [Fact]
    public async Task TermsCarryTheRelationshipData()
    {
        var distributor = SeedActive();

        var terms = await Offers().ResolveTermsAsync(_world.Tenant, _buyer, Ct);

        Assert.Equal(distributor.Id.Value, terms.DistributorId);
        Assert.Equal(distributor.CustomerId, terms.CustomerId);
        Assert.Equal(distributor.AdvisorMemberId, terms.AdvisorMembershipId);
        Assert.Equal(distributor.BillingCompanyId, terms.BillingCompanyId);
        Assert.Equal(("COP", 15m, "Mayoristas"), (terms.Currency, terms.DiscountPercent, terms.PriceListName));
        Assert.Null(terms.VisibleCategoryIds);
    }

    [Fact]
    public async Task AProductOutsideTheCategoriesIsNotVisible()
    {
        var hogar = _catalog.AddCategory("Hogar");
        var cocina = _catalog.AddCategory("Cocina");
        var olla = _catalog.AddProduct("C-1", "Olla", cocina, cop: 10000m);
        UseCategories(SeedActive(), 10m, hogar);

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() =>
            Offers().PriceVisibleAsync(_world.Tenant, _buyer, [olla], Ct));

        Assert.Equal(WholesaleErrorCodes.ProductNotVisible, error.Code);
        Assert.Contains("C-1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownVariantIsNotVisible()
    {
        SeedActive();

        var error = await Assert.ThrowsAsync<WholesaleDomainException>(() =>
            Offers().PriceVisibleAsync(_world.Tenant, _buyer, [Guid.CreateVersion7()], Ct));

        Assert.Equal(WholesaleErrorCodes.ProductNotVisible, error.Code);
    }

    [Fact]
    public async Task ADescendantCategoryIsVisible()
    {
        var hogar = _catalog.AddCategory("Hogar");
        var bano = _catalog.AddCategory("Baño", hogar);
        var toalla = _catalog.AddProduct("B-1", "Toalla", bano, cop: 20000m);
        UseCategories(SeedActive(), 10m, hogar);

        var (terms, offers) = await Offers().PriceVisibleAsync(_world.Tenant, _buyer, [toalla], Ct);

        Assert.Equal([hogar], terms.VisibleCategoryIds!);
        Assert.Equal(18000m, offers[toalla].ListPrice);
    }

    [Fact]
    public async Task AllActiveSeesEveryPricedVariant()
    {
        _catalog.AddProduct("A-1", "Olla", _catalog.AddCategory("Cocina"), cop: 10000m);
        _catalog.AddProduct("A-2", "Martillo", null, cop: 30000m);
        _catalog.AddProduct("A-3", "Tetera", null, cop: null, usd: 9m);
        _catalog.AddProduct("A-4", "Vieja", null, cop: 5000m, isActive: false);
        SeedActive();

        var page = await Offers().SearchAsync(_world.Tenant, _buyer, null, 1, 50, Ct);

        Assert.Equal(["A-2", "A-1"], page.Items.Select(item => item.Code));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task ListPriceUsesTheWholesalerRounding()
    {
        var first = _catalog.AddProduct("R-1", "Uno", null, cop: 10000m);
        var second = _catalog.AddProduct("R-2", "Dos", null, cop: 9999m);
        var distributor = SeedActive();
        var list = _world.PriceLists.Seed(PriceList.Create(
            _world.Tenant, "Doce y medio", 12.5m, PriceListVisibility.AllActive, [], DistributorWorld.Now));

        var (_, fifteen) = await Offers().PriceVisibleAsync(_world.Tenant, _buyer, [first], Ct);
        distributor.ChangeTerms(list.Id, distributor.AdvisorMemberId, distributor.BillingCompanyId, DistributorWorld.Now);
        var (_, twelve) = await Offers().PriceVisibleAsync(_world.Tenant, _buyer, [second], Ct);

        Assert.Equal(8500m, fifteen[first].ListPrice);
        // 9999 × 0,875 = 8749,125 → 8749 con COP sin decimales.
        Assert.Equal(8749m, twelve[second].ListPrice);
        Assert.All(_rounding.AskedCurrencies, currency => Assert.Equal("COP", currency));
    }

    [Fact]
    public async Task SearchReturnsBaseDiscountListAndTax()
    {
        _catalog.AddProduct("S-1", "Olla grande", null, cop: 11900m, taxRatePercent: 19m);
        _catalog.AddProduct("S-2", "Martillo", null, cop: 5000m);
        SeedActive();

        var page = await Offers().SearchAsync(_world.Tenant, _buyer, "olla", 1, 50, Ct);

        Assert.Equal(("COP", "Mayoristas", 15m, 1), (page.Currency, page.PriceListName, page.DiscountPercent, page.Total));
        var item = Assert.Single(page.Items);
        Assert.Equal(("S-1", "Olla grande", 11900m, 15m, 10115m, 19m),
            (item.Code, item.Name, item.BasePrice, item.DiscountPercent, item.ListPrice, item.TaxRatePercent));
        Assert.Equal([("COP", (IReadOnlyCollection<Guid>?)null, "olla", 1, 50)], _catalog.SearchCalls);
    }

    [Fact]
    public async Task SearchOnlyPassesTheVisibleCategories()
    {
        var hogar = _catalog.AddCategory("Hogar");
        UseCategories(SeedActive(), 10m, hogar);

        await Offers().SearchAsync(_world.Tenant, _buyer, null, 2, 20, Ct);

        var call = Assert.Single(_catalog.SearchCalls);
        Assert.Equal([hogar], call.CategoryIds!);
        Assert.Equal((2, 20), (call.Page, call.PageSize));
    }

    [Fact]
    public async Task OrderPricesIgnoreBlockedAndVisibility()
    {
        var hogar = _catalog.AddCategory("Hogar");
        var cocina = _catalog.AddProduct("C-1", "Olla", _catalog.AddCategory("Cocina"), cop: 10000m);
        var distributor = SeedActive();
        UseCategories(distributor, 15m, hogar);
        distributor.Block(DistributorWorld.Now);

        var prices = await Offers().ListPricesForOrderAsync(_world.Tenant, _buyer, "COP", [cocina], Ct);

        Assert.Equal(8500m, prices[cocina]);
    }

    [Fact]
    public async Task OrderPricesWithoutRelationshipAreEmpty()
    {
        var olla = _catalog.AddProduct("C-1", "Olla", null, cop: 10000m);

        var prices = await Offers().ListPricesForOrderAsync(_world.Tenant, _buyer, "COP", [olla], Ct);

        Assert.Empty(prices);
    }
}
