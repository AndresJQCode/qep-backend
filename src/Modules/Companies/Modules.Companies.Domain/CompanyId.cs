namespace Modules.Companies.Domain;

public readonly record struct CompanyId(Guid Value)
{
    public static CompanyId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
