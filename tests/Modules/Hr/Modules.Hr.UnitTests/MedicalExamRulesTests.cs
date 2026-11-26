using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

/// <summary>Spec 2026-11-23 §5: the exam aggregate, the exit requirement recalculation and the control status. Pure, no database.</summary>
public sealed class MedicalExamRulesTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly EmployeeId Employee = new(Guid.Parse("01900000-0000-7000-8000-0000000000e1"));
    private static readonly DateOnly Today = new(2026, 11, 23);
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);

    private static MedicalExam Exam(MedicalExamType type, DateOnly date) =>
        MedicalExam.Create(MedicalExamId.New(), Tenant, Employee, type, date, Today, null, null, null, null, Now);

    private static ExitExamRequirement Requirement(DateOnly terminatedOn) =>
        ExitExamRequirement.Create(
            ExitExamRequirementId.New(), Tenant, Employee, EmploymentContractId.New(), terminatedOn, terminatedOn.AddDays(7), Now);

    private static string CodeOf(Action action) => Assert.Throws<HrDomainException>(action).Code;

    [Fact]
    public void RecalculateClosesTheOldestRequirementWithTheOldestQualifyingExam()
    {
        var requirement = Requirement(new DateOnly(2026, 10, 1));
        var newer = Exam(MedicalExamType.Exit, new DateOnly(2026, 10, 10));
        var older = Exam(MedicalExamType.Exit, new DateOnly(2026, 10, 5));

        MedicalExamRules.Recalculate([requirement], [newer, older, Exam(MedicalExamType.Periodic, new DateOnly(2026, 10, 2))]);

        Assert.Equal(ExitExamStatus.Done, requirement.Status);
        Assert.Equal(older.Id, requirement.ExamId);
    }

    [Fact]
    public void RecalculateTwoExitsTwoRequirementsNeitherSharesAnExam()
    {
        var first = Requirement(new DateOnly(2026, 3, 1));
        var second = Requirement(new DateOnly(2026, 10, 1));
        var march = Exam(MedicalExamType.Exit, new DateOnly(2026, 3, 5));
        var october = Exam(MedicalExamType.Exit, new DateOnly(2026, 10, 5));

        MedicalExamRules.Recalculate([second, first], [october, march]);

        Assert.Equal(march.Id, first.ExamId);
        Assert.Equal(october.Id, second.ExamId);

        // With a single exam after both terminations only the oldest requirement takes it.
        var third = Requirement(new DateOnly(2026, 3, 1));
        var fourth = Requirement(new DateOnly(2026, 10, 1));
        MedicalExamRules.Recalculate([third, fourth], [october]);

        Assert.Equal(ExitExamStatus.Done, third.Status);
        Assert.Equal(october.Id, third.ExamId);
        Assert.Equal(ExitExamStatus.Pending, fourth.Status);
        Assert.Null(fourth.ExamId);
    }

    [Fact]
    public void RecalculateReopensToNotAttendedWhenThereWasANotice()
    {
        var requirement = Requirement(new DateOnly(2026, 10, 1));
        requirement.MarkNotAttended(new DateOnly(2026, 10, 3), "Called twice", Today);
        var exit = Exam(MedicalExamType.Exit, new DateOnly(2026, 10, 8));
        MedicalExamRules.Recalculate([requirement], [exit]);
        Assert.Equal(ExitExamStatus.Done, requirement.Status);

        exit.Update(MedicalExamType.Periodic, exit.ExamDate, Today, null, null, null, null, Now);
        MedicalExamRules.Recalculate([requirement], [exit]);

        Assert.Equal(ExitExamStatus.NotAttended, requirement.Status);
        Assert.Null(requirement.ExamId);
        Assert.Equal(new DateOnly(2026, 10, 3), requirement.NotifiedOn);
    }

    [Fact]
    public void RecalculateReopensToPendingWhenThereWasNoNotice()
    {
        var requirement = Requirement(new DateOnly(2026, 10, 1));
        var exit = Exam(MedicalExamType.Exit, new DateOnly(2026, 10, 8));
        MedicalExamRules.Recalculate([requirement], [exit]);
        Assert.Equal(ExitExamStatus.Done, requirement.Status);

        MedicalExamRules.Recalculate([requirement], []);

        Assert.Equal(ExitExamStatus.Pending, requirement.Status);
        Assert.Null(requirement.ExamId);
    }

    [Fact]
    public void RecalculateExamBeforeTerminatedOnDoesNotClose()
    {
        var requirement = Requirement(new DateOnly(2026, 10, 10));

        MedicalExamRules.Recalculate([requirement], [Exam(MedicalExamType.Exit, new DateOnly(2026, 10, 9))]);

        Assert.Equal(ExitExamStatus.Pending, requirement.Status);
        Assert.Null(requirement.ExamId);
    }

    [Fact]
    public void MarkNotAttendedOnlyWorksOnAPendingRequirementAndNotInTheFuture()
    {
        var requirement = Requirement(new DateOnly(2026, 10, 1));

        Assert.Equal(
            HrErrorCodes.MedicalExamFutureDate,
            CodeOf(() => requirement.MarkNotAttended(Today.AddDays(1), null, Today)));

        requirement.MarkNotAttended(Today, "  Did not show up  ", Today);

        Assert.Equal(ExitExamStatus.NotAttended, requirement.Status);
        Assert.Equal("Did not show up", requirement.Notes);
        Assert.Equal(
            HrErrorCodes.MedicalExamNoPendingExit,
            CodeOf(() => requirement.MarkNotAttended(Today, null, Today)));
    }

    [Theory]
    [InlineData(EmployeeStatus.Active, true, true, "2026-01-01", MedicalExamStatus.ExitPending)]
    [InlineData(EmployeeStatus.Retired, true, true, "2026-01-01", MedicalExamStatus.ExitPending)]
    [InlineData(EmployeeStatus.Active, false, false, null, MedicalExamStatus.NoExams)]
    [InlineData(EmployeeStatus.Active, false, true, "2026-11-22", MedicalExamStatus.Overdue)]
    [InlineData(EmployeeStatus.Active, false, true, "2026-11-23", MedicalExamStatus.DueSoon)]
    [InlineData(EmployeeStatus.Active, false, true, "2026-12-23", MedicalExamStatus.DueSoon)]
    [InlineData(EmployeeStatus.Active, false, true, "2026-12-24", MedicalExamStatus.UpToDate)]
    [InlineData(EmployeeStatus.Active, false, true, null, MedicalExamStatus.NotRequired)]
    public void StatusOfFollowsTheFirstMatchingRule(
        EmployeeStatus employee, bool pendingExit, bool anyExam, string? nextDue, MedicalExamStatus expected)
    {
        DateOnly? next = nextDue is null ? null : DateOnly.Parse(nextDue, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, MedicalExamRules.StatusOf(employee, pendingExit, anyExam, next, Today));
    }

    [Theory]
    [InlineData(EmployeeStatus.Deactivated, false)]
    [InlineData(EmployeeStatus.Deactivated, true)]
    [InlineData(EmployeeStatus.Retired, false)]
    public void StatusOfIsNullOutsideTheUniverse(EmployeeStatus employee, bool pendingExit) =>
        Assert.Null(MedicalExamRules.StatusOf(employee, pendingExit, true, new DateOnly(2026, 1, 1), Today));

    [Fact]
    public void NextPeriodicDueAddsMonths()
    {
        Assert.Equal(new DateOnly(2027, 11, 15), MedicalExamRules.NextPeriodicDue(new DateOnly(2026, 11, 15), 12));
        Assert.Equal(new DateOnly(2027, 2, 28), MedicalExamRules.NextPeriodicDue(new DateOnly(2026, 11, 30), 3));
    }

    [Fact]
    public void CreateRejectsFutureDate()
    {
        var code = CodeOf(() => Exam(MedicalExamType.Periodic, Today.AddDays(1)));

        Assert.Equal(HrErrorCodes.MedicalExamFutureDate, code);
        Assert.Equal(Today, Exam(MedicalExamType.Periodic, Today).ExamDate);
    }

    [Fact]
    public void CreateRejectsRestrictionsUntilWithoutRestrictions()
    {
        static MedicalExam WithUntil(string? restrictions, DateOnly until) =>
            MedicalExam.Create(
                MedicalExamId.New(), Tenant, Employee, MedicalExamType.Periodic, new DateOnly(2026, 11, 1), Today,
                null, restrictions, null, until, Now);

        Assert.Equal(HrErrorCodes.MedicalExamRestrictionsUntilInvalid, CodeOf(() => WithUntil(null, new DateOnly(2026, 12, 1))));
        Assert.Equal(HrErrorCodes.MedicalExamRestrictionsUntilInvalid, CodeOf(() => WithUntil("   ", new DateOnly(2026, 12, 1))));
        Assert.Equal(HrErrorCodes.MedicalExamRestrictionsUntilInvalid, CodeOf(() => WithUntil("No lifting", new DateOnly(2026, 10, 31))));
        Assert.Equal(new DateOnly(2026, 11, 1), WithUntil("No lifting", new DateOnly(2026, 11, 1)).RestrictionsUntil);
    }

    [Fact]
    public void UpdateAndSetDocumentBumpTheVersion()
    {
        var exam = Exam(MedicalExamType.Periodic, new DateOnly(2026, 11, 1));
        Assert.Equal(1, exam.Version);

        exam.Update(MedicalExamType.FollowUp, new DateOnly(2026, 11, 2), Today, " IPS ", "  ", null, null, Now);
        var fileId = Guid.NewGuid();
        exam.SetDocument(fileId, Now);

        Assert.Equal(3, exam.Version);
        Assert.Equal(MedicalExamType.FollowUp, exam.Type);
        Assert.Equal("IPS", exam.Provider);
        Assert.Null(exam.Restrictions);
        Assert.Equal(fileId, exam.DocumentFileId);
    }
}
