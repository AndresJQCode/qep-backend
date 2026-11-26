using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportTicketAttachmentRepository(SupportDbContext dbContext) : ISupportTicketAttachmentRepository
{
    public void Add(SupportTicketAttachment attachment) => dbContext.TicketAttachments.Add(attachment);

    public async Task<IReadOnlyList<SupportTicketAttachment>> ListAsync(
        Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken) =>
        await dbContext.TicketAttachments
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.TicketId == ticketId)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

    public Task<SupportTicketAttachment?> FindAsync(
        Guid tenantId, SupportTicketId ticketId, Guid attachmentId, CancellationToken cancellationToken) =>
        dbContext.TicketAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.TenantId == tenantId && item.TicketId == ticketId && item.Id == attachmentId, cancellationToken);
}

/// <summary>
/// Plan F36: Soporte retiene el archivo de Storage que un ticket reclamó, para que el barrido de staging no borre un adjunto vivo.
/// Storage la consulta por cada adjunto <c>Available</c> sin reclamar pasada la retención; uno sin fila acá se purga. Usa los índices
/// únicos <c>IX_ticket_attachments_file</c> y, para los archivos de las notas internas (spec 2026-11-15 §5.4),
/// <c>IX_ticket_note_files_file</c>. Precedente: <c>ProductGalleryFileReferenceProbe</c> de Catalog.
/// </summary>
internal sealed class SupportAttachmentReferenceProbe(SupportDbContext dbContext) : IFileReferenceProbe
{
    public string Source => "support";

    public async Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken) =>
        await dbContext.TicketAttachments.AnyAsync(item => item.FileId == fileId, cancellationToken)
        || await dbContext.TicketNoteFiles.AnyAsync(item => item.FileId == fileId, cancellationToken);
}
