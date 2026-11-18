namespace Modules.Wholesale.Domain;

public readonly record struct DistributorId(Guid Value)
{
    public static DistributorId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
