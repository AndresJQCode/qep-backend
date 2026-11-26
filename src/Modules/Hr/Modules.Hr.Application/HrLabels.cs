using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Las etiquetas de la tabla «Enums y etiquetas» del contrato HTTP: las que muestra la UI y las que acepta la importación. Un solo
/// lugar; el frontend las copia literal.
/// </summary>
public static class HrLabels
{
    public const string Yes = "Sí";

    public const string No = "No";

    public static IReadOnlyDictionary<HrDocumentType, string> DocumentTypes { get; } = new Dictionary<HrDocumentType, string>
    {
        [HrDocumentType.CC] = "Cédula de ciudadanía",
        [HrDocumentType.CE] = "Cédula de extranjería",
        [HrDocumentType.PEP] = "Permiso especial de permanencia",
        [HrDocumentType.PPT] = "Permiso por protección temporal",
        [HrDocumentType.PA] = "Pasaporte",
        [HrDocumentType.CD] = "Carné diplomático",
        [HrDocumentType.SC] = "Salvoconducto",
    };

    public static IReadOnlyDictionary<ContributorSubtype, string> ContributorSubtypes { get; } = new Dictionary<ContributorSubtype, string>
    {
        [ContributorSubtype.Standard] = "Estándar",
        [ContributorSubtype.Pensioner] = "Pensionado",
        [ContributorSubtype.NotRequiredByAge] = "No obligado por edad",
        [ContributorSubtype.PensionRequirementsMet] = "Cumplió requisitos de pensión",
        [ContributorSubtype.SubstituteIndemnity] = "Recibió indemnización sustitutiva",
    };

    public static IReadOnlyDictionary<PaymentMethod, string> PaymentMethods { get; } = new Dictionary<PaymentMethod, string>
    {
        [PaymentMethod.BankTransfer] = "Transferencia bancaria",
        [PaymentMethod.Other] = "Otro",
    };

    public static IReadOnlyDictionary<BankAccountType, string> BankAccountTypes { get; } = new Dictionary<BankAccountType, string>
    {
        [BankAccountType.Savings] = "Ahorros",
        [BankAccountType.Checking] = "Corriente",
    };

    /// <summary><c>RestDay</c> es <see cref="DayOfWeek"/> por nombre.</summary>
    public static IReadOnlyDictionary<DayOfWeek, string> RestDays { get; } = new Dictionary<DayOfWeek, string>
    {
        [DayOfWeek.Monday] = "Lunes",
        [DayOfWeek.Tuesday] = "Martes",
        [DayOfWeek.Wednesday] = "Miércoles",
        [DayOfWeek.Thursday] = "Jueves",
        [DayOfWeek.Friday] = "Viernes",
        [DayOfWeek.Saturday] = "Sábado",
        [DayOfWeek.Sunday] = "Domingo",
    };

    public static IReadOnlyDictionary<ContractType, string> ContractTypes { get; } = new Dictionary<ContractType, string>
    {
        [ContractType.Employee] = "Empleado",
        [ContractType.Contractor] = "Contratista",
        [ContractType.PartTime] = "Tiempo parcial",
        [ContractType.SenaApprenticeTeaching] = "Aprendiz SENA lectiva",
        [ContractType.SenaApprenticeProductive] = "Aprendiz SENA productiva",
        [ContractType.SenaApprenticeDual] = "Aprendiz SENA dual",
        [ContractType.UniversityIntern] = "Practicante universitario",
        [ContractType.StudentLaw789] = "Estudiante Ley 789",
        [ContractType.StudentDecree055] = "Estudiante Decreto 055",
    };

    public static IReadOnlyDictionary<ContractDuration, string> ContractDurations { get; } = new Dictionary<ContractDuration, string>
    {
        [ContractDuration.FixedTerm] = "Término fijo",
        [ContractDuration.Indefinite] = "Término indefinido",
        [ContractDuration.WorkOrLabor] = "Obra o labor",
    };

    public static IReadOnlyDictionary<SalaryType, string> SalaryTypes { get; } = new Dictionary<SalaryType, string>
    {
        [SalaryType.Monthly] = "Mensual",
        [SalaryType.HalfTime] = "Medio tiempo",
        [SalaryType.Integral] = "Salario integral",
    };

    public static IReadOnlyDictionary<ContractStatus, string> ContractStatuses { get; } = new Dictionary<ContractStatus, string>
    {
        [ContractStatus.Active] = "Vigente",
        [ContractStatus.Terminated] = "Terminado",
        [ContractStatus.Converted] = "Convertido",
        [ContractStatus.Closed] = "Cerrado",
    };

    public static IReadOnlyDictionary<TerminationReason, string> TerminationReasons { get; } = new Dictionary<TerminationReason, string>
    {
        [TerminationReason.Resignation] = "Renuncia",
        [TerminationReason.DismissalWithJustCause] = "Despido con justa causa",
        [TerminationReason.DismissalWithoutJustCause] = "Despido sin justa causa",
        [TerminationReason.TermExpiry] = "Vencimiento del plazo",
        [TerminationReason.WorkCompletion] = "Terminación de la obra o labor",
        [TerminationReason.MutualAgreement] = "Mutuo acuerdo",
        [TerminationReason.ProbationPeriod] = "Terminación en período de prueba",
        [TerminationReason.CompanyClosure] = "Cierre o liquidación de la empresa",
        [TerminationReason.Death] = "Fallecimiento",
    };

    public static IReadOnlyDictionary<EmployeeStatus, string> EmployeeStatuses { get; } = new Dictionary<EmployeeStatus, string>
    {
        [EmployeeStatus.Active] = "Activo",
        [EmployeeStatus.Retired] = "Retirado",
        [EmployeeStatus.Deactivated] = "Desactivado",
    };

    public static IReadOnlyDictionary<ArlRiskClass, string> ArlRiskClasses { get; } = new Dictionary<ArlRiskClass, string>
    {
        [ArlRiskClass.I] = "Clase I",
        [ArlRiskClass.II] = "Clase II",
        [ArlRiskClass.III] = "Clase III",
        [ArlRiskClass.IV] = "Clase IV",
        [ArlRiskClass.V] = "Clase V",
    };

    public static IReadOnlyDictionary<HrActivityAction, string> ActivityActions { get; } = new Dictionary<HrActivityAction, string>
    {
        [HrActivityAction.EmployeeCreated] = "Alta",
        [HrActivityAction.EmployeeImported] = "Alta por importación",
        [HrActivityAction.PersonalDataUpdated] = "Datos personales",
        [HrActivityAction.PaymentUpdated] = "Datos de pago",
        [HrActivityAction.SalaryCreated] = "Salario nuevo",
        [HrActivityAction.SalaryRewritten] = "Salario reescrito",
        [HrActivityAction.AssignmentCreated] = "Cargo y lugar de trabajo",
        [HrActivityAction.ContractRenewed] = "Prórroga",
        [HrActivityAction.ContractConvertedToIndefinite] = "Conversión a término indefinido",
        [HrActivityAction.ContractTypeChanged] = "Cambio de tipo de contrato",
        [HrActivityAction.ContractTerminated] = "Terminación",
        [HrActivityAction.EmployeeDeactivated] = "Desactivación",
        [HrActivityAction.EmployeeReincorporated] = "Reincorporación",
        [HrActivityAction.DocumentAttached] = "Documento",
        [HrActivityAction.MedicalExamRecorded] = "Examen médico registrado",
        [HrActivityAction.MedicalExamUpdated] = "Examen médico editado",
        [HrActivityAction.MedicalExamDeleted] = "Examen médico eliminado",
        [HrActivityAction.ExitExamNotAttended] = "Egreso sin asistir al examen",
    };

    /// <summary>
    /// La etiqueta, sin mayúsculas, tildes ni espacios de más (<see cref="HrText.Fold"/>): la importación acepta «término fijo» y no
    /// el nombre del miembro (<c>FixedTerm</c>), porque el contrato sólo acepta las listas de «Valores válidos».
    /// </summary>
    public static bool TryParse<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (HrText.Fold(text) is not { } folded)
        {
            return false;
        }

        foreach (var (member, label) in LabelsOf<TEnum>())
        {
            if (string.Equals(HrText.Fold(label), folded, StringComparison.Ordinal))
            {
                value = member;
                return true;
            }
        }

        return false;
    }

    /// <summary>Todas las etiquetas del enum, en el orden de la tabla.</summary>
    public static IEnumerable<string> Of<TEnum>()
        where TEnum : struct, Enum => LabelsOf<TEnum>().Select(pair => pair.Label);

    private static IEnumerable<(TEnum Member, string Label)> LabelsOf<TEnum>()
        where TEnum : struct, Enum
    {
        object table = typeof(TEnum) switch
        {
            var type when type == typeof(HrDocumentType) => DocumentTypes,
            var type when type == typeof(ContributorSubtype) => ContributorSubtypes,
            var type when type == typeof(PaymentMethod) => PaymentMethods,
            var type when type == typeof(BankAccountType) => BankAccountTypes,
            var type when type == typeof(DayOfWeek) => RestDays,
            var type when type == typeof(ContractType) => ContractTypes,
            var type when type == typeof(ContractDuration) => ContractDurations,
            var type when type == typeof(SalaryType) => SalaryTypes,
            var type when type == typeof(ContractStatus) => ContractStatuses,
            var type when type == typeof(TerminationReason) => TerminationReasons,
            var type when type == typeof(EmployeeStatus) => EmployeeStatuses,
            var type when type == typeof(ArlRiskClass) => ArlRiskClasses,
            var type when type == typeof(HrActivityAction) => ActivityActions,
            _ => throw new InvalidOperationException($"'{typeof(TEnum).Name}' has no labels."),
        };
        return ((IReadOnlyDictionary<TEnum, string>)table).Select(pair => (pair.Key, pair.Value));
    }
}
