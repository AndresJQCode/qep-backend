namespace Modules.Hr.Domain;

/// <summary>
/// Los códigos de dominio de Hr (plan 2026-11-20, «Mensajes de dominio»). Sólo existen los que el contrato HTTP nombra: un
/// código nuevo se agrega a la tabla del plan primero. Todos llevan el prefijo <c>hr.</c>.
/// </summary>
public static class HrErrorCodes
{
    // 404
    public const string EmployeeNotFound = "hr.employee.not_found";
    public const string ContractNotFound = "hr.contract.not_found";
    public const string SalaryNotFound = "hr.salary.not_found";
    public const string AssignmentNotFound = "hr.assignment.not_found";
    public const string RenewalNotFound = "hr.renewal.not_found";
    public const string SiteNotFound = "hr.site.not_found";
    public const string WorkplaceNotFound = "hr.workplace.not_found";
    public const string AreaNotFound = "hr.area.not_found";
    public const string PositionNotFound = "hr.position.not_found";
    public const string FileNotFound = "hr.file.not_found";

    // 409
    public const string DocumentTaken = "hr.employee.document_taken";
    public const string MemberTaken = "hr.employee.member_taken";
    public const string SettingsNameTaken = "hr.settings.name_taken";
    public const string SettingsInUse = "hr.settings.in_use";
    public const string SettingsAlreadyArchived = "hr.settings.already_archived";
    public const string SettingsNotArchived = "hr.settings.not_archived";

    // 422
    public const string MemberInvalid = "hr.employee.member_invalid";
    public const string EmployeeNotActive = "hr.employee.not_active";
    public const string EmployeeAlreadyActive = "hr.employee.already_active";
    public const string EmployeeNotDeletable = "hr.employee.not_deletable";
    public const string ContractNotActive = "hr.contract.not_active";
    public const string ContractNotFixedTerm = "hr.contract.not_fixed_term";
    public const string FixedTermCapExceeded = "hr.contract.fixed_term_cap_exceeded";
    public const string RenewalMinimumOneYear = "hr.contract.renewal_minimum_one_year";
    public const string RecordsAfterEnd = "hr.contract.records_after_end";
    public const string StartNotAfterPrevious = "hr.contract.start_not_after_previous";
    public const string CompanyChangeNotAllowed = "hr.contract.company_change_not_allowed";
    public const string SalaryEffectiveDateNotAfterLast = "hr.salary.effective_date_not_after_last";
    public const string BelowIntegralFloor = "hr.salary.below_integral_floor";
    public const string BelowMinimumUnacknowledged = "hr.salary.below_minimum_unacknowledged";
    public const string SalaryRewriteUnconfirmed = "hr.salary.rewrite_unconfirmed";
    public const string AssignmentEffectiveDateNotAfterLast = "hr.assignment.effective_date_not_after_last";
    public const string SiteChangeNotFirstOfMonth = "hr.assignment.site_change_not_first_of_month";
    public const string AssignmentUnchanged = "hr.assignment.unchanged";
    public const string WorkplaceOtherCompany = "hr.assignment.workplace_other_company";
    public const string SetupIncomplete = "hr.settings.setup_incomplete";
    public const string SiteArchived = "hr.settings.site_archived";
    public const string SiteHasActiveWorkplaces = "hr.settings.site_has_active_workplaces";
    public const string LegalParameterMissing = "hr.legal_parameter.missing";
    public const string FileInvalid = "hr.file.invalid";
    public const string FileRejected = "hr.file.rejected";

    // Exámenes médicos ocupacionales (spec 2026-11-23 §7.3). `not_found` es 404 y está en HrNotFound; el resto es 422, ninguno es conflicto.
    public const string MedicalExamNotFound = "hr.medical_exam.not_found";
    public const string MedicalExamDuplicate = "hr.medical_exam.duplicate";
    public const string MedicalExamFutureDate = "hr.medical_exam.future_date";
    public const string MedicalExamRestrictionsUntilInvalid = "hr.medical_exam.restrictions_until_invalid";
    public const string MedicalExamNoPendingExit = "hr.medical_exam.no_pending_exit";
    public const string MedicalExamStatusInvalid = "hr.medical_exam.status_invalid";
    public const string ExamFrequencyInvalid = "hr.settings.exam_frequency_invalid";

    // Birthday notices (spec 2026-11-26 §5.1): all 422, each marks its field.
    public const string BirthdayManagerNotFound = "hr.birthday_notice.manager_not_found";
    public const string BirthdayManagerWithoutAccess = "hr.birthday_notice.manager_without_access";
    public const string BirthdayConnectionRequired = "hr.birthday_notice.connection_required";
    public const string BirthdayConnectionInvalid = "hr.birthday_notice.connection_invalid";

    // 422, importación por Excel (Tarea 10): errores de archivo y confirmación.
    public const string ImportFileRequired = "hr.import.file_required";
    public const string ImportExtensionInvalid = "hr.import.extension_invalid";
    public const string ImportTooLarge = "hr.import.too_large";
    public const string ImportUnreadable = "hr.import.unreadable";
    public const string ImportHeaderMismatch = "hr.import.header_mismatch";
    public const string ImportEmpty = "hr.import.empty";
    public const string ImportTooManyRows = "hr.import.too_many_rows";
    public const string ImportRowsInvalid = "hr.import.rows_invalid";

    // Consola de parámetros legales del operador (spec 2026-11-22). `in_use` es 409 y `key_unknown` es 404, pero NO están en
    // `Conflicts`: salen como ResourceConflictException y ResourceNotFoundException directamente. El resto es 422.
    public const string LegalParameterInUse = "hr.legal_parameter.in_use";
    public const string LegalParameterValueInvalid = "hr.legal_parameter.value_invalid";
    public const string LegalParameterDateInvalid = "hr.legal_parameter.date_invalid";
    public const string LegalParameterLastRow = "hr.legal_parameter.last_row";
    public const string LegalParameterKeyUnknown = "hr.legal_parameter.key_unknown";

    // 422, carga de parámetros legales por Excel: errores de archivo, el aviso de la vista previa y los errores por fila.
    public const string LegalParameterImportFileRequired = "hr.legal_parameter.import.file_required";
    public const string LegalParameterImportExtensionInvalid = "hr.legal_parameter.import.extension_invalid";
    public const string LegalParameterImportTooLarge = "hr.legal_parameter.import.too_large";
    public const string LegalParameterImportUnreadable = "hr.legal_parameter.import.unreadable";
    public const string LegalParameterImportHeaderMismatch = "hr.legal_parameter.import.header_mismatch";
    public const string LegalParameterImportEmpty = "hr.legal_parameter.import.empty";
    public const string LegalParameterImportTooManyRows = "hr.legal_parameter.import.too_many_rows";
    public const string LegalParameterImportRowsInvalid = "hr.legal_parameter.import.rows_invalid";
    public const string LegalParameterImportNextYearMissing = "hr.legal_parameter.import.next_year_missing";

    /// <summary>Los códigos de fila de la carga por Excel de parámetros legales: viajan en el análisis, nunca como excepción.</summary>
    public static class LegalParameterImportRow
    {
        public const string KeyUnknown = "hr.legal_parameter.import.row.key_unknown";
        public const string DateInvalid = "hr.legal_parameter.import.row.date_invalid";
        public const string ValueInvalid = "hr.legal_parameter.import.row.value_invalid";
        public const string BracketsInvalid = "hr.legal_parameter.import.row.brackets_invalid";
        public const string Duplicated = "hr.legal_parameter.import.row.duplicated";
        public const string InUse = "hr.legal_parameter.import.row.in_use";
    }

    /// <summary>
    /// Los códigos de fila de la importación (<c>hr.import.row.*</c>): errores por fila y el aviso <c>below_minimum</c>. Viajan en
    /// <c>EmployeeImportRowIssueDto</c>, nunca como excepción.
    /// </summary>
    public static class ImportRow
    {
        public const string FirstNameRequired = "hr.import.row.first_name_required";
        public const string LastNameRequired = "hr.import.row.last_name_required";
        public const string DocumentTypeInvalid = "hr.import.row.document_type_invalid";
        public const string DocumentNumberRequired = "hr.import.row.document_number_required";
        public const string DocumentDuplicatedInFile = "hr.import.row.document_duplicated_in_file";
        public const string DocumentTaken = "hr.import.row.document_taken";
        public const string CountryInvalid = "hr.import.row.country_invalid";
        public const string CountryDocumentMismatch = "hr.import.row.country_document_mismatch";
        public const string BirthDateInvalid = "hr.import.row.birth_date_invalid";
        public const string PhoneInvalid = "hr.import.row.phone_invalid";
        public const string PersonalEmailInvalid = "hr.import.row.personal_email_invalid";
        public const string WorkEmailInvalid = "hr.import.row.work_email_invalid";
        public const string CityNotFound = "hr.import.row.city_not_found";
        public const string CityAmbiguous = "hr.import.row.city_ambiguous";
        public const string ContributorSubtypeInvalid = "hr.import.row.contributor_subtype_invalid";
        public const string YesNoInvalid = "hr.import.row.yes_no_invalid";
        public const string ForeignPensionExemptNotAllowed = "hr.import.row.foreign_pension_exempt_not_allowed";
        public const string RestDaysInvalid = "hr.import.row.rest_days_invalid";
        public const string PaymentMethodInvalid = "hr.import.row.payment_method_invalid";
        public const string BankIncomplete = "hr.import.row.bank_incomplete";
        public const string BankNotAllowed = "hr.import.row.bank_not_allowed";
        public const string BankAccountTypeInvalid = "hr.import.row.bank_account_type_invalid";
        public const string BankAccountNumberInvalid = "hr.import.row.bank_account_number_invalid";
        public const string CompanyRequired = "hr.import.row.company_required";
        public const string CompanyNotFound = "hr.import.row.company_not_found";
        public const string ContractTypeInvalid = "hr.import.row.contract_type_invalid";
        public const string DurationInvalid = "hr.import.row.duration_invalid";
        public const string SalaryTypeInvalid = "hr.import.row.salary_type_invalid";
        public const string IntegralNotAllowed = "hr.import.row.integral_not_allowed";
        public const string StartDateInvalid = "hr.import.row.start_date_invalid";
        public const string EndDateInvalid = "hr.import.row.end_date_invalid";
        public const string EndDateRequired = "hr.import.row.end_date_required";
        public const string EndDateForbidden = "hr.import.row.end_date_forbidden";
        public const string EndDateBeforeStart = "hr.import.row.end_date_before_start";
        public const string FixedTermCapExceeded = "hr.import.row.fixed_term_cap_exceeded";
        public const string SalaryInvalid = "hr.import.row.salary_invalid";
        public const string BelowIntegralFloor = "hr.import.row.below_integral_floor";
        public const string LegalParameterMissing = "hr.import.row.legal_parameter_missing";
        public const string PositionNotFound = "hr.import.row.position_not_found";
        public const string AreaNotFound = "hr.import.row.area_not_found";
        public const string WorkplaceNotFound = "hr.import.row.workplace_not_found";
        public const string WorkplaceOtherCompany = "hr.import.row.workplace_other_company";
        public const string RetirementDateInvalid = "hr.import.row.retirement_date_invalid";
        public const string RetirementDateInFuture = "hr.import.row.retirement_date_in_future";
        public const string RetirementBeforeStart = "hr.import.row.retirement_before_start";
        public const string RetirementReasonRequired = "hr.import.row.retirement_reason_required";
        public const string RetirementReasonInvalid = "hr.import.row.retirement_reason_invalid";
        public const string TextTooLong = "hr.import.row.text_too_long";
        public const string BelowMinimum = "hr.import.row.below_minimum";
    }

    /// <summary>
    /// Row codes of the medical exam history import (<c>hr.medical_exam.import.row.*</c>, spec 2026-11-23 §8): they travel in the
    /// analysis, never as an exception. The file-level errors reuse the <c>hr.import.*</c> codes of the employee import.
    /// </summary>
    public static class MedicalExamImportRow
    {
        public const string DocumentTypeInvalid = "hr.medical_exam.import.row.document_type_invalid";
        public const string DocumentNumberRequired = "hr.medical_exam.import.row.document_number_required";
        public const string EmployeeNotFound = "hr.medical_exam.import.row.employee_not_found";
        public const string ExamTypeInvalid = "hr.medical_exam.import.row.exam_type_invalid";
        public const string ExamDateInvalid = "hr.medical_exam.import.row.exam_date_invalid";
        public const string ExamDateInFuture = "hr.medical_exam.import.row.exam_date_in_future";
        public const string RestrictionsUntilInvalid = "hr.medical_exam.import.row.restrictions_until_invalid";
        public const string TextTooLong = "hr.medical_exam.import.row.text_too_long";
        public const string Duplicate = "hr.medical_exam.import.row.duplicate";
        public const string DuplicatedInFile = "hr.medical_exam.import.row.duplicated_in_file";
    }

    /// <summary>Los seis que se responden 409 (<c>ResourceConflictException</c>): el resto de los de dominio es 422.</summary>
    public static readonly IReadOnlySet<string> Conflicts = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentTaken,
        MemberTaken,
        SettingsNameTaken,
        SettingsInUse,
        SettingsAlreadyArchived,
        SettingsNotArchived,
    };
}
