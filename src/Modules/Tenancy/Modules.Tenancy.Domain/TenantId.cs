namespace Modules.Tenancy.Domain;

public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.CreateVersion7());

    public static TenantId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}
