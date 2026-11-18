namespace Modules.Wholesale.Domain;

/// <summary>El estado de la invitación como lo ve quien abre el link: derivado del estado de la relación y de la hora.</summary>
public enum InvitationViewState
{
    Pending,
    Accepted,
    Expired,
}
