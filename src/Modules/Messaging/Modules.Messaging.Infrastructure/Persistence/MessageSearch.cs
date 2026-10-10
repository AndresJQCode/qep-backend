using System.Globalization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;
using Npgsql;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§7.3 y §8.8: la consulta con <c>statement_timeout</c> local a su transacción y
/// <c>LIMIT limit + 1</c>; el <c>57014</c> es 422 en <c>q</c>, nunca 500 (P8: se traduce acá, en
/// Infrastructure, porque Application no conoce Npgsql).</summary>
internal sealed class MessageSearch(MessagingDbContext dbContext, IOptions<MessagingSearchOptions> options) : IMessageSearch
{
    public async Task<IReadOnlyList<string?>> LexemizeAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var array = tokens.ToArray();
        // Cada token por to_tsvector; se queda con el lexema de la primera posición. Una stop word deja
        // el tsvector vacío y su fila sale con Lexeme NULL.
        var rows = await dbContext.Database.SqlQuery<LexemeRow>(
            $"""
            SELECT t.ordinality AS "Ordinal",
                   (SELECT v.lexeme FROM unnest(to_tsvector('messaging.es_unaccent', t.token)) AS v
                    ORDER BY v.positions[1] LIMIT 1) AS "Lexeme"
            FROM unnest({array}::text[]) WITH ORDINALITY AS t(token, ordinality)
            """).ToListAsync(cancellationToken);
        var result = new string?[array.Length];
        foreach (var row in rows)
        {
            result[row.Ordinal - 1] = row.Lexeme;
        }

        return result;
    }

    public Task<MessageCursor?> FindCursorAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.Messages.AsNoTracking()
            .Where(message => message.TenantId == tenantId && message.Id == messageId)
            .Select(message => new MessageCursor(message.OccurredAt, message.Id))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<MessageRow>> SearchAsync(
        Guid tenantId,
        string tsQuery,
        Guid? conversationId,
        DateTimeOffset? since,
        DateTimeOffset? until,
        MessageCursor? before,
        int take,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // set_config(…, true) es SET LOCAL con parámetro: vale sólo para esta transacción.
            var timeout = Math.Max(1, options.Value.StatementTimeoutMs).ToString(CultureInfo.InvariantCulture) + "ms";
            await dbContext.Database.ExecuteSqlAsync($"SELECT set_config('statement_timeout', {timeout}, true)", cancellationToken);

            // El filtro de texto va en SQL (tsquery ya armada por SearchTerms, como parámetro); el resto se
            // compone encima con LINQ, así los filtros opcionales no viajan como parámetros NULL sin tipo.
            var query = dbContext.Messages
                .FromSql($"""
                    SELECT * FROM messaging.messages
                    WHERE tenant_id = {tenantId}
                      AND search_vector @@ to_tsquery('messaging.es_unaccent', {tsQuery})
                    """)
                .AsNoTracking();
            if (conversationId is { } conversation)
            {
                query = query.Where(message => message.ConversationId == conversation);
            }

            if (since is { } fromValue)
            {
                query = query.Where(message => message.OccurredAt >= fromValue);
            }

            if (until is { } toValue)
            {
                query = query.Where(message => message.OccurredAt <= toValue);
            }

            if (before is { } cursor)
            {
                query = query.Where(message =>
                    EF.Functions.LessThan(ValueTuple.Create(message.OccurredAt, message.Id), ValueTuple.Create(cursor.OccurredAt, cursor.Id)));
            }

            var rows = await query
                .OrderByDescending(message => message.OccurredAt).ThenByDescending(message => message.Id)
                .Take(take)
                .Select(MessageQueries.RowProjection)
                .ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rows;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            throw new ValidationException([new ValidationFailure("q", SearchMessagesHandler.TimeoutMessage)]);
        }
    }

    private sealed class LexemeRow
    {
        public long Ordinal { get; set; }

        public string? Lexeme { get; set; }
    }
}
