namespace Modules.Hr.Domain;

/// <summary>Pure rules of the medical exams (spec 2026-11-23 §5.3 and §5.4): no clock, no database.</summary>
public static class MedicalExamRules
{
    /// <summary>"Due soon" window: the next periodic exam falls within this many days (decision D-5).</summary>
    public const int DueSoonDays = 30;

    /// <summary>The exit exam is due this many business days after the termination (Resolution 1843 of 2025).</summary>
    public const int ExitExamBusinessDays = 5;

    /// <summary>
    /// The one deterministic rule that closes and reopens exit requirements, run after every write of the exams of an employee.
    /// Each requirement, from the oldest termination, takes the oldest <see cref="MedicalExamType.Exit"/> exam dated on or after
    /// its termination that no other requirement took. Without one it returns to <c>NotAttended</c> if there was a notice and to
    /// <c>Pending</c> if not.
    /// </summary>
    public static void Recalculate(IReadOnlyList<ExitExamRequirement> requirements, IReadOnlyList<MedicalExam> exams)
    {
        var available = exams
            .Where(exam => exam.Type == MedicalExamType.Exit)
            .OrderBy(exam => exam.ExamDate)
            .ThenBy(exam => exam.Id.Value)
            .ToList();

        foreach (var requirement in requirements.OrderBy(item => item.TerminatedOn).ThenBy(item => item.Id.Value))
        {
            var taken = available.FirstOrDefault(exam => exam.ExamDate >= requirement.TerminatedOn);
            if (taken is null)
            {
                requirement.Reopen();
                continue;
            }

            available.Remove(taken);
            requirement.Close(taken.Id);
        }
    }

    /// <summary>
    /// The control status. The first rule that matches wins, in the order of <see cref="MedicalExamStatus"/>; <c>null</c> when the
    /// employee is outside the universe (only an active employee, or a retired one with a pending exit exam, is inside).
    /// </summary>
    public static MedicalExamStatus? StatusOf(
        EmployeeStatus employee,
        bool hasPendingExit,
        bool hasAnyExam,
        DateOnly? nextPeriodicDue,
        DateOnly today)
    {
        if (employee == EmployeeStatus.Deactivated || (employee == EmployeeStatus.Retired && !hasPendingExit))
        {
            return null;
        }

        if (hasPendingExit)
        {
            return MedicalExamStatus.ExitPending;
        }

        if (!hasAnyExam)
        {
            return MedicalExamStatus.NoExams;
        }

        if (nextPeriodicDue is not { } next)
        {
            return MedicalExamStatus.NotRequired;
        }

        if (next < today)
        {
            return MedicalExamStatus.Overdue;
        }

        return next <= today.AddDays(DueSoonDays) ? MedicalExamStatus.DueSoon : MedicalExamStatus.UpToDate;
    }

    public static DateOnly NextPeriodicDue(DateOnly lastExam, int frequencyMonths) => lastExam.AddMonths(frequencyMonths);
}
