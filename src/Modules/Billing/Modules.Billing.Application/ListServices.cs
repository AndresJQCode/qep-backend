using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>
/// <c>Status</c> es <c>active | paused | cancelled | all</c>, sin distinguir mayúsculas (spec §4); el endpoint manda
/// <c>active</c> si el cliente no lo pidió.
/// </summary>
public sealed record ListServicesQuery(
    Guid TenantId,
    Guid? CustomerId,
    string Status,
    string? Search,
    int Page,
    int PageSize)
    : IQuery<ServicePageDto>
{
    public const string DefaultStatus = "active";

    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

public sealed class ListServicesValidator : AbstractValidator<ListServicesQuery>
{
    public ListServicesValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => ListServicesHandler.TryParseStatus(status, out _))
            .WithMessage("Status must be active, paused, cancelled or all.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListServicesQuery.MaxPageSize);
    }
}

/// <summary>
/// La lista va por <c>customer.name</c> y luego <c>name</c> (spec §4). El nombre del cliente vive en el esquema de Customers, así
/// que el repositorio trae los servicios filtrados sin paginar, el handler ordena con los nombres que resuelve
/// <see cref="IBillingCustomerLookup"/> y corta la página en memoria (ruling de la Tarea 4: los servicios por tenant son pocos
/// en la fase 1; si crecen, se denormaliza el nombre del cliente).
/// </summary>
public sealed class ListServicesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IBillingCustomerLookup customers,
    IBillingServiceRepository services,
    ServiceDetailComposer composer,
    IValidator<ListServicesQuery> validator)
    : IQueryHandler<ListServicesQuery, ServicePageDto>
{
    /// <summary>Null en <paramref name="status"/> de salida es «todos».</summary>
    internal static bool TryParseStatus(string? text, out BillingServiceStatus? status)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "active":
                status = BillingServiceStatus.Active;
                return true;
            case "paused":
                status = BillingServiceStatus.Paused;
                return true;
            case "cancelled":
                status = BillingServiceStatus.Cancelled;
                return true;
            case "all":
                status = null;
                return true;
            default:
                status = null;
                return false;
        }
    }

    public async Task<ServicePageDto> HandleAsync(ListServicesQuery query, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, BillingPermissions.Read, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!TryParseStatus(query.Status, out var status))
        {
            throw new InvalidOperationException($"Status '{query.Status}' passed validation but is not a service status.");
        }

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var customerIdsBySearch = search is null
            ? null
            : await customers.SearchIdsAsync(query.TenantId, search, cancellationToken);
        var filtered = await services.ListAllAsync(
            new BillingServiceQuery(query.TenantId, query.CustomerId, status, search, customerIdsBySearch, 1, int.MaxValue),
            cancellationToken);

        var customerRefs = (await customers.FindManyAsync(
                query.TenantId, filtered.Select(service => service.CustomerId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(customer => customer.Id, customer => new CustomerRefDto(customer.Id, customer.Name, customer.PhoneE164, customer.Email));

        // Invariante y con desempates: el orden no depende de la cultura del servidor ni de la base, y paginar es estable. Un
        // cliente que ya no resuelve (nombre vacío) va primero, junto, en vez de desaparecer de la lista.
        var ordered = filtered
            .OrderBy(service => customerRefs.TryGetValue(service.CustomerId, out var customer) ? customer.Name : string.Empty, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(service => service.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(service => service.Id.Value)
            .ToList();
        var page = ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();

        var items = await composer.ComposeSummariesAsync(query.TenantId, page, customerRefs, cancellationToken);

        return new ServicePageDto(items, ordered.Count, query.Page, query.PageSize);
    }
}
