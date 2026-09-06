namespace Modules.Quotations.Domain;

/// <summary>
/// La empresa y la cuenta con la que se factura esta cotización, congeladas al guardarla.
///
/// <b>Copia, no referencia</b>, el mismo criterio que <see cref="QuotationParty"/>: la cotización
/// es un documento histórico que ya salió en un PDF y por WhatsApp, y la empresa puede cerrar esa
/// cuenta mañana. Guardar una FK haría que corregir un dígito en la ficha de la empresa
/// reescribiera cotizaciones ya enviadas. (Además, <c>CompanyBankAccount</c> es un value object
/// sin identidad propia — no hay a qué apuntar; ver su propio comentario.)
///
/// <see cref="CompanyId"/> sí queda como referencia blanda, para poder decir "a nombre de quién"
/// —razón social y NIT de hoy— aunque el número de cuenta haya cambiado. Sin FK, como toda
/// referencia entre módulos.
///
/// Nulo es válido y es el estado inicial: una cotización en borrador todavía no eligió con qué
/// cuenta se cobra, y <c>EnsureSendable</c> no lo exige a propósito. Quien sí lo exige es
/// <c>EnsureConvertibleToSale</c>: una venta tiene que decir a dónde se paga.
/// </summary>
public sealed record QuotationBillingAccount
{
    public required Guid CompanyId { get; init; }

    public required string BankName { get; init; }

    public required string AccountNumber { get; init; }

    /// <summary>Código ISO 4217 de tres letras, en mayúsculas.</summary>
    public required string Currency { get; init; }

    // Espejan los anchos de companies.company_bank_accounts: esto es una copia de esa fila y no
    // puede aceptar más de lo que el original acepta.
    public const int BankNameMaxLength = 120;

    public const int AccountNumberMaxLength = 32;

    public const int CurrencyLength = 3;

    internal QuotationBillingAccount Normalized() => new()
    {
        CompanyId = CompanyId == Guid.Empty
            ? throw new QuotationsDomainException(
                "quotation.billing.company_required",
                "The billing company is required.")
            : CompanyId,
        BankName = NormalizeRequired(
            BankName,
            BankNameMaxLength,
            "quotation.billing.bank_name_required",
            "The billing bank name is required.",
            "quotation.billing.bank_name_too_long",
            $"The billing bank name cannot exceed {BankNameMaxLength} characters."),
        AccountNumber = NormalizeRequired(
            AccountNumber,
            AccountNumberMaxLength,
            "quotation.billing.account_number_required",
            "The billing account number is required.",
            "quotation.billing.account_number_too_long",
            $"The billing account number cannot exceed {AccountNumberMaxLength} characters."),
        Currency = NormalizeCurrency(Currency)
    };

    private static string NormalizeRequired(
        string value,
        int maxLength,
        string requiredCode,
        string requiredMessage,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new QuotationsDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new QuotationsDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    private static string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new QuotationsDomainException(
                "quotation.billing.currency_invalid",
                "The billing currency must be a three-letter ISO 4217 code.");
        }

        var trimmed = currency.Trim();
        return trimmed.Length != CurrencyLength || !trimmed.All(char.IsLetter)
            ? throw new QuotationsDomainException(
                "quotation.billing.currency_invalid",
                "The billing currency must be a three-letter ISO 4217 code.")
            : trimmed.ToUpperInvariant();
    }
}
