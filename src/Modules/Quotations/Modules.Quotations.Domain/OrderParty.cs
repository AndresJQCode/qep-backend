namespace Modules.Quotations.Domain;

/// <summary>La parte de facturación o envío del pedido, copia de <see cref="QuotationParty"/> con las
/// mismas reglas de normalización y de completitud (spec 2026-10-15 §4.2).</summary>
public sealed class OrderParty
{
    private OrderParty()
    {
    }

    private OrderParty(OrderPartyId id, OrderId orderId, QuotationPartyRole role, QuotationPartyDetails details)
    {
        Id = id;
        OrderId = orderId;
        Role = role;
        Apply(details);
    }

    public OrderPartyId Id { get; private set; }

    public OrderId OrderId { get; private set; }

    public QuotationPartyRole Role { get; private set; }

    public string? Name { get; private set; }

    public string? IdentificationNumber { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Address { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public Guid? CityId { get; private set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(Phone)
        && !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(Address)
        && DepartmentId is not null
        && CityId is not null;

    public static OrderParty Create(OrderId orderId, QuotationPartyRole role, QuotationPartyDetails details) =>
        new(OrderPartyId.New(), orderId, role, details);

    public QuotationPartyDetails ToDetails() => new()
    {
        Name = Name,
        IdentificationNumber = IdentificationNumber,
        Phone = Phone,
        Email = Email,
        Address = Address,
        DepartmentId = DepartmentId,
        CityId = CityId,
    };

    public void Apply(QuotationPartyDetails details)
    {
        var normalized = (Role == QuotationPartyRole.Billing
            ? details
            : details with { IdentificationNumber = null }).Normalized();
        Name = normalized.Name;
        IdentificationNumber = normalized.IdentificationNumber;
        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
        DepartmentId = normalized.DepartmentId;
        CityId = normalized.CityId;
    }
}
