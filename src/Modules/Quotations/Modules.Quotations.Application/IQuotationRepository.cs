using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar. Mismo criterio que ICustomerRepository.
//
// Las fechas de los filtros llegan como instantes: `createdFrom` inclusivo y `createdBefore`
// exclusivo, ya cortados en el día del tenant por quien llama (spec 2026-09-17, punto 3). El
// repositorio no decide husos.
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
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Un lote de las cotizaciones que pasan los filtros del listado, en su mismo orden:
    /// lo que lee QuotationsExportProcessor para el Excel. Mismos filtros y misma semántica que
    /// <see cref="SearchAsync"/> —<c>clientIds</c> incluido— porque el archivo tiene que ser lo que
    /// la tabla muestra. Por lotes y no entero: la memoria del worker queda acotada al lote (D8).
    /// El volumen total lo acota el rango obligatorio de a lo sumo un año.
    ///
    /// Keyset y no offset: <paramref name="after"/> es la clave de la última fila del lote
    /// anterior (<c>null</c> en el primero), y el lote es lo que viene después en el orden
    /// <c>(CreatedAt DESC, QuotationNumber DESC)</c>. Una cotización creada o que sale del filtro
    /// durante el export no corre las filas.</summary>
    Task<IReadOnlyList<Quotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        QuotationExportCursor? after,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Si hay al menos una cotización con esos filtros: el paso 3 de D4, antes de encolar
    /// una exportación. Mismos filtros y misma semántica de <c>clientIds</c> que
    /// <see cref="SearchAsync"/>.</summary>
    Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdBefore,
        string? quotationNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// De los ids dados, cuales tienen al menos una linea. Existe para el listado: su busqueda no
    /// trae las lineas --la tabla no las pinta-- pero la fila si necesita saber si la cotizacion
    /// ya es un documento presentable, y preguntarlo cargando cada agregado seria un SELECT por
    /// fila.
    /// </summary>
    Task<IReadOnlySet<Guid>> FindIdsWithItemsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken);

    void Add(Quotation quotation);

    /// <summary>
    /// Agrega una entrada a la línea de tiempo de la cotización (§2.3 del modelo de datos). Vive
    /// en este repositorio y no en uno propio: siempre se escribe junto con una mutación de
    /// <see cref="Quotation"/>, en la misma unidad de trabajo.
    /// </summary>
    void AddHistoryEntry(QuotationHistoryEntry entry);

    /// <summary>
    /// El PDF ya generado de una cotización, si existe. Vive en este repositorio y no en uno
    /// propio por el mismo motivo que <see cref="AddHistoryEntry"/>: se escribe en la misma
    /// unidad de trabajo que la operación que lo produjo -- exportar o enviar.
    /// </summary>
    Task<QuotationPdf?> FindPdfAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);

    void AddPdf(QuotationPdf pdf);

    /// <summary>
    /// La línea de tiempo completa de una cotización, de lo más nuevo a lo más viejo. Sin paginar
    /// a propósito: una cotización acumula decenas de entradas, no miles, y la pantalla las
    /// muestra todas — paginar acá sería complejidad sin caso de uso detrás.
    /// </summary>
    Task<IReadOnlyList<QuotationHistoryEntry>> ListHistoryAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);
}

/// <summary>
/// Dónde quedó el export (spec 2026-09-12, D8): la fecha de alta y el número de la última fila
/// leída. El número y no el id porque <see cref="QuotationId"/> no se compara, y el número es único
/// por tenant (<c>IX_quotations_tenant_number</c>), así que la clave nunca empata.
/// </summary>
public sealed record QuotationExportCursor(DateTimeOffset CreatedAt, string QuotationNumber);
