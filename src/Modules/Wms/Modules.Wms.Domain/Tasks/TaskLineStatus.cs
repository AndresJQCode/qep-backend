namespace Modules.Wms.Domain.Tasks;

/// <summary>Estado de una línea de tarea. Se persiste por nombre; no se renombra.</summary>
public enum TaskLineStatus
{
    Open,
    Done,
    // El nombre es el del contrato (spec 2026-11-26 §6.3) y se persiste: CA1720 lo lee como el tipo short.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1720:Identifier contains type name", Justification = "Name fixed by the spec's contract and persisted.")]
    Short,
    Cancelled,
}
