namespace Modules.Siigo.Domain;

/// <summary>
/// Spec 2026-11-18 §5.2: <c>QuotationBillingAccount</c> no trae id de cuenta, así que el vínculo con la forma de pago de Siigo se
/// hace por banco y número normalizados: mayúsculas, sin espacios, guiones ni puntos. El <c>|</c> entre los dos evita que «AB» + «1»
/// choque con «A» + «B1». Si se renombra la cuenta en la empresa, la clave cambia y el vínculo hay que rehacerlo (la pantalla lo
/// deja vincular, con el texto viejo, mientras haya pedidos con la foto anterior).
/// </summary>
public static class BankAccountKey
{
    public const int MaxLength = 300;

    private const char Separator = '|';

    /// <summary>La clave, o <c>null</c> si falta el banco o el número (o quedan vacíos tras normalizar).</summary>
    public static string? Normalize(string? bankName, string? accountNumber)
    {
        var bank = Clean(bankName);
        var number = Clean(accountNumber);
        return bank.Length == 0 || number.Length == 0 ? null : string.Concat(bank, Separator, number);
    }

    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : string.Concat(value.Where(character => !char.IsWhiteSpace(character) && character is not ('-' or '.' or Separator)))
                .ToUpperInvariant();
}
