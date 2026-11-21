namespace Modules.Coordinadora.Domain;

/// <summary>Los códigos de error de Coordinadora (<c>coordinadora.*</c>). Son contrato con el frontend, que traduce por ellos: no se
/// renombran. El status lo decide el mapeo central (<c>ApiExceptionHandler</c>), nunca un handler.</summary>
public static class CoordinadoraErrorCodes
{
    /// <summary>422: un valor de la configuración está fuera de rango o pasa del largo de su columna (los validadores lo frenan antes
    /// con el campo).</summary>
    public const string SettingsValueInvalid = "coordinadora.settings.value_invalid";

    /// <summary>422: el paquete (medidas, peso o unidades) está fuera de rango.</summary>
    public const string PackageInvalid = "coordinadora.package.invalid";

    /// <summary>409: la conexión no se puede usar (pausada, con la clave rechazada, ilegible…); el motivo viaja en el mensaje.</summary>
    public const string ConnectionUnavailable = "coordinadora.connection_unavailable";

    /// <summary>503: Coordinadora no contestó o respondió un error transitorio.</summary>
    public const string Unavailable = "coordinadora.unavailable";

    /// <summary>422: Coordinadora rechazó una lectura (rastreo, rótulo) con un error propio; el detalle va en el mensaje.</summary>
    public const string CarrierRejected = "coordinadora.carrier_rejected";

    /// <summary>422: el pedido no está listo para la guía; <c>details.missing</c> trae todo lo que falta (spec §5.3).</summary>
    public const string ShipmentNotReady = "coordinadora.shipment.not_ready";

    /// <summary>409: el índice único parcial de <c>coordinadora.shipments</c> (una guía viva por pedido).</summary>
    public const string ShipmentAlreadyExists = "coordinadora.shipment.already_exists";

    /// <summary>422: la guía no está en el estado que la acción exige (resolver sólo desde <c>Unknown</c>; rótulo, rastreo y anulación
    /// sólo de una <c>Created</c>).</summary>
    public const string ShipmentStateInvalid = "coordinadora.shipment.state_invalid";

    /// <summary>404: el pedido no tiene guía de Coordinadora.</summary>
    public const string ShipmentNotFound = "coordinadora.shipment.not_found";

    /// <summary>422: Coordinadora no aceptó anular la guía; el detalle va en el mensaje.</summary>
    public const string ShipmentCancelRejected = "coordinadora.shipment.cancel_rejected";

    /// <summary>422: una guía <c>Unknown</c> se resuelve como <c>exists</c> sin el número de guía.</summary>
    public const string ShipmentGuideNumberRequired = "coordinadora.shipment.guide_number_required";
}
