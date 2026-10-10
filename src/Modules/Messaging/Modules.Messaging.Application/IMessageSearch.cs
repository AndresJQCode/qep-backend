namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §7.3 y §8.8: la búsqueda full-text del historial de un tenant.
/// <see cref="LexemizeAsync"/> devuelve, por token, el primer lexema de
/// <c>to_tsvector('messaging.es_unaccent', token)</c> o <c>null</c>; <see cref="SearchAsync"/> corre con
/// <c>SET LOCAL statement_timeout</c> y traduce el <c>57014</c> a <c>ValidationException</c> en <c>q</c> (P8).</summary>
public interface IMessageSearch
{
    /// <summary>Un elemento por token, en el mismo orden; <c>null</c> si el token no deja lexema (stop word).</summary>
    Task<IReadOnlyList<string?>> LexemizeAsync(IReadOnlyList<string> tokens, CancellationToken cancellationToken);

    /// <summary>El cursor de <paramref name="messageId"/> sólo si es del tenant; <c>null</c> si no.</summary>
    Task<MessageCursor?> FindCursorAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>La consulta de §7.3: del más nuevo al más viejo, a lo sumo <paramref name="take"/> filas.
    /// <paramref name="since"/> y <paramref name="until"/> son <c>from</c> y <c>to</c> del query string
    /// (<c>to</c> choca con CA1716).</summary>
    Task<IReadOnlyList<MessageRow>> SearchAsync(
        Guid tenantId,
        string tsQuery,
        Guid? conversationId,
        DateTimeOffset? since,
        DateTimeOffset? until,
        MessageCursor? before,
        int take,
        CancellationToken cancellationToken);
}
