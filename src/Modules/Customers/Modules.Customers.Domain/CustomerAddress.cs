namespace Modules.Customers.Domain;

/// <summary>
/// Una direccion del cliente: a donde se le entrega. Entidad hija de <see cref="Customer"/> —sin
/// repositorio propio ni construccion fuera del agregado, mismo criterio que <c>PriceScale</c>
/// dentro de <c>Product</c>—, porque su unicidad ("una sola principal") es un invariante del
/// cliente, no de cada fila.
///
/// Reemplaza al par <c>address</c>/<c>city_id</c> que vivia en <c>customers</c>: un cliente tiene
/// una bodega, un local y la casa de quien recibe, y ese par solo alcanzaba para uno.
///
/// <para><b>La ciudad se guarda; el departamento no.</b> Es el de la ciudad, y guardarlo seria
/// una segunda verdad que puede contradecir a la primera. El formulario si lo muestra, porque el
/// combobox de ciudad filtra por el.</para>
/// </summary>
public sealed class CustomerAddress
{
    /// <summary>A quien pertenece la direccion ("Bodega Norte", "Casa de Ana"), no la calle. Es
    /// lo que la persona lee para elegir a donde entregar.</summary>
    public const int NameMaxLength = 120;

    public const int AddressMaxLength = 200;

    public const int PhoneMaxLength = 32;

    // EF Core materializa por aca. El codigo nunca construye la entidad asi: solo el agregado.
    private CustomerAddress()
    {
        Name = string.Empty;
        Address = string.Empty;
    }

    private CustomerAddress(
        CustomerAddressId id,
        CustomerId customerId,
        CustomerAddressDetails details,
        DateTimeOffset occurredAt)
    {
        Id = id;
        CustomerId = customerId;
        Name = string.Empty;
        Address = string.Empty;
        CreatedAt = occurredAt;
        Apply(details, occurredAt);
    }

    public CustomerAddressId Id { get; private set; }

    public CustomerId CustomerId { get; private set; }

    public string Name { get; private set; }

    public string Address { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>FK blanda al modulo <c>Geography</c>, con el mismo criterio que tenia
    /// <c>Customer.CityId</c>: <see cref="Guid"/> y no un id fuertemente tipado de otro dominio.
    /// </summary>
    public Guid CityId { get; private set; }

    /// <summary>La que la cotizacion propone por defecto. El agregado garantiza que haya
    /// exactamente una mientras el cliente tenga direcciones; la base lo respalda con un indice
    /// unico parcial.</summary>
    public bool IsPrincipal { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    internal static CustomerAddress Create(
        CustomerId customerId,
        CustomerAddressDetails details,
        DateTimeOffset occurredAt) =>
        new(CustomerAddressId.New(), customerId, details, occurredAt);

    internal void Apply(CustomerAddressDetails details, DateTimeOffset occurredAt)
    {
        var normalized = details.Normalized();

        Name = normalized.Name;
        Address = normalized.Address;
        Phone = normalized.Phone;
        CityId = normalized.CityId;
        UpdatedAt = occurredAt;
    }

    // Solo el agregado decide cual es la principal: hacerlo publico permitiria dejar dos, o
    // ninguna, sin que nadie lo note hasta que una cotizacion no sepa a donde entregar.
    internal void MarkPrincipal(bool isPrincipal, DateTimeOffset occurredAt)
    {
        if (IsPrincipal == isPrincipal)
        {
            return;
        }

        IsPrincipal = isPrincipal;
        UpdatedAt = occurredAt;
    }
}

/// <summary>Los datos de una direccion tal como entran al agregado. Value object: valida y
/// normaliza, misma relacion con <see cref="CustomerAddress"/> que <c>CustomerContactInfo</c> con
/// <c>Customer</c>.</summary>
public sealed record CustomerAddressDetails
{
    public required string Name { get; init; }

    public required string Address { get; init; }

    public required Guid CityId { get; init; }

    public string? Phone { get; init; }

    internal CustomerAddressDetails Normalized() => new()
    {
        Name = NormalizeRequired(
            Name,
            NameMaxLengthOf(),
            "customers.address.name_required",
            "The address name is required.",
            "customers.address.name_too_long",
            $"The address name cannot exceed {CustomerAddress.NameMaxLength} characters."),
        Address = NormalizeRequired(
            Address,
            CustomerAddress.AddressMaxLength,
            "customers.address.address_required",
            "The address is required.",
            "customers.address.address_too_long",
            $"The address cannot exceed {CustomerAddress.AddressMaxLength} characters."),
        CityId = CityId == Guid.Empty
            ? throw new CustomersDomainException(
                "customers.address.city_required", "The address city is required.")
            : CityId,
        Phone = NormalizeOptional(Phone)
    };

    private static int NameMaxLengthOf() => CustomerAddress.NameMaxLength;

    private static string NormalizeRequired(
        string? value,
        int maxLength,
        string requiredCode,
        string requiredMessage,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CustomersDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CustomersDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    // Vacio y ausente son lo mismo para un campo opcional, mismo criterio que
    // CustomerContactInfo.NormalizeOptional.
    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > CustomerAddress.PhoneMaxLength
            ? throw new CustomersDomainException(
                "customers.address.phone_too_long",
                $"The address phone cannot exceed {CustomerAddress.PhoneMaxLength} characters.")
            : trimmed;
    }
}
