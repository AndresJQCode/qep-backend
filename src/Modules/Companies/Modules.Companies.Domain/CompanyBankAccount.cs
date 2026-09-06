namespace Modules.Companies.Domain;

/// <summary>
/// Una cuenta bancaria de la empresa: banco, moneda y numero.
///
/// Es un value object, no una entidad: una cuenta no existe fuera de su empresa y no tiene
/// identidad propia que alguien referencie desde afuera. Por eso el PUT la reemplaza entera en
/// vez de parchear fila por fila, y por eso la persistencia la mapea como coleccion **owned**.
///
/// Cuando la cotizacion necesito decir "a esta cuenta se paga", no se le dio identidad a este
/// tipo: <c>QuotationBillingAccount</c> **copia** banco, numero y moneda al guardarse. Una FK
/// haria que corregir un digito aca reescribiera cotizaciones ya enviadas, y una cotizacion es
/// un documento historico. Este tipo sigue sin id, a proposito.
///
/// Las propiedades son <c>init</c> y no posicionales, igual que en <see cref="CompanyContactInfo"/>:
/// <c>BankName</c>, <c>AccountNumber</c> y <c>Currency</c> son los tres <c>string</c>, y sueltos en
/// una firma posicional nada impide intercambiarlos. Un banco en la moneda compila sin una queja.
/// </summary>
public sealed record CompanyBankAccount
{
    public required string BankName { get; init; }

    public required string AccountNumber { get; init; }

    /// <summary>Codigo ISO 4217 de tres letras, en mayusculas.</summary>
    public required string Currency { get; init; }

    // Espejan los anchos de columna de companies.company_bank_accounts, con el mismo criterio que
    // el resto del modulo: un valor demasiado largo falla como 422 con codigo de dominio en vez de
    // llegar a PostgreSQL y volver como 500 server.unexpected.
    //
    // AccountNumberMaxLength conserva el 32 que tenia Company.AccountNumber antes de EMP-08. El
    // ancho no cambio; lo que cambio es donde vive la columna.
    public const int BankNameMaxLength = 120;

    public const int AccountNumberMaxLength = 32;

    public const int CurrencyLength = 3;

    /// <summary>
    /// Tope defensivo por empresa. No sale de un requisito: sale de que el cuerpo de un POST lo
    /// escribe el cliente, y sin limite una lista de diez mil cuentas se convierte en diez mil
    /// INSERT dentro de una sola transaccion. Cortarlo como 422 es barato; descubrirlo como
    /// timeout de la unidad de trabajo no lo es.
    /// </summary>
    public const int MaxPerCompany = 20;

    /// <summary>
    /// Normaliza y hace cumplir los invariantes de una cuenta. Lo llama <see cref="Company"/>; no
    /// es punto de entrada publico, del mismo modo que <c>Company.Create</c> es el unico que
    /// construye el agregado.
    /// </summary>
    internal CompanyBankAccount Normalized() => new()
    {
        BankName = NormalizeRequired(
            BankName,
            BankNameMaxLength,
            "companies.company.bank_name_required",
            "The bank name is required.",
            "companies.company.bank_name_too_long",
            $"The bank name cannot exceed {BankNameMaxLength} characters."),

        // Se recorta pero **no** se pasa a mayusculas, que es exactamente lo que hacia
        // Company.AccountNumber antes de este slice. Seria defendible, pero nada en el frontend ni
        // en los requisitos dice que "cta-1" y "CTA-1" sean la misma cuenta, y el precedente del
        // modulo vecino —Product.Code— tampoco lo hace.
        AccountNumber = NormalizeRequired(
            AccountNumber,
            AccountNumberMaxLength,
            "companies.company.account_number_required",
            "The company account number is required.",
            "companies.company.account_number_too_long",
            $"The company account number cannot exceed {AccountNumberMaxLength} characters."),

        Currency = NormalizeCurrency(Currency)
    };

    /// <summary>
    /// La clave con la que se detectan duplicados dentro de una misma empresa.
    ///
    /// El nombre del banco compara sin distinguir mayusculas porque es texto libre que escribe una
    /// persona: "Bancolombia" y "bancolombia" son el mismo banco, y tratarlos como distintos deja
    /// entrar el duplicado que este invariante existe para frenar. El numero **si** distingue,
    /// coherente con que tampoco se normalice su caja al guardarlo: colapsarlo aca y no alla
    /// significaria rechazar como repetidas dos cuentas que la base guarda como distintas.
    ///
    /// Se calcula sobre valores ya normalizados. Sobre los crudos, " CTA-1" y "CTA-1" pasarian
    /// como dos cuentas.
    /// </summary>
    internal (string Bank, string Number, string Currency) DeduplicationKey() =>
        (BankName.ToLowerInvariant(), AccountNumber, Currency);

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
            throw new CompaniesDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CompaniesDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    // Tres letras y a mayusculas, igual que ProductDetails en catalogo. No se valida contra una
    // tabla de monedas a proposito: mantener esa tabla al dia es un problema propio, y ningun
    // requisito dice cuales acepta el producto. Lo que si se rechaza es lo que evidentemente no es
    // un codigo ISO 4217.
    private static string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new CompaniesDomainException(
                "companies.company.currency_invalid",
                "The currency must be a three-letter ISO 4217 code.");
        }

        var trimmed = currency.Trim();
        return trimmed.Length != CurrencyLength || !trimmed.All(char.IsLetter)
            ? throw new CompaniesDomainException(
                "companies.company.currency_invalid",
                "The currency must be a three-letter ISO 4217 code.")
            : trimmed.ToUpperInvariant();
    }
}
