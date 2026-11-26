using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// «Sin retención» explícito (owner 2026-11-13, plan P12): la marca sólo lleva tipos municipales del país de la empresa, y
/// no puede llegar en el mismo cuerpo con un id municipal ni con un porcentaje no nulo del mismo tipo. Se valida siempre,
/// también con cambio de empresa o de municipio.
/// </summary>
public sealed class MunicipalSelectionMarkerTests
{
    [Theory]
    [InlineData("quotation")]
    [InlineData("order")]
    public void RetefuenteIsWithholdingTypeInvalid(string prefix)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            MunicipalSelectionMarker.Validate(["Retefuente"], "CO", [], null, prefix));

        Assert.Equal($"{prefix}.billing.withholding_type_invalid", error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ES")]
    public void TypeWithoutCompanyCountryIsWithholdingTypeInvalid(string? country)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            MunicipalSelectionMarker.Validate(["ReteICA"], country, [], null, "quotation"));

        Assert.Equal("quotation.billing.withholding_type_invalid", error.Code);
    }

    [Fact]
    public void ClearedAndSelectedIsSelectionConflict()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            MunicipalSelectionMarker.Validate(["ReteICA"], "CO", ["ReteICA"], null, "order"));

        Assert.Equal("order.billing.withholding_selection_conflict", error.Code);
    }

    [Fact]
    public void ClearedWithNonNullPercentageIsSelectionConflict()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            MunicipalSelectionMarker.Validate(
                ["ReteICA"], "CO", [], [new WithholdingPercentageRequest("ReteICA", 0.7m)], "quotation"));

        Assert.Equal("quotation.billing.withholding_selection_conflict", error.Code);
    }

    [Fact]
    public void ClearedWithNullPercentagePasses() =>
        MunicipalSelectionMarker.Validate(
            ["ReteICA"], " co ", [], [new WithholdingPercentageRequest("ReteICA", null)], "quotation");

    [Fact]
    public void EmptyOrNullListPasses()
    {
        // Sin marca no hay nada que validar, ni siquiera el país: un cuerpo sin empresa sigue pasando.
        MunicipalSelectionMarker.Validate(null, null, ["ReteICA"], [new WithholdingPercentageRequest("ReteICA", 0.7m)], "quotation");
        MunicipalSelectionMarker.Validate([], null, ["ReteICA"], null, "order");
    }
}
