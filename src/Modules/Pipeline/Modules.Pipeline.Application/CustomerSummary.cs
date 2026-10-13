using BuildingBlocks.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pipeline.Application;

/// <summary>
/// BFF (D36): la ficha del cliente dentro de la conversación dibuja esta sección sin lógica propia. La pantalla no ve etapas,
/// membresías, conversaciones ni decisiones de triage, así que el servidor resuelve todo: nombre de la etapa y del dueño,
/// estancado, días en la etapa, el último cerrado como contexto y qué acciones de triage ofrecer. Viaja aparte de la ficha de
/// Customers porque Customers no puede referenciar Pipeline.
/// </summary>
public sealed record CustomerDealsSummaryDto(
    Guid CustomerId,
    IReadOnlyList<CustomerOpenDealDto> OpenDeals,
    CustomerClosedDealDto? LastClosedDeal,
    ConversationTriageStateDto? Triage);

public sealed record CustomerOpenDealDto(
    Guid Id, string Title, Guid StageId, string StageName, decimal? Amount, string Currency,
    Guid OwnerMemberId, string OwnerName, bool IsStalled, int DaysInStage, string? PrimaryQuotationNumber, long Version);

/// <summary>El cerrado más reciente, ganado o perdido, como contexto («perdido hace 2 meses por precio»). Uno solo: el histórico
/// completo es del slice 2.</summary>
public sealed record CustomerClosedDealDto(Guid Id, string Title, string Outcome, DateTimeOffset ClosedAt, string? LossReasonName);

/// <summary>Status: <c>Pending</c> | <c>Decided</c> | <c>NotApplicable</c>. <c>NotApplicable</c> = la conversación no está abierta,
/// el cliente ya tiene un negocio abierto, o el tenant no tiene <c>messaging</c>. <see cref="AvailableActions"/> viene ya filtrado
/// por estado y por permiso (sin <c>pipeline.deal.manage</c>, vacío): la pantalla dibuja los botones que vienen, nada más.</summary>
public sealed record ConversationTriageStateDto(
    Guid ConversationId, string Status, IReadOnlyList<string> AvailableActions, TriageDecisionDto? LastDecision);

public sealed record TriageDecisionDto(string Kind, DateTimeOffset DecidedAt, string? DismissReasonName);

public sealed record GetCustomerDealsSummaryQuery(Guid TenantId, Guid CustomerId, Guid? ConversationId) : IQuery<CustomerDealsSummaryDto>;

public sealed class GetCustomerDealsSummaryHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    IDealQuotationRepository quotations,
    IPipelineReasonRepository reasons,
    ITriageDecisionRepository decisions,
    IPipelineCustomerDirectory customers,
    IPipelineMemberNames members,
    IPipelineConversationDirectory conversations,
    IClock clock)
    : IQueryHandler<GetCustomerDealsSummaryQuery, CustomerDealsSummaryDto>
{
    private static readonly string[] TriageActions = ["create", "dismiss"];

    public async Task<CustomerDealsSummaryDto> HandleAsync(GetCustomerDealsSummaryQuery query, CancellationToken cancellationToken)
    {
        await PipelineAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, PipelinePermissions.DealRead, cancellationToken);
        if (!(await customers.FindAsync(query.TenantId, [query.CustomerId], cancellationToken)).ContainsKey(query.CustomerId))
        {
            throw PipelineNotFound.Customer(query.CustomerId);
        }

        var pipeline = await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var now = clock.UtcNow;
        var open = await deals.ListOpenByCustomerAsync(query.TenantId, query.CustomerId, cancellationToken);
        var lastClosed = await deals.FindLastClosedByCustomerAsync(query.TenantId, query.CustomerId, cancellationToken);
        var activity = (await conversations.FindActivityAsync(query.TenantId, [query.CustomerId], cancellationToken)).GetValueOrDefault(query.CustomerId);
        var names = await members.FindAsync(query.TenantId, [.. open.Select(deal => deal.OwnerMemberId).Distinct()], cancellationToken);

        var openDeals = new List<CustomerOpenDealDto>();
        foreach (var deal in open)
        {
            var number = deal.PrimaryQuotationId is { } primaryId
                ? (await quotations.FindAsync(query.TenantId, primaryId, cancellationToken))?.QuotationNumber
                : null;
            // Etapa, dueño, días y estancado: el mismo DealCardMapper del tablero y del detalle.
            var card = DealCardMapper.Describe(
                pipeline, deal.StageId, deal.StageEnteredAt, deal.LastChangedAt, deal.OwnerMemberId, names, activity?.LastMessageAt, now);
            openDeals.Add(new CustomerOpenDealDto(
                deal.Id, deal.Title, deal.StageId, card.StageName, deal.Amount, deal.Currency, deal.OwnerMemberId,
                card.OwnerName, card.IsStalled, card.DaysInStage, number, deal.Version));
        }

        CustomerClosedDealDto? closedDto = null;
        if (lastClosed is { ClosedAt: { } closedAt })
        {
            var reasonName = lastClosed.LossReasonId is { } reasonId
                ? (await reasons.FindNamesAsync(query.TenantId, [reasonId], cancellationToken)).GetValueOrDefault(reasonId)
                : null;
            var outcome = pipeline.FindStage(lastClosed.StageId)?.Kind ?? StageKind.Lost;
            closedDto = new CustomerClosedDealDto(lastClosed.Id, lastClosed.Title, outcome.ToString(), closedAt, reasonName);
        }

        var triage = query.ConversationId is { } conversationId
            ? await TriageAsync(query.TenantId, query.CustomerId, conversationId, open.Count > 0, cancellationToken)
            : null;
        return new CustomerDealsSummaryDto(query.CustomerId, openDeals, closedDto, triage);
    }

    private async Task<ConversationTriageStateDto> TriageAsync(
        Guid tenantId, Guid customerId, Guid conversationId, bool customerHasOpenDeal, CancellationToken cancellationToken)
    {
        var conversation = await conversations.FindAsync(tenantId, conversationId, cancellationToken);
        if (conversation is null || conversation.CustomerId != customerId)
        {
            throw PipelineNotFound.Conversation(conversationId);
        }

        if (!await TriageState.MessagingEnabledAsync(tenantModules, tenantId, cancellationToken))
        {
            return new ConversationTriageStateDto(conversationId, "NotApplicable", [], null);
        }

        var (pending, last) = await TriageState.EvaluateAsync(deals, decisions, tenantId, conversation, cancellationToken);
        var status = pending ? "Pending" : !conversation.IsOpen || customerHasOpenDeal ? "NotApplicable" : "Decided";
        IReadOnlyList<string> actions = pending && executionContext.HasPermission(PipelinePermissions.DealManage) ? TriageActions : [];
        TriageDecisionDto? lastDto = null;
        if (last is not null)
        {
            var reasonName = last.DismissReasonId is { } reasonId
                ? (await reasons.FindNamesAsync(tenantId, [reasonId], cancellationToken)).GetValueOrDefault(reasonId)
                : null;
            lastDto = new TriageDecisionDto(last.Kind.ToString(), last.DecidedAt, reasonName);
        }

        return new ConversationTriageStateDto(conversationId, status, actions, lastDto);
    }
}
