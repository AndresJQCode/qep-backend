namespace Modules.Customers.Domain;

/// <summary>
/// El tamano o segmento comercial del cliente. Opcional: el formulario lo deja vacio y nada en el
/// contrato obliga a clasificar a alguien al darlo de alta.
///
/// Los valores son los que ya ofrece <c>CLASSIFICATIONS</c> en <c>customer-form.schema.ts</c>.
/// </summary>
public enum CustomerClassification
{
    Pequeno,
    Mediano,
    Grande
}

public static class CustomerClassificationParser
{
    private static readonly Dictionary<string, CustomerClassification> ByWireValue =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PEQUENO"] = CustomerClassification.Pequeno,
            ["MEDIANO"] = CustomerClassification.Mediano,
            ["GRANDE"] = CustomerClassification.Grande
        };

    public static string ToWireValue(this CustomerClassification classification) =>
        classification switch
        {
            CustomerClassification.Pequeno => "PEQUENO",
            CustomerClassification.Mediano => "MEDIANO",
            CustomerClassification.Grande => "GRANDE",
            _ => throw new CustomersDomainException(
                "customers.customer.classification_invalid",
                "The classification is not one of the supported values.")
        };

    /// <summary>
    /// Vacio es ausente, como en todo campo opcional del proyecto: el formulario manda "" cuando
    /// el usuario no elige nada. Un valor presente pero desconocido si falla — es un dato mal
    /// escrito, no un dato ausente.
    /// </summary>
    public static CustomerClassification? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (ByWireValue.TryGetValue(value.Trim(), out var classification))
        {
            return classification;
        }

        throw new CustomersDomainException(
            "customers.customer.classification_invalid",
            "The classification must be one of PEQUENO, MEDIANO or GRANDE.");
    }

    public static IReadOnlyCollection<string> SupportedWireValues => ByWireValue.Keys;
}
