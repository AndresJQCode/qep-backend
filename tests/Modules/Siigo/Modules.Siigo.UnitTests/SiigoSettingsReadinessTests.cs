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
    public void WithoutDocumentTypeOrSellerTheDocumentStepNamesWhatIsMissing()
    {
        var settings = NewSettings();

        Assert.Equal(["document_type_missing", "seller_missing"], Step(settings, SiigoSetupStep.Document).MissingCodes);

        settings.UpdateDocument(new SiigoDocumentSettings("doc-1", null, null, null, false, false, false, null));
        Assert.Equal(["seller_missing"], Step(settings, SiigoSetupStep.Document).MissingCodes);
    }

    [Fact]
    public void WithEverythingTheSixStepsAreComplete()
    {
        var settings = NewSettings();
        settings.AssignCompany(Guid.CreateVersion7());
        settings.UpdateDocument(new SiigoDocumentSettings("doc-1", "seller-1", null, null, false, false, false, null));

        var steps = SiigoSettingsReadiness.Evaluate(settings);

        Assert.Equal(
            [SiigoSetupStep.Credentials, SiigoSetupStep.Company, SiigoSetupStep.Document, SiigoSetupStep.Taxes, SiigoSetupStep.Payments, SiigoSetupStep.Products],
            steps.Select(step => step.Step));
        Assert.All(steps, step => Assert.True(step.Complete));
        Assert.All(steps, step => Assert.Empty(step.MissingCodes));
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
