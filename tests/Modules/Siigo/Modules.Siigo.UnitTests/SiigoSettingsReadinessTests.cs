using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §5.3: el <c>readiness</c> por paso que usa el asistente para saber dónde seguir.</summary>
public sealed class SiigoSettingsReadinessTests
{
    private static SiigoSettings NewSettings() => SiigoSettings.Create(Guid.CreateVersion7(), Guid.CreateVersion7());

    private static SiigoStepReadiness Step(SiigoSettings settings, SiigoSetupStep step) =>
        SiigoSettingsReadiness.Evaluate(settings).Single(value => value.Step == step);

    [Fact]
    public void WithoutACompanyTheCompanyStepIsIncomplete()
    {
        var step = Step(NewSettings(), SiigoSetupStep.Company);

        Assert.False(step.Complete);
        Assert.Equal(["company_missing"], step.MissingCodes);
    }

    [Fact]
    public void WithoutDocumentTypeTheDocumentStepNamesWhatIsMissing()
    {
        var settings = NewSettings();

        Assert.Equal(["document_type_missing"], Step(settings, SiigoSetupStep.Document).MissingCodes);

        // El vendedor ya no se configura: es el asesor de cada pedido (spec 2026-11-20).
        settings.UpdateDocument(new SiigoDocumentSettings("doc-1", null, null, false, false, false, null));
        Assert.True(Step(settings, SiigoSetupStep.Document).Complete);
    }

    [Fact]
    public void WithEverythingTheSevenStepsAreComplete()
    {
        var settings = NewSettings();
        settings.AssignCompany(Guid.CreateVersion7());
        settings.UpdateDocument(new SiigoDocumentSettings("doc-1", null, null, false, false, false, null));
        settings.ReplaceTaxLinks([new SiigoTaxLink(19, "13156")]);
        settings.UpdatePayments(new SiigoPaymentSettings([new SiigoPaymentLink("BANCO|1", "pay-1")], null, null, null, null));
        settings.ReplaceSellerLinks([new SiigoSellerLink(Guid.CreateVersion7(), "800", "VANESA HERNANDEZ")]);

        var steps = SiigoSettingsReadiness.Evaluate(settings);

        Assert.Equal(
            [
                SiigoSetupStep.Credentials, SiigoSetupStep.Company, SiigoSetupStep.Document, SiigoSetupStep.Taxes, SiigoSetupStep.Payments,
                SiigoSetupStep.Sellers, SiigoSetupStep.Products,
            ],
            steps.Select(step => step.Step));
        Assert.All(steps, step => Assert.True(step.Complete));
        Assert.All(steps, step => Assert.Empty(step.MissingCodes));
    }

    [Fact]
    public void WithoutSellerLinksTheSellersStepNamesWhatIsMissing()
    {
        var settings = NewSettings();
        Assert.Equal(["seller_link_missing"], Step(settings, SiigoSetupStep.Sellers).MissingCodes);

        settings.ReplaceSellerLinks([new SiigoSellerLink(Guid.CreateVersion7(), "800", "VANESA HERNANDEZ")]);
        Assert.True(Step(settings, SiigoSetupStep.Sellers).Complete);
    }

    [Fact]
    public void WithoutAnyTaxLinkTheTaxesStepIsIncomplete()
    {
        var step = Step(NewSettings(), SiigoSetupStep.Taxes);

        Assert.False(step.Complete);
        Assert.Equal(["tax_link_missing"], step.MissingCodes);
    }

    [Fact]
    public void ALinkedVatRateCompletesTheTaxesStep()
    {
        var settings = NewSettings();
        settings.ReplaceTaxLinks([new SiigoTaxLink(0, null), new SiigoTaxLink(19, "13156")]);

        Assert.True(Step(settings, SiigoSetupStep.Taxes).Complete);
    }

    [Fact]
    public void ALinkedWithholdingAloneCompletesTheTaxesStep()
    {
        var settings = NewSettings();
        settings.ReplaceWithholdingLinks([new SiigoWithholdingLink(Guid.CreateVersion7(), "13160")]);

        Assert.True(Step(settings, SiigoSetupStep.Taxes).Complete);
    }

    [Fact]
    public void LinksWithoutASiigoTaxDoNotCompleteTheTaxesStep()
    {
        var settings = NewSettings();
        settings.ReplaceTaxLinks([new SiigoTaxLink(19, null)]);
        settings.ReplaceWithholdingLinks([new SiigoWithholdingLink(Guid.CreateVersion7(), " ")]);

        Assert.False(Step(settings, SiigoSetupStep.Taxes).Complete);
    }

    [Fact]
    public void WithoutAnyPaymentLinkThePaymentsStepIsIncomplete()
    {
        var step = Step(NewSettings(), SiigoSetupStep.Payments);

        Assert.False(step.Complete);
        Assert.Equal(["payment_link_missing"], step.MissingCodes);
    }

    [Fact]
    public void AnAccountPaymentTypeCompletesThePaymentsStepButCreditOrDefaultDoNot()
    {
        var withDefault = NewSettings();
        withDefault.UpdatePayments(new SiigoPaymentSettings([], "pay-3", 30, "pay-4", "pay-1"));
        var withAccount = NewSettings();
        withAccount.UpdatePayments(new SiigoPaymentSettings([new SiigoPaymentLink("BANCO|1", "pay-2")], null, null, null, null));

        Assert.False(Step(withDefault, SiigoSetupStep.Payments).Complete);
        Assert.True(Step(withAccount, SiigoSetupStep.Payments).Complete);
    }

    // La cobertura se informa, no bloquea: un tenant sin ningún vínculo ya puede terminar el asistente.
    [Fact]
    public void TheProductsStepIsCompleteWithZeroCoverage() =>
        Assert.True(Step(NewSettings(), SiigoSetupStep.Products).Complete);

    // Las credenciales las decide la conexión de Integrations (Tarea 6), no esta fila.
    [Fact]
    public void TheCredentialsStepIsAlwaysCompleteHere() =>
        Assert.True(Step(NewSettings(), SiigoSetupStep.Credentials).Complete);
}
