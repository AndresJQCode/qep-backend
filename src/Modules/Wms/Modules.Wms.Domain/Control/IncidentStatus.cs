namespace Modules.Wms.Domain.Control;

/// <summary>Estado de una novedad. Se persiste por nombre; no se renombra.</summary>
public enum IncidentStatus
{
    Open,
    InReview,
    Resolved,
    Rejected,
}
