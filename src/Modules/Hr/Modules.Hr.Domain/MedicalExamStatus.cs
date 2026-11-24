namespace Modules.Hr.Domain;

/// <summary>
/// The control status of an employee (spec 2026-11-23 §5.4). The declaration order is the urgency order of the overview: the
/// first one that matches wins and the most urgent comes first.
/// </summary>
public enum MedicalExamStatus
{
    ExitPending,
    NoExams,
    Overdue,
    DueSoon,
    UpToDate,
    NotRequired,
}
