namespace Modules.Store.Domain;

/// <summary>El bloque de facturación del pedido, completo y congelado tal como lo escribió el visitante.</summary>
public sealed record StoreRequestBilling
{
    // Los mismos cuatro tipos del cliente (IdentificationType) y de las partes del pedido.
    public const int NameMaxLength = 150;

    public const int IdentificationNumberMaxLength = 32;

    public const int EmailMaxLength = 254;

    private static readonly string[] IdentificationTypes = ["NIT", "CC", "CE", "PASAPORTE"];

    // EF Core materializa por acá: no puede enlazar el lugar (otro tipo mapeado) a un parámetro del constructor validador, y los datos
    // que lee ya pasaron por él al guardarse.
    private StoreRequestBilling()
    {
        Name = string.Empty;
        IdentificationType = string.Empty;
        IdentificationNumber = string.Empty;
        Phone = string.Empty;
        PhoneE164 = string.Empty;
        Email = string.Empty;
        Address = string.Empty;
        Place = null!;
    }

    public StoreRequestBilling(
        string name,
        string identificationType,
        string identificationNumber,
        string phone,
        string phoneE164,
        string email,
        string address,
        StorePlace place)
    {
        var type = StoreText.Required(identificationType, 16, "El tipo de documento").ToUpperInvariant();
        if (!IdentificationTypes.Contains(type, StringComparer.Ordinal))
        {
            throw new StoreDomainException(StoreErrorCodes.ValidationFailed, "El tipo de documento no es válido.");
        }

        Name = StoreText.Required(name, NameMaxLength, "El nombre");
        IdentificationType = type;
        IdentificationNumber = StoreText.Required(identificationNumber, IdentificationNumberMaxLength, "El documento");
        Phone = StoreText.Required(phone, 32, "El teléfono");
        PhoneE164 = StoreText.Required(phoneE164, 20, "El teléfono normalizado");
        Email = StoreText.Required(email, EmailMaxLength, "El correo");
        Address = StoreText.Required(address, 200, "La dirección");
        Place = place ?? throw new ArgumentNullException(nameof(place));
    }

    public string Name { get; }

    public string IdentificationType { get; }

    public string IdentificationNumber { get; }

    public string Phone { get; }

    public string PhoneE164 { get; }

    public string Email { get; }

    public string Address { get; }

    public StorePlace Place { get; }
}
