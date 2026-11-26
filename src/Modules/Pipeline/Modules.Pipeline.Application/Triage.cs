using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Con <c>Created</c> viaja el negocio entero, para que el panel del chat lo dibuje sin otra llamada.</summary>
public sealed record TriageResultDto(Guid ConversationId, string Decision, DealDetailDto? Deal);

/// <param name="CustomerId">Decisión del owner 2026-11-16: en qué cliente nace el negocio, el contacto de la conversación o su
/// empresa. Sin él, en la empresa si la hay (spec 2026-11-10 §4.5) y si no en el contacto. <c>dismiss</c> lo ignora.</param>
public sealed record DecideTriageCommand(
    Guid TenantId, Guid ConversationId, string? Action, string? Title, Guid? DismissReasonId, Guid? CustomerId = null)
    : ICommand<TriageResultDto>;

/// <summary>D29: sólo <c>create</c> y <c>dismiss</c>. Sin motivo de descarte NO es <c>validation.failed</c>: el contrato pide
/// <c>pipeline.triage.dismiss_reason_required</c> (P10 del plan).</summary>
public sealed class DecideTriageValidator : AbstractValidator<DecideTriageCommand>
{
    public DecideTriageValidator()
    {
        RuleFor(command => command.Action).Must(action => action is "create" or "dismiss").WithMessage("Action must be 'create' or 'dismiss'.");
        RuleFor(command => command.Title).MaximumLength(Deal.TitleMaxLength);
    }
}

/// <summary>§5.1: si una conversación está pendiente y cuál fue su última decisión. Lo comparten la decisión de triage y la ficha
/// (D36).</summary>
internal static class TriageState
{
    /// <summary>Decisión del owner 2026-11-16: el triage es por contacto y la empresa no bloquea nada. El contacto «tiene un negocio
    /// abierto» si tiene uno propio (la regla de antes del spec 2026-11-10) o si sigue abierto uno nacido de esta conversación, aunque
    /// esté en la empresa: sin eso, el negocio que el triage abrió en la empresa volvería a pedir triage con cada entrante.
    /// <c>HasOpenDeal</c> viaja para que la ficha nombre el estado.</summary>
    public static async Task<(bool Pending, TriageDecision? Last, bool HasOpenDeal)> EvaluateAsync(
        IDealRepository deals, ITriageDecisionRepository decisions, Guid tenantId, PipelineConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var last = (await decisions.FindLatestAsync(tenantId, [conversation.Id], cancellationToken)).GetValueOrDefault(conversation.Id);
        var hasOpenDeal = conversation.CustomerId is { } customerId &&
            ((await deals.CustomersWithOpenDealsAsync(tenantId, [customerId], cancellationToken)).Contains(customerId) ||
             await deals.HasOpenDealFromConversationAsync(tenantId, conversation.Id, cancellationToken));
        return (TriageRules.IsPending(conversation.IsOpen, conversation.CustomerId, hasOpenDeal, conversation.LastInboundAt, last?.DecidedAgainstInboundAt), last, hasOpenDeal);
    }

    /// <summary>D30: sin <c>messaging</c> no hay triage. Con el tenant simulado por el stub (sin fila), como <c>TenantModuleGuard</c>, no bloquea.</summary>
    public static async Task<bool> MessagingEnabledAsync(ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken) =>
        await tenantModules.FindAsync(tenantId, cancellationToken) is not { } modules || modules.IsEnabled(TenantModuleKeys.Messaging);
}

/// <summary>§5.1 y D29: crear (dueño = quien llama, origen = la conversación, sin monto) o descartar con motivo del catálogo.
/// Otro asesor que se adelantó deja la conversación sin pendiente: <c>pipeline.triage.not_pending</c>; si los dos deciden a la
/// vez, la base deja pasar uno y el otro sale <c>pipeline.triage.already_decided</c> (D38).</summary>
public sealed class DecideTriageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineConversationDirectory conversations,
    IPipelineCustomerDirectory customers,
    IDealRepository deals,
    ITriageDecisionRepository decisions,
    IPipelineReasonRepository reasons,
    PipelineCaller caller,
    IPipelineUnitOfWork unitOfWork,
    IPipelineAuditRecorder audit,
    DealCreator creator,
    IClock clock,
    IValidator<DecideTriageCommand> validator)
    : ICommandHandler<DecideTriageCommand, TriageResultDto>
{
    public async Task<TriageResultDto> HandleAsync(DecideTriageCommand command, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, PipelinePermissions.DealManage, cancellationToken);
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, command.TenantId, TenantModuleKeys.Messaging, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var conversation = await conversations.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw PipelineNotFound.Conversation(command.ConversationId);
        var (pending, _, _) = await TriageState.EvaluateAsync(deals, decisions, command.TenantId, conversation, cancellationToken);
        if (!pending)
        {
            throw new PipelineDomainException(PipelineErrorCodes.TriageNotPending, "The conversation is no longer pending triage.");
        }

        var customerId = conversation.CustomerId!.Value;
        var actor = DealActor.ForUser(await caller.RequireAsync(command.TenantId, cancellationToken));
        var now = clock.UtcNow;

        if (command.Action == "dismiss")
        {
            if (command.DismissReasonId is not { } reasonId || reasonId == Guid.Empty)
            {
                throw new PipelineDomainException(PipelineErrorCodes.TriageDismissReasonRequired, "Dismissing needs a reason from the catalog.");
            }

            if (await reasons.FindActiveAsync(command.TenantId, ReasonKind.Dismiss, reasonId, cancellationToken) is null)
            {
                throw new PipelineDomainException(PipelineErrorCodes.TriageDismissReasonNotFound, "The dismiss reason is not an active reason of this tenant.");
            }

            var dismissal = TriageDecision.Dismissed(command.TenantId, conversation.Id, customerId, reasonId, conversation.LastInboundAt, actor, now);
            decisions.Add(dismissal);
            audit.Record(command.TenantId, executionContext.SubjectId, false, PipelineAuditActions.TriageDismissed, PipelineAuditActions.TriageResource, dismissal.Id, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new TriageResultDto(conversation.Id, TriageDecisionKind.Dismissed.ToString(), null);
        }

        // G11: la decisión conserva al cliente de la conversación (la persona). El negocio nace donde se eligió (decisión del owner
        // 2026-11-16): el contacto o su empresa; sin elección, la empresa si la hay. Su título por defecto es el de quien lo recibe
        // (A4 para la empresa).
        var company = await customers.FindCompanyAsync(command.TenantId, customerId, cancellationToken);
        var onCompany = command.CustomerId switch
        {
            null => company is not null,
            { } chosen when chosen == customerId => false,
            { } chosen when company is not null && chosen == company.Id => true,
            _ => throw new PipelineDomainException(
                PipelineErrorCodes.TriageDealCustomerInvalid, "The deal must belong to the conversation's customer or to its company."),
        };
        var dealCustomerId = onCompany ? company!.Id : customerId;
        var dealCustomer = (await customers.FindAsync(command.TenantId, [dealCustomerId], cancellationToken)).GetValueOrDefault(dealCustomerId);
        DealCustomers.EnsureCanReceiveDeals(dealCustomer);
        var defaultTitle = onCompany ? company!.Name : DealCardMapper.CustomerName(dealCustomer);

        var title = (!string.IsNullOrWhiteSpace(command.Title) ? command.Title : defaultTitle).Trim();
        if (title.Length > Deal.TitleMaxLength)
        {
            title = title[..Deal.TitleMaxLength];
        }

        // D29: dueño = quien llama, origen = la conversación, sin monto y con la moneda del tenant. Crear lo hace DealCreator, el
        // mismo camino que POST /deals; la decisión entra en su SaveChanges por beforeSave. D38: si otro asesor decidió contra el
        // mismo entrante entre la comprobación de arriba y ese SaveChanges, el índice único lo rechaza
        // (pipeline.triage.already_decided) y ni el negocio ni la decisión quedan.
        var created = await creator.CreateAsync(
            new DealDraft(command.TenantId, dealCustomerId, actor.MemberId!.Value, title, null, null, null, DealSource.Conversation, conversation.Id),
            actor,
            now,
            deal =>
            {
                var creation = TriageDecision.Created(command.TenantId, conversation.Id, customerId, deal.Id, conversation.LastInboundAt, actor, now);
                decisions.Add(creation);
                audit.Record(command.TenantId, executionContext.SubjectId, false, PipelineAuditActions.TriageCreated, PipelineAuditActions.TriageResource, creation.Id, now);
            },
            cancellationToken);

        return new TriageResultDto(conversation.Id, TriageDecisionKind.Created.ToString(), created);
    }
}
