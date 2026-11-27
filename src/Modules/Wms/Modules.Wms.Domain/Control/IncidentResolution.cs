namespace Modules.Wms.Domain.Control;

/// <summary>Cómo se resolvió una novedad (spec §8.3). Se persiste por nombre; no se renombra. Las resoluciones que ponen o sacan
/// stock (devolver, dar de baja, reubicar, contar, ajustar) llegan con su corte.</summary>
public enum IncidentResolution
{
    /// <summary>Se acepta lo que el operario declaró: no cambia nada en el ledger.</summary>
    Accept,

    /// <summary>Se acepta la acción que llegó sin lease y se aplica al ledger.</summary>
    AcceptIntoStock,
}
