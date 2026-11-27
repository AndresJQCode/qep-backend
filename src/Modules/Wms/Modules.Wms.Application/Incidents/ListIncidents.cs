using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Control;

namespace Modules.Wms.Application;

public sealed record ListIncidentsQuery(
    Guid TenantId, Guid? WarehouseId, string? Status, string? Type, int Page, int PageSize) : IQuery<IncidentPageDto>;

/// <summary>Un estado o un tipo que no es un nombre exacto del enum es un 422 <c>validation.failed</c> con la clave del campo.</summary>
public sealed class ListIncidentsValidator : AbstractValidator<ListIncidentsQuery>
{
    public ListIncidentsValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => Enum.GetNames<IncidentStatus>().Contains(status, StringComparer.Ordinal))
            .When(query => !string.IsNullOrWhiteSpace(query.Status))
            .WithMessage($"The status must be one of: {string.Join(", ", Enum.GetNames<IncidentStatus>())}.");
        RuleFor(query => query.Type)
            .Must(type => Enum.GetNames<IncidentType>().Contains(type, StringComparer.Ordinal))
            .When(query => !string.IsNullOrWhiteSpace(query.Type))
            .WithMessage($"The type must be one of: {string.Join(", ", Enum.GetNames<IncidentType>())}.");
    }
}

/// <summary>La bandeja de novedades, las más recientes primero. La lee quien ve documentos (los operarios también: ven lo que
/// reportaron y lo que quedó pendiente, DA-49).</summary>
public sealed class ListIncidentsHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IIncidentRepository incidents,
    IncidentDtoComposer composer,
    IValidator<ListIncidentsQuery> validator)
    : IQueryHandler<ListIncidentsQuery, IncidentPageDto>
{
    public async Task<IncidentPageDto> HandleAsync(ListIncidentsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.DocumentRead, warehouseId: query.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var page = WmsPaging.NormalizePage(query.Page);
        var pageSize = WmsPaging.NormalizePageSize(query.PageSize);
        IncidentStatus? status = string.IsNullOrWhiteSpace(query.Status) ? null : Enum.Parse<IncidentStatus>(query.Status);
        IncidentType? type = string.IsNullOrWhiteSpace(query.Type) ? null : Enum.Parse<IncidentType>(query.Type);

        var result = await incidents.ListAsync(query.TenantId, query.WarehouseId, status, type, page, pageSize, cancellationToken);
        var items = await composer.ComposeSummariesAsync(query.TenantId, result.Items, cancellationToken);
        return new IncidentPageDto(items, page, pageSize, result.TotalCount);
    }
}
