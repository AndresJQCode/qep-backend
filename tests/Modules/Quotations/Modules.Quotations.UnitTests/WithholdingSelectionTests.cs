using BuildingBlocks.Domain.Taxes;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Plan P11 (spec impuestos por tipo §7.5, D23): un selector sólo acepta tasas del tenant, activas, que retienen, del
/// país de la empresa facturadora y una por tipo. El código lleva el prefijo del documento.
/// </summary>
public sealed class WithholdingSelectionTests
{
    private static readonly Guid TenantId = Guid.Parse("0193b6a2-0000-7000-8000-000000001301");
    private static readonly DateOnly DocumentDate = new(2026, 11, 11);

    private static readonly WithholdingRate RetefuenteServicios = Rate("Retefuente servicios 4 %", "Retefuente", 4m);
    private static readonly WithholdingRate RetefuenteCompras = Rate("Retefuente compras 2,5 %", "Retefuente", 2.5m);
    private static readonly WithholdingRate ReteIca = Rate("ReteICA Bogotá", "ReteICA", 0.966m);
    private static readonly WithholdingRate Inactive = Rate("Retefuente vieja", "Retefuente", 3.5m);
    private static readonly WithholdingRate Vat = Rate("IVA 19 %", "IVA", 19m);
    private static readonly WithholdingRate Spanish = Rate("Retención IRPF 15 %", "Retefuente", 15m, "ES");

    private static StubQuotationWithholdingLookup Catalog() =>
        new StubQuotationWithholdingLookup()
            .WithRate(RetefuenteServicios)
            .WithRate(RetefuenteCompras)
            .WithRate(ReteIca)
            .WithRate(Inactive, isActive: false)
            .WithRate(Vat, withholds: false)
            .WithRate(Spanish);

    public static TheoryData<string, string, string?> InvalidSelections() => new()
    {
        { "missing", "quotation", "CO" },
        { "inactive", "quotation", "CO" },
        { "adds", "quotation", "CO" },
        { "other-country", "quotation", "CO" },
        { "no-company", "quotation", null },
        { "missing", "order", "CO" },
        { "no-company", "order", null },
    };

    [Theory]
    [MemberData(nameof(InvalidSelections))]
    public async Task AnUnusableRateIsInvalidWithTheDocumentPrefix(string selection, string prefix, string? companyCountry)
    {
        var id = selection switch
        {
            "missing" => Guid.Parse("0193b6a2-0000-7000-8000-0000000013ff"),
            "inactive" => Inactive.TaxRateId,
            "adds" => Vat.TaxRateId,
            "other-country" => Spanish.TaxRateId,
            _ => RetefuenteServicios.TaxRateId,
        };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ResolveAsync(
            [id], companyCountry, prefix));

        Assert.Equal($"{prefix}.billing.withholding_tax_rate_invalid", error.Code);
    }

    [Theory]
    [InlineData("quotation")]
    [InlineData("order")]
    public async Task TwoRatesOfTheSameTypeAreRepeated(string prefix)
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ResolveAsync(
            [RetefuenteServicios.TaxRateId, RetefuenteCompras.TaxRateId], "CO", prefix));

        Assert.Equal($"{prefix}.billing.withholding_type_repeated", error.Code);
    }

    // Spec 2026-11-18 §4.3: una de bienes y una de servicios del mismo tipo conviven; All con otra, o dos del mismo
    // alcance, son repetidas con el código de siempre.
    private static readonly WithholdingRate RetefuenteGoods =
        Rate("Retefuente bienes 2,5 %", "Retefuente", 2.5m) with { AppliesTo = WithholdingScope.Goods };

    private static readonly WithholdingRate RetefuenteForServices =
        Rate("Retefuente servicios 4 % (servicios)", "Retefuente", 4m) with { AppliesTo = WithholdingScope.Services };

    private static readonly WithholdingRate OtherRetefuenteForServices =
        Rate("Retefuente servicios 6 %", "Retefuente", 6m) with { AppliesTo = WithholdingScope.Services };

    private static Task<IReadOnlyList<WithholdingRate>> ResolveScopedAsync(IReadOnlyCollection<Guid> ids, string prefix) =>
        WithholdingSelection.ResolveAsync(
            Catalog().WithRate(RetefuenteGoods).WithRate(RetefuenteForServices).WithRate(OtherRetefuenteForServices),
            TenantId, ids, "CO", DocumentDate, MunicipalSelectorScope.None, prefix, TestContext.Current.CancellationToken);

    [Fact]
    public async Task GoodsAndServicesOfTheSameTypeAreAccepted()
    {
        var rates = await ResolveScopedAsync([RetefuenteGoods.TaxRateId, RetefuenteForServices.TaxRateId], "quotation");

        Assert.Equal(new[] { RetefuenteGoods, RetefuenteForServices }, rates);
    }

    [Theory]
    [InlineData("quotation")]
    [InlineData("order")]
    public async Task AllWithAnotherScopeIsRepeated(string prefix)
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ResolveScopedAsync(
            [RetefuenteCompras.TaxRateId, RetefuenteForServices.TaxRateId], prefix));

        Assert.Equal($"{prefix}.billing.withholding_type_repeated", error.Code);
    }

    [Fact]
    public async Task TwoOfTheSameScopeAreRepeated()
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ResolveScopedAsync(
            [RetefuenteForServices.TaxRateId, OtherRetefuenteForServices.TaxRateId], "order"));

        Assert.Equal("order.billing.withholding_type_repeated", error.Code);
    }

    [Fact]
    public async Task ValidRatesComeBackInTheOrderTheyArrived()
    {
        var rates = await ResolveAsync(
            [ReteIca.TaxRateId, RetefuenteServicios.TaxRateId], " co ", "quotation", Offered(ReteIca, ignoreUnmatched: false));

        Assert.Equal(new[] { ReteIca, RetefuenteServicios }, rates);
    }

    [Fact]
    public async Task NoIdsNeedNoCompanyAndNoCatalog()
    {
        var lookup = Catalog();

        var rates = await WithholdingSelection.ResolveAsync(
            lookup, TenantId, [], companyCountry: null, DocumentDate, MunicipalSelectorScope.None, "quotation",
            TestContext.Current.CancellationToken);

        Assert.Empty(rates);
        Assert.Empty(lookup.RequestedDates);
    }

    [Fact]
    public async Task TheDocumentDateReachesTheCatalog()
    {
        var lookup = Catalog();

        await WithholdingSelection.ResolveAsync(
            lookup, TenantId, [RetefuenteServicios.TaxRateId], "CO", DocumentDate, MunicipalSelectorScope.None,
            "quotation", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { DocumentDate }, lookup.RequestedDates);
    }

    // Spec ReteICA por empresa §7.5: un id municipal sólo vale si es la tasa de la empresa para el municipio de la
    // operación; sin cambio de empresa ni de municipio, otro es 422 con el código de siempre.
    [Theory]
    [InlineData("quotation")]
    [InlineData("order")]
    public async Task MunicipalIdNotOfferedIs422(string prefix)
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ResolveAsync(
            [ReteIca.TaxRateId], "CO", prefix, MunicipalSelectorScope.None));

        Assert.Equal($"{prefix}.billing.withholding_tax_rate_invalid", error.Code);
    }

    // A11 (owner 2026-11-13): con cambio de empresa o de municipio en el mismo cuerpo, el id elegido contra lo anterior
    // se ignora sin 422 y queda lo que dejó la re-resolución.
    [Fact]
    public async Task UnmatchedMunicipalIdIgnoredWhenScopeSaysSo()
    {
        var rates = await ResolveAsync(
            [ReteIca.TaxRateId, RetefuenteServicios.TaxRateId], "CO", "quotation",
            new MunicipalSelectorScope(new HashSet<Guid>(), IgnoreUnmatched: true));

        Assert.Equal(new[] { RetefuenteServicios }, rates);
    }

    [Fact]
    public async Task OfferedMunicipalIdPassesEvenWhenIgnoringUnmatched()
    {
        var rates = await ResolveAsync(
            [RetefuenteServicios.TaxRateId, ReteIca.TaxRateId], "CO", "quotation", Offered(ReteIca, ignoreUnmatched: true));

        Assert.Equal(new[] { RetefuenteServicios, ReteIca }, rates);
    }

    [Fact]
    public async Task OfferedMunicipalIdPasses()
    {
        var rates = await ResolveAsync([ReteIca.TaxRateId], "CO", "order", Offered(ReteIca, ignoreUnmatched: false));

        Assert.Equal(new[] { ReteIca }, rates);
    }

    // P6: con la re-resolución, cada tipo sin id aceptado conserva la fila que ella dejó, al final; un id aceptado la
    // reemplaza.
    [Fact]
    public async Task ReResolvedRowIsAppendedForATypeWithoutAnAcceptedId()
    {
        var reResolved = ReteIca with { Name = "ReteICA Medellín" };
        var scope = new MunicipalSelectorScope(new HashSet<Guid>(), IgnoreUnmatched: true) { ReResolved = [reResolved] };

        var rates = await ResolveAsync([ReteIca.TaxRateId, RetefuenteServicios.TaxRateId], "CO", "quotation", scope);
        var withoutIds = await ResolveAsync([], "CO", "quotation", scope);

        Assert.Equal(new[] { RetefuenteServicios, reResolved }, rates);
        Assert.Equal(new[] { reResolved }, withoutIds);
    }

    // P12: un tipo marcado «Sin retención» no recibe la fila re-resuelta.
    [Fact]
    public async Task ClearedTypeGetsNoReResolvedRow()
    {
        var scope = new MunicipalSelectorScope(new HashSet<Guid>(), IgnoreUnmatched: true) { ReResolved = [ReteIca] };

        var rates = await WithholdingSelection.ResolveAsync(
            Catalog(), TenantId, [RetefuenteServicios.TaxRateId], "CO", DocumentDate, scope, "quotation",
            TestContext.Current.CancellationToken, clearedTypes: new HashSet<string>(["ReteICA"]));

        Assert.Equal(new[] { RetefuenteServicios }, rates);
    }

    // A18 (owner 2026-11-14, plan P14): sin cambio de empresa ni de municipio, el id de la fila municipal guardada se acepta
    // aunque la empresa ya no lo ofrezca —la reemplazó, la desactivaron o ya no está en el catálogo— y vuelve la foto
    // congelada tal cual, no la del catálogo de hoy.
    [Theory]
    [InlineData("replaced")]
    [InlineData("inactive")]
    [InlineData("missing")]
    public async Task AStoredMunicipalIdOutsideTheOffersIsKeptAsIs(string catalogState)
    {
        var stored = Rate("ReteICA Bogotá vieja", "ReteICA", 1.1m) with
        {
            CityId = Guid.Parse("0193b6a2-0000-7000-8000-000000001310"),
        };
        var today = stored with { Percentage = 1.3m, CatalogPercentage = 1.3m };
        var lookup = catalogState switch
        {
            "replaced" => Catalog().WithRate(today),
            "inactive" => Catalog().WithRate(today, isActive: false),
            _ => Catalog(),
        };
        var scope = Offered(ReteIca, ignoreUnmatched: false) with { Stored = [stored, RetefuenteServicios] };

        var rates = await WithholdingSelection.ResolveAsync(
            lookup, TenantId, [RetefuenteCompras.TaxRateId, stored.TaxRateId], "CO", DocumentDate, scope, "quotation",
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { RetefuenteCompras, stored }, rates);
    }

    // Aunque la empresa la siga ofreciendo, el id guardado no relee el catálogo de hoy: el 0,8 % editado sobrevive.
    [Theory]
    [InlineData("quotation")]
    [InlineData("order")]
    public async Task AStoredMunicipalIdKeepsItsEditedPercentage(string prefix)
    {
        var edited = ReteIca with { Percentage = 0.8m };
        var scope = Offered(ReteIca, ignoreUnmatched: false) with { Stored = [edited] };

        var rates = await ResolveAsync([edited.TaxRateId, RetefuenteServicios.TaxRateId], "CO", prefix, scope);

        Assert.Equal(new[] { edited, RetefuenteServicios }, rates);
        Assert.Equal((0.8m, 0.966m, true), (rates[0].Percentage, rates[0].CatalogPercentage, rates[0].Overridden));
    }

    // Un id municipal distinto del guardado se valida contra la oferta de la empresa, como siempre (A4).
    [Theory]
    [InlineData("quotation")]
    [InlineData("order")]
    public async Task ADifferentMunicipalIdStillNeedsTheOffer(string prefix)
    {
        var other = Rate("ReteICA Bogotá nueva", "ReteICA", 1.2m);
        var lookup = Catalog().WithRate(other);
        var scope = MunicipalSelectorScope.None with { Stored = [ReteIca] };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => WithholdingSelection.ResolveAsync(
            lookup, TenantId, [other.TaxRateId], "CO", DocumentDate, scope, prefix, TestContext.Current.CancellationToken));

        Assert.Equal($"{prefix}.billing.withholding_tax_rate_invalid", error.Code);
    }

    // La fila guardada de un país distinto al de la empresa no se tolera: sigue el 422 de siempre.
    [Fact]
    public async Task AStoredMunicipalIdOfAnotherCountryIsInvalid()
    {
        var scope = MunicipalSelectorScope.None with { Stored = [ReteIca] };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => ResolveAsync(
            [ReteIca.TaxRateId], "EC", "order", scope));

        Assert.Equal("order.billing.withholding_tax_rate_invalid", error.Code);
    }

    // Con cambio de empresa o de municipio rige A11: el id guardado se eligió contra lo anterior y se ignora; queda la fila
    // de la re-resolución.
    [Fact]
    public async Task WithAContextChangeTheStoredIdFollowsA11()
    {
        var edited = ReteIca with { Percentage = 0.8m };
        var reResolved = ReteIca with { TaxRateId = Guid.CreateVersion7(), Name = "ReteICA Medellín", Percentage = 1m };
        var scope = new MunicipalSelectorScope(new HashSet<Guid>(), IgnoreUnmatched: true)
        {
            ReResolved = [reResolved],
            Stored = [edited],
        };

        var rates = await ResolveAsync([edited.TaxRateId, RetefuenteServicios.TaxRateId], "CO", "quotation", scope);

        Assert.Equal(new[] { RetefuenteServicios, reResolved }, rates);
    }

    private static MunicipalSelectorScope Offered(WithholdingRate rate, bool ignoreUnmatched) =>
        new(new HashSet<Guid>([rate.TaxRateId]), ignoreUnmatched);

    private static Task<IReadOnlyList<WithholdingRate>> ResolveAsync(
        IReadOnlyCollection<Guid> ids, string? companyCountry, string prefix, MunicipalSelectorScope? scope = null) =>
        WithholdingSelection.ResolveAsync(
            Catalog(), TenantId, ids, companyCountry, DocumentDate, scope ?? MunicipalSelectorScope.None, prefix,
            TestContext.Current.CancellationToken);

    private static WithholdingRate Rate(string name, string type, decimal percentage, string country = "CO") =>
        new(Guid.CreateVersion7(), name, type, country, percentage, null, null, null);
}
