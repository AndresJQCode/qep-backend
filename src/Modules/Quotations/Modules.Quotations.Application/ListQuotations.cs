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
    decimal Total);

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
        var status = QuotationListing.ParseStatus(query.Status);
        var advisorId = query.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await QuotationListing.ResolveClientIdsByNitAsync(
            customerLookup, query.TenantId, query.ClientNit, cancellationToken);

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
        // fila. La fila muestra el correo aunque la asesora tenga nombre: el nombre sólo llega al
        // PDF (spec 2026-09-11, D1).
        var advisors = quotations.Count == 0
            ? new Dictionary<Guid, QuotationAdvisor>()
            : await advisorLookup.FindAsync(
                query.TenantId,
                quotations.Select(quotation => quotation.AdvisorId.Value).Distinct().ToArray(),
                cancellationToken);

        var items = quotations
            .Select(quotation => quotation.ToListItemDto(
                clientNames.GetValueOrDefault(quotation.ClientId),
                advisors.GetValueOrDefault(quotation.AdvisorId.Value)?.Email))
            .ToArray();
        return new QuotationPage(items, total, page, pageSize);
    }
}
