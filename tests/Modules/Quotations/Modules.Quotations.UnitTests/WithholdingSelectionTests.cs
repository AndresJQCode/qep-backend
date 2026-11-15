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
