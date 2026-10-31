namespace Modules.ApiAccess.Domain;

public readonly record struct ApiKeyId(Guid Value)
{
    public static ApiKeyId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
