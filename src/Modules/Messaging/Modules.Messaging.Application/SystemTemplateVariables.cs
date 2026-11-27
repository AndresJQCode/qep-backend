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

    /// <summary>Spec 2026-11-09 D2: el pedido no tiene vigencia. La clave es <c>numero_pedido</c> y no <c>order_number</c>, que
    /// <see cref="LegacyKeys"/> traduce a <c>numero_cotizacion</c> en toda plantilla.</summary>
    public static IReadOnlyList<SystemTemplateVariable> Order { get; } =
    [
        .. Fixed,
        new("numero_pedido", "Número de pedido", "PED-000123"),
        new("valor_total", "Valor total", "2.450.000 COP"),
    ];

    /// <summary>Spec 2026-11-21: la factura de Siigo. Lleva <c>numero_factura</c> (el número legal de la factura, no el del pedido) y el total
    /// de esa factura; sin vigencia, como el pedido.</summary>
    public static IReadOnlyList<SystemTemplateVariable> Invoice { get; } =
    [
        .. Fixed,
        new("numero_factura", "Número de factura", "FV-1-000123"),
        new("valor_total", "Valor total", "2.450.000 COP"),
    ];

    /// <summary>Soporte fase 2: la plantilla «Aviso de respuesta». <c>respuesta</c> es el texto del asesor, así que al enviar tiene que
    /// cumplir <c>TemplateParameterRules</c> (sin saltos de línea).</summary>
    public static IReadOnlyList<SystemTemplateVariable> SupportReply { get; } =
    [
        .. Fixed,
        new("radicado", "Radicado del caso", "SOP-000127"),
        new("respuesta", "Respuesta del asesor", "Revisamos tu solicitud y ya quedó resuelta."),
    ];

    /// <summary>Soporte fase 4b (H10): la plantilla «Respuesta formal» lleva las mismas variables que «Aviso de respuesta»; el PDF va en el
    /// encabezado, no en una variable.</summary>
    public static IReadOnlyList<SystemTemplateVariable> SupportFormalReply => SupportReply;

    /// <summary>Soporte fase 4b (H16): la encuesta de satisfacción. Los botones son fijos y no son variables.</summary>
    public static IReadOnlyList<SystemTemplateVariable> SupportSurvey { get; } =
    [
        .. Fixed,
        new("radicado", "Radicado del caso", "SOP-000127"),
    ];

    /// <summary>Mayorista: el aviso por WhatsApp de una invitación de distribuidor. <c>mayorista</c> es el nombre del tenant que invita y
    /// <c>correo</c> el correo completo al que salió la invitación; ni el link ni el token llegan a Mensajería.</summary>
    public static IReadOnlyList<SystemTemplateVariable> WholesaleInvitationNotice { get; } =
    [
        .. Fixed,
        new("mayorista", "Nombre del mayorista", "Distribuciones Andinas"),
        new("correo", "Correo de la invitación", "compras@distribuidora.co"),
    ];

    /// <summary>Support task notices: the assignment notice. <c>numero</c> is the ticket number (SOP-0042); <c>asesor</c> is the fixed variable
    /// filled with the name of whoever assigned. Neither the subject nor the description is a variable: they never travel by WhatsApp.</summary>
    public static IReadOnlyList<SystemTemplateVariable> TaskNotice { get; } =
    [
        .. Fixed,
        new("numero", "Número del caso", "SOP-000127"),
    ];

    /// <summary>HR birthday notices: the digest sent to a manager. Only <c>nombre_contacto</c> of the fixed variables: the notice is not signed by an
    /// advisor and says nothing about the company. <c>hoy</c> and <c>proximos</c> are name lists the sender folds to a short text; <c>fecha</c> is the
    /// date of the upcoming birthdays. No birth year and no age ever travel.</summary>
    public static IReadOnlyList<SystemTemplateVariable> HrBirthdayNotice { get; } =
    [
        Fixed[0],
        new("hoy", "Cumpleañeros de hoy", "Ana Pérez y Luis Rojas"),
        new("fecha", "Fecha de los próximos cumpleaños", "viernes 4 de diciembre"),
        new("proximos", "Próximos cumpleañeros", "Marta Díaz"),
    ];

    /// <summary>Agenda: el recordatorio de una cita. <c>fecha_hora</c> ya viene formateada en la zona del tenant por el emisor; <c>recurso</c> y
    /// <c>sede</c> son los nombres vigentes al enviar. Las fijas van primero, como en toda plantilla del sistema.</summary>
    public static IReadOnlyList<SystemTemplateVariable> AppointmentReminder { get; } =
    [
        .. Fixed,
        new("nombre_cliente", "Nombre del cliente", "María González"),
        new("servicio", "Servicio", "Consulta general"),
        new("fecha_hora", "Fecha y hora de la cita", "martes 24 de noviembre a las 15:00"),
        new("recurso", "Recurso o profesional", "Dra. Paula Ruiz"),
        new("sede", "Sede", "Sede Centro"),
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
