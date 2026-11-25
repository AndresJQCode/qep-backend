namespace Modules.Quotations.Domain;

/// <summary>La parte de facturación o envío del pedido, copia de <see cref="QuotationParty"/> con las
/// mismas reglas de normalización y de completitud (spec 2026-10-15 §4.2).</summary>
public sealed class OrderParty
{
    private OrderParty()
    {
    }

    private OrderParty(
        OrderPartyId id, OrderId orderId, QuotationPartyRole role, QuotationPartyDetails details, string? fieldPrefix)
    {
        Id = id;
        OrderId = orderId;
        Role = role;
        Apply(details, fieldPrefix);
    }

    public OrderPartyId Id { get; private set; }

    public OrderId OrderId { get; private set; }

    public QuotationPartyRole Role { get; private set; }

    public string? Name { get; private set; }

    public string? IdentificationNumber { get; private set; }

    /// <summary>Natural o jurídica; siempre null en la parte de entrega (spec 2026-10-26).</summary>
    public PartyPersonType? PersonType { get; private set; }

    /// <summary>Tipo del <see cref="IdentificationNumber"/>; siempre null en la parte de entrega.</summary>
    public PartyIdentificationType? IdentificationType { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Address { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public Guid? CityId { get; private set; }

    /// <summary>ISO alfa-2; <c>null</c> es Colombia (ver <see cref="QuotationPartyDetails.Country"/>).</summary>
    public string? Country { get; private set; }

    /// <summary>La ciudad como texto, sólo fuera de Colombia.</summary>
    public string? CityName { get; private set; }

    /// <summary>The region of a party outside Colombia; see <see cref="QuotationPartyDetails.RegionId"/>.</summary>
    public Guid? RegionId { get; private set; }

    /// <summary>The postal code of the delivery party; see <see cref="QuotationPartyDetails.PostalCode"/>.</summary>
    public string? PostalCode { get; private set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(Phone)
        && !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(Address)
        && new QuotationPartyDetails
        {
            Country = Country,
            DepartmentId = DepartmentId,
            CityId = CityId,
            CityName = CityName
        }.HasCompleteCity;

    /// <param name="fieldPrefix">Con qué clave salen los errores de la parte en el mapa <c>errors</c>; null es
    /// <c>Parties.{rol}</c>, la forma del cuerpo del alta. La entrega editable usa <c>Delivery.Shipping</c>.</param>
    public static OrderParty Create(
        OrderId orderId, QuotationPartyRole role, QuotationPartyDetails details, string? fieldPrefix = null) =>
        new(OrderPartyId.New(), orderId, role, details, fieldPrefix);

    public QuotationPartyDetails ToDetails() => new()
    {
        Name = Name,
        IdentificationNumber = IdentificationNumber,
        PersonType = PersonType,
        IdentificationType = IdentificationType,
        Phone = Phone,
        Email = Email,
        Address = Address,
        DepartmentId = DepartmentId,
        CityId = CityId,
        Country = Country,
        CityName = CityName,
        RegionId = RegionId,
        PostalCode = PostalCode,
    };

    public void Apply(QuotationPartyDetails details, string? fieldPrefix = null)
    {
        var normalized = (Role == QuotationPartyRole.Billing
            ? details with { PostalCode = null }
            : details with { IdentificationNumber = null, PersonType = null, IdentificationType = null })
            .Normalized(fieldPrefix ?? $"Parties.{Role}");
        Name = normalized.Name;
        IdentificationNumber = normalized.IdentificationNumber;
        PersonType = normalized.PersonType;
        IdentificationType = normalized.IdentificationType;
        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
        DepartmentId = normalized.DepartmentId;
        CityId = normalized.CityId;
        Country = normalized.Country;
        CityName = normalized.CityName;
        RegionId = normalized.RegionId;
        PostalCode = normalized.PostalCode;
    }
}
