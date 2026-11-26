using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.1 y §7 H5, plan D3–D4: los cinco pasos del asistente con su estado, lo que falta y de qué
/// paso esperan. La pantalla no deduce nada: todo sale de acá. Precedencia: <c>Done</c> &gt; <c>Blocked</c> &gt; <c>Pending</c>.
/// </summary>
public sealed class ShopifySetupStepsTests
{
    private static readonly ShopifySetupFacts Fresh = new(
        HasCompany: false,
        CompanyActive: false,
        CompanyHasTaxId: false,
        HasAdvisor: false,
        AdvisorActive: false,
        HasClassification: false,
        IsConfigured: false,
        ScopeGranted: false,
        LastImportStatus: null,
        CoverageAccepted: false,
        CoverageComplete: false,
        HistoryRan: false);

    private static readonly ShopifySetupFacts WithCompany = Fresh with { HasCompany = true, CompanyActive = true, CompanyHasTaxId = true };

    private static readonly ShopifySetupFacts Configured = WithCompany with
    {
        HasAdvisor = true,
        AdvisorActive = true,
        HasClassification = true,
        IsConfigured = true,
    };

    private static ShopifySetupStep Step(ShopifySetupFacts facts, string key) =>
        Assert.Single(ShopifySetupSteps.Evaluate(facts), step => step.Key == key);

    [Fact]
    public void TheStepsAreTheFiveInOrder()
    {
        Assert.Equal(["company", "advisor", "buyers", "products", "history"], ShopifySetupStepKeys.All);
        Assert.Equal(ShopifySetupStepKeys.All, ShopifySetupSteps.Evaluate(Fresh).Select(step => step.Key));
    }

    [Fact]
    public void AFreshStoreHasEveryStepPendingOrBlocked()
    {
        var steps = ShopifySetupSteps.Evaluate(Fresh);

        Assert.Equal(
            [
                ("company", ShopifySetupStepStatus.Pending, "", "company_missing"),
                ("advisor", ShopifySetupStepStatus.Blocked, "company", "advisor_missing"),
                ("buyers", ShopifySetupStepStatus.Blocked, "company,advisor", "classification_missing,document_source_missing"),
                ("products", ShopifySetupStepStatus.Blocked, "company", "scope_missing"),
                ("history", ShopifySetupStepStatus.Blocked, "company,advisor", "history_not_run"),
            ],
            steps.Select(step => (step.Key, step.Status, string.Join(',', step.BlockedBy), string.Join(',', step.Missing))));
        Assert.Equal("company", ShopifySetupSteps.OpenStep(steps));
    }

    [Fact]
    public void ACompanyWithoutTaxIdIsPendingTaxIdMissing()
    {
        var company = Step(Fresh with { HasCompany = true, CompanyActive = true }, ShopifySetupStepKeys.Company);

        Assert.Equal(ShopifySetupStepStatus.Pending, company.Status);
        Assert.Equal([ShopifySetupMissing.TaxIdMissing], company.Missing);
        Assert.Empty(company.BlockedBy);
    }

    [Fact]
    public void AnInactiveCompanyIsCompanyMissing()
    {
        var facts = Fresh with { HasCompany = true, CompanyActive = false, CompanyHasTaxId = true };

        Assert.Equal([ShopifySetupMissing.CompanyMissing], Step(facts, ShopifySetupStepKeys.Company).Missing);
        // La tienda sí tiene empresa (inactiva): los demás pasos no esperan por ella, el PUT ya no la exige.
        Assert.Empty(Step(facts, ShopifySetupStepKeys.Advisor).BlockedBy);
    }

    [Fact]
    public void AnInactiveAdvisorIsAdvisorInactive()
    {
        var advisor = Step(WithCompany with { HasAdvisor = true, AdvisorActive = false }, ShopifySetupStepKeys.Advisor);

        Assert.Equal(ShopifySetupStepStatus.Pending, advisor.Status);
        Assert.Equal([ShopifySetupMissing.AdvisorInactive], advisor.Missing);
    }

    [Fact]
    public void BuyersWaitForTheAdvisor()
    {
        var buyers = Step(WithCompany, ShopifySetupStepKeys.Buyers);

        Assert.Equal(ShopifySetupStepStatus.Blocked, buyers.Status);
        Assert.Equal([ShopifySetupStepKeys.Advisor], buyers.BlockedBy);
    }

    [Theory]
    [InlineData(false, false, new[] { "classification_missing", "document_source_missing" })]
    [InlineData(true, false, new[] { "document_source_missing" })]
    [InlineData(false, true, new[] { "classification_missing" })]
    public void BuyersMissingInOrder(bool classification, bool configured, string[] expected)
    {
        var facts = WithCompany with { HasAdvisor = true, AdvisorActive = true, HasClassification = classification, IsConfigured = configured };

        var buyers = Step(facts, ShopifySetupStepKeys.Buyers);

        Assert.Equal(ShopifySetupStepStatus.Pending, buyers.Status);
        Assert.Equal(expected, buyers.Missing);
    }

    [Fact]
    public void BuyersAreDoneWithClassificationAndAConfiguredStore()
    {
        var facts = WithCompany with { HasAdvisor = true, AdvisorActive = true, HasClassification = true, IsConfigured = true };

        Assert.Equal(ShopifySetupStepStatus.Done, Step(facts, ShopifySetupStepKeys.Buyers).Status);
    }

    [Theory]
    [InlineData(false, null, "scope_missing")]
    [InlineData(false, ShopifyProductImportStatus.Failed, "scope_missing")]
    [InlineData(true, ShopifyProductImportStatus.Failed, "import_failed")]
    [InlineData(true, null, "import_not_done")]
    [InlineData(true, ShopifyProductImportStatus.Running, "import_not_done")]
    [InlineData(true, ShopifyProductImportStatus.Pending, "import_not_done")]
    public void ProductsMissingByPrecedence(bool scope, ShopifyProductImportStatus? last, string expected)
    {
        var products = Step(WithCompany with { ScopeGranted = scope, LastImportStatus = last }, ShopifySetupStepKeys.Products);

        Assert.Equal(ShopifySetupStepStatus.Pending, products.Status);
        Assert.Equal([expected], products.Missing);
    }

    [Theory]
    [InlineData(ShopifyProductImportStatus.Done, false, false)]
    [InlineData(null, true, false)]
    [InlineData(ShopifyProductImportStatus.Failed, false, true)]
    public void ProductsDoneByAnyOfThree(ShopifyProductImportStatus? last, bool accepted, bool complete)
    {
        var facts = WithCompany with { LastImportStatus = last, CoverageAccepted = accepted, CoverageComplete = complete };

        var products = Step(facts, ShopifySetupStepKeys.Products);

        Assert.Equal(ShopifySetupStepStatus.Done, products.Status);
        Assert.Empty(products.Missing);
    }

    [Theory]
    [InlineData(ShopifyProductImportStatus.Pending)]
    [InlineData(ShopifyProductImportStatus.Running)]
    public void HistoryIsBlockedWhileAnImportIsLive(ShopifyProductImportStatus live)
    {
        var history = Step(Configured with { ScopeGranted = true, LastImportStatus = live }, ShopifySetupStepKeys.History);

        Assert.Equal(ShopifySetupStepStatus.Blocked, history.Status);
        Assert.Equal([ShopifySetupStepKeys.Products], history.BlockedBy);
        Assert.Equal([ShopifySetupMissing.HistoryNotRun], history.Missing);
    }

    // Revisión: sin la tienda configurada la sincronización no importa nada (sin empresa ni asesor no hay pedido), así que el
    // historial espera de los pasos que falten, además de la importación viva.
    [Fact]
    public void HistoryWaitsForTheCompanyAndAdvisorUntilTheStoreIsConfigured()
    {
        Assert.Equal(
            [ShopifySetupStepKeys.Company, ShopifySetupStepKeys.Advisor],
            Step(Fresh, ShopifySetupStepKeys.History).BlockedBy);
        Assert.Equal([ShopifySetupStepKeys.Advisor], Step(WithCompany, ShopifySetupStepKeys.History).BlockedBy);
        Assert.Equal(
            [ShopifySetupStepKeys.Advisor, ShopifySetupStepKeys.Products],
            Step(WithCompany with { LastImportStatus = ShopifyProductImportStatus.Running }, ShopifySetupStepKeys.History).BlockedBy);

        var configured = Step(Configured, ShopifySetupStepKeys.History);
        Assert.Equal(ShopifySetupStepStatus.Pending, configured.Status);
        Assert.Empty(configured.BlockedBy);
    }

    [Fact]
    public void ADoneStepNeverCarriesMissingOrBlockedBy()
    {
        // Hecho aunque la importación siga viva y la tienda no tenga empresa: Done gana sobre Blocked.
        var facts = Fresh with { HistoryRan = true, LastImportStatus = ShopifyProductImportStatus.Running, CoverageAccepted = true };

        var steps = ShopifySetupSteps.Evaluate(facts);

        Assert.All(
            steps.Where(step => step.Status == ShopifySetupStepStatus.Done),
            step => Assert.True(step.Missing.Count == 0 && step.BlockedBy.Count == 0));
        Assert.Equal(ShopifySetupStepStatus.Done, Step(facts, ShopifySetupStepKeys.History).Status);
        Assert.Equal(ShopifySetupStepStatus.Done, Step(facts, ShopifySetupStepKeys.Products).Status);
    }

    [Fact]
    public void TheOpenStepIsTheFirstNotDone()
    {
        Assert.Equal("advisor", ShopifySetupSteps.OpenStep(ShopifySetupSteps.Evaluate(WithCompany)));

        var everything = WithCompany with
        {
            HasAdvisor = true,
            AdvisorActive = true,
            HasClassification = true,
            IsConfigured = true,
            ScopeGranted = true,
            LastImportStatus = ShopifyProductImportStatus.Done,
            HistoryRan = true,
        };
        Assert.Null(ShopifySetupSteps.OpenStep(ShopifySetupSteps.Evaluate(everything)));
    }
}
