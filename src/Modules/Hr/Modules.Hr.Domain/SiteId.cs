namespace Modules.Hr.Domain;

public readonly record struct SiteId(Guid Value)
{
    public static SiteId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
