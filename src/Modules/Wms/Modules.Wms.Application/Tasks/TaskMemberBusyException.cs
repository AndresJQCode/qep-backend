using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Wms.Domain;

namespace Modules.Wms.Application;

/// <summary>El operario ya tiene otra tarea en curso (<c>wms.task.member_busy</c>, 409). <c>taskId</c> viaja en las extensiones para
/// que la app lo lleve a ella; falta cuando lo detecta el índice único de la base, que no dice cuál es la otra.</summary>
public sealed class TaskMemberBusyException(Guid? taskId, Exception? innerException = null)
    : ResourceConflictException(WmsErrorCodes.TaskMemberBusy, "Ya tienes otra tarea en curso.", innerException),
      IHasProblemExtensions
{
    public Guid? TaskId { get; } = taskId;

    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = taskId is { } id
        ? new Dictionary<string, object?> { ["taskId"] = id }
        : new Dictionary<string, object?>();
}
