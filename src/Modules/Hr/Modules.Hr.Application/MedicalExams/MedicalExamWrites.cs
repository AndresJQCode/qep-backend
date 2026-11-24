using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>What a use case loaded of one employee: the exams as a list it may add to or remove from, and the exit requirements.</summary>
public sealed record MedicalExamContext(
    Employee Employee,
    List<MedicalExam> Exams,
    IReadOnlyList<ExitExamRequirement> Requirements,
    TenantCalendar Calendar);

/// <summary>
/// What the writes of the exams repeat (spec 2026-11-23 §7): authorize, load the employee with their exams and requirements, and at
/// the end recalculate the exit requirements, save everything in one unit of work and answer the whole
/// <see cref="EmployeeMedicalExamsDto"/>. Every endpoint needs <c>hr.employee.read</c> and <c>hr.medical.read</c>; writing also needs
/// <c>hr.medical.manage</c>.
/// </summary>
public sealed class MedicalExamWrites(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IEmployeeRepository employees,
    IMedicalExamRepository medicalExams,
    IHrUnitOfWork unitOfWork,
    ActivityWriter activity,
    IHrAuditPublisher auditPublisher,
    EmployeeMedicalExamsComposer composer)
{
    public Task AuthorizeReadAsync(Guid tenantId, CancellationToken cancellationToken) => HrAuthorization.EnsureAsync(
        executionContext, tenantModules, tenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.MedicalRead);

    public Task AuthorizeWriteAsync(Guid tenantId, CancellationToken cancellationToken) => HrAuthorization.EnsureAsync(
        executionContext,
        tenantModules,
        tenantId,
        cancellationToken,
        HrPermissions.EmployeeRead,
        HrPermissions.MedicalRead,
        HrPermissions.MedicalManage);

    public async Task<MedicalExamContext> LoadAsync(Guid tenantId, Guid employeeId, CancellationToken cancellationToken)
    {
        var id = new EmployeeId(employeeId);
        var employee = await employees.FindAsync(tenantId, id, cancellationToken) ?? throw HrNotFound.Employee(employeeId);
        var exams = await medicalExams.ListExamsAsync(tenantId, id, cancellationToken);
        var requirements = await medicalExams.ListRequirementsAsync(tenantId, id, cancellationToken);
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);

        return new MedicalExamContext(employee, [.. exams], requirements, calendar);
    }

    public void Add(MedicalExamContext context, MedicalExam exam)
    {
        medicalExams.Add(exam);
        context.Exams.Add(exam);
    }

    public void Remove(MedicalExamContext context, MedicalExam exam)
    {
        medicalExams.Remove(exam);
        context.Exams.Remove(exam);
    }

    public Task<EmployeeMedicalExamsDto> ComposeAsync(Guid tenantId, MedicalExamContext context, CancellationToken cancellationToken) =>
        composer.ComposeAsync(tenantId, context.Employee, context.Exams, context.Requirements, context.Calendar.Today, cancellationToken);

    /// <summary>
    /// After <b>every</b> write of exams the exit requirements are recalculated (spec §5.3), and the activity entry and the audit
    /// row are saved in the same unit of work as the write itself (spec §10). <paramref name="changes"/> only ever carries the type,
    /// the date and whether there is a PDF, and the audit only the names of those fields: restrictions, recommendations and the
    /// provider never leave the exam.
    /// </summary>
    public async Task<EmployeeMedicalExamsDto> CommitAsync(
        Guid tenantId,
        MedicalExamContext context,
        HrActivityAction action,
        IReadOnlyList<ActivityChange> changes,
        string auditAction,
        string auditResource,
        Guid auditResourceId,
        CancellationToken cancellationToken)
    {
        MedicalExamRules.Recalculate(context.Requirements, context.Exams);
        await activity.AppendAsync(tenantId, context.Employee, action, HrActivityCategory.Medical, null, changes, cancellationToken);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, tenantId, auditAction, auditResource, auditResourceId,
            changes.Select(change => change.Field).ToList(), context.Calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ComposeAsync(tenantId, context, cancellationToken);
    }

    /// <summary>The same type and date of another exam of the employee: the unique index would say the same, later and uglier.</summary>
    public static void EnsureNotDuplicated(MedicalExamContext context, MedicalExamType type, DateOnly examDate, MedicalExamId? except)
    {
        if (context.Exams.Any(exam => exam.Type == type && exam.ExamDate == examDate && exam.Id != except))
        {
            throw new HrDomainException(
                HrErrorCodes.MedicalExamDuplicate,
                "The employee already has an exam of that type on that date.",
                new Dictionary<string, string[]> { ["examDate"] = ["Ya hay un examen de ese tipo registrado en esa fecha."] });
        }
    }

    public static MedicalExam Find(MedicalExamContext context, Guid examId) =>
        context.Exams.FirstOrDefault(exam => exam.Id.Value == examId) ?? throw HrNotFound.MedicalExam(examId);
}
