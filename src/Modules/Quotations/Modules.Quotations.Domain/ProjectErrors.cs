namespace Modules.Quotations.Domain;

/// <summary>Los códigos de contrato de las cotizaciones y los pedidos de proyecto (spec 2026-11-26 §6.7). Todos son
/// 422.</summary>
public static class ProjectErrors
{
    /// <summary>El servicio elegido no existe, está inactivo o no es un servicio.</summary>
    public const string ServiceInvalid = "quotation.project.service_invalid";

    /// <summary>Líneas mandadas a mano en una cotización de proyecto: salen del alcance.</summary>
    public const string ItemsNotAllowed = "quotation.project.items_not_allowed";

    /// <summary>Enviar o convertir sin servicio o sin precio.</summary>
    public const string Incomplete = "quotation.project.incomplete";

    /// <summary>Cambiar las líneas o los regalos de un pedido de proyecto.</summary>
    public const string ItemsLocked = "order.project.items_locked";
}
