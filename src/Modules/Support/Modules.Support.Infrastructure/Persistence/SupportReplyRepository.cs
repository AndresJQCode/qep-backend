using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportReplyRepository(SupportDbContext dbContext) : ISupportReplyRepository
{
    public void Add(SupportTicketReply reply) => dbContext.TicketReplies.Add(reply);

    public void Remove(SupportTicketReply reply) => dbContext.TicketReplies.Remove(reply);

    public void AddDocument(SupportReplyDocument document) => dbContext.ReplyDocuments.Add(document);

    public Task<SupportReplyDocument?> FindDocumentAsync(
        Guid tenantId, SupportTicketId ticketId, Guid replyId, CancellationToken cancellationToken) =>
        dbContext.ReplyDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                document => document.TenantId == tenantId && document.TicketId == ticketId && document.ReplyId == replyId,
                cancellationToken);

    public async Task<IReadOnlyList<SupportReplyDocument>> ListDocumentsAsync(
        Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken) =>
        await dbContext.ReplyDocuments
            .AsNoTracking()
            .Where(document => document.TenantId == tenantId && document.TicketId == ticketId)
            .OrderBy(document => document.CreatedAt)
            .ThenBy(document => document.ReplyId)
            .ToListAsync(cancellationToken);

    public Task<SupportTicketReply?> FindByClientIdAsync(
        Guid tenantId, SupportTicketId ticketId, Guid clientId, CancellationToken cancellationToken) =>
        dbContext.TicketReplies.FirstOrDefaultAsync(
            reply => reply.TenantId == tenantId && reply.TicketId == ticketId && reply.ClientId == clientId, cancellationToken);

    public async Task<IReadOnlyList<SupportTicketReply>> ListAsync(
        Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken) =>
        await dbContext.TicketReplies
            .AsNoTracking()
            .Where(reply => reply.TenantId == tenantId && reply.TicketId == ticketId)
            .OrderBy(reply => reply.CreatedAt)
            .ThenBy(reply => reply.Id)
            .ToListAsync(cancellationToken);
}
