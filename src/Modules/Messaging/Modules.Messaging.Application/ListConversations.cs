using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record ListConversationsQuery(
    Guid TenantId, string? Status, string? Search, string? Cursor, int? PageSize, string? Assigned = null, string? Channel = null,
    Guid? TagId = null, bool? Unread = null, string? Sort = null)
    : IQuery<ConversationPageDto>;

/// <summary>Spec §6.4: status ∈ Open|Resolved (default Open), search ≤ 100, pageSize 1–50 (default 30), cursor emitido
/// por <see cref="ConversationCursor.Encode"/>;
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
        RuleFor(query => query.Cursor)
            .Must(value => ConversationCursor.TryDecode(value, out _)).When(query => query.Cursor is not null)
            .OverridePropertyName("cursor").WithMessage("cursor is not a cursor issued by this list.");
        RuleFor(query => query.PageSize).InclusiveBetween(1, 50).When(query => query.PageSize is not null).OverridePropertyName("pageSize");
        RuleFor(query => query.Assigned)
            .Must(value => value is null or "me" or "none" or "all")
            .OverridePropertyName("assigned").WithMessage("assigned must be me, none or all.");
        // Spec 2026-10-14 §5.2: ausente = todos los canales.
        RuleFor(query => query.Channel)
            .Must(value => value is null or nameof(ConversationChannel.WhatsApp) or nameof(ConversationChannel.Instagram))
            .OverridePropertyName("channel").WithMessage("channel must be WhatsApp or Instagram.");
        // Spec qep-frontend 2026-10-15: ausente = newest. El guid de tagId lo valida el binding; uno ajeno da lista vacía.
        RuleFor(query => query.Sort)
            .Must(value => value is null or "newest" or "oldest")
            .OverridePropertyName("sort").WithMessage("sort must be newest or oldest.");
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
        var pageSize = query.PageSize ?? DefaultPageSize;
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var assigned = query.Assigned switch { "me" => AssignedFilter.Mine, "none" => AssignedFilter.Unassigned, _ => AssignedFilter.All };
        // P20: la membresía de quien llama, una vez por request; la reusan el filtro, counts.mine e isMe (mismo scoped).
        var me = await caller.FindAsync(query.TenantId, cancellationToken);
        // §6.1.6: los clientes cuyo nombre contiene el término aportan sus ids (y sus números, para las conversaciones viejas).
        var customerWaIds = search is null ? Array.Empty<string>() : await customers.FindWaIdsByNameAsync(query.TenantId, search, cancellationToken);
        var customerIds = search is null ? Array.Empty<Guid>() : await customers.FindIdsByNameAsync(query.TenantId, search, cancellationToken);
        ConversationChannel? channel = query.Channel switch
        {
            nameof(ConversationChannel.WhatsApp) => ConversationChannel.WhatsApp,
            nameof(ConversationChannel.Instagram) => ConversationChannel.Instagram,
            _ => null,
        };
        // Spec qep-frontend 2026-10-15: la etiqueta se resuelve en Customers y filtra por customer_id, como la búsqueda por nombre.
        var tagCustomerIds = query.TagId is { } tagId ? await customers.FindIdsByTagAsync(query.TenantId, tagId, cancellationToken) : null;
        var sort = query.Sort is "oldest" ? ConversationSort.Oldest : ConversationSort.Newest;
        var filter = new ConversationListFilter(
            status, assigned, me, search, customerWaIds, customerIds, channel, tagCustomerIds, query.Unread is true, sort);

        ConversationCursor? after = null;
        if (query.Cursor is not null && ConversationCursor.TryDecode(query.Cursor, out var decoded))
        {
            after = decoded;
        }

        // La fila pageSize + 1 sólo decide si hay otra página, como el hilo de mensajes (ListMessagesHandler).
        var rows = await queries.ListAsync(query.TenantId, filter, after, pageSize + 1, cancellationToken);
        var page = rows.Take(pageSize).ToArray();
        var nextCursor = rows.Count > pageSize
            ? ConversationCursor.Encode(new ConversationCursor(page[^1].LastActivityAt, page[^1].Id))
            : null;
        var counts = await queries.CountsAsync(query.TenantId, me, cancellationToken);
        return new ConversationPageDto(await summaries.BuildAsync(query.TenantId, page, cancellationToken), nextCursor, counts);
    }
}
