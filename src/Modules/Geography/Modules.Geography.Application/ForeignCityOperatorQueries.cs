using BuildingBlocks.Application;
using FluentValidation;
using Modules.Geography.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Application;

// Spec 2026-11-26 «Consola del operador»: las lecturas de la consola de ciudades del exterior. En cada handler la autorización
// de operador (Tenancy) va primero, antes de validar o tocar un repositorio: un 403 no depende ni de la entrada ni de si el id
// existe. Mismo diseño que CityOperatorQueries, por país en vez de por departamento.

public sealed record ListBillingCountriesQuery(TenantId TenantId) : IQuery<IReadOnlyList<BillingCountryDto>>;

public sealed record SearchBillingCitiesQuery(TenantId TenantId, string? Country, string? Search)
    : IQuery<IReadOnlyList<BillingCityRefDto>>;

public sealed record ListUnresolvedForeignCitiesQuery(
    TenantId TenantId, string? Country, string? Status, string? Search, int Page, int PageSize)
    : IQuery<UnresolvedForeignCityPageDto>;

public sealed record GetUnresolvedForeignCityQuery(TenantId TenantId, UnresolvedForeignCityId Id)
    : IQuery<UnresolvedForeignCityDetailDto>;

public sealed record ListForeignCitySynonymsQuery(TenantId TenantId, string? Country, string? Search, int Page, int PageSize)
    : IQuery<ForeignCitySynonymPageDto>;

/// <summary>El país es obligatorio y de dos letras: el catálogo se busca dentro de un país. Un valor inválido es
/// <c>422 validation.failed</c> con la clave del campo (<c>Country</c>, <c>Search</c>), nunca 500.</summary>
public sealed class SearchBillingCitiesValidator : AbstractValidator<SearchBillingCitiesQuery>
{
    public SearchBillingCitiesValidator()
    {
        RuleFor(query => query.Country)
            .NotEmpty()
            .Must(country => country is null || ForeignCityOperatorInput.IsCountryCode(country))
            .WithMessage("The country must be a two-letter code.");
        RuleFor(query => query.Search).MaximumLength(CityOperatorInput.MaxSearchLength);
    }
}

/// <summary>Un valor fuera de rango o un estado que no es <c>Pending</c> ni <c>Discarded</c> es <c>422 validation.failed</c>, con
/// la clave del campo (<c>Country</c>, <c>Status</c>, <c>Page</c>, <c>PageSize</c>, <c>Search</c>), nunca 500.</summary>
public sealed class ListUnresolvedForeignCitiesValidator : AbstractValidator<ListUnresolvedForeignCitiesQuery>
{
    public ListUnresolvedForeignCitiesValidator()
    {
        RuleFor(query => query.Country)
            .Must(country => country is null || ForeignCityOperatorInput.IsCountryCode(country))
            .WithMessage("The country must be a two-letter code.");
        RuleFor(query => query.Status)
            .Must(status => status is null || CityOperatorInput.IsListStatus(status))
            .WithMessage("The status must be Pending or Discarded.");
        RuleFor(query => query.Search).MaximumLength(CityOperatorInput.MaxSearchLength);
        RuleFor(query => query.Page).InclusiveBetween(1, CityOperatorInput.MaxPage);
        RuleFor(query => query.PageSize).InclusiveBetween(1, CityOperatorInput.MaxPageSize);
    }
}

public sealed class ListForeignCitySynonymsValidator : AbstractValidator<ListForeignCitySynonymsQuery>
{
    public ListForeignCitySynonymsValidator()
    {
        RuleFor(query => query.Country)
            .Must(country => country is null || ForeignCityOperatorInput.IsCountryCode(country))
            .WithMessage("The country must be a two-letter code.");
        RuleFor(query => query.Search).MaximumLength(CityOperatorInput.MaxSearchLength);
        RuleFor(query => query.Page).InclusiveBetween(1, CityOperatorInput.MaxPage);
        RuleFor(query => query.PageSize).InclusiveBetween(1, CityOperatorInput.MaxPageSize);
    }
}

public sealed class ListBillingCountriesHandler(
    IBillingCityRepository billingCities,
    IUnresolvedForeignCityRepository unresolved,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<ListBillingCountriesQuery, IReadOnlyList<BillingCountryDto>>
{
    public async Task<IReadOnlyList<BillingCountryDto>> HandleAsync(
        ListBillingCountriesQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);

        var countries = await billingCities.ListCountriesAsync(cancellationToken);
        var pending = await unresolved.CountPendingByCountryAsync(cancellationToken);

        // Con pendientes primero; luego por el nombre sin tildes, así «Emiratos Árabes Unidos» no cae al final por la tilde.
        return countries
            .Select(country => new BillingCountryDto(country.Code, country.Name, pending.GetValueOrDefault(country.Code)))
            .OrderBy(country => country.PendingCount > 0 ? 0 : 1)
            .ThenBy(country => CityText.Normalize(country.Name), StringComparer.Ordinal)
            .ThenBy(country => country.Code, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed class SearchBillingCitiesHandler(
    IBillingCityRepository billingCities,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<SearchBillingCitiesQuery> validator)
    : IQueryHandler<SearchBillingCitiesQuery, IReadOnlyList<BillingCityRefDto>>
{
    public async Task<IReadOnlyList<BillingCityRefDto>> HandleAsync(
        SearchBillingCitiesQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : CityText.Normalize(query.Search);
        var cities = await billingCities.SearchAsync(
            ForeignCityOperatorInput.NormalizeCountry(query.Country)!, search, ForeignCityOperatorInput.CatalogLimit, cancellationToken);
        return cities.Select(ForeignCityViews.City).ToList();
    }
}

public sealed class ListUnresolvedForeignCitiesHandler(
    IUnresolvedForeignCityRepository unresolved,
    IBillingCityRepository billingCities,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<ListUnresolvedForeignCitiesQuery> validator)
    : IQueryHandler<ListUnresolvedForeignCitiesQuery, UnresolvedForeignCityPageDto>
{
    public async Task<UnresolvedForeignCityPageDto> HandleAsync(
        ListUnresolvedForeignCitiesQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : CityText.Normalize(query.Search);
        var (rows, total) = await unresolved.ListAsync(
            CityOperatorInput.ParseListStatus(query.Status),
            ForeignCityOperatorInput.NormalizeCountry(query.Country),
            search,
            query.Page,
            query.PageSize,
            cancellationToken);

        // Una consulta por cosa y no por fila: todas las ciudades de los países de la página, para las sugerencias.
        var countryCodes = rows.Select(row => row.City.CountryCode).Distinct().ToList();
        var citiesByCountry = (await billingCities.ListByCountriesAsync(countryCodes, cancellationToken))
            .GroupBy(city => city.CountryCode)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BillingCity>)group.ToList());

        var items = rows
            .Select(row => ForeignCityViews.ListItem(row, citiesByCountry.GetValueOrDefault(row.City.CountryCode) ?? []))
            .ToList();
        // La página pedida, no un recorte: la pantalla distingue «vacío» de «te pasaste de página» con ella.
        return new UnresolvedForeignCityPageDto(items, total, query.Page, query.PageSize);
    }
}

public sealed class GetUnresolvedForeignCityHandler(
    IUnresolvedForeignCityRepository unresolved,
    IBillingCityRepository billingCities,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<GetUnresolvedForeignCityQuery, UnresolvedForeignCityDetailDto>
{
    public async Task<UnresolvedForeignCityDetailDto> HandleAsync(
        GetUnresolvedForeignCityQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);

        // Resolved y Discarded también responden, con su estado: sólo un id que nunca existió es 404.
        var row = await unresolved.FindAsync(query.Id, cancellationToken)
            ?? throw new ResourceNotFoundException("geography.unresolved.not_found", "The unresolved city was not found.");
        var countryCities = await billingCities.ListByCountriesAsync([row.CountryCode], cancellationToken);
        var occurrences = await unresolved.ListOccurrencesAsync(row.Id, cancellationToken);

        var byTenant = occurrences
            .GroupBy(occurrence => occurrence.TenantId)
            .Select(group => (TenantId: group.Key, Count: group.Count()))
            .ToList();
        var names = await insights.ReadTenantNamesAsync(byTenant.Select(tenant => tenant.TenantId).ToList(), cancellationToken);
        var tenants = byTenant
            .Select(tenant => new UnresolvedCityTenantDto(tenant.TenantId, UnresolvedCityViews.TenantName(names, tenant.TenantId), tenant.Count))
            .OrderByDescending(tenant => tenant.OrderCount)
            .ThenBy(tenant => tenant.Name, StringComparer.Ordinal)
            .ToList();

        var suggestions = ForeignCityViews.Suggestions(row, countryCities);
        return new UnresolvedForeignCityDetailDto(
            row.Id.Value,
            row.CityText,
            row.NormalizedText,
            ForeignCityViews.CountryOf(row.CountryCode, countryCities),
            row.Status.ToString(),
            occurrences.Count,
            tenants.Count,
            row.FirstSeenAt,
            row.LastSeenAt,
            suggestions.Count == 0 ? null : suggestions[0],
            row.Version,
            tenants,
            suggestions);
    }
}

public sealed class ListForeignCitySynonymsHandler(
    IForeignCitySynonymRepository synonyms,
    IBillingCityRepository billingCities,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<ListForeignCitySynonymsQuery> validator)
    : IQueryHandler<ListForeignCitySynonymsQuery, ForeignCitySynonymPageDto>
{
    public async Task<ForeignCitySynonymPageDto> HandleAsync(
        ListForeignCitySynonymsQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : CityText.Normalize(query.Search);
        var (items, total) = await synonyms.ListAsync(
            ForeignCityOperatorInput.NormalizeCountry(query.Country), search, query.Page, query.PageSize, cancellationToken);

        // Una consulta por cosa y no por fila: las ciudades y los correos de toda la página.
        var citiesById = (await billingCities.ListByIdsAsync(
                items.Select(synonym => synonym.BillingCityId).Distinct().ToList(), cancellationToken))
            .ToDictionary(city => city.Id);
        var emails = await insights.ReadUserEmailsAsync(items.Select(synonym => synonym.CreatedBy).Distinct().ToList(), cancellationToken);

        var dtos = items
            .Select(synonym => ForeignCityViews.Synonym(
                synonym,
                citiesById.GetValueOrDefault(synonym.BillingCityId)
                    ?? throw new InvalidOperationException($"The billing city '{synonym.BillingCityId}' of synonym '{synonym.Id}' does not exist."),
                emails.GetValueOrDefault(synonym.CreatedBy)))
            .ToList();
        return new ForeignCitySynonymPageDto(dtos, total, query.Page, query.PageSize);
    }
}
