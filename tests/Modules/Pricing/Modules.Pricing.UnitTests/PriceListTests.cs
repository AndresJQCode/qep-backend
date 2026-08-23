using Modules.Pricing.Domain;

namespace Modules.Pricing.UnitTests;

public sealed class PriceListTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateStartsActive()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);

        Assert.True(priceList.IsActive);
        Assert.Equal(TenantId, priceList.TenantId);
        Assert.Equal("Mayorista", priceList.Name);
        Assert.Equal("MAY", priceList.Prefix);
        Assert.Equal(1, priceList.Version);
        Assert.Equal(Now, priceList.CreatedAt);
        Assert.Equal(Now, priceList.UpdatedAt);
    }

    // Mismo criterio que ClientClassification y TaxRate: los indices unicos son sobre
    // (tenant_id, name) y (tenant_id, prefix), y " Mayorista " contra "Mayorista" serian dos
    // filas para lo que una persona lee como la misma lista.
    [Fact]
    public void CreateTrimsNameAndPrefix()
    {
        var priceList = PriceList.Create(
            PriceListId.New(), TenantId, "  Mayorista  ", "  MAY  ", Now);

        Assert.Equal("Mayorista", priceList.Name);
        Assert.Equal("MAY", priceList.Prefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankName(string name)
    {
        var error = Assert.Throws<PricingDomainException>(() =>
            PriceList.Create(PriceListId.New(), TenantId, name, "MAY", Now));

        Assert.Equal("pricing.price_list.name_required", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankPrefix(string prefix)
    {
        var error = Assert.Throws<PricingDomainException>(() =>
            PriceList.Create(PriceListId.New(), TenantId, "Mayorista", prefix, Now));

        Assert.Equal("pricing.price_list.prefix_required", error.Code);
    }

    // La columna es varchar(120). Sin guarda de dominio el valor llega a PostgreSQL y vuelve
    // como 500 server.unexpected.
    [Fact]
    public void CreateRejectsNameOverOneHundredTwentyCharacters()
    {
        var error = Assert.Throws<PricingDomainException>(() =>
            PriceList.Create(PriceListId.New(), TenantId, new string('a', 121), "MAY", Now));

        Assert.Equal("pricing.price_list.name_too_long", error.Code);
    }

    // La columna es varchar(20).
    [Fact]
    public void CreateRejectsPrefixOverTwentyCharacters()
    {
        var error = Assert.Throws<PricingDomainException>(() =>
            PriceList.Create(PriceListId.New(), TenantId, "Mayorista", new string('a', 21), Now));

        Assert.Equal("pricing.price_list.prefix_too_long", error.Code);
    }

    [Fact]
    public void UpdateChangesNameAndPrefixAndAdvancesUpdatedAt()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);
        var later = Now.AddMinutes(5);

        priceList.Update("Minorista", "MIN", later);

        Assert.Equal("Minorista", priceList.Name);
        Assert.Equal("MIN", priceList.Prefix);
        Assert.Equal(later, priceList.UpdatedAt);
        Assert.Equal(Now, priceList.CreatedAt);
    }

    [Fact]
    public void UpdateAdvancesTheConcurrencyToken()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);

        priceList.Update("Minorista", "MIN", Now.AddMinutes(5));

        Assert.Equal(2, priceList.Version);
    }

    [Fact]
    public void UpdateRejectsAnInactivePriceList()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);
        priceList.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<PricingDomainException>(() =>
            priceList.Update("Minorista", "MIN", Now.AddMinutes(10)));

        Assert.Equal("pricing.price_list.inactive", error.Code);
    }

    [Fact]
    public void DeactivateTurnsPriceListInactiveAndAdvancesUpdatedAt()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);
        var later = Now.AddMinutes(5);

        priceList.Deactivate(later);

        Assert.False(priceList.IsActive);
        Assert.Equal(later, priceList.UpdatedAt);
        Assert.Equal(2, priceList.Version);
    }

    [Fact]
    public void DeactivateRejectsAnAlreadyInactivePriceList()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);
        priceList.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<PricingDomainException>(() =>
            priceList.Deactivate(Now.AddMinutes(10)));

        Assert.Equal("pricing.price_list.already_inactive", error.Code);
    }

    [Fact]
    public void ActivateTurnsPriceListActiveAndAdvancesUpdatedAt()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);
        priceList.Deactivate(Now.AddMinutes(5));
        var later = Now.AddMinutes(10);

        priceList.Activate(later);

        Assert.True(priceList.IsActive);
        Assert.Equal(later, priceList.UpdatedAt);
    }

    [Fact]
    public void ActivateRejectsAnAlreadyActivePriceList()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);

        var error = Assert.Throws<PricingDomainException>(() =>
            priceList.Activate(Now.AddMinutes(5)));

        Assert.Equal("pricing.price_list.already_active", error.Code);
    }

    // Create deja 1, Deactivate 2, Activate 3.
    [Fact]
    public void ActivateAdvancesTheConcurrencyToken()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);
        priceList.Deactivate(Now.AddMinutes(5));

        priceList.Activate(Now.AddMinutes(10));

        Assert.Equal(3, priceList.Version);
    }

    // El caso que prueba que Update vuelve a funcionar despues de Activate: sin esto se puede
    // entregar un Activate que responde bien y deja la lista igual de congelada, porque Update
    // sigue abriendo con EnsureActive().
    [Fact]
    public void ActivateReopensUpdate()
    {
        var priceList = PriceList.Create(PriceListId.New(), TenantId, "Mayorista", "MAY", Now);
        priceList.Deactivate(Now.AddMinutes(5));
        priceList.Activate(Now.AddMinutes(10));

        priceList.Update("Minorista", "MIN", Now.AddMinutes(15));

        Assert.Equal("Minorista", priceList.Name);
        Assert.Equal("MIN", priceList.Prefix);
    }
}
