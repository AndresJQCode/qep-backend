namespace Modules.Support.Domain;

/// <summary>Foto del solicitante tomada al radicar (plan D7, spec §9.3). No sigue al cliente vinculado: si el cliente cambia, el
/// ticket conserva lo que dijo el solicitante. La ciudad (Geography) entra en la fase 3.</summary>
public sealed record TicketRequester(
    string? FirstNames,
    string? LastNames,
    string? BusinessName,
    string? DocumentType,
    string? DocumentNumber,
    string? Email,
    string? MobilePhone,
    string? LandlinePhone,
    string CountryCode,
    string? Address)
{
    public const string NitDocumentType = "NIT";

    /// <summary>Plan D6: hay nombre si vienen los nombres, o la razón social con NIT (spec §9.3: sólo el NIT cambia los nombres
    /// por la razón social).</summary>
    public bool HasName =>
        !string.IsNullOrWhiteSpace(FirstNames) ||
        (IsNit && !string.IsNullOrWhiteSpace(BusinessName));

    private bool IsNit => string.Equals(DocumentType?.Trim(), NitDocumentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Regla de <c>RequesterDisplayName</c>: nombres y apellidos; si no, razón social; si no, «NIT &lt;número&gt;» (spec
    /// §9.3, razón social opcional). Sin nada de eso, vacío.</summary>
    public string DisplayName()
    {
        if (!string.IsNullOrWhiteSpace(FirstNames))
        {
            return string.IsNullOrWhiteSpace(LastNames) ? FirstNames.Trim() : $"{FirstNames.Trim()} {LastNames.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(BusinessName))
        {
            return BusinessName.Trim();
        }

        return IsNit && !string.IsNullOrWhiteSpace(DocumentNumber) ? $"{NitDocumentType} {DocumentNumber.Trim()}" : string.Empty;
    }
}
