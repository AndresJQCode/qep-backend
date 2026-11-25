namespace Modules.Hr.Domain;

public readonly record struct ExitExamRequirementId(Guid Value)
{
    public static ExitExamRequirementId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
