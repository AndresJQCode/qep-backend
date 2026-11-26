namespace Modules.Hr.Domain;

public readonly record struct ContractRenewalId(Guid Value)
{
    public static ContractRenewalId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
