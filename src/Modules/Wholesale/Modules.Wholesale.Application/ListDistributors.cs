using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary><c>Status</c> llega como texto para que un valor raro sea un 422 con <c>errors.Status</c> y no un 400 del binding.</summary>
public sealed record ListDistributorsQuery(
    Guid TenantId,
    string? Status,
    string? Search,
    int Page,
    int PageSize) : IQuery<DistributorsResponse>;

/// <summary>D-22: <c>status</c> nulo o vacío es «todos»; si viene, el nombre de un estado sin importar mayúsculas.</summary>
public sealed class ListDistributorsValidator : AbstractValidator<ListDistributorsQuery>
{
    public ListDistributorsValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => DistributorStatusFilter.TryParse(status, out _))
            .WithMessage("Status must be one of Invited, Active or Blocked.");
    }
}

internal static class DistributorStatusFilter
{
    /// <summary>Por nombre y no con <c>Enum.TryParse</c> a secas, que aceptaría <c>"1"</c>.</summary>
    public static bool TryParse(string? value, out DistributorStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        foreach (var candidate in Enum.GetValues<DistributorStatus>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// La lista con sus pestañas: página por <c>created_at</c> descendente y <c>Counts</c> con las cuatro claves, con
/// <c>search</c> aplicado y sin <c>status</c>.
/// </summary>
public sealed class ListDistributorsHandler(
    IExecutionContext executionContext,
    IDistributorRepository distributors,
    DistributorComposer composer,
    IValidator<ListDistributorsQuery> validator)
    : IQueryHandler<ListDistributorsQuery, DistributorsResponse>
{
    public async Task<DistributorsResponse> HandleAsync(ListDistributorsQuery query, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, query.TenantId, WholesalePermissions.DistributorRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!DistributorStatusFilter.TryParse(query.Status, out var status))
        {
            throw new InvalidOperationException($"Status '{query.Status}' passed validation but is not a distributor status.");
        }

        var page = DistributorPaging.NormalizePage(query.Page);
        var pageSize = DistributorPaging.NormalizePageSize(query.PageSize);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();

        var (items, total) = await distributors.SearchAsync(query.TenantId, status, search, page, pageSize, cancellationToken);
        var counts = await distributors.CountByStatusAsync(query.TenantId, search, cancellationToken);
        var summaries = await composer.SummariesAsync(query.TenantId, items, cancellationToken);

        return new DistributorsResponse(summaries, total, page, pageSize, ToCounts(counts));
    }

    private static DistributorStatusCountsDto ToCounts(IReadOnlyDictionary<DistributorStatus, int> counts)
    {
        int CountOf(DistributorStatus status) => counts.TryGetValue(status, out var count) ? count : 0;

        var invited = CountOf(DistributorStatus.Invited);
        var active = CountOf(DistributorStatus.Active);
        var blocked = CountOf(DistributorStatus.Blocked);
        return new DistributorStatusCountsDto(invited, active, blocked, invited + active + blocked);
    }
}
