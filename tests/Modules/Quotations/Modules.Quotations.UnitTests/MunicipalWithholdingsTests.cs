using Modules.Quotations.Domain;
using static Modules.Quotations.UnitTests.TestWithholdings;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La resolución de un tipo municipal (spec ReteICA por empresa §7.5) y el porqué que se explica al leer (§9.4): la
/// función de dominio que comparten la cotización y el pedido, y el municipio en las candidatas y los selectores.
/// </summary>
public sealed class MunicipalWithholdingsTests
{
    private static readonly Guid Bogota = Guid.Parse("01900000-0000-7000-8000-0000000b0901");
    private static readonly Guid Medellin = Guid.Parse("01900000-0000-7000-8000-0000000e0d01");

    private static readonly WithholdingRate ReteIca = Rate("ReteICA Bogotá", "ReteICA", 0.966m, Bogota);
    private static readonly WithholdingRate Retefuente = RetefuenteCo;

    private static readonly QuotationBillingAccount ColombianAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "CO",
    };

    [Fact]
    public void CustomerRuleNeedsTheCompanyRateAndAnAgentCustomerInACity()
    {
        Assert.Equal(ReteIca, MunicipalWithholdings.ByCustomerRule(new("ReteICA", ReteIca, CustomerIsAgent: true), Bogota));
        Assert.Null(MunicipalWithholdings.ByCustomerRule(new("ReteICA", ReteIca, CustomerIsAgent: false), Bogota));
        Assert.Null(MunicipalWithholdings.ByCustomerRule(new("ReteICA", null, true), Bogota));
        Assert.Null(MunicipalWithholdings.ByCustomerRule(new("ReteICA", ReteIca, true), null));
    }

    [Fact]
    public void SelectorRuleKeepsASelectionEvenIfTheCustomerIsNotAnAgent()
    {
        Assert.Null(MunicipalWithholdings.BySelectorRule(new("ReteICA", ReteIca, false), Bogota, hadSelection: false));
        Assert.Equal(ReteIca, MunicipalWithholdings.BySelectorRule(new("ReteICA", ReteIca, false), Bogota, hadSelection: true));
        Assert.Null(MunicipalWithholdings.BySelectorRule(new("ReteICA", null, false), Bogota, hadSelection: true));
        Assert.Null(MunicipalWithholdings.BySelectorRule(new("ReteICA", ReteIca, true), null, hadSelection: true));
    }

    [Fact]
    public void DescribeFollowsThePrecedenceOfTheSpec()
    {
        Assert.Equal(MunicipalWithholdingStatus.Resolved, MunicipalWithholdings.Describe(true, null, false, false, false));
        Assert.Equal(MunicipalWithholdingStatus.NoOperationCity, MunicipalWithholdings.Describe(false, null, true, null, false));
        Assert.Equal(MunicipalWithholdingStatus.NoCompanyRate, MunicipalWithholdings.Describe(false, Bogota, false, true, false));
        Assert.Equal(MunicipalWithholdingStatus.CustomerNotAgent, MunicipalWithholdings.Describe(false, Bogota, true, false, false));
        Assert.Equal(MunicipalWithholdingStatus.NotSelected, MunicipalWithholdings.Describe(false, Bogota, true, true, false));
        Assert.Equal(MunicipalWithholdingStatus.NotSelected, MunicipalWithholdings.Describe(false, Bogota, true, null, ownBilling: true));
        // Con facturación propia el tercero no tiene lista de agentes: aunque el cliente no lo sea, es «sin elegir».
        Assert.Equal(MunicipalWithholdingStatus.NotSelected, MunicipalWithholdings.Describe(false, Bogota, true, false, ownBilling: true));
    }

    [Fact]
    public void OverriddenIsFalseWithEqualPercentages()
    {
        Assert.False(ReteIca.Overridden);
        Assert.Equal(0.966m, ReteIca.CatalogPercentage);

        var edited = ReteIca with { Percentage = 0.8m };

        Assert.True(edited.Overridden);
        Assert.Equal(0.966m, edited.CatalogPercentage);
        Assert.Equal(0.8m, edited.Percentage);
    }

    [Fact]
    public void ContextNoneHasNoCityAndNoOffers()
    {
        Assert.Null(MunicipalContext.None.OperationCityId);
        Assert.Empty(MunicipalContext.None.Offers);
    }

    [Fact]
    public void CandidatesDropAMunicipalRateOfAnotherCity()
    {
        var candidates = WithholdingCandidates.For([ReteIca, Retefuente], false, ColombianAccount, "COP", Medellin);

        Assert.Equal([Retefuente], candidates);
    }

    [Fact]
    public void CandidatesKeepTheMunicipalRateOfTheOperationCity()
    {
        var candidates = WithholdingCandidates.For([ReteIca, Retefuente], false, ColombianAccount, "COP", Bogota);

        Assert.Equal([ReteIca, Retefuente], candidates);
    }

    [Fact]
    public void CandidatesKeepNationalAsToday()
    {
        var candidates = WithholdingCandidates.For([Retefuente], false, ColombianAccount, "COP", operationCityId: null);

        Assert.Equal([Retefuente], candidates);
    }

    [Fact]
    public void SelectorRulesRejectAMunicipalRateOfAnotherCity()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            WithholdingSelectorRules.Ensure([ReteIca, Retefuente], "CO", Medellin, "quotation.billing"));

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", error.Code);
    }

    [Fact]
    public void SelectorRulesAcceptAMunicipalRateOfTheOperationCity()
    {
        WithholdingSelectorRules.Ensure([ReteIca, Retefuente], "CO", Bogota, "order.billing");
    }
}
