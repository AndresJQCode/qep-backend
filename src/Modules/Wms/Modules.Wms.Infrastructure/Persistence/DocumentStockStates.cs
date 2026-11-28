using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>La fila por documento del inventario básico, rastreada en el contexto del consumidor.</summary>
internal sealed class DocumentStockStates(WmsDbContext dbContext) : IDocumentStockStates
{
    public Task<DocumentStockState?> FindAsync(Guid tenantId, string documentType, Guid documentId, CancellationToken ct) =>
        dbContext.DocumentStockStates.FirstOrDefaultAsync(
            state => state.TenantId == tenantId && state.DocumentType == documentType && state.DocumentId == documentId, ct);

    public void Add(DocumentStockState state) => dbContext.DocumentStockStates.Add(state);
}
