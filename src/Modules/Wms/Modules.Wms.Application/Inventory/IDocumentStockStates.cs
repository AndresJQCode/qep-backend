using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

/// <summary>La fila por documento del inventario básico (spec §5.2). Rastreada: lo que cambia el aplicador se guarda con el
/// <c>SaveChanges</c> del consumidor, junto con el inbox.</summary>
public interface IDocumentStockStates
{
    Task<DocumentStockState?> FindAsync(Guid tenantId, string documentType, Guid documentId, CancellationToken ct);

    void Add(DocumentStockState state);
}

/// <summary>Lo que el aplicador avisa de lo que aparta o corrige: log y métrica, que Application no conoce.</summary>
public interface IInventoryEventTelemetry
{
    /// <summary>Una línea que no se mueve (§5.6): advertencia y contador por motivo.</summary>
    void LineSkipped(Guid tenantId, string documentType, Guid documentId, Guid lineId, SkipReason reason);

    /// <summary>El <c>occurredAt</c> del evento quedó fuera de la ventana del ledger y se usó la hora del proceso.</summary>
    void OccurredAtOutOfWindow(Guid tenantId, string documentType, Guid documentId, DateTimeOffset occurredAt);
}
