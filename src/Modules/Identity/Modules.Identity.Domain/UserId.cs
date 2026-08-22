namespace Modules.Identity.Domain;

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.CreateVersion7());

    public static UserId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}
