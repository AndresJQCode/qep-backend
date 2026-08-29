namespace Modules.Authorization.Domain;

public readonly record struct RoleId(Guid Value)
{
    public static RoleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
