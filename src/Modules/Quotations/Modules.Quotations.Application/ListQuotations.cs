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
    int Page,
    int PageSize) : IQuery<QuotationPage>;

/// <summary>Fila resumida del listado (US-8): "cada fila muestra número de cotización, cliente,
/// asesora, fecha, total y estado" -- no hace falta traer las líneas de producto de cada
/// cotización sólo para pintar una tabla.</summary>
public sealed record QuotationListItemDto(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    Guid AdvisorId,
    string Status,
    DateTimeOffset CreatedAt,
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

        var (quotations, total) = await repository.SearchAsync(
            query.TenantId,
            query.ClientId,
            advisorId,
            status,
            query.CreatedFrom,
            query.CreatedTo,
            page,
            pageSize,
            cancellationToken);

        var items = quotations.Select(quotation => quotation.ToListItemDto()).ToArray();
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
