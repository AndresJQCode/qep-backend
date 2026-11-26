using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>Un comando y no una consulta, como el adjunto del formulario: emitir la URL deja auditoría.</summary>
public sealed record GetReplyDocumentDownloadCommand(Guid TenantId, Guid TicketId, Guid ReplyId) : ICommand<SupportDocumentLinkDto>;

/// <summary>
/// Fase 4b (H13): bajar el PDF de una respuesta formal desde la consola. Lo autoriza <c>support.ticket.view</c> (leer o gestionar),
/// revalidado acá: otro tenant es 403, nunca 404. El documento se busca por tenant, ticket y respuesta: el de otro ticket, o una respuesta
/// sin documento, es <c>404 support.reply.document_not_found</c>. La URL firmada vive 5 minutos y la auditoría es de Soporte, con el ticket
/// como recurso.
/// </summary>
public sealed class GetReplyDocumentDownloadHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportReplyRepository replies,
    ISupportDocumentStorage storage,
    ISupportAuditPublisher auditPublisher,
    ISupportUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<GetReplyDocumentDownloadCommand, SupportDocumentLinkDto>
{
    /// <summary>H8: la vida de la URL firmada en la consola.</summary>
    public static readonly TimeSpan ConsoleLifetime = TimeSpan.FromMinutes(5);

    private static readonly string[] NoFields = [];

    public async Task<SupportDocumentLinkDto> HandleAsync(GetReplyDocumentDownloadCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);

        var ticketId = new SupportTicketId(command.TicketId);
        _ = await scope.ViewAsync(command.TenantId, command.TicketId, cancellationToken);
        var document = await replies.FindDocumentAsync(command.TenantId, ticketId, command.ReplyId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                SupportErrorCodes.ReplyDocumentNotFound, $"Reply '{command.ReplyId}' has no formal document in this ticket.");

        var now = clock.UtcNow;
        var url = await storage.CreateDownloadUrlAsync(document.StorageKey, document.FileName, ConsoleLifetime, cancellationToken);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.TicketReplyDocumentDownloaded,
            SupportAuditActions.Resources.Ticket,
            ticketId.ToString(),
            "success",
            NoFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SupportDocumentLinkDto(url, document.FileName, now + ConsoleLifetime);
    }
}
