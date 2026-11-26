using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// Las etiquetas de la tabla «Enums y etiquetas» del contrato HTTP: las que muestra la UI y las que acepta la importación de
/// novedades (comparación sin mayúsculas, tildes ni espacios de más). Un solo lugar; el frontend las copia literal.
/// </summary>
public static class PayrollLabels
{
    public const string Yes = "Sí";

    public const string No = "No";

    public static IReadOnlyDictionary<PayrollPeriodStatus, string> PeriodStatuses { get; } = new Dictionary<PayrollPeriodStatus, string>
    {
        [PayrollPeriodStatus.Draft] = "Borrador",
        [PayrollPeriodStatus.Liquidated] = "Liquidado",
        [PayrollPeriodStatus.Approved] = "Aprobado",
        [PayrollPeriodStatus.Closed] = "Cerrado",
        [PayrollPeriodStatus.Reopened] = "Reabierto",
    };

    public static IReadOnlyDictionary<PayrollHalf, string> Halves { get; } = new Dictionary<PayrollHalf, string>
    {
        [PayrollHalf.First] = "Primera quincena",
        [PayrollHalf.Second] = "Segunda quincena",
    };

    public static IReadOnlyDictionary<TransportFrequency, string> TransportFrequencies { get; } = new Dictionary<TransportFrequency, string>
    {
        [TransportFrequency.Biweekly] = "Quincenal",
        [TransportFrequency.Monthly] = "Mensual",
    };

    public static IReadOnlyDictionary<TransportCapBase, string> TransportCapBases { get; } = new Dictionary<TransportCapBase, string>
    {
        [TransportCapBase.SalaryOnly] = "Sólo salario",
        [TransportCapBase.SalaryAndSalaryIncome] = "Salario más ingresos salariales sin extras",
        [TransportCapBase.SalaryAndPaidAbsences] = "Salario más ausencias pagas sin comisiones",
    };

    public static IReadOnlyDictionary<OvertimeBase, string> OvertimeBases { get; } = new Dictionary<OvertimeBase, string>
    {
        [OvertimeBase.SalaryOnly] = "Sólo salario",
        [OvertimeBase.SalaryAndMonthSalaryIncome] = "Salario más ingresos salariales del mes",
    };

    public static IReadOnlyDictionary<ConceptKind, string> ConceptKinds { get; } = new Dictionary<ConceptKind, string>
    {
        [ConceptKind.Overtime] = "Horas extras y recargos",
        [ConceptKind.Income] = "Ingreso",
        [ConceptKind.Deduction] = "Deducción",
    };

    public static IReadOnlyDictionary<RecurringMode, string> RecurringModes { get; } = new Dictionary<RecurringMode, string>
    {
        [RecurringMode.Fixed] = "Fijo",
        [RecurringMode.Proportional] = "Proporcional a los días",
    };

    public static IReadOnlyDictionary<RecurringStatus, string> RecurringStatuses { get; } = new Dictionary<RecurringStatus, string>
    {
        [RecurringStatus.Active] = "Activo",
        [RecurringStatus.Inactive] = "Inactivo",
        [RecurringStatus.Exhausted] = "Tope alcanzado",
    };

    public static IReadOnlyDictionary<PayrollLineGroup, string> LineGroups { get; } = new Dictionary<PayrollLineGroup, string>
    {
        [PayrollLineGroup.Earning] = "Devengado",
        [PayrollLineGroup.Deduction] = "Deducción",
        [PayrollLineGroup.EmployerContribution] = "Aporte del empleador",
        [PayrollLineGroup.Provision] = "Provisión",
        [PayrollLineGroup.Info] = "Informativo",
    };

    public static IReadOnlyDictionary<ExclusionReason, string> ExclusionReasons { get; } = new Dictionary<ExclusionReason, string>
    {
        [ExclusionReason.ContractTypeNotSupported] = "El tipo de contrato no se liquida todavía",
        [ExclusionReason.SalaryTypeNotSupported] = "El tipo de salario no se liquida todavía",
        [ExclusionReason.ContributorSubtypeNotSupported] = "El subtipo de cotizante no se liquida todavía",
        [ExclusionReason.ForeignPensionExempt] = "Los exentos de pensión no se liquidan todavía",
        [ExclusionReason.SalaryBelowMinimum] = "El salario es menor que el mínimo",
        [ExclusionReason.MultipleContractsInPeriod] = "Tiene más de un contrato en la quincena",
    };

    public static IReadOnlyDictionary<BlockerReason, string> BlockerReasons { get; } = new Dictionary<BlockerReason, string>
    {
        [BlockerReason.MissingSalary] = "No tiene salario vigente en la quincena",
        [BlockerReason.MissingWorkplace] = "No tiene centro de trabajo en la quincena",
        [BlockerReason.MissingRiskClass] = "Su centro de trabajo no tiene clase de riesgo",
        [BlockerReason.VacationOutsideContract] = "Tiene vacaciones fuera de su contrato: corrígelas",
    };

    public static IReadOnlyDictionary<PeriodAction, string> PeriodActions { get; } = new Dictionary<PeriodAction, string>
    {
        [PeriodAction.Created] = "Abierta",
        [PeriodAction.InputsImported] = "Novedades por Excel",
        [PeriodAction.Liquidated] = "Liquidada",
        [PeriodAction.Approved] = "Aprobada",
        [PeriodAction.Closed] = "Cerrada",
        [PeriodAction.Reopened] = "Reabierta",
        [PeriodAction.MarkedOutdated] = "Desactualizada",
    };

    /// <summary>
    /// El miembro cuya etiqueta es <paramref name="text"/>, sin mayúsculas, tildes ni espacios de más
    /// (<see cref="PayrollText.Fold"/>). Un texto que no es una etiqueta no es un miembro: nunca se acepta el nombre del enum ni su número.
    /// </summary>
    public static bool TryParse<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        var wanted = PayrollText.Fold(text);
        if (wanted is null || LabelsOf<TEnum>() is not { } labels)
        {
            return false;
        }

        foreach (var (member, label) in labels)
        {
            if (PayrollText.Fold(label) == wanted)
            {
                value = member;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<TEnum, string>? LabelsOf<TEnum>()
        where TEnum : struct, Enum => (typeof(TEnum).Name switch
        {
            nameof(PayrollPeriodStatus) => PeriodStatuses,
            nameof(PayrollHalf) => Halves,
            nameof(TransportFrequency) => TransportFrequencies,
            nameof(TransportCapBase) => TransportCapBases,
            nameof(OvertimeBase) => OvertimeBases,
            nameof(ConceptKind) => ConceptKinds,
            nameof(RecurringMode) => RecurringModes,
            nameof(RecurringStatus) => RecurringStatuses,
            nameof(PayrollLineGroup) => LineGroups,
            nameof(ExclusionReason) => ExclusionReasons,
            nameof(BlockerReason) => BlockerReasons,
            nameof(PeriodAction) => PeriodActions,
            _ => (object?)null,
        }) as IReadOnlyDictionary<TEnum, string>;
}
