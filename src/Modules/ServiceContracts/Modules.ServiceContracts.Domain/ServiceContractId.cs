namespace Modules.ServiceContracts.Domain;

public readonly record struct ServiceContractId(Guid Value)
{
    public static ServiceContractId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
