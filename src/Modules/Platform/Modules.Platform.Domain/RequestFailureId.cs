namespace Modules.Platform.Domain;

public readonly record struct RequestFailureId(Guid Value)
{
    public static RequestFailureId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
