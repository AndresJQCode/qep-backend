namespace Modules.Hr.Domain;

public readonly record struct MedicalExamId(Guid Value)
{
    public static MedicalExamId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
