namespace Modules.Support.Domain;

/// <summary>Los eventos de la traza de la fase 1 (plan D9). Se persisten por nombre.</summary>
public enum SupportTicketEventKind
{
    Created,
    StatusChanged,
    AssigneeChanged,
    TypeChanged,
    CustomerChanged,
}
