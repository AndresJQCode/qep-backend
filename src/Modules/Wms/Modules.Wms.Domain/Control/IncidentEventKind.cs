namespace Modules.Wms.Domain.Control;

/// <summary>Qué pasó en la historia de una novedad. Se persiste por nombre y sin <c>CHECK</c> (como <c>task_events.kind</c>): el
/// vocabulario crece sin migración; no se renombra.</summary>
public enum IncidentEventKind
{
    Reported,
    InReview,
    Resolved,
    Rejected,
    VerificationLinked,
    VerificationCancelled,
}
