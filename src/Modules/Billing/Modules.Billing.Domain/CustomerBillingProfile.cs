namespace Modules.Billing.Domain;

/// <summary>Por dónde se le avisa a un cliente y quién del equipo es su encargado. Uno por cliente.</summary>
public sealed class CustomerBillingProfile
{
    private CustomerBillingProfile()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid CustomerId { get; private set; }

    public BillingChannel Channel { get; private set; }

    public Guid AssigneeMemberId { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static CustomerBillingProfile Create(
        Guid tenantId, Guid customerId, BillingChannel channel, Guid assigneeMemberId, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        CustomerId = customerId,
        Channel = channel,
        AssigneeMemberId = assigneeMemberId,
        Version = 1,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Change(BillingChannel channel, Guid assigneeMemberId, DateTimeOffset now)
    {
        Channel = channel;
        AssigneeMemberId = assigneeMemberId;
        Version++;
        UpdatedAt = now;
    }
}
