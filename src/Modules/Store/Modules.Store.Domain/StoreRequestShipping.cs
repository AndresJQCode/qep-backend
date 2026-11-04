namespace Modules.Store.Domain;

/// <summary>
/// El bloque de envío, opcional: sin él el pedido usa los datos de facturación. Sin correo propio:
/// al convertir toma el de facturación.
/// </summary>
public sealed record StoreRequestShipping
{
    // EF Core materializa por acá, por la misma razón que StoreRequestBilling.
    private StoreRequestShipping()
    {
        Phone = string.Empty;
        PhoneE164 = string.Empty;
        Address = string.Empty;
        Place = null!;
    }

    public StoreRequestShipping(
        string? recipientName,
        string phone,
        string phoneE164,
        string address,
        StorePlace place)
    {
        RecipientName = StoreText.Optional(recipientName, 120, "El destinatario");
        Phone = StoreText.Required(phone, 32, "El teléfono");
        PhoneE164 = StoreText.Required(phoneE164, 20, "El teléfono normalizado");
        Address = StoreText.Required(address, 200, "La dirección");
        Place = place ?? throw new ArgumentNullException(nameof(place));
    }

    public string? RecipientName { get; }

    public string Phone { get; }

    public string PhoneE164 { get; }

    public string Address { get; }

    public StorePlace Place { get; }

    /// <summary>El destinatario, o el nombre de facturación cuando el visitante lo dejó vacío.</summary>
    public string EffectiveRecipientName(StoreRequestBilling billing) => RecipientName ?? billing.Name;
}
