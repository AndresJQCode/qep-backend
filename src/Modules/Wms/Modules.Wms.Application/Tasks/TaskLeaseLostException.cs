using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Wms.Domain;

namespace Modules.Wms.Application;

/// <summary>El token no es el del reclamo vigente (<c>wms.task.lease_lost</c>, 409). <c>resumable</c> viaja en las extensiones para
/// que la app sepa qué hacer: <c>true</c> si el lease venció y nadie más tomó la tarea (puede pedirla otra vez con <c>claim</c> y
/// seguir), <c>false</c> si es de otra persona, se la reasignaron, la retuvieron o ya la soltó. Un <c>unclaim</c> repetido llega con
/// <c>false</c> y la app lo trata como éxito.</summary>
public sealed class TaskLeaseLostException(bool resumable)
    : ResourceConflictException(
        WmsErrorCodes.TaskLeaseLost,
        resumable ? "Tu reclamo de esta tarea venció. Pídela otra vez para seguir." : "Esta tarea ya no es tuya."),
      IHasProblemExtensions
{
    public bool Resumable { get; } = resumable;

    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
        new Dictionary<string, object?> { ["resumable"] = resumable };
}
