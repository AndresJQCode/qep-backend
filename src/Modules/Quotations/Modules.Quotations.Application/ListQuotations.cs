using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ListQuotationsQuery(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    DateOnly? CreatedFrom,
    DateOnly? CreatedTo,
    /// <summary>Texto libre contra el NIT/numero de identificacion del cliente — Quotation no
    /// guarda ese dato, asi que el handler lo resuelve a ids contra Customers antes de filtrar.</summary>
    string? ClientNit,
    string? QuotationNumber,
    int Page,
    int PageSize) : IQuery<QuotationPage>;

/// <summary>Fila resumida del listado (US-8): "cada fila muestra número de cotización, cliente,
/// asesora, fecha, total y estado" -- no hace falta traer las líneas de producto de cada
/// cotización sólo para pintar una tabla.</summary>
public sealed record QuotationListItemDto(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    /// <summary>Nombre del cliente, resuelto contra Customers para toda la página de una sola
    /// vez. Nullable porque <c>ClientId</c> es una referencia blanda entre módulos: si no
    /// resuelve, la fila viaja igual y quien la muestra elige el respaldo.</summary>
    string? ClientName,
    Guid AdvisorId,
    /// <summary>Correo de la asesora, resuelto contra Tenancy/Identity para toda la página de
    /// una vez. Nullable por la misma razón que <c>ClientName</c>: es una referencia blanda
    /// entre módulos.</summary>
    string? AdvisorEmail,
    string Status,
    DateTimeOffset CreatedAt,
    /// <summary>La moneda de <c>Total</c>: la grilla mezcla cotizaciones en pesos y en dolares
    /// y una columna de importes sin moneda seria ilegible.</summary>
    string Currency,
    decimal Total,
    /// <summary>Si el envio tiene sentido ahora (<see cref="Quotation.CanBeSent"/>). Mismo campo
    /// y mismo significado que en <c>QuotationResponse</c>: la fila ofrece las mismas acciones
    /// que el detalle, y sin esto quien las pinta tendria que pedir la cotizacion entera por
    /// fila solo para saber cuales habilitar.</summary>
    bool CanBeSent,
    /// <summary>Si ya es un documento presentable: tiene al menos una linea, vigencia y cuenta
    /// de cobro. Es lo que el detalle enumera como "lo que le falta" antes de dejar enviar, y
    /// viaja resuelto porque las lineas no vienen en la fila.</summary>
    bool IsComplete,
    /// <summary>La venta que salio de esta cotizacion, si ya se convirtio. <c>null</c> es "sin
    /// convertir": no hay un estado "convertida" que mirar --la cotizacion se queda en
    /// <c>Sent</c>-- y la existencia de la venta es la unica senal.</summary>
    Guid? SaleId,
    /// <summary><c>Pending</c> mientras esa venta espera el visto bueno, <c>Approved</c> despues.
    /// <c>null</c> cuando no hay venta.</summary>
    string? SaleStatus);

/// <summary>Una página del listado y el total que la UI necesita para paginar. Mismo criterio
/// que <c>CustomerPage</c> en Customers.</summary>
public sealed record QuotationPage(
    IReadOnlyList<QuotationListItemDto> Items,
    int Total,
    int Page,
    int PageSize);

public static class QuotationPaging
{
    public const int DefaultPageSize = 50;

    /// <summary>Tope duro, mismo criterio y mismo valor que <c>CustomerPaging.MaxPageSize</c>:
    /// sin límite, un <c>?pageSize=1000000</c> se traduce en traerse el tenant entero a
    /// memoria.</summary>
    public const int MaxPageSize = 200;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize
    };
}

public sealed class ListQuotationsHandler(
    IQuotationRepository repository,
    ISaleRepository saleRepository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListQuotationsQuery, QuotationPage>
{
    public async Task<QuotationPage> HandleAsync(
        ListQuotationsQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, QuotationsPermissions.QuotationRead);

        var page = QuotationPaging.NormalizePage(query.Page);
        var pageSize = QuotationPaging.NormalizePageSize(query.PageSize);
        var status = ParseStatus(query.Status);
        var advisorId = query.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;

        // El NIT no vive en Quotation: se resuelve a ids contra Customers antes de filtrar, mismo
        // criterio que ListCustomersHandler con el filtro de Departamento -> ids de ciudad. Sin
        // termino, `clientIds` queda null ("sin filtro"); con termino sin match, la busqueda ya
        // sabe que no hay nada que traer.
        IReadOnlyCollection<Guid>? clientIds = null;
        if (!string.IsNullOrWhiteSpace(query.ClientNit))
        {
            var matchedIds = await customerLookup.SearchIdsByIdentificationAsync(
                query.TenantId, query.ClientNit, cancellationToken);
            clientIds = matchedIds.ToArray();
        }

        var (quotations, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            query.CreatedFrom,
            query.CreatedTo,
            query.QuotationNumber,
            page,
            pageSize,
            cancellationToken);

        // Los nombres de cliente de toda la pagina en una sola consulta: la tabla muestra el
        // nombre, no el id, y resolverlo del lado del que consume el listado es un GET por fila
        // contra Customers. Los ids van sin repetir -- varias cotizaciones del mismo cliente son
        // lo normal en una pagina.
        var clientNames = quotations.Count == 0
            ? new Dictionary<Guid, string>()
            : await customerLookup.FindNamesAsync(
                query.TenantId,
                quotations.Select(quotation => quotation.ClientId).Distinct().ToArray(),
                cancellationToken);

        // Misma idea que los nombres de cliente: una ida por página, con los ids sin repetir.
        // Antes el frontend se traía el padrón de miembros entero para poner un correo en cada
        // fila.
        var advisorEmails = quotations.Count == 0
            ? new Dictionary<Guid, string?>()
            : await advisorLookup.FindEmailsAsync(
                query.TenantId,
                quotations.Select(quotation => quotation.AdvisorId.Value).Distinct().ToArray(),
                cancellationToken);

        var quotationIds = quotations.Select(quotation => quotation.Id).ToArray();

        // Cuales tienen al menos una linea, en una sola consulta para toda la pagina. La busqueda
        // no trae las lineas a proposito --la tabla no las pinta-- y preguntarlo por fila seria
        // exactamente el N+1 que esa decision evita.
        var withItems = quotations.Count == 0
            ? new HashSet<Guid>()
            : await repository.FindIdsWithItemsAsync(
                query.TenantId, quotationIds, cancellationToken);

        // La venta de cada cotizacion --1:1-- para que la fila sepa si ya se convirtio y si esa
        // venta sigue esperando el visto bueno. Otra ida por pagina, mismo criterio que los
        // nombres de cliente y los correos de las asesoras.
        //
        // Solo para quien puede leer ventas: poder ver cotizaciones no es poder saber cuales se
        // cobraron. Sin ese permiso la fila viaja sin venta --que es todo lo que esa persona
        // puede saber-- y la pantalla no le ofrece ir a aprobar nada, que tampoco podria.
        var sales = quotations.Count == 0
            || !executionContext.HasPermission(SalesPermissions.SaleRead)
            ? new Dictionary<Guid, Sale>()
            : await saleRepository.FindByQuotationIdsAsync(
                query.TenantId, quotationIds, cancellationToken);

        var items = quotations
            .Select(quotation => quotation.ToListItemDto(
                clientNames.GetValueOrDefault(quotation.ClientId),
                advisorEmails.GetValueOrDefault(quotation.AdvisorId.Value),
                withItems.Contains(quotation.Id.Value),
                sales.GetValueOrDefault(quotation.Id.Value)))
            .ToArray();
        return new QuotationPage(items, total, page, pageSize);
    }

    // El valor llega como texto libre por query string, así que una entrada que no matchea
    // ningún valor del enum es un 422 con código de dominio -- no un filtro que en silencio no
    // devuelve nada, ni un 500 de un cast que falla.
    private static QuotationStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return Enum.TryParse<QuotationStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : throw new QuotationsDomainException(
                "quotation.quotation.status_invalid",
                $"'{status}' is not a valid quotation status.");
    }
}
