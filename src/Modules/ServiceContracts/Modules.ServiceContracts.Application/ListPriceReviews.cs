using BuildingBlocks.Application;
using BuildingBlocks.Domain.Pricing;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// <c>State</c> es <c>all | due | held</c>, sin distinguir mayúsculas; el endpoint manda <c>all</c> si el cliente no lo pidió.
/// <c>AssigneeMe</c> es <c>assignee=me</c>.
/// </summary>
public sealed record ListPriceReviewsQuery(
    Guid TenantId,
    string State,
    bool AssigneeMe,
    string? Search,
    int Page,
    int PageSize)
    : IQuery<PriceReviewPageDto>
{
    public const string DefaultState = "all";

    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

public sealed class ListPriceReviewsValidator : AbstractValidator<ListPriceReviewsQuery>
{
    public ListPriceReviewsValidator()
    {
        RuleFor(query => query.State)
            .Must(state => ListPriceReviewsHandler.TryParseState(state, out _))
            .WithMessage("State must be all, due or held.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListPriceReviewsQuery.MaxPageSize);
    }
}

/// <summary>
/// Los contratos que esperan una decisión de precio (spec reajuste §8). Las revisiones se calculan en memoria sobre los
/// contratos activos (<see cref="PriceReviewReader"/>), así que el estado, el orden y la página también; el encargado y la
/// búsqueda los resuelve la base con la misma regla de la bandeja de cobros.
/// </summary>
public sealed class ListPriceReviewsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ITenantPriceRounding priceRounding,
    IMembershipDirectory membershipDirectory,
    IServiceContractsCustomerLookup customers,
    IServiceContractsMemberDirectory members,
    ICustomerBillingProfileRepository profiles,
    IServiceContractRepository services,
    IIpcRateRepository ipcRates,
    PriceReviewReader reader,
    IValidator<ListPriceReviewsQuery> validator)
    : IQueryHandler<ListPriceReviewsQuery, PriceReviewPageDto>
{
    /// <summary>Null en <paramref name="state"/> de salida es «todas».</summary>
    internal static bool TryParseState(string? text, out ReviewState? state)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "all":
                state = null;
                return true;
            case "due":
                state = ReviewState.Due;
                return true;
            case "held":
                state = ReviewState.Held;
                return true;
            default:
                state = null;
                return false;
        }
    }

    public async Task<PriceReviewPageDto> HandleAsync(ListPriceReviewsQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Read, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!TryParseState(query.State, out var state))
        {
            throw new InvalidOperationException($"State '{query.State}' passed validation but is not a review state.");
        }

        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;
        var open = await reader.ListOpenAsync(query.TenantId, today, cancellationToken);
        var rates = (await ipcRates.ListAsync(query.TenantId, cancellationToken)).ToDictionary(rate => rate.Year, rate => rate.Rate);
        var summary = PriceReviewReader.Count(open);
        var ipcYears = open
            .Where(entry => entry.Review.IpcYear is not null)
            .GroupBy(entry => entry.Review.IpcYear!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new IpcYearNeedDto(group.Key, RateOf(rates, group.Key), group.Count()))
            .ToList();

        Guid? assigneeMemberId = null;
        if (query.AssigneeMe)
        {
            assigneeMemberId = await membershipDirectory.FindActiveMembershipIdAsync(
                executionContext.SubjectId, query.TenantId, cancellationToken);
            if (assigneeMemberId is null)
            {
                // Sin membresía activa no se es encargado de nadie, como en la bandeja de cobros.
                return new PriceReviewPageDto([], 0, query.Page, query.PageSize, summary, ipcYears);
            }
        }

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var customerIdsBySearch = search is null
            ? null
            : await customers.SearchIdsAsync(query.TenantId, search, cancellationToken);
        var inState = open.Where(entry => state is null || entry.Review.State == state).ToList();
        var allowed = await services.FilterIdsAsync(
            query.TenantId,
            inState.Select(entry => entry.Service.Id).ToArray(),
            assigneeMemberId,
            search,
            customerIdsBySearch,
            cancellationToken);
        var filtered = inState
            .Where(entry => allowed.Contains(entry.Service.Id))
            .OrderBy(entry => entry.Review.GenerationDate)
            .ThenBy(entry => entry.Service.Id.Value)
            .ToList();
        var page = filtered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();

        var rows = await ComposeAsync(query.TenantId, page, rates, cancellationToken);

        return new PriceReviewPageDto(rows, filtered.Count, query.Page, query.PageSize, summary, ipcYears);
    }

    private static decimal? RateOf(Dictionary<int, decimal> rates, int? year) =>
        year is { } value && rates.TryGetValue(value, out var rate) ? rate : null;

    // Cada lectura es un viaje por lote (clientes, perfiles, miembros) y la regla de redondeo una por moneda.
    private async Task<IReadOnlyList<PriceReviewRowDto>> ComposeAsync(
        Guid tenantId,
        List<PriceReviewEntry> page,
        Dictionary<int, decimal> rates,
        CancellationToken cancellationToken)
    {
        if (page.Count == 0)
        {
            return [];
        }

        var customerIds = page.Select(entry => entry.Service.CustomerId).Distinct().ToArray();
        var customerRefs = (await customers.FindManyAsync(tenantId, customerIds, cancellationToken))
            .ToDictionary(customer => customer.Id);
        var assignees = (await profiles.ListByCustomersAsync(tenantId, customerIds, cancellationToken))
            .ToDictionary(profile => profile.CustomerId, profile => profile.AssigneeMemberId);
        var memberIds = assignees.Values
            .Concat(page.Select(entry => entry.Pending?.CreatedByMemberId).OfType<Guid>())
            .Distinct()
            .ToArray();
        var membersById = memberIds.Length == 0
            ? new Dictionary<Guid, ServiceContractsMemberRef>()
            : (await members.FindAsync(tenantId, memberIds, cancellationToken)).ToDictionary(member => member.MemberId);

        MemberRefDto? MemberOf(Guid? memberId) =>
            memberId is { } id ? ServiceContractsMapping.ToMemberRefDto(id, membersById.GetValueOrDefault(id)) : null;

        var roundings = new Dictionary<string, PriceRounding>(StringComparer.Ordinal);
        var rows = new List<PriceReviewRowDto>(page.Count);
        foreach (var entry in page)
        {
            var service = entry.Service;
            var rate = RateOf(rates, entry.Review.IpcYear);
            decimal? computed = null;
            if (rate is { } value)
            {
                if (!roundings.TryGetValue(service.Currency, out var rounding))
                {
                    rounding = await priceRounding.GetAsync(tenantId, service.Currency, cancellationToken);
                    roundings[service.Currency] = rounding;
                }

                computed = PriceReview.IpcPrice(service.Price, value, service.IpcExtraPoints, rounding);
            }

            var customer = customerRefs.TryGetValue(service.CustomerId, out var found)
                ? new CustomerRefDto(found.Id, found.Name, found.PhoneE164, found.Email, found.BusinessName)
                : new CustomerRefDto(service.CustomerId, ChargeDtoComposer.UnknownCustomerName, null, null);
            rows.Add(new PriceReviewRowDto(
                new PriceReviewServiceRefDto(
                    service.Id.Value, service.Name, customer, service.Price, service.Currency, service.Cycle.ToString(), service.Version),
                MemberOf(assignees.TryGetValue(service.CustomerId, out var assignee) ? assignee : null),
                ServiceContractsMapping.ToPriceReviewDto(
                    service, entry.Pending, entry.Review, MemberOf(entry.Pending?.CreatedByMemberId)),
                rate,
                computed));
        }

        return rows;
    }
}
