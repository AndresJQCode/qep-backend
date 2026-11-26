using BuildingBlocks.Domain;

namespace Modules.Wms.Domain.Tasks;

/// <summary>Una tarea o una de sus líneas recibió una acción que su estado no admite (<c>wms.task.invalid_transition</c>, 422). Misma
/// forma que <c>wms.document.invalid_transition</c>: lleva el estado de partida y la acción.</summary>
public sealed class TaskTransitionException(string from, string action)
    : DomainException(WmsErrorCodes.TaskInvalidTransition, $"La tarea está en estado {from} y no admite la acción '{action}'."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>
    {
        ["from"] = from,
        ["action"] = action,
    };
}
