using FluentValidation;
using Modules.Payroll.Application;

namespace Modules.Payroll.UnitTests;

/// <summary>La forma de los cuerpos de configuración y conceptos. Las claves de <c>errors</c> son las del contrato HTTP.</summary>
public sealed class PayrollRequestValidatorsTests
{
    private static string[] Failed<T>(PayrollValidator<T> validator, T request)
    {
        try
        {
            validator.ValidateAndThrow(request);
            return [];
        }
        catch (ValidationException exception)
        {
            return exception.Errors.Select(failure => failure.PropertyName).Distinct().Order().ToArray();
        }
    }

    private static string[] FailedConcept(ConceptRequest request) => Failed(new ConceptRequestValidator(), request);

    [Fact]
    public void AValidConceptOfEachKindPasses()
    {
        Assert.Empty(FailedConcept(new ConceptRequest("Overtime", "Hora extra especial", null, 1.4m)));
        Assert.Empty(FailedConcept(new ConceptRequest("Income", "Auxilio", true, null)));
        Assert.Empty(FailedConcept(new ConceptRequest("Income", "Auxilio sin sugerencia", null, null)));
        Assert.Empty(FailedConcept(new ConceptRequest("Deduction", "Libranza", null, null)));
    }

    [Fact]
    public void AnOvertimeConceptNeedsAMultiplier()
    {
        Assert.Equal(["multiplier"], FailedConcept(new ConceptRequest("Overtime", "Hora extra", null, null)));
        Assert.Equal(["multiplier"], FailedConcept(new ConceptRequest("Overtime", "Hora extra", null, 0m)));
        Assert.Equal(["multiplier"], FailedConcept(new ConceptRequest("Overtime", "Hora extra", null, 10.5m)));
        Assert.Equal(["multiplier"], FailedConcept(new ConceptRequest("Overtime", "Hora extra", null, 1.23456m)));
    }

    [Fact]
    public void AMultiplierOnlyForOvertime()
    {
        Assert.Equal(["multiplier"], FailedConcept(new ConceptRequest("Income", "Bono", true, 1.5m)));
        Assert.Equal(["multiplier"], FailedConcept(new ConceptRequest("Deduction", "Libranza", null, 1.5m)));
    }

    [Fact]
    public void ADefaultClassificationOnlyForIncome()
    {
        Assert.Equal(["defaultSalaryConstitutive"], FailedConcept(new ConceptRequest("Deduction", "Libranza", true, null)));
        Assert.Equal(["defaultSalaryConstitutive"], FailedConcept(new ConceptRequest("Overtime", "Hora", false, 1.5m)));
    }

    [Fact]
    public void ANameIsOneToEightyCharacters()
    {
        Assert.Equal(["name"], FailedConcept(new ConceptRequest("Income", "   ", null, null)));
        Assert.Equal(["name"], FailedConcept(new ConceptRequest("Income", null, null, null)));
        Assert.Equal(["name"], FailedConcept(new ConceptRequest("Income", new string('a', 81), null, null)));
        Assert.Empty(FailedConcept(new ConceptRequest("Income", new string('a', 80), null, null)));
    }

    [Fact]
    public void TheKindMustBeAKnownName()
    {
        Assert.Equal(["kind"], FailedConcept(new ConceptRequest("Ingreso", "Bono", null, null)));
        Assert.Equal(["kind"], FailedConcept(new ConceptRequest(null, "Bono", null, null)));
    }

    [Fact]
    public void ASettingsBodyNeedsEveryFieldWithKnownNames()
    {
        var validator = new PayrollSettingsRequestValidator();

        Assert.Empty(Failed(validator, new PayrollSettingsRequest("Biweekly", "SalaryOnly", "SalaryOnly", true, false)));
        Assert.Equal(
            ["dualControl", "exonerated", "overtimeBase", "transportCapBase", "transportFrequency"],
            Failed(validator, new PayrollSettingsRequest("Weekly", "Other", "3", null, null)));
    }
}
