namespace Modules.Messaging.Application;

/// <summary>Una variable que el sistema sabe llenar: la clave que va entre llaves dobles, su etiqueta para la pantalla y un ejemplo para Meta.</summary>
public sealed record SystemTemplateVariable(string Key, string Label, string Example);

/// <summary>Las listas cerradas de variables de las plantillas del sistema, una por tipo. Las cuatro fijas van primero en todas.
/// La autoridad de las fijas es <c>FIXED_VARIABLE_DETAILS</c> en el frontend (<c>src/components/whatsapp-templates/message-template.ts</c>);
/// aquí viven copiadas porque son dos repositorios, y se cambian juntas. Los ejemplos no llevan <c>$</c>, <c>#</c> ni <c>%</c>: Meta los rechaza.</summary>
public static class SystemTemplateVariables
{
    public const string ContactName = "nombre_contacto";
    public const string Advisor = "asesor";
    public const string Company = "empresa";
    public const string Greeting = "saludo";

    private static readonly SystemTemplateVariable[] Fixed =
    [
        new(ContactName, "Nombre del contacto", "María González"),
        new(Advisor, "Nombre del asesor", "Carlos Ramírez"),
        new(Company, "Nombre de la empresa", "Mi Empresa"),
        new(Greeting, "Saludo según la hora", "Buenos días"),
    ];

    public static IReadOnlyList<SystemTemplateVariable> Quotation { get; } =
    [
        .. Fixed,
        new("numero_cotizacion", "Número de cotización", "COT-000123"),
        new("valor_total", "Valor total", "2.450.000 COP"),
        new("vigencia", "Vigencia", "30 de septiembre de 2026"),
    ];

    public static IReadOnlyList<SystemTemplateVariable> BillingReminder { get; } =
    [
        .. Fixed,
        new("servicio", "Servicio", "Hosting plan pro"),
        new("valor", "Valor", "150.000 COP"),
        new("vencimiento", "Vencimiento", "10/03/2026"),
    ];

    public static IReadOnlyList<SystemTemplateVariable> BillingOverdue { get; } =
    [
        .. Fixed,
        new("cantidad_cobros", "Cantidad de cobros", "2"),
        new("total_vencido", "Total vencido", "450.000 COP"),
    ];

    /// <summary>Las claves viejas de la plantilla de cotización y la clave nueva que las reemplaza.</summary>
    public static IReadOnlyDictionary<string, string> LegacyKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fullname"] = ContactName,
        ["order_number"] = "numero_cotizacion",
        ["total"] = "valor_total",
        ["valid_until"] = "vigencia",
    };

    public static IReadOnlySet<string> KeysOf(IReadOnlyList<SystemTemplateVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return variables.Select(variable => variable.Key).ToHashSet(StringComparer.Ordinal);
    }
}
