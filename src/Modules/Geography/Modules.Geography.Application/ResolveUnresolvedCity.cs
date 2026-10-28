using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Geography.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Application;

/// <summary>El impacto de confirmar (spec 2026-10-26 §4.4): no escribe nada.</summary>
public sealed record PreviewResolveImpactQuery(TenantId TenantId, UnresolvedCityId Id, Guid CityId) : IQuery<ResolveImpactDto>;

/// <summary>Confirmar un texto de la bandeja como sinónimo de una ciudad de su departamento.</summary>
public sealed record ResolveUnresolvedCityCommand(TenantId TenantId, UnresolvedCityId Id, Guid CityId) : ICommand<CitySynonymDto>;

/// <summary><c>cityId</c> ausente o vacío es <c>422 validation.failed</c> con la clave <c>CityId</c>.</summary>
public sealed class PreviewResolveImpactValidator : AbstractValidator<PreviewResolveImpactQuery>
{
    public PreviewResolveImpactValidator()
    {
        RuleFor(query => query.CityId).NotEmpty();
    }
}

/// <summary>Igual que <see cref="PreviewResolveImpactValidator"/>.</summary>
public sealed class ResolveUnresolvedCityValidator : AbstractValidator<ResolveUnresolvedCityCommand>
{
    public ResolveUnresolvedCityValidator()
    {
        RuleFor(command => command.CityId).NotEmpty();
    }
}

/// <summary>
/// La validación previa que comparten el impacto y confirmar: la fila está <c>Pending</c>, la ciudad existe y es del departamento
/// de la fila, y el texto no es el nombre de una ciudad real del departamento (D-C2, Review Focus 1). Un sinónimo así nunca
/// casaría —el nombre real gana (§4.1)— y sólo confundiría.
/// </summary>
internal static class ResolvePrecheck
{
    public static UnresolvedCity Found(UnresolvedCity? row) =>
        row ?? throw new ResourceNotFoundException("geography.unresolved.not_found", "The unresolved city was not found.");

    public static async Task<City> EnsureResolvableAsync(
        UnresolvedCity row, Guid cityId, ICityRepository cities, CancellationToken cancellationToken)
    {
        if (row.Status != UnresolvedCityStatus.Pending)
        {
            throw new GeographyDomainException("geography.unresolved.not_pending", "The city is not pending.");
        }

        var city = await cities.FindAsync(new CityId(cityId), cancellationToken);
        if (city is null || city.DepartmentId != row.DepartmentId)
        {
            throw new GeographyDomainException(
                "geography.city.not_in_department", "The city does not belong to the department of the text.");
        }

        var departmentCities = await cities.ListByDepartmentAsync(row.DepartmentId, cancellationToken);
        if (departmentCities.Any(candidate =>
                CityText.Normalize(candidate.Name) == row.NormalizedText
                || CityText.Normalize(candidate.DivipolaName) == row.NormalizedText))
        {
            throw new GeographyDomainException(
                "geography.synonym.matches_city", "The text is already the name of a city of the department.");
        }

        return city;
    }
}

public sealed class PreviewResolveImpactHandler(
    IUnresolvedCityRepository unresolvedCities,
    ICityRepository cities,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<PreviewResolveImpactQuery> validator)
    : IQueryHandler<PreviewResolveImpactQuery, ResolveImpactDto>
{
    public async Task<ResolveImpactDto> HandleAsync(PreviewResolveImpactQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var row = ResolvePrecheck.Found(await unresolvedCities.FindAsync(query.Id, cancellationToken));
        var city = await ResolvePrecheck.EnsureResolvableAsync(row, query.CityId, cities, cancellationToken);
        var occurrences = await unresolvedCities.ListOccurrencesAsync(row.Id, cancellationToken);

        var orders = await insights.ReadOrdersAsync(
            occurrences.Select(occurrence => new TenantOrderKey(occurrence.TenantId, occurrence.OrderId)).Distinct().ToList(),
            cancellationToken);
        var customers = await insights.ReadCustomersAsync(
            occurrences
                .Where(occurrence => occurrence.CustomerId is not null)
                .Select(occurrence => new TenantCustomerKey(occurrence.TenantId, occurrence.CustomerId!.Value))
                .Distinct()
                .ToList(),
            cancellationToken);
        var tenantNames = await insights.ReadTenantNamesAsync(
            occurrences.Select(occurrence => occurrence.TenantId).Distinct().ToList(), cancellationToken);

        return ResolveImpactCalculator.Compute(UnresolvedCityViews.City(city), occurrences, orders, customers, tenantNames);
    }
}

/// <summary>
/// Spec 2026-10-26 §4.4: todo en una transacción de Geography y bajo el candado del texto
/// (<see cref="IGeographyUnitOfWork.LockCityTextAsync"/>), el mismo que toma el reporter: un reporte que cruza la confirmación o
/// entra antes (y su ocurrencia va en el evento) o espera y encuentra el sinónimo (<c>SynonymExists</c>). Nunca deja una
/// ocurrencia nueva en una fila ya resuelta. El sinónimo casa desde el commit; el evento sale por el outbox en la misma
/// transacción.
/// </summary>
public sealed class ResolveUnresolvedCityHandler(
    IUnresolvedCityRepository unresolvedCities,
    ICitySynonymRepository synonyms,
    ICityRepository cities,
    IDepartmentRepository departments,
    IGeographyUnitOfWork unitOfWork,
    IGeographyEventPublisher events,
    IGeographyAuditRecorder audit,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock,
    IValidator<ResolveUnresolvedCityCommand> validator)
    : ICommandHandler<ResolveUnresolvedCityCommand, CitySynonymDto>
{
    public async Task<CitySynonymDto> HandleAsync(ResolveUnresolvedCityCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        CitySynonym synonym;
        // Disponer sin commit hace rollback y suelta el candado.
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // La primera lectura sólo dice qué candado tomar (departamento y texto no cambian); con el candado se relee.
            var row = ResolvePrecheck.Found(await unresolvedCities.FindAsync(command.Id, cancellationToken));
            await unitOfWork.LockCityTextAsync(row.DepartmentId, row.NormalizedText, cancellationToken);
            await unresolvedCities.RefreshAsync(row, cancellationToken);

            var city = await ResolvePrecheck.EnsureResolvableAsync(row, command.CityId, cities, cancellationToken);
            if (await synonyms.FindActiveAsync(row.DepartmentId, row.NormalizedText, cancellationToken) is not null)
            {
                throw new ResourceConflictException(
                    "geography.synonym.already_exists", "There is already a synonym for that text in the department.");
            }

            var now = clock.UtcNow;
            // P17: la llave es la de la fila, sin volver a normalizar el texto visible recortado.
            synonym = CitySynonym.Create(CitySynonymId.New(), row, city.Id, executionContext.SubjectId, now);
            row.Resolve(synonym.Id, now);
            synonyms.Add(synonym);

            // Leídas con el candado tomado: ningún reporte del texto puede sumar una después.
            var occurrences = await unresolvedCities.ListOccurrencesAsync(row.Id, cancellationToken);
            events.PublishSynonymResolved(synonym, occurrences, command.TenantId.Value, now);
            audit.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                AuditActorType.Human,
                GeographyAuditActions.SynonymCreated,
                GeographyAuditActions.SynonymResourceType,
                synonym.Id.Value.ToString(),
                JsonSerializer.Serialize(new
                {
                    unresolvedCityId = row.Id.Value,
                    departmentId = row.DepartmentId.Value,
                    normalizedText = synonym.NormalizedText,
                    cityId = synonym.CityId.Value,
                    occurrences = occurrences.Count,
                }),
                now);

            // Version es token de concurrencia (412) y el índice único del texto vivo da 409 (GeographyUnitOfWork).
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await CitySynonymMapping.LoadAsync(synonym, departments, cities, insights, cancellationToken);
    }
}
