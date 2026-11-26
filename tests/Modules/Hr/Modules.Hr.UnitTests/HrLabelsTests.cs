using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

/// <summary>Las etiquetas de la tabla «Enums y etiquetas» del contrato: las que muestra la UI y acepta la importación.</summary>
public sealed class HrLabelsTests
{
    [Fact]
    public void EveryEnumMemberHasItsLabel()
    {
        Assert.Equal(Enum.GetValues<HrDocumentType>().Length, HrLabels.DocumentTypes.Count);
        Assert.Equal(Enum.GetValues<ContributorSubtype>().Length, HrLabels.ContributorSubtypes.Count);
        Assert.Equal(Enum.GetValues<PaymentMethod>().Length, HrLabels.PaymentMethods.Count);
        Assert.Equal(Enum.GetValues<BankAccountType>().Length, HrLabels.BankAccountTypes.Count);
        Assert.Equal(7, HrLabels.RestDays.Count);
        Assert.Equal(Enum.GetValues<ContractType>().Length, HrLabels.ContractTypes.Count);
        Assert.Equal(Enum.GetValues<ContractDuration>().Length, HrLabels.ContractDurations.Count);
        Assert.Equal(Enum.GetValues<SalaryType>().Length, HrLabels.SalaryTypes.Count);
        Assert.Equal(Enum.GetValues<ContractStatus>().Length, HrLabels.ContractStatuses.Count);
        Assert.Equal(Enum.GetValues<TerminationReason>().Length, HrLabels.TerminationReasons.Count);
        Assert.Equal(Enum.GetValues<EmployeeStatus>().Length, HrLabels.EmployeeStatuses.Count);
        Assert.Equal(Enum.GetValues<ArlRiskClass>().Length, HrLabels.ArlRiskClasses.Count);
        Assert.Equal(Enum.GetValues<HrActivityAction>().Length, HrLabels.ActivityActions.Count);
    }

    [Fact]
    public void LabelsAreTheContractsLetterByLetter()
    {
        Assert.Equal("Cédula de ciudadanía", HrLabels.DocumentTypes[HrDocumentType.CC]);
        Assert.Equal("Permiso por protección temporal", HrLabels.DocumentTypes[HrDocumentType.PPT]);
        Assert.Equal("Cumplió requisitos de pensión", HrLabels.ContributorSubtypes[ContributorSubtype.PensionRequirementsMet]);
        Assert.Equal("Aprendiz SENA lectiva", HrLabels.ContractTypes[ContractType.SenaApprenticeTeaching]);
        Assert.Equal("Término fijo", HrLabels.ContractDurations[ContractDuration.FixedTerm]);
        Assert.Equal("Salario integral", HrLabels.SalaryTypes[SalaryType.Integral]);
        Assert.Equal("Terminación en período de prueba", HrLabels.TerminationReasons[TerminationReason.ProbationPeriod]);
        Assert.Equal("Miércoles", HrLabels.RestDays[DayOfWeek.Wednesday]);
        Assert.Equal("Sí", HrLabels.Yes);
        Assert.Equal("No", HrLabels.No);
    }

    [Fact]
    public void TryParseIgnoresAccentsCaseAndSpacesAndOnlyTakesLabels()
    {
        Assert.True(HrLabels.TryParse<ContractDuration>("  termino   FIJO ", out var duration));
        Assert.Equal(ContractDuration.FixedTerm, duration);
        Assert.True(HrLabels.TryParse<HrDocumentType>("cedula de extranjeria", out var type));
        Assert.Equal(HrDocumentType.CE, type);
        Assert.False(HrLabels.TryParse<HrDocumentType>("ppt", out _));
        Assert.False(HrLabels.TryParse<ContractDuration>("FixedTerm", out _));
        Assert.False(HrLabels.TryParse<ContractDuration>("eterno", out _));
        Assert.False(HrLabels.TryParse<ContractDuration>("   ", out _));
        Assert.False(HrLabels.TryParse<ContractDuration>(null, out _));
    }
}
