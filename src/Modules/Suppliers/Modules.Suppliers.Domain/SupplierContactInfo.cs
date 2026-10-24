namespace Modules.Suppliers.Domain;

/// <summary>
/// Cómo se localiza al proveedor: teléfono, correo, dirección y país. Teléfono, correo y
/// dirección son **opcionales** (un proveedor puede cargarse sólo con su documento); el país es
/// obligatorio porque decide qué carril de ciudad aplica (ver <see cref="Supplier"/>).
///
/// Copia de <c>CustomerContactInfo</c> sin su ciudad: en proveedores la ciudad vive en el
/// agregado, junto a la regla que la liga al país.
/// </summary>
public sealed record SupplierContactInfo
{
    public string? Phone { get; init; }

    public string? Email { get; init; }

    public string? Address { get; init; }

    /// <summary>ISO-3166-1 alpha-2 en mayúsculas.</summary>
    public required string Country { get; init; }

    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 200;

    public const int CountryLength = 2;

    internal SupplierContactInfo Normalized() => new()
    {
        Phone = NormalizeOptional(
            Phone,
            PhoneMaxLength,
            "suppliers.supplier.phone_too_long",
            $"The supplier phone cannot exceed {PhoneMaxLength} characters."),
        Email = NormalizeEmail(Email),
        Address = NormalizeOptional(
            Address,
            AddressMaxLength,
            "suppliers.supplier.address_too_long",
            $"The supplier address cannot exceed {AddressMaxLength} characters."),
        Country = NormalizeCountry(Country)
    };

    // A mayúsculas por la misma razón que el correo va a minúsculas: "co" y "CO" son el mismo
    // país, y dejar las dos formas rompe en silencio la comparación con Supplier.ColombiaCountryCode.
    private static string NormalizeCountry(string? country)
    {
        var trimmed = country?.Trim();
        return trimmed is { Length: CountryLength } && trimmed.All(char.IsAsciiLetter)
            ? trimmed.ToUpperInvariant()
            : throw new SuppliersDomainException(
                "suppliers.supplier.country_invalid",
                $"The supplier country must be an ISO-3166-1 alpha-2 code ({CountryLength} letters).");
    }

    // Vacío y ausente son lo mismo: el formulario manda "" cuando se borra el input, y eso se
    // guarda como null, no como cadena vacía.
    private static string? NormalizeOptional(
        string? value,
        int maxLength,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new SuppliersDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    private static string? NormalizeEmail(string? email)
    {
        var trimmed = NormalizeOptional(
            email,
            EmailMaxLength,
            "suppliers.supplier.email_too_long",
            $"The supplier email cannot exceed {EmailMaxLength} characters.");

        return trimmed is null
            ? null
            : IsPlausibleEmail(trimmed)
                ? trimmed.ToLowerInvariant()
                : throw new SuppliersDomainException(
                    "suppliers.supplier.email_invalid",
                    "The supplier email is not a valid address.");
    }

    // Comprobación estructural, no una expresión regular: la sintaxis completa de RFC 5322 no se
    // valida con un patrón sin abrir la puerta al backtracking catastrófico; acá sólo se rechaza lo
    // que evidentemente no es una dirección. Copiado de CustomerContactInfo a propósito.
    internal static bool IsPlausibleEmail(string value)
    {
        if (value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != value.LastIndexOf('@'))
        {
            return false;
        }

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && dot < domain.Length - 1;
    }
}
