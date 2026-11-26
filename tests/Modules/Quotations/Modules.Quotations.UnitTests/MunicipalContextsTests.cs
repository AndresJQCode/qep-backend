using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El contexto municipal que la aplicación arma antes de llamar al agregado (plan P4, spec ReteICA por empresa §7.5): lo
/// que la empresa ofrece por cada tipo municipal de su país en el municipio de la operación y si el cliente es agente ahí.
/// </summary>
public sealed class MunicipalContextsTests
{
    private static readonly Guid TenantId = Guid.Parse("0193b6a2-0000-7000-8000-000000002101");
    private static readonly Guid CustomerId = Guid.Parse("0193b6a2-0000-7000-8000-000000002102");
    private static readonly Guid CompanyId = Guid.Parse("0193b6a2-0000-7000-8000-000000002103");
    private static readonly Guid Bogota = Guid.Parse("0193b6a2-0000-7000-8000-000000002104");
    private static readonly Guid Medellin = Guid.Parse("0193b6a2-0000-7000-8000-000000002105");
    private static readonly DateOnly DocumentDate = new(2026, 11, 14);

    private static readonly WithholdingRate ReteIcaBogota =
        new(Guid.CreateVersion7(), "ReteICA Bogotá", "ReteICA", "CO", 0.966m, null, null, null, Bogota);

    private static readonly StubQuotationGeographyLookup Geography = new(new Dictionary<Guid, string>
    {
        [Bogota] = "Bogotá, D.C.",
        [Medellin] = "Medellín",
    });

    [Theory]
    [InlineData("quotation")]
    [InlineData("order")]
    public async Task ExplicitUnknownCityIs422(string prefix)
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ResolveAsync(
            new StubQuotationWithholdingLookup(), Account("CO", Bogota), Guid.NewGuid(), explicitCity: true, prefix));

        Assert.Equal($"{prefix}.billing.operation_city_invalid", error.Code);
    }

    [Fact]
    public async Task NonMunicipalCountryGivesNone()
    {
        var context = await ResolveAsync(
            new StubQuotationWithholdingLookup(), Account("ES", Bogota), operationCityId: null, explicitCity: false);
        var withoutAccount = await ResolveAsync(
            new StubQuotationWithholdingLookup(), null, operationCityId: null, explicitCity: false);

        Assert.Same(MunicipalContext.None, context);
        Assert.Same(MunicipalContext.None, withoutAccount);
    }

    [Fact]
    public async Task OneOfferPerMunicipalType()
    {
        var lookup = new StubQuotationWithholdingLookup()
            .WithCompanyRate(CompanyId, ReteIcaBogota)
            .WithAgent(CustomerId, "ReteICA", Bogota);

        var bogota = await ResolveAsync(lookup, Account("CO", Bogota), Bogota, explicitCity: false);
        var medellin = await ResolveAsync(lookup, Account("CO", Bogota), Medellin, explicitCity: true);
        var withoutCity = await ResolveAsync(lookup, Account("CO", null), operationCityId: null, explicitCity: false);

        Assert.Equal(Bogota, bogota.OperationCityId);
        Assert.Equal(new[] { new MunicipalOffer("ReteICA", ReteIcaBogota, CustomerIsAgent: true) }, bogota.Offers);
        Assert.Equal(Medellin, medellin.OperationCityId);
        Assert.Equal(new[] { new MunicipalOffer("ReteICA", null, CustomerIsAgent: false) }, medellin.Offers);
        // Sin municipio no hay tasa que buscar, pero el tipo sigue en la lista: el agregado sabe qué tipos quitar.
        Assert.Null(withoutCity.OperationCityId);
        Assert.Equal(new[] { new MunicipalOffer("ReteICA", null, CustomerIsAgent: false) }, withoutCity.Offers);
    }

    private static Task<MunicipalContext> ResolveAsync(
        StubQuotationWithholdingLookup lookup,
        QuotationBillingAccount? account,
        Guid? operationCityId,
        bool explicitCity,
        string prefix = "quotation") =>
        MunicipalContexts.ResolveAsync(
            lookup, Geography, TenantId, CustomerId, account, operationCityId, explicitCity, DocumentDate, prefix,
            TestContext.Current.CancellationToken);

    private static QuotationBillingAccount Account(string country, Guid? cityId) => new()
    {
        CompanyId = CompanyId,
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = country == "CO" ? "COP" : "EUR",
        Country = country,
        CityId = cityId,
    };
}
