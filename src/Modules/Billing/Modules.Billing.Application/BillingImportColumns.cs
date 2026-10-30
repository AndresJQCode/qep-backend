namespace Modules.Billing.Application;

/// <summary>
/// Los encabezados exactos de la plantilla de cobros recurrentes (§3.2b), en el orden en que la plantilla los escribe. El
/// lector los ubica por nombre, sin mayúsculas ni tildes (un encabezado retipeado a mano sin tilde se sigue encontrando).
/// </summary>
public static class BillingImportColumns
{
    public const string CustomerName = "Cliente";

    public const string IdentificationType = "Tipo de documento";

    public const string IdentificationNumber = "Número de documento";

    public const string Phone = "Teléfono";

    public const string Email = "Correo";

    public const string Country = "País";

    public const string ServiceName = "Servicio";

    public const string Price = "Precio";

    public const string Currency = "Moneda";

    public const string Cycle = "Ciclo";

    /// <summary>El próximo cobro: la generación arranca en esa fecha (owner, 2026-10-29), aunque sea pasada.</summary>
    public const string DueDate = "Próximo vencimiento";

    public const string Channel = "Canal de aviso";

    public const string Assignee = "Encargado";

    public static readonly IReadOnlyList<string> Ordered =
    [
        CustomerName,
        IdentificationType,
        IdentificationNumber,
        Phone,
        Email,
        Country,
        ServiceName,
        Price,
        Currency,
        Cycle,
        DueDate,
        Channel,
        Assignee,
    ];

    /// <summary>Los valores del desplegable de ciclo: los que acepta <c>cycle_invalid</c>.</summary>
    public static readonly IReadOnlyList<string> Cycles = ["Mensual", "Semestral", "Anual"];

    public static readonly IReadOnlyList<string> Channels = ["WhatsApp", "Email"];
}
