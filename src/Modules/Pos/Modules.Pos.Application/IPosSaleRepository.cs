using Modules.Pos.Domain;

namespace Modules.Pos.Application;

public interface IPosSaleRepository
{
    /// <summary>Con líneas y pagos, rastreada (la anulación la modifica).</summary>
    Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken cancellationToken);

    void Add(PosSale sale);
}
