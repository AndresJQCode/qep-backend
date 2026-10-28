namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §6.2 (y P7 del plan): los códigos del embudo, en un solo lugar. Los 422 salen por
/// <c>PipelineDomainException</c>; los 404, por <c>ResourceNotFoundException</c> en Application.
/// </summary>
public static class PipelineErrorCodes
{
    public const string DealNotFound = "pipeline.deal.not_found";
    public const string CustomerNotFound = "pipeline.customer.not_found";
    public const string DealCustomerNotFound = "pipeline.deal.customer_not_found";
    public const string DealOwnerNotMember = "pipeline.deal.owner_not_member";
    public const string DealTitleInvalid = "pipeline.deal.title_invalid";
    public const string DealAmountInvalid = "pipeline.deal.amount_invalid";
    public const string DealAmountFollowsQuotation = "pipeline.deal.amount_follows_quotation";
    public const string DealAlreadyClosed = "pipeline.deal.already_closed";
    public const string DealNotClosed = "pipeline.deal.not_closed";
    public const string DealStageNotOpen = "pipeline.deal.stage_not_open";
    public const string DealLossReasonRequired = "pipeline.deal.loss_reason_required";
    public const string DealLossReasonNotFound = "pipeline.deal.loss_reason_not_found";
    public const string DealLossNoteInvalid = "pipeline.deal.loss_note_invalid";
    public const string DealQuotationNotFound = "pipeline.deal.quotation_not_found";
    public const string DealQuotationNotLinked = "pipeline.deal.quotation_not_linked";
    public const string DealQuotationCustomerMismatch = "pipeline.deal.quotation_customer_mismatch";
    public const string DealOrderNotFound = "pipeline.deal.order_not_found";
    public const string DealOrderCustomerMismatch = "pipeline.deal.order_customer_mismatch";
    public const string DealOrderAlreadyLinked = "pipeline.deal.order_already_linked";
    public const string StageNotFound = "pipeline.stage.not_found";
    public const string StageLayoutInvalid = "pipeline.stage.layout_invalid";
    public const string StageHasDeals = "pipeline.stage.has_deals";
    public const string ReasonLayoutInvalid = "pipeline.reason.layout_invalid";
    public const string TriageConversationNotFound = "pipeline.triage.conversation_not_found";
    public const string TriageNotPending = "pipeline.triage.not_pending";
    public const string TriageDismissReasonRequired = "pipeline.triage.dismiss_reason_required";
    public const string TriageDismissReasonNotFound = "pipeline.triage.dismiss_reason_not_found";

    /// <summary>Spec 2026-10-13 D38: otro asesor ya decidió esta conversación contra el mismo entrante
    /// (<c>IX_triage_decisions_conversation_inbound</c>).</summary>
    public const string TriageAlreadyDecided = "pipeline.triage.already_decided";
}
