namespace Modules.ServiceContracts.Domain;

/// <summary>El estado de la revisión de precio de un contrato (spec reajuste §6.1). No se guarda: se calcula al leer.</summary>
public enum ReviewState
{
    /// <summary>El contrato no está <c>Active</c>.</summary>
    None = 1,

    Upcoming = 2,

    /// <summary>Ya empezó el aviso y nadie decidió.</summary>
    Due = 3,

    /// <summary>El cobro de la revisión está retenido.</summary>
    Held = 4,

    /// <summary>Hay un cambio de precio programado que cubre la revisión.</summary>
    Scheduled = 5,
}
