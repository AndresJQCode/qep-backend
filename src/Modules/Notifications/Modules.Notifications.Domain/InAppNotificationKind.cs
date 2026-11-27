namespace Modules.Notifications.Domain;

/// <summary>
/// Spec 2026-11-22 §6: what a bell entry is about. Travels by name and is stored by name; the screen owns the texts, so a kind it does
/// not know (a newer backend) is drawn generically.
/// </summary>
public enum InAppNotificationKind
{
    ConversationAssigned,
    ExportReady,
    ExportFailed,
    SupportCustomerReplied,
    SupportTicketUnassigned,
    WholesaleOrderPendingApproval,
    ServiceChargesOverdue,
    SupportTaskAssigned,
    SupportTicketAssigned,
    HrMedicalExamsDue,
    HrExitExamRequired,
    ServicePriceReviewsDue,
    HrBirthdays,
}
