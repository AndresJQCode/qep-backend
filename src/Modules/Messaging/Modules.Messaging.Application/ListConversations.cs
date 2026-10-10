using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record ListConversationsQuery(Guid TenantId, string? Status, string? Search, int? Page, int? PageSize, string? Assigned = null)
    : IQuery<ConversationPageDto>;

/// <summary>Spec §6.4: status ∈ Open|Resolved (default Open), search ≤ 100, page ≥ 1, pageSize 1–50 (default 30);
/// spec 2026-10-10 §5.1: assigned ∈ me|none|all (default all).
/// La clave de <c>errors</c> es el nombre del query string (<c>OverridePropertyName</c>, como
/// <c>CompleteWhatsAppSignupValidator</c>): <c>WithName</c> sólo cambia el texto, no la clave.</summary>
public sealed class ListConversationsValidator : AbstractValidator<ListConversationsQuery>
{
    public ListConversationsValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => status is null || status is nameof(ConversationStatus.Open) or nameof(ConversationStatus.Resolved))
            .OverridePropertyName("status").WithMessage("status must be Open or Resolved.");
        RuleFor(query => query.Search).MaximumLength(100).OverridePropertyName("search");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).When(query => query.Page is not null).OverridePropertyName("page");
        RuleFor(query => query.PageSize).InclusiveBetween(1, 50).When(query => query.PageSize is not null).OverridePropertyName("pageSize");
        RuleFor(query => query.Assigned)
            .Must(value => value is null or "me" or "none" or "all")
            .OverridePropertyName("assigned").WithMessage("assigned must be me, none or all.");
    }
}

/// <summary>§8.7: la lista nunca toca <c>messages</c>; counts cuentan el tenant entero, sin búsqueda (§5.4).</summary>
public sealed class ListConversationsHandler(
    IConversationQueries queries,
    IMessagingCustomerDirectory customers,
    ConversationSummaryBuilder summaries,
    CallerMembership caller,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<ListConversationsQuery> validator)
    : IQueryHandler<ListConversationsQuery, ConversationPageDto>
{
    public const int DefaultPageSize = 30;

    public async Task<ConversationPageDto> HandleAsync(ListConversationsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var status = query.Status is nameof(ConversationStatus.Resolved) ? ConversationStatus.Resolved : ConversationStatus.Open;
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var assigned = query.Assigned switch { "me" => AssignedFilter.Mine, "none" => AssignedFilter.Unassigned, _ => AssignedFilter.All };
        // P20: la membresía de quien llama, una vez por request; la reusan el filtro, counts.mine e isMe (mismo scoped).
        var me = await caller.FindAsync(query.TenantId, cancellationToken);
        // §6.1.6: los clientes cuyo nombre contiene el término aportan sus ids (y sus números, para las conversaciones viejas).
        var customerWaIds = search is null ? Array.Empty<string>() : await customers.FindWaIdsByNameAsync(query.TenantId, search, cancellationToken);
        var customerIds = search is null ? Array.Empty<Guid>() : await customers.FindIdsByNameAsync(query.TenantId, search, cancellationToken);
        var filter = new ConversationListFilter(status, assigned, me, search, customerWaIds, customerIds);

        var (rows, total) = await queries.ListAsync(query.TenantId, filter, page, pageSize, cancellationToken);
        var counts = await queries.CountsAsync(query.TenantId, me, cancellationToken);
        return new ConversationPageDto(await summaries.BuildAsync(query.TenantId, rows, cancellationToken), total, page, pageSize, counts);
    }
}
