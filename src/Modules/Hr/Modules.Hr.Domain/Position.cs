namespace Modules.Hr.Domain;

public sealed class Position : OrganizationSetting
{
    private Position()
    {
    }

    public PositionId Id { get; private set; }

    /// <summary>
    /// How often the periodic medical exam is due for whoever holds this position (spec 2026-11-23 §5.2), from 1 to 36 months;
    /// <c>null</c> means the position carries no periodic exam. Written only by <see cref="SetExamFrequency"/>, never by a rename.
    /// </summary>
    public int? ExamFrequencyMonths { get; private set; }

    public const int MinExamFrequencyMonths = 1;

    public const int MaxExamFrequencyMonths = 36;

    public static Position Create(PositionId id, Guid tenantId, string name, DateTimeOffset now)
    {
        var setting = new Position { Id = id };
        setting.Initialize(tenantId, name, now);
        return setting;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        SetName(name);
        Touch(now);
    }

    public void SetExamFrequency(int? months, DateTimeOffset now)
    {
        if (months is < MinExamFrequencyMonths or > MaxExamFrequencyMonths)
        {
            throw new HrDomainException(
                HrErrorCodes.ExamFrequencyInvalid,
                $"The exam frequency must be between {MinExamFrequencyMonths} and {MaxExamFrequencyMonths} months.");
        }

        ExamFrequencyMonths = months;
        Touch(now);
    }
}
