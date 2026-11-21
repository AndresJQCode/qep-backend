namespace Modules.Hr.Application;

/// <summary>
/// Acciones de <c>audit.entries</c> de Hr. <c>audit.entries</c> guarda <b>nombres</b> de campos (camelCase), nunca valores: el
/// antes y el después viven en <c>hr.employee_activity</c>.
/// </summary>
public static class HrAuditActions
{
    public const string EmployeeCreated = "hr.employee.created";
    public const string EmployeeUpdated = "hr.employee.updated";
    public const string EmployeePaymentUpdated = "hr.employee.payment_updated";
    public const string EmployeeDeactivated = "hr.employee.deactivated";
    public const string EmployeeReincorporated = "hr.employee.reincorporated";
    public const string EmployeeDeleted = "hr.employee.deleted";
    public const string EmployeeImported = "hr.employee.imported";

    public const string ContractSalaryCreated = "hr.contract.salary_created";
    public const string ContractSalaryRewritten = "hr.contract.salary_rewritten";
    public const string ContractAssignmentCreated = "hr.contract.assignment_created";
    public const string ContractRenewed = "hr.contract.renewed";
    public const string ContractConvertedToIndefinite = "hr.contract.converted_to_indefinite";
    public const string ContractTypeChanged = "hr.contract.type_changed";
    public const string ContractTerminated = "hr.contract.terminated";
    public const string ContractDocumentUpdated = "hr.contract.document_updated";

    public const string SettingsCreated = "hr.settings.created";
    public const string SettingsUpdated = "hr.settings.updated";
    public const string SettingsArchived = "hr.settings.archived";
    public const string SettingsUnarchived = "hr.settings.unarchived";
    public const string SettingsDeleted = "hr.settings.deleted";

    public static class Resources
    {
        public const string Employee = "employee";
        public const string Contract = "contract";
        public const string Site = "site";
        public const string Workplace = "workplace";
        public const string Area = "area";
        public const string Position = "position";
    }
}
