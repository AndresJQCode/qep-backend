namespace Modules.Hr.Application;

// Tres segmentos, module.resource.action, como los demás permisos. Los cinco del spec 2026-11-20: ver el empleado no incluye su
// contrato ni su dinero (D-1: cada `manage` exige también su `read`).
public static class HrPermissions
{
    public const string EmployeeRead = "hr.employee.read";

    public const string EmployeeManage = "hr.employee.manage";

    public const string ContractRead = "hr.contract.read";

    public const string ContractManage = "hr.contract.manage";

    public const string SettingsManage = "hr.settings.manage";

    // Spec 2026-11-23 (medical exams): health data. Every medical endpoint also requires EmployeeRead; writing requires both.
    public const string MedicalRead = "hr.medical.read";

    public const string MedicalManage = "hr.medical.manage";
}
