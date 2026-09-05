namespace Modules.Quotations.Domain;

/// <summary>
/// Los datos de una parte tal como entran al agregado (<see cref="Quotation.Create"/> /
/// <see cref="Quotation.UpdateDetails"/>). Value object, misma relación con
/// <see cref="QuotationParty"/> que <c>CustomerContactInfo</c> con <c>Customer</c>: la entidad
/// expone los campos planos, esto es la forma de entrada y quien normaliza.
/// </summary>
public sealed record QuotationPartyDetails
{
    public string? Name { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public string? Address { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? CityId { get; init; }

    // Mismos topes que Customer para los campos que son el mismo dato: una parte es una copia
    // puntual de la ficha del cliente, no puede aceptar más de lo que la ficha acepta.
    public const int NameMaxLength = 255;

    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 255;

    internal QuotationPartyDetails Normalized() => new()
    {
        Name = NormalizeOptional(
            Name,
            NameMaxLength,
            "quotation.party.name_too_long",
            $"The party name cannot exceed {NameMaxLength} characters."),
        Phone = NormalizeOptional(
            Phone,
            PhoneMaxLength,
            "quotation.party.phone_too_long",
            $"The party phone cannot exceed {PhoneMaxLength} characters."),
        Email = NormalizeOptional(
            Email,
            EmailMaxLength,
            "quotation.party.email_too_long",
            $"The party email cannot exceed {EmailMaxLength} characters."),
        Address = NormalizeOptional(
            Address,
            AddressMaxLength,
            "quotation.party.address_too_long",
            $"The party address cannot exceed {AddressMaxLength} characters."),
        DepartmentId = NormalizeOptionalId(DepartmentId),
        CityId = NormalizeOptionalId(CityId)
    };

    // Vacio y ausente son lo mismo para un campo opcional, mismo criterio que
    // CustomerContactInfo.NormalizeOptional.
    private static string? NormalizeOptional(
        string? value, int maxLength, string tooLongCode, string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new QuotationsDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    // Guid.Empty llega de un formulario que mandó el select vacío: es "sin elegir", no un id.
    private static Guid? NormalizeOptionalId(Guid? value) =>
        value is null || value == Guid.Empty ? null : value;
}

/// <summary>
/// Las dos partes de una cotización tal como entran al agregado. <b>Null es el caso normal</b>:
/// "esta cotización factura (o entrega) a los datos del cliente", que es exactamente lo que
/// muestra el switch encendido de la UI. <see cref="Quotation.UpdateDetails"/> reemplaza el
/// recurso entero, así que un null que antes tenía fila la borra.
/// </summary>
public sealed record QuotationParties(
    QuotationPartyDetails? Billing,
    QuotationPartyDetails? Shipping)
{
    /// <summary>Las dos partes tomadas del cliente: ninguna fila. El estado por defecto de una
    /// cotización nueva.</summary>
    public static QuotationParties Empty { get; } = new(null, null);
}
