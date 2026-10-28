namespace Modules.Customers.Domain;

/// <summary>
/// El tipo de persona del cliente para facturar en Colombia (spec 2026-10-26). Nulo en el cliente es
/// «no se sabe»: por eso el enum no tiene un valor «Ninguno» y la propiedad es <c>PersonType?</c>.
/// El texto del contrato va sin tilde (<c>Juridica</c>), igual que la columna.
/// </summary>
public enum PersonType
{
    Natural,
    Juridica
}

public static class PersonTypeParser
{
    private static readonly Dictionary<string, PersonType> ByWireValue =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Natural"] = PersonType.Natural,
            ["Juridica"] = PersonType.Juridica
        };

    public static string ToWireValue(this PersonType type) => type switch
    {
        PersonType.Natural => "Natural",
        PersonType.Juridica => "Juridica",
        _ => throw new CustomersDomainException(
            "customers.customer.person_type_invalid",
            "The person type is not one of the supported values.")
    };

    /// <summary>
    /// En blanco es «sin tipo» (<c>null</c>); un texto que no se reconoce **falla** en vez de caer en un
    /// default, por lo mismo que <see cref="IdentificationTypeParser.Parse"/>.
    /// </summary>
    public static PersonType? ParseOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (ByWireValue.TryGetValue(value.Trim(), out var type))
        {
            return type;
        }

        throw new CustomersDomainException(
            "customers.customer.person_type_invalid",
            "The person type must be Natural or Juridica.");
    }

    public static IReadOnlyCollection<string> SupportedWireValues => ByWireValue.Keys;
}
