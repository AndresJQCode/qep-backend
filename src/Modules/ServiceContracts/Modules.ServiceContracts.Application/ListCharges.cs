using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// <c>Status</c> es <c>open | paid | cancelled</c>, sin distinguir mayúsculas (spec §4); el endpoint manda <c>open</c> si el
/// cliente no lo pidió. <c>AssigneeMe</c> es <c>assignee=me</c>.
/// </summary>
public sealed record ListChargesQuery(
    Guid TenantId,
    string Status,
    bool AssigneeMe,
    Guid? CustomerId,
    string? Search,
    int Page,
    int PageSize)
    : IQuery<ChargePageDto>
{
    public const string DefaultStatus = "open";

    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

public sealed class ListChargesValidator : AbstractValidator<ListChargesQuery>
{
    public ListChargesValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => ListChargesHandler.TryParseStatus(status, out _))
            .WithMessage("Status must be open, paid or cancelled.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListChargesQuery.MaxPageSize);
    }
}

/// <summary>
/// La bandeja «Por cobrar» (spec §4). Resuelve la «hoy» del tenant una sola vez y la usa para marcar <c>Overdue</c> y para el
/// <c>summary</c>, que es la deuda del tenant entero sin los filtros de la lista.
/// </summary>
public sealed class ListChargesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IMembershipDirectory membershipDirectory,
    IServiceContractsCustomerLookup customers,
    IChargeRepository charges,
    IServiceContractRepository services,
    ChargeDtoComposer composer,
    IValidator<ListChargesQuery> validator)
    : IQueryHandler<ListChargesQuery, ChargePageDto>
{
    internal static bool TryParseStatus(string? text, out ChargeListStatus status)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "open":
                status = ChargeListStatus.Open;
                return true;
            case "paid":
                status = ChargeListStatus.Paid;
                return true;
            case "cancelled":
                status = ChargeListStatus.Cancelled;
                return true;
            default:
                status = default;
                return false;
        }
    }

    public async Task<ChargePageDto> HandleAsync(ListChargesQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Read, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!TryParseStatus(query.Status, out var status))
        {
            throw new InvalidOperationException($"Status '{query.Status}' passed validation but is not a charge list status.");
        }

        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;
        var summary = await charges.SummaryAsync(query.TenantId, today, cancellationToken);

        Guid? assigneeMemberId = null;
        if (query.AssigneeMe)
        {
            assigneeMemberId = await membershipDirectory.FindActiveMembershipIdAsync(
                executionContext.SubjectId, query.TenantId, cancellationToken);
            if (assigneeMemberId is null)
            {
                // Sin membresía activa no se es encargado de nadie: vacía, no todo el tenant ni 403 (el resumen sigue siendo el del tenant).
                return new ChargePageDto([], 0, query.Page, query.PageSize, summary);
            }
        }

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var customerIdsBySearch = search is null
            ? null
            : await customers.SearchIdsAsync(query.TenantId, search, cancellationToken);
        var (items, total) = await charges.ListAsync(
            new ChargeQuery(
                query.TenantId, status, query.CustomerId, assigneeMemberId, search, customerIdsBySearch, query.Page, query.PageSize),
            cancellationToken);

        var servicesById = (await services.FindManyAsync(
                query.TenantId, items.Select(charge => charge.ServiceId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(service => service.Id);
        var dtos = await composer.ComposeAsync(query.TenantId, items, servicesById, today, cancellationToken);

        return new ChargePageDto(dtos, total, query.Page, query.PageSize, summary);
    }
}
