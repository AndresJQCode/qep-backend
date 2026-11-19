namespace Modules.Automations.Domain.Flow;

/// <summary>
/// Spec 2026-11-19 (chatbot CRM action): what the «Acción de CRM» block writes in <c>{saveTo}.result</c>, and which output each code
/// leaves by. They are a contract with the frontend (branching on the cause). <see cref="TagMissing"/> and <see cref="StageMissing"/>
/// at runtime mean a tag or stage deleted after the flow was published.
/// </summary>
public static class CrmResultCodes
{
    // ok: done, or already in the desired state.
    public const string Added = "added";
    public const string AlreadyTagged = "already_tagged";
    public const string Removed = "removed";
    public const string NotTagged = "not_tagged";
    public const string Moved = "moved";
    public const string AlreadyInStage = "already_in_stage";

    // skipped: nothing to act on.
    public const string NoOpenDeal = "no_open_deal";
    public const string SeveralOpenDeals = "several_open_deals";
    public const string NoCustomer = "no_customer";

    // error.
    public const string CustomerUnavailable = "customer_unavailable";
    public const string TagLimit = "tag_limit";
    public const string DealConflict = "deal_conflict";
    public const string StageNotOpen = "stage_not_open";
    public const string ModuleOff = "module_off";
    public const string TagMissing = "tag_missing";
    public const string StageMissing = "stage_missing";
    public const string Failed = "failed";

    /// <summary>The output a result code leaves by. An unknown code (written by a future version) is an <c>error</c>.</summary>
    public static string OutputOf(string code) => code switch
    {
        Added or AlreadyTagged or Removed or NotTagged or Moved or AlreadyInStage => FlowOutputs.Ok,
        NoOpenDeal or SeveralOpenDeals or NoCustomer => FlowOutputs.Skipped,
        _ => FlowOutputs.Error,
    };

    /// <summary>The code of a successful <paramref name="operation"/>: what the simulator answers without touching the CRM.</summary>
    public static string SuccessCodeOf(string operation) => operation switch
    {
        CrmOperations.AddTag => Added,
        CrmOperations.RemoveTag => Removed,
        CrmOperations.MoveDeal => Moved,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown CRM operation."),
    };
}
