namespace Modules.Payroll.Domain;

/// <summary>
/// Los códigos de dominio de Payroll (plan 2026-11-21, «Mensajes de dominio» y «Importación de novedades»). Sólo existen los que
/// el contrato HTTP nombra: un código nuevo se agrega a la tabla del plan primero. Todos llevan el prefijo <c>payroll.</c>.
/// </summary>
public static class PayrollErrorCodes
{
    // 404
    public const string CompanyNotFound = "payroll.company.not_found";
    public const string PeriodNotFound = "payroll.period.not_found";
    public const string EmployeeNotFound = "payroll.employee.not_found";
    public const string EmployeeNotInPeriod = "payroll.employee.not_in_period";
    public const string ConceptNotFound = "payroll.concept.not_found";
    public const string RecurringNotFound = "payroll.recurring.not_found";
    public const string VacationNotFound = "payroll.vacation.not_found";

    // 409
    public const string ConceptNameTaken = "payroll.concept.name_taken";
    public const string ConceptAlreadyArchived = "payroll.concept.already_archived";
    public const string ConceptNotArchived = "payroll.concept.not_archived";
    public const string RecurringAlreadyActive = "payroll.recurring.already_active";
    public const string RecurringAlreadyInactive = "payroll.recurring.already_inactive";
    public const string VacationOverlap = "payroll.vacation.overlap";

    // 422
    public const string CompanyInactive = "payroll.company.inactive";
    public const string EmployeeNotLiquidable = "payroll.employee.not_liquidable";
    public const string ConceptSystemReadonly = "payroll.concept.system_readonly";
    public const string ConceptArchived = "payroll.concept.archived";
    public const string ConceptKindMismatch = "payroll.concept.kind_mismatch";
    public const string PeriodNotEditable = "payroll.period.not_editable";
    public const string PeriodInvalidTransition = "payroll.period.invalid_transition";
    public const string PeriodOutdated = "payroll.period.outdated";
    public const string PeriodNegativeNet = "payroll.period.negative_net";
    public const string PeriodFirstHalfNotLiquidated = "payroll.period.first_half_not_liquidated";
    public const string PeriodFirstHalfNotApproved = "payroll.period.first_half_not_approved";
    public const string PeriodSamePersonCannotApprove = "payroll.period.same_person_cannot_approve";
    public const string PeriodNotExportable = "payroll.period.not_exportable";
    public const string LiquidationAbsencesUnconfirmed = "payroll.liquidation.absences_unconfirmed";
    public const string LiquidationBlocked = "payroll.liquidation.blocked";
    public const string LiquidationNobodyToLiquidate = "payroll.liquidation.nobody_to_liquidate";
    public const string LegalParameterMissing = "payroll.legal_parameter.missing";

    // 422 de las vacaciones disfrutadas (spec 2026-11-23): los dos primeros sólo con origen Qep.
    public const string VacationStartsOnRestDay = "payroll.vacation.starts_on_rest_day";
    public const string VacationNoBusinessDays = "payroll.vacation.no_business_days";
    public const string VacationOutsideContract = "payroll.vacation.outside_contract";
    public const string VacationBeforeFirstPeriod = "payroll.vacation.before_first_period";
    public const string VacationNotPaidYet = "payroll.vacation.not_paid_yet";
    public const string VacationPaidPeriodLocked = "payroll.vacation.paid_period_locked";

    // Interno: el índice único de períodos lo traduce `PayrollUnitOfWork` y `EnsureCurrentPeriods` lo atrapa; nunca sale por HTTP.
    public const string PeriodAlreadyExists = "payroll.period.already_exists";

    // 422 de la importación: errores de archivo (sin filas)
    public const string ImportFileRequired = "payroll.import.file_required";
    public const string ImportExtensionInvalid = "payroll.import.extension_invalid";
    public const string ImportTooLarge = "payroll.import.too_large";
    public const string ImportUnreadable = "payroll.import.unreadable";
    public const string ImportHeaderMismatch = "payroll.import.header_mismatch";
    public const string ImportEmpty = "payroll.import.empty";
    public const string ImportTooManyRows = "payroll.import.too_many_rows";
    public const string ImportRowsInvalid = "payroll.import.rows_invalid";

    // 422 de la importación: códigos de fila (payroll.import.row.*)
    public const string ImportRowDocumentRequired = "payroll.import.row.document_required";
    public const string ImportRowEmployeeNotInPeriod = "payroll.import.row.employee_not_in_period";
    public const string ImportRowEmployeeNotLiquidable = "payroll.import.row.employee_not_liquidable";
    public const string ImportRowConceptRequired = "payroll.import.row.concept_required";
    public const string ImportRowConceptNotFound = "payroll.import.row.concept_not_found";
    public const string ImportRowHoursRequired = "payroll.import.row.hours_required";
    public const string ImportRowHoursInvalid = "payroll.import.row.hours_invalid";
    public const string ImportRowHoursNotAllowed = "payroll.import.row.hours_not_allowed";
    public const string ImportRowAmountRequired = "payroll.import.row.amount_required";
    public const string ImportRowAmountInvalid = "payroll.import.row.amount_invalid";
    public const string ImportRowAmountNotAllowed = "payroll.import.row.amount_not_allowed";
    public const string ImportRowYesNoInvalid = "payroll.import.row.yes_no_invalid";
    public const string ImportRowSalaryConstitutiveRequired = "payroll.import.row.salary_constitutive_required";
    public const string ImportRowSalaryConstitutiveNotAllowed = "payroll.import.row.salary_constitutive_not_allowed";
    public const string ImportRowOvertimeDuplicatedInFile = "payroll.import.row.overtime_duplicated_in_file";
    public const string ImportRowTooManyLines = "payroll.import.row.too_many_lines";

    /// <summary>Los códigos que se responden 409: el caso de uso los convierte, y el resto de <see cref="PayrollDomainException"/> sigue 422.</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal)
    {
        ConceptNameTaken,
        ConceptAlreadyArchived,
        ConceptNotArchived,
        RecurringAlreadyActive,
        RecurringAlreadyInactive,
        VacationOverlap,
    };
}
