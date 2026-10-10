using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record SearchMessagesQuery(
    Guid TenantId, string? Q, Guid? ConversationId, DateTimeOffset? From, DateTimeOffset? To, int? Limit, Guid? Before) : IQuery<SearchPageDto>;

/// <summary>Spec §8.8: <c>q</c> 2–100 tras <c>Trim</c> y sin <c>\0</c>; <c>from ≤ to</c>; <c>limit</c> 1–100.
/// Las claves de <c>errors</c> son los nombres del query string (<c>OverridePropertyName</c>).</summary>
public sealed class SearchMessagesValidator : AbstractValidator<SearchMessagesQuery>
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 100;

    public SearchMessagesValidator()
    {
        RuleFor(query => query.Q)
            .Must(q => q is not null && q.Trim().Length is >= MinQueryLength and <= MaxQueryLength && !q.Contains('\0', StringComparison.Ordinal))
            .OverridePropertyName("q").WithMessage("q must have between 2 and 100 characters.");
        RuleFor(query => query.ConversationId).NotEqual(Guid.Empty).When(query => query.ConversationId is not null).OverridePropertyName("conversationId");
        RuleFor(query => query.To).GreaterThanOrEqualTo(query => query.From!.Value).When(query => query.From is not null && query.To is not null)
            .OverridePropertyName("to").WithMessage("to must be on or after from.");
        RuleFor(query => query.Limit).InclusiveBetween(1, 100).When(query => query.Limit is not null).OverridePropertyName("limit");
        RuleFor(query => query.Before).NotEqual(Guid.Empty).When(query => query.Before is not null).OverridePropertyName("before");
    }
}

/// <summary>Spec 2026-10-09 §8.8 (historia 17): conversación y <c>before</c> del tenant o 404; la
/// <c>tsquery</c> la arma el servidor (§7.3) y sin lexemas no se consulta; la fila <c>limit + 1</c> sólo
/// decide <c>hasMore</c>; conversación, cliente, conexión y miembros se resuelven una vez por página.</summary>
public sealed class SearchMessagesHandler(
    IMessageSearch search,
    IConversationQueries conversations,
    ConversationSummaryBuilder summaries,
    IMessagingMemberNames memberNames,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<SearchMessagesQuery> validator)
    : IQueryHandler<SearchMessagesQuery, SearchPageDto>
{
    public const int DefaultLimit = 50;

    /// <summary>§7.3, «El límite honesto»: lo que ve la persona cuando la búsqueda pasa el
    /// <c>statement_timeout</c>.</summary>
    public const string TimeoutMessage = "Busca con una palabra más específica o acota las fechas";

    public async Task<SearchPageDto> HandleAsync(SearchMessagesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        if (query.ConversationId is { } conversationId)
        {
            _ = await conversations.FindAsync(query.TenantId, conversationId, cancellationToken)
                ?? throw MessagingNotFound.Conversation(conversationId);
        }

        MessageCursor? cursor = null;
        if (query.Before is { } before)
        {
            cursor = await search.FindCursorAsync(query.TenantId, before, cancellationToken)
                ?? throw MessagingNotFound.Message(before);
        }

        var tokens = SearchTerms.Tokenize(query.Q!);
        IReadOnlyList<string?> lexemes = tokens.Count == 0 ? [] : await search.LexemizeAsync(tokens, cancellationToken);
        var tsQuery = SearchTerms.Compose(tokens.Select((token, index) => (token, lexemes[index])).ToArray());
        if (tsQuery is null)
        {
            return new SearchPageDto([], false);
        }

        var limit = query.Limit ?? DefaultLimit;
        var rows = await search.SearchAsync(query.TenantId, tsQuery, query.ConversationId, query.From, query.To, cursor, limit + 1, cancellationToken);
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToArray();
        if (page.Length == 0)
        {
            return new SearchPageDto([], hasMore);
        }

        var conversationRows = await conversations.FindManyAsync(query.TenantId, page.Select(row => row.ConversationId).Distinct().ToArray(), cancellationToken);
        var summaryById = (await summaries.BuildAsync(query.TenantId, conversationRows, cancellationToken)).ToDictionary(summary => summary.Id);
        var names = await memberNames.FindAsync(
            query.TenantId, page.Where(row => row.SentByMemberId is not null).Select(row => row.SentByMemberId!.Value).Distinct().ToArray(), cancellationToken);

        return new SearchPageDto(page.Select(row =>
        {
            var message = MessageMapping.ToDto(row, query.TenantId, names);
            var summary = summaryById[row.ConversationId];
            return new MessageHitDto(
                message.Id, message.Direction, message.Kind, message.Text, message.Media, message.Location, message.Status, message.FailureReason,
                message.At, message.SentBy, message.ClientId, row.ConversationId, summary.Contact, summary.Customer, summary.ConnectionName);
        }).ToArray(), hasMore);
    }
}
