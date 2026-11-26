using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

/// <summary>Las etiquetas son las de la tabla «Enums y etiquetas» del contrato HTTP, copiadas literal: el frontend las copia de ahí.</summary>
public sealed class PayrollLabelsTests
{
    public static TheoryData<string> EnumNames { get; } =
    [
        nameof(PayrollPeriodStatus), nameof(PayrollHalf), nameof(TransportFrequency), nameof(TransportCapBase), nameof(OvertimeBase),
        nameof(ConceptKind), nameof(RecurringMode), nameof(RecurringStatus), nameof(PayrollLineGroup), nameof(ExclusionReason),
        nameof(BlockerReason), nameof(PeriodAction),
    ];

    [Theory]
    [MemberData(nameof(EnumNames))]
    public void EveryEnumMemberHasItsContractLabel(string name)
    {
        switch (name)
        {
            case nameof(PayrollPeriodStatus):
                Check(
                    PayrollLabels.PeriodStatuses,
                    (PayrollPeriodStatus.Draft, "Borrador"), (PayrollPeriodStatus.Liquidated, "Liquidado"),
                    (PayrollPeriodStatus.Approved, "Aprobado"), (PayrollPeriodStatus.Closed, "Cerrado"),
                    (PayrollPeriodStatus.Reopened, "Reabierto"));
                break;
            case nameof(PayrollHalf):
                Check(PayrollLabels.Halves, (PayrollHalf.First, "Primera quincena"), (PayrollHalf.Second, "Segunda quincena"));
                break;
            case nameof(TransportFrequency):
                Check(
                    PayrollLabels.TransportFrequencies,
                    (TransportFrequency.Biweekly, "Quincenal"), (TransportFrequency.Monthly, "Mensual"));
                break;
            case nameof(TransportCapBase):
                Check(
                    PayrollLabels.TransportCapBases,
                    (TransportCapBase.SalaryOnly, "Sólo salario"),
                    (TransportCapBase.SalaryAndSalaryIncome, "Salario más ingresos salariales sin extras"),
                    (TransportCapBase.SalaryAndPaidAbsences, "Salario más ausencias pagas sin comisiones"));
                break;
            case nameof(OvertimeBase):
                Check(
                    PayrollLabels.OvertimeBases,
                    (OvertimeBase.SalaryOnly, "Sólo salario"),
                    (OvertimeBase.SalaryAndMonthSalaryIncome, "Salario más ingresos salariales del mes"));
                break;
            case nameof(ConceptKind):
                Check(
                    PayrollLabels.ConceptKinds,
                    (ConceptKind.Overtime, "Horas extras y recargos"), (ConceptKind.Income, "Ingreso"),
                    (ConceptKind.Deduction, "Deducción"));
                break;
            case nameof(RecurringMode):
                Check(
                    PayrollLabels.RecurringModes,
                    (RecurringMode.Fixed, "Fijo"), (RecurringMode.Proportional, "Proporcional a los días"));
                break;
            case nameof(RecurringStatus):
                Check(
                    PayrollLabels.RecurringStatuses,
                    (RecurringStatus.Active, "Activo"), (RecurringStatus.Inactive, "Inactivo"),
                    (RecurringStatus.Exhausted, "Tope alcanzado"));
                break;
            case nameof(PayrollLineGroup):
                Check(
                    PayrollLabels.LineGroups,
                    (PayrollLineGroup.Earning, "Devengado"), (PayrollLineGroup.Deduction, "Deducción"),
                    (PayrollLineGroup.EmployerContribution, "Aporte del empleador"), (PayrollLineGroup.Provision, "Provisión"),
                    (PayrollLineGroup.Info, "Informativo"));
                break;
            case nameof(ExclusionReason):
                Check(
                    PayrollLabels.ExclusionReasons,
                    (ExclusionReason.ContractTypeNotSupported, "El tipo de contrato no se liquida todavía"),
                    (ExclusionReason.SalaryTypeNotSupported, "El tipo de salario no se liquida todavía"),
                    (ExclusionReason.ContributorSubtypeNotSupported, "El subtipo de cotizante no se liquida todavía"),
                    (ExclusionReason.ForeignPensionExempt, "Los exentos de pensión no se liquidan todavía"),
                    (ExclusionReason.SalaryBelowMinimum, "El salario es menor que el mínimo"),
                    (ExclusionReason.MultipleContractsInPeriod, "Tiene más de un contrato en la quincena"));
                break;
            case nameof(BlockerReason):
                Check(
                    PayrollLabels.BlockerReasons,
                    (BlockerReason.MissingSalary, "No tiene salario vigente en la quincena"),
                    (BlockerReason.MissingWorkplace, "No tiene centro de trabajo en la quincena"),
                    (BlockerReason.MissingRiskClass, "Su centro de trabajo no tiene clase de riesgo"),
                    (BlockerReason.VacationOutsideContract, "Tiene vacaciones fuera de su contrato: corrígelas"));
                break;
            case nameof(PeriodAction):
                Check(
                    PayrollLabels.PeriodActions,
                    (PeriodAction.Created, "Abierta"), (PeriodAction.InputsImported, "Novedades por Excel"),
                    (PeriodAction.Liquidated, "Liquidada"), (PeriodAction.Approved, "Aprobada"),
                    (PeriodAction.Closed, "Cerrada"), (PeriodAction.Reopened, "Reabierta"),
                    (PeriodAction.MarkedOutdated, "Desactualizada"));
                break;
            default:
                throw new InvalidOperationException($"No contract table for {name}.");
        }
    }

    [Fact]
    public void ParsingIgnoresCaseAccentsAndExtraSpaces()
    {
        Assert.True(PayrollLabels.TryParse("  primera   QUINCENA ", out PayrollHalf half));
        Assert.Equal(PayrollHalf.First, half);
        Assert.True(PayrollLabels.TryParse("deduccion", out ConceptKind kind));
        Assert.Equal(ConceptKind.Deduction, kind);
        Assert.False(PayrollLabels.TryParse("Tercera quincena", out PayrollHalf _));
        Assert.False(PayrollLabels.TryParse(null, out ConceptKind _));
    }

    private static void Check<TEnum>(IReadOnlyDictionary<TEnum, string> labels, params (TEnum Value, string Label)[] contract)
        where TEnum : struct, Enum
    {
        // Ni una etiqueta de más ni un miembro sin etiqueta.
        Assert.Equal(Enum.GetValues<TEnum>().Length, contract.Length);
        Assert.Equal(contract.Length, labels.Count);
        foreach (var (value, label) in contract)
        {
            Assert.Equal(label, labels[value]);
        }
    }
}
