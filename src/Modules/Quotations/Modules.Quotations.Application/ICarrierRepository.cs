using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Las transportadoras del tenant (spec 2026-10-17 §4.1). Sin <c>Remove</c>: no se borran
/// (D-F2). Devuelve entidades rastreadas, así que una mutación se persiste con
/// <see cref="IQuotationsUnitOfWork.SaveChangesAsync"/>, como el resto de los repositorios.</summary>
public interface ICarrierRepository
{
    /// <summary>Todas, activas e inactivas, ordenadas por <c>SortOrder</c>.</summary>
    Task<IReadOnlyList<Carrier>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<Carrier?> FindAsync(Guid tenantId, CarrierId id, CancellationToken cancellationToken);

    void Add(Carrier carrier);
}
