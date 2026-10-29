namespace Modules.Billing.Domain;

public readonly record struct BillingServiceId(Guid Value)
{
    public static BillingServiceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
