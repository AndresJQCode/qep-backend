namespace Modules.Suppliers.Domain;

/// <summary>
/// Una cuenta bancaria del proveedor: banco, número y moneda. Es un value object, no una
/// entidad: no existe fuera de su proveedor y el PUT la reemplaza entera, así que la
/// persistencia la mapea como colección **owned**. Copia de <c>CompanyBankAccount</c> con los
/// códigos de <c>suppliers.supplier.*</c>. Las propiedades son <c>init</c> y no posicionales
/// porque son tres <c>string</c> y, sueltos en una firma, nada impide intercambiarlos.
/// </summary>
public sealed record SupplierBankAccount
{
    public required string BankName { get; init; }

    public required string AccountNumber { get; init; }

    /// <summary>Código ISO 4217 de tres letras, en mayúsculas.</summary>
    public required string Currency { get; init; }

    public const int BankNameMaxLength = 120;

    public const int AccountNumberMaxLength = 32;

    public const int CurrencyLength = 3;

    /// <summary>
    /// Tope defensivo por proveedor: el cuerpo de un POST lo escribe el cliente, y sin límite
    /// una lista enorme se vuelve una transacción enorme. Cortarlo como 422 es barato.
    /// </summary>
    public const int MaxPerSupplier = 20;

    internal SupplierBankAccount Normalized() => new()
    {
        BankName = NormalizeRequired(
            BankName,
            BankNameMaxLength,
            "suppliers.supplier.bank_name_required",
            "The bank name is required.",
            "suppliers.supplier.bank_name_too_long",
            $"The bank name cannot exceed {BankNameMaxLength} characters."),

        // Se recorta pero no se pasa a mayúsculas: nada dice que "cta-1" y "CTA-1" sean la
        // misma cuenta.
        AccountNumber = NormalizeRequired(
            AccountNumber,
            AccountNumberMaxLength,
            "suppliers.supplier.account_number_required",
            "The supplier account number is required.",
            "suppliers.supplier.account_number_too_long",
            $"The supplier account number cannot exceed {AccountNumberMaxLength} characters."),

        Currency = NormalizeCurrency(Currency)
    };

    /// <summary>
    /// La clave con la que se detectan duplicados dentro de un mismo proveedor. El banco compara
    /// sin distinguir mayúsculas (es texto libre); el número sí las distingue, coherente con que
    /// tampoco se normalice su caja. Se calcula sobre valores ya normalizados.
    /// </summary>
    internal (string Bank, string Number, string Currency) DeduplicationKey() =>
        (BankName.ToLowerInvariant(), AccountNumber, Currency);

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
            throw new SuppliersDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new SuppliersDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    // Forma, no catálogo: el dominio no ve Currencies (Tenancy.Application); el catálogo lo hace
    // cumplir la capa de aplicación.
    private static string NormalizeCurrency(string? currency)
    {
        var trimmed = currency?.Trim();
        return trimmed is { Length: CurrencyLength } && trimmed.All(char.IsLetter)
            ? trimmed.ToUpperInvariant()
            : throw new SuppliersDomainException(
                "suppliers.supplier.currency_invalid",
                "The currency must be a three-letter ISO 4217 code.");
    }
}
