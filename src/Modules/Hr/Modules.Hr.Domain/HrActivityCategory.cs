namespace Modules.Hr.Domain;

public enum HrActivityCategory
{
    Personal,
    Lifecycle,
    Payment,
    Salary,
    Assignment,
    Contract,

    /// <summary>Occupational medical exams (spec 2026-11-23 §10): only whoever has <c>hr.medical.read</c> sees these entries.</summary>
    Medical,
}
