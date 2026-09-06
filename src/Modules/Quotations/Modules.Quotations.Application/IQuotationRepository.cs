using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar. Mismo criterio que ICustomerRepository.
public interface IQuotationRepository
{
    Task<Quotation?> FindAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);

    /// <summary>Una página del listado y el total que la acompaña (US-8), con los filtros
    /// combinables de la propuesta. Cada filtro es opcional y sólo se aplica cuando llega
    /// distinto de null; los que llegan se combinan con AND.
    ///
    /// <c>clientIds</c> es el filtro por NIT: <c>Quotation</c> no guarda el NIT del cliente, así
    /// que <c>ListQuotationsHandler</c> ya lo resolvió a una lista de ids antes de llegar acá
    /// (mismo criterio que <c>cityIds</c> en <c>ICustomerRepository.SearchAsync</c> con el
    /// filtro de Departamento). <c>null</c> es "sin filtro"; una colección vacía es "no matchear
    /// ninguna fila" (el NIT buscado no resolvió a ningún cliente). Independiente de
    /// <c>clientId</c>, que sigue siendo el filtro puntual por combobox.</summary>
    Task<(IReadOnlyList<Quotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
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

    /// <summary>
    /// La línea de tiempo completa de una cotización, de lo más nuevo a lo más viejo. Sin paginar
    /// a propósito: una cotización acumula decenas de entradas, no miles, y la pantalla las
    /// muestra todas — paginar acá sería complejidad sin caso de uso detrás.
    /// </summary>
    Task<IReadOnlyList<QuotationHistoryEntry>> ListHistoryAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);
}
