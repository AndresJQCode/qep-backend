using BuildingBlocks.Application;
using BuildingBlocks.Domain.Taxes;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Catalog.Application;

// La consola «Unidad tributaria» del operador. En cada handler la autorización de operador (Tenancy) va primero, antes de
// tocar un repositorio: un 403 no depende de la entrada. «Hoy» y «año actual» salen del calendario del tenant operador.

public sealed record ListTaxUnitsQuery(TenantId TenantId) : IQuery<TaxUnitsDto>;

public sealed record SetTaxUnitValueCommand(TenantId TenantId, string Country, int Year, decimal Value)
    : ICommand<TaxUnitValueDto>;

public sealed record TaxUnitsDto(IReadOnlyList<TaxUnitCountryDto> Countries);

/// <summary><c>Values</c> va del año más reciente al más antiguo.</summary>
public sealed record TaxUnitCountryDto(
    string Country,
    string TaxUnit,
    string Currency,
    bool NextYearMissing,
    IReadOnlyList<TaxUnitValueDto> Values);

public sealed record TaxUnitValueDto(int Year, decimal Value, DateTimeOffset UpdatedAt);

public sealed class ListTaxUnitsHandler(
    ITaxUnitValueRepository repository,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    ITenantClock tenantClock)
    : IQueryHandler<ListTaxUnitsQuery, TaxUnitsDto>
{
    public async Task<TaxUnitsDto> HandleAsync(ListTaxUnitsQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogOperatorPermissions.TaxUnitsRead, operatorTenant);

        var calendar = await tenantClock.GetAsync(query.TenantId.Value, cancellationToken);
        var values = await repository.ListAsync(null, cancellationToken);

        // Sólo los países con unidad: un país nuevo sin ella no aparece en la pantalla.
        var countries = TaxCountries.All
            .Where(country => country.TaxUnitName is not null)
            .Select(country => new TaxUnitCountryDto(
                country.Country,
                country.TaxUnitName!,
                country.TaxCurrency,
                TaxUnitValues.NextYearMissing(values, country.Country, calendar.Today),
                values
                    .Where(value => value.Country == country.Country)
                    .OrderByDescending(value => value.Year)
                    .Select(ToDto)
                    .ToList()))
            .ToList();
        return new TaxUnitsDto(countries);
    }

    internal static TaxUnitValueDto ToDto(TaxUnitValue value) => new(value.Year, value.Value, value.UpdatedAt);
}

public sealed class SetTaxUnitValueHandler(
    ITaxUnitValueRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    ITenantClock tenantClock)
    : ICommandHandler<SetTaxUnitValueCommand, TaxUnitValueDto>
{
    public async Task<TaxUnitValueDto> HandleAsync(SetTaxUnitValueCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogOperatorPermissions.TaxUnitsManage, operatorTenant);

        var calendar = await tenantClock.GetAsync(command.TenantId.Value, cancellationToken);

        // El repositorio compara el país tal cual: se le pasa el código canónico. Uno desconocido sigue su camino y lo
        // rechaza TaxUnitValue.Create con country_without_unit.
        var country = TaxCountries.Find(command.Country)?.Country ?? command.Country;
        var unit = await repository.FindAsync(country, command.Year, cancellationToken);
        if (unit is null)
        {
            unit = TaxUnitValue.Create(
                country, command.Year, command.Value, executionContext.SubjectId, calendar.UtcNow, calendar.Today.Year);
            repository.Add(unit);
        }
        else
        {
            unit.Replace(command.Value, executionContext.SubjectId, calendar.UtcNow);
        }

        auditPublisher.Publish(
            command.TenantId.Value,
            executionContext.SubjectId,
            "catalog.tax_unit.value_set",
            $"{unit.Country}-{unit.Year}",
            "success",
            calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ListTaxUnitsHandler.ToDto(unit);
    }
}
