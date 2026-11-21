using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.CallFailures;

/// <summary>
/// <c>GET …/siigo/call-failures?page&amp;pageSize&amp;operation&amp;status&amp;from&amp;to</c> (<c>siigo.calls.read</c>). <c>Operation</c> es el nombre
/// de una <see cref="SiigoCallOperation"/>: un nombre desconocido es 422 <c>validation.failed</c> con el campo, no un 400 de enlace.
/// </summary>
public sealed record ListSiigoCallFailuresQuery(
    Guid TenantId, string? Operation, int? Status, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize)
    : IQuery<SiigoCallFailurePageDto>;

public static class SiigoCallFailurePaging
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize,
    };
}

public sealed class ListSiigoCallFailuresValidator : AbstractValidator<ListSiigoCallFailuresQuery>
{
    public ListSiigoCallFailuresValidator()
    {
        RuleFor(query => query.Operation)
            .Must(operation => Enum.TryParse<SiigoCallOperation>(operation, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            .When(query => !string.IsNullOrWhiteSpace(query.Operation))
            .WithMessage("The operation is not one of the known Siigo operations.")
            .OverridePropertyName("operation");
        RuleFor(query => query.Status).InclusiveBetween(100, 599).When(query => query.Status is not null).OverridePropertyName("status");
    }
}

/// <summary>
/// Spec 2026-11-21. En orden: tenant, permiso y módulo (403, nunca 404); la forma (422). Sin curl ni respuesta en la lista: ver
/// <see cref="SiigoCallFailureSummaryDto"/>.
/// </summary>
public sealed class ListSiigoCallFailuresHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoCallFailureRepository repository,
    IValidator<ListSiigoCallFailuresQuery> validator) : IQueryHandler<ListSiigoCallFailuresQuery, SiigoCallFailurePageDto>
{
    public async Task<SiigoCallFailurePageDto> HandleAsync(ListSiigoCallFailuresQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, SiigoPermissions.CallsRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var page = SiigoCallFailurePaging.NormalizePage(query.Page);
        var pageSize = SiigoCallFailurePaging.NormalizePageSize(query.PageSize);
        SiigoCallOperation? operation = string.IsNullOrWhiteSpace(query.Operation)
            ? null
            : Enum.Parse<SiigoCallOperation>(query.Operation, ignoreCase: true);

        var (items, total) = await repository.SearchAsync(
            query.TenantId, operation, query.Status, query.From, query.To, page, pageSize, cancellationToken);
        return new SiigoCallFailurePageDto([.. items.Select(SiigoCallFailureSummaryDto.From)], total, page, pageSize);
    }
}
