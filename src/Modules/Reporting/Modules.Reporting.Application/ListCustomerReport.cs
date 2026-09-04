using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El listado paginado del padron de clientes (Clientes CUC), con clasificacion y geografia
/// resueltas.
///
/// El filtro por departamento se resuelve del otro lado del puerto: <c>Customer</c> solo guarda
/// <c>CityId</c>, asi que filtrar por departamento primero necesita traducirlo a que ciudades
/// caen dentro — exactamente lo que ya hace el listado de <c>customers</c>.
/// </summary>
public sealed record ListCustomerReportQuery(
    CustomerReportFilter Filter,
    int Page,
    int PageSize) : IQuery<ReportPage<CustomerReportItemDto>>;

public sealed class ListCustomerReportHandler(
    ICustomerReportSource source,
    IValidator<CustomerReportFilter> validator,
    IExecutionContext executionContext)
    : IQueryHandler<ListCustomerReportQuery, ReportPage<CustomerReportItemDto>>
{
    public async Task<ReportPage<CustomerReportItemDto>> HandleAsync(
        ListCustomerReportQuery query,
        CancellationToken cancellationToken)
    {
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.CustomerRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var page = ReportPaging.NormalizePage(query.Page);
        var pageSize = ReportPaging.NormalizePageSize(query.PageSize);

        var (items, total) = await source.ListAsync(
            query.Filter.ToCriteria(), page, pageSize, cancellationToken);

        return new ReportPage<CustomerReportItemDto>(items, total, page, pageSize);
    }
}
