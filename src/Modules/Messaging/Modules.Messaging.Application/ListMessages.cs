using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record ListMessagesQuery(Guid TenantId, Guid ConversationId, int? Limit, Guid? Before) : IQuery<MessagePageDto>;

/// <summary>Spec §6.4: <c>limit</c> 1–100 (default 50); <c>before</c> no vacío. Que <c>before</c> sea de
/// esa conversación lo decide el handler contra la base.</summary>
public sealed class ListMessagesValidator : AbstractValidator<ListMessagesQuery>
{
    public ListMessagesValidator()
    {
        RuleFor(query => query.Limit).InclusiveBetween(1, 100).When(query => query.Limit is not null).OverridePropertyName("limit");
        RuleFor(query => query.Before).NotEqual(Guid.Empty).When(query => query.Before is not null).OverridePropertyName("before");
    }
}

/// <summary>§8.7: los <c>limit</c> más nuevos, o los anteriores a <c>before</c> (keyset por
/// <c>(occurred_at, id)</c>); la fila <c>limit + 1</c> sólo decide <c>hasMore</c>; items cronológicos.
/// Un <c>before</c> que no es de esa conversación es <c>validation.failed</c> (Review Focus 3): la misma
/// respuesta que un GUID inexistente, así que no confirma nada.</summary>
public sealed class ListMessagesHandler(
    IConversationQueries conversations,
    IMessageQueries messages,
    IMessagingMemberNames memberNames,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<ListMessagesQuery> validator)
    : IQueryHandler<ListMessagesQuery, MessagePageDto>
{
    public const int DefaultLimit = 50;

    public async Task<MessagePageDto> HandleAsync(ListMessagesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        _ = await conversations.FindAsync(query.TenantId, query.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(query.ConversationId);

        MessageCursor? cursor = null;
        if (query.Before is { } before)
        {
            cursor = await messages.FindCursorAsync(query.ConversationId, before, cancellationToken)
                ?? throw new ValidationException([new ValidationFailure("before", "before must be a message of this conversation.")]);
        }

        var limit = query.Limit ?? DefaultLimit;
        var rows = await messages.ListThreadAsync(query.ConversationId, cursor, limit + 1, cancellationToken);
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).Reverse().ToArray();
        var names = await memberNames.FindAsync(query.TenantId, MessageMapping.MemberIdsOf(page), cancellationToken);
        var targets = await messages.FindReplyTargetsAsync(query.TenantId, MessageMapping.ReplyTargetIdsOf(page), cancellationToken);
        return new MessagePageDto(page.Select(row => MessageMapping.ToDto(row, query.TenantId, names, targets)).ToArray(), hasMore);
    }
}
