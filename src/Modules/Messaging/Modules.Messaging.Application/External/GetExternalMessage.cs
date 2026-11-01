using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Un mensaje de la API externa (<c>sent_by_kind = 4</c>) con lo justo para decir su estado y a qué conexión pertenece.</summary>
public sealed record ExternalMessageRow(Guid Id, Guid ConversationId, Guid ConnectionId, MessageStatus Status, int? FailureCode);

/// <summary>Lecturas sobre los mensajes enviados por clave de API. Todas filtran por tenant y por autor: un mensaje de un miembro o de
/// un chatbot no existe para el integrador.</summary>
public interface IExternalMessageQueries
{
    Task<ExternalMessageRow?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExternalMessageRow>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);

    /// <summary>Los ids de los mensajes de clave de API que fallaron desde <paramref name="since"/>.</summary>
    Task<IReadOnlyList<Guid>> ListFailedIdsAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);
}

public sealed record ExternalMessageDto(Guid MessageId, Guid ConversationId, string Status, int? FailureCode, string? FailureReason);

/// <summary><c>GET /api/v1/external/whatsapp/messages/{messageId}</c>.</summary>
public sealed record GetExternalMessageQuery(Guid MessageId) : IQuery<ExternalMessageDto>;

public sealed class GetExternalMessageHandler(IApiCaller caller, ITenantModules tenantModules, IExternalMessageQueries messages, IExternalCallNotes notes)
    : IQueryHandler<GetExternalMessageQuery, ExternalMessageDto>
{
    public async Task<ExternalMessageDto> HandleAsync(GetExternalMessageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ExternalApiAccess.EnsureAsync(caller, tenantModules, MessagingScopes.TemplatesSend, cancellationToken);

        // Sin fila o con una conexión que la clave no puede usar es lo mismo: 404. EnsureConnectionAsync respondería 403 y revelaría que existe.
        var row = await messages.FindAsync(caller.TenantId, query.MessageId, cancellationToken);
        if (row is null || !caller.CanUseConnection(row.ConnectionId))
        {
            throw new ResourceNotFoundException(MessagingErrorCodes.ExternalMessageNotFound, "No encontramos ese mensaje.");
        }

        notes.NoteMessage(row.Id);
        var state = ExternalMessageState.Describe(row.Status, row.FailureCode);
        return new ExternalMessageDto(row.Id, row.ConversationId, state.Status, state.FailureCode, state.FailureReason);
    }
}
