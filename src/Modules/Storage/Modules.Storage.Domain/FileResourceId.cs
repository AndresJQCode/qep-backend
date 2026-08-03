namespace Modules.Storage.Domain;

public readonly record struct FileResourceId(Guid Value)
{
    public static FileResourceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
