namespace Modules.Suppliers.Domain;

/// <summary>
/// Una persona de contacto del proveedor (quien atiende compras, quien cobra). Value object como
/// <see cref="SupplierBankAccount"/>: no existe fuera de su proveedor y se reemplaza entera con
/// el PUT. Sólo el nombre es obligatorio; rol, teléfono y correo vacíos se guardan como null.
/// </summary>
public sealed record SupplierContact
{
    public required string Name { get; init; }

    public string? Role { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public const int NameMaxLength = 120;

    public const int RoleMaxLength = 80;

    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    /// <summary>Tope defensivo por proveedor, con el mismo criterio que las cuentas bancarias.</summary>
    public const int MaxPerSupplier = 10;

    public static SupplierContact Create(string? name, string? role, string? phone, string? email)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.contact_name_required",
                "The contact name is required.");
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > NameMaxLength)
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.contact_name_too_long",
                $"The contact name cannot exceed {NameMaxLength} characters.");
        }

        var trimmedEmail = Optional(
            email,
            EmailMaxLength,
            "suppliers.supplier.contact_email_invalid",
            $"The contact email cannot exceed {EmailMaxLength} characters.");
        if (trimmedEmail is not null && !SupplierContactInfo.IsPlausibleEmail(trimmedEmail))
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.contact_email_invalid",
                "The contact email is not a valid address.");
        }

        return new SupplierContact
        {
            Name = trimmedName,
            Role = Optional(
                role,
                RoleMaxLength,
                "suppliers.supplier.contact_role_too_long",
                $"The contact role cannot exceed {RoleMaxLength} characters."),
            Phone = Optional(
                phone,
                PhoneMaxLength,
                "suppliers.supplier.contact_phone_too_long",
                $"The contact phone cannot exceed {PhoneMaxLength} characters."),
            Email = trimmedEmail?.ToLowerInvariant()
        };
    }

    // Quien construye el record con `new` o `with` también pasa por las reglas: Supplier vuelve a
    // normalizar cada contacto antes de guardarlo.
    internal SupplierContact Normalized() => Create(Name, Role, Phone, Email);

    private static string? Optional(string? value, int maxLength, string tooLongCode, string tooLongMessage)
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
}
