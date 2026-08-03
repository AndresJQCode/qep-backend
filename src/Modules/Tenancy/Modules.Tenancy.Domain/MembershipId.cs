namespace Modules.Tenancy.Domain;

public readonly record struct MembershipId(Guid Value)
{
    public static MembershipId New() => new(Guid.CreateVersion7());

    public static MembershipId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}
