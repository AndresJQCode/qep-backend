namespace Modules.Wms.Domain.Tasks;

/// <summary>Qué vale el token de exclusión que trae un operario (spec 2026-11-26 §6.7). No se persiste.</summary>
public enum LeaseCheck
{
    /// <summary>Es el reclamo vigente.</summary>
    Valid,

    /// <summary>El lease venció y nadie más reclamó la tarea: una confirmación tardía la reanuda.</summary>
    Resumable,

    /// <summary>La tarea ya no es suya.</summary>
    Lost,
}
