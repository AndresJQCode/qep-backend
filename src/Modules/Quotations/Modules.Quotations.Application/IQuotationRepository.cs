using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar. Mismo criterio que ICustomerRepository.
public interface IQuotationRepository
{
    Task<Quotation?> FindAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);

    /// <summary>Una página del listado y el total que la acompaña (US-8), con los cuatro
    /// filtros combinables de la propuesta. Cada filtro es opcional y sólo se aplica cuando
    /// llega distinto de null.</summary>
    Task<(IReadOnlyList<Quotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    void Add(Quotation quotation);

    /// <summary>
    /// Agrega una entrada a la línea de tiempo de la cotización (§2.3 del modelo de datos). Vive
    /// en este repositorio y no en uno propio: siempre se escribe junto con una mutación de
    /// <see cref="Quotation"/>, en la misma unidad de trabajo.
    /// </summary>
    void AddHistoryEntry(QuotationHistoryEntry entry);
}
