namespace Modules.Wms.Domain.Tasks;

/// <summary>El proceso que creó la tarea. Se persiste por nombre; no se renombra.</summary>
public enum TaskSourceType
{
    Manual,

    /// <summary>La creó una novedad (el <c>Count</c> de verificación): <c>SourceId</c> es la novedad que la originó.</summary>
    Incident,
}
