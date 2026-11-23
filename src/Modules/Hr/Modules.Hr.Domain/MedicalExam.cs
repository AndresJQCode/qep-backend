namespace Modules.Hr.Domain;

/// <summary>
/// An occupational medical exam of an employee (spec 2026-11-23 §5.1). Own aggregate with its own <see cref="Version"/>, so that
/// recording an exam never collides with whoever edits the employee record. Restrictions, recommendations and the provider are
/// health data: they never travel to the activity log, the audit or an event.
/// </summary>
public sealed class MedicalExam
{
    private MedicalExam()
    {
    }

    public MedicalExamId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public EmployeeId EmployeeId { get; private set; }

    public MedicalExamType Type { get; private set; }

    public DateOnly ExamDate { get; private set; }

    public string? Provider { get; private set; }

    public string? Restrictions { get; private set; }

    public string? Recommendations { get; private set; }

    public DateOnly? RestrictionsUntil { get; private set; }

    public Guid? DocumentFileId { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static MedicalExam Create(
        MedicalExamId id,
        Guid tenantId,
        EmployeeId employeeId,
        MedicalExamType type,
        DateOnly examDate,
        DateOnly today,
        string? provider,
        string? restrictions,
        string? recommendations,
        DateOnly? restrictionsUntil,
        DateTimeOffset now)
    {
        var exam = new MedicalExam { Id = id, TenantId = tenantId, EmployeeId = employeeId, CreatedAt = now };
        exam.Apply(type, examDate, today, provider, restrictions, recommendations, restrictionsUntil);
        exam.Version = 1;
        exam.UpdatedAt = now;
        return exam;
    }

    public void Update(
        MedicalExamType type,
        DateOnly examDate,
        DateOnly today,
        string? provider,
        string? restrictions,
        string? recommendations,
        DateOnly? restrictionsUntil,
        DateTimeOffset now)
    {
        Apply(type, examDate, today, provider, restrictions, recommendations, restrictionsUntil);
        Touch(now);
    }

    public void SetDocument(Guid? fileId, DateTimeOffset now)
    {
        DocumentFileId = fileId;
        Touch(now);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Everything is validated before anything is assigned, so a rejected update leaves the aggregate intact.
    private void Apply(
        MedicalExamType type,
        DateOnly examDate,
        DateOnly today,
        string? provider,
        string? restrictions,
        string? recommendations,
        DateOnly? restrictionsUntil)
    {
        if (examDate > today)
        {
            throw new HrDomainException(
                HrErrorCodes.MedicalExamFutureDate,
                "The exam date cannot be in the future.",
                new Dictionary<string, string[]> { ["examDate"] = ["La fecha del examen no puede ser futura."] });
        }

        var cleanRestrictions = Clean(restrictions);
        if (restrictionsUntil is { } until && (cleanRestrictions is null || until < examDate))
        {
            throw new HrDomainException(
                HrErrorCodes.MedicalExamRestrictionsUntilInvalid,
                "The restrictions end date needs restrictions and cannot be before the exam date.",
                new Dictionary<string, string[]>
                {
                    ["restrictionsUntil"] = ["La fecha solo aplica si hay restricciones y no puede ser anterior a la del examen."],
                });
        }

        Type = type;
        ExamDate = examDate;
        Provider = Clean(provider);
        Restrictions = cleanRestrictions;
        Recommendations = Clean(recommendations);
        RestrictionsUntil = restrictionsUntil;
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
