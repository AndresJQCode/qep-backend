using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Geography.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Application;

// Spec 2026-10-26 §4.6, §4.7 y §6: la lista de sinónimos y corregir, borrar y reintentar. En cada handler la autorización de
// operador (Tenancy) va primero, antes de validar o tocar un repositorio: un 403 no depende de la entrada ni de si el id existe.
// Un sinónimo borrado responde 404 en todas sus rutas.

public sealed record ListCitySynonymsQuery(TenantId TenantId, Guid? DepartmentId, string? Search, int Page, int PageSize)
    : IQuery<CitySynonymPageDto>;

/// <summary>El impacto de corregir el sinónimo a <paramref name="CityId"/> (spec §4.6): no escribe nada.</summary>
public sealed record PreviewSynonymCorrectionImpactQuery(TenantId TenantId, CitySynonymId Id, Guid CityId) : IQuery<CorrectImpactDto>;

/// <summary>Cambia la ciudad del sinónimo. <paramref name="ExpectedVersion"/> es la <c>version</c> que la pantalla tenía: si el
/// sinónimo cambió desde entonces, <c>412 concurrency.conflict</c>.</summary>
public sealed record CorrectCitySynonymCommand(TenantId TenantId, CitySynonymId Id, Guid CityId, long ExpectedVersion)
    : ICommand<CitySynonymDto>;

/// <summary>Borrado lógico (§4.7): deja de casar, no deshace lo ya corregido (O6) y no publica nada.</summary>
public sealed record DeleteCitySynonymCommand(TenantId TenantId, CitySynonymId Id) : ICommand<bool>;

/// <summary>Devuelve a la cola los trabajos fallidos del sinónimo.</summary>
public sealed record RetryCitySynonymCorrectionsCommand(TenantId TenantId, CitySynonymId Id) : ICommand<CitySynonymDto>;

/// <summary>Un valor fuera de rango es <c>422 validation.failed</c> con la clave del campo (<c>Page</c>, <c>PageSize</c>,
/// <c>Search</c>), nunca 500.</summary>
public sealed class ListCitySynonymsValidator : AbstractValidator<ListCitySynonymsQuery>
{
    public ListCitySynonymsValidator()
    {
        RuleFor(query => query.Search).MaximumLength(CityOperatorInput.MaxSearchLength);
        RuleFor(query => query.Page).InclusiveBetween(1, CityOperatorInput.MaxPage);
        RuleFor(query => query.PageSize).InclusiveBetween(1, CityOperatorInput.MaxPageSize);
    }
}

/// <summary><c>cityId</c> ausente o vacío es <c>422 validation.failed</c> con la clave <c>CityId</c>.</summary>
public sealed class PreviewSynonymCorrectionImpactValidator : AbstractValidator<PreviewSynonymCorrectionImpactQuery>
{
    public PreviewSynonymCorrectionImpactValidator()
    {
        RuleFor(query => query.CityId).NotEmpty();
    }
}

/// <summary>Además de <c>CityId</c>, una <c>expectedVersion</c> ausente o menor que 1 es <c>422</c> con la clave
/// <c>ExpectedVersion</c>: la versión de un sinónimo empieza en 1.</summary>
public sealed class CorrectCitySynonymValidator : AbstractValidator<CorrectCitySynonymCommand>
{
    public CorrectCitySynonymValidator()
    {
        RuleFor(command => command.CityId).NotEmpty();
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(1);
    }
}

/// <summary>Lo que comparten los handlers de un sinónimo concreto.</summary>
internal static class SynonymPrecheck
{
    /// <summary>Un sinónimo inexistente o borrado es 404.</summary>
    public static CitySynonym Live(CitySynonym? synonym) =>
        synonym is { IsDeleted: false }
            ? synonym
            : throw new ResourceNotFoundException("geography.synonym.not_found", "The city synonym was not found.");

    /// <summary>La ciudad elegida existe y es del departamento del sinónimo; si no, 422.</summary>
    public static async Task<City> CityOfTheDepartmentAsync(
        CitySynonym synonym, Guid cityId, ICityRepository cities, CancellationToken cancellationToken)
    {
        var city = await cities.FindAsync(new CityId(cityId), cancellationToken);
        return city is not null && city.DepartmentId == synonym.DepartmentId
            ? city
            : throw new GeographyDomainException(
                "geography.city.not_in_department", "The city does not belong to the department of the text.");
    }
}

public sealed class ListCitySynonymsHandler(
    ICitySynonymRepository synonyms,
    IDepartmentRepository departments,
    ICityRepository cities,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<ListCitySynonymsQuery> validator)
    : IQueryHandler<ListCitySynonymsQuery, CitySynonymPageDto>
{
    public async Task<CitySynonymPageDto> HandleAsync(ListCitySynonymsQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : CityText.Normalize(query.Search);
        var (items, total) = await synonyms.ListAsync(
            query.DepartmentId is { } departmentId ? new DepartmentId(departmentId) : null,
            search,
            query.Page,
            query.PageSize,
            cancellationToken);

        // Una consulta por cosa y no por fila: departamentos, ciudades, contadores y correos de toda la página.
        var departmentsById = (await departments.ListByIdsAsync(
                items.Select(synonym => synonym.DepartmentId).Distinct().ToList(), cancellationToken))
            .ToDictionary(department => department.Id);
        var citiesById = (await cities.ListByIdsAsync(items.Select(synonym => synonym.CityId).Distinct().ToList(), cancellationToken))
            .ToDictionary(city => city.Id);
        var counts = await insights.CountCorrectionsAsync(items.Select(synonym => synonym.Id).ToList(), cancellationToken);
        var emails = await insights.ReadUserEmailsAsync(items.Select(synonym => synonym.CreatedBy).Distinct().ToList(), cancellationToken);

        var dtos = items
            .Select(synonym => CitySynonymMapping.ToDto(
                synonym,
                departmentsById.GetValueOrDefault(synonym.DepartmentId)
                    ?? throw new InvalidOperationException($"The department '{synonym.DepartmentId}' of synonym '{synonym.Id}' does not exist."),
                citiesById.GetValueOrDefault(synonym.CityId)
                    ?? throw new InvalidOperationException($"The city '{synonym.CityId}' of synonym '{synonym.Id}' does not exist."),
                counts.GetValueOrDefault(synonym.Id.Value) ?? new SynonymCorrectionCounts(0, 0, 0, 0),
                emails.GetValueOrDefault(synonym.CreatedBy)))
            .ToList();
        // La página pedida, no un recorte: la pantalla distingue «vacío» de «te pasaste de página» con ella.
        return new CitySynonymPageDto(dtos, total, query.Page, query.PageSize);
    }
}

public sealed class PreviewSynonymCorrectionImpactHandler(
    ICitySynonymRepository synonyms,
    ICityRepository cities,
    IUnresolvedCityRepository unresolvedCities,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<PreviewSynonymCorrectionImpactQuery> validator)
    : IQueryHandler<PreviewSynonymCorrectionImpactQuery, CorrectImpactDto>
{
    public async Task<CorrectImpactDto> HandleAsync(PreviewSynonymCorrectionImpactQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, GeographyOperatorPermissions.CitiesRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var synonym = SynonymPrecheck.Live(await synonyms.FindAsync(query.Id, cancellationToken));
        var to = await SynonymPrecheck.CityOfTheDepartmentAsync(synonym, query.CityId, cities, cancellationToken);
        if (to.Id == synonym.CityId)
        {
            throw new GeographyDomainException("geography.synonym.same_city", "The synonym already points to that city.");
        }

        var from = await cities.FindAsync(synonym.CityId, cancellationToken)
            ?? throw new InvalidOperationException($"The city '{synonym.CityId}' of synonym '{synonym.Id}' does not exist.");

        // Las huellas se leen una sola vez por petición (una consulta, no una por pedido): hoy no tienen tope por sinónimo y
        // viajan completas porque «ordersNotPending» es por contrato la lista entera de pedidos a los que avisar.
        var corrections = await insights.ReadCorrectionsAsync(synonym.Id, cancellationToken);
        var occurrences = await unresolvedCities.ListOccurrencesBySynonymAsync(synonym.Id, cancellationToken);
        var tenantNames = await insights.ReadTenantNamesAsync(
            corrections.Select(correction => correction.TenantId)
                .Concat(occurrences.Select(occurrence => occurrence.TenantId))
                .Distinct()
                .ToList(),
            cancellationToken);

        return CorrectImpactCalculator.Compute(
            UnresolvedCityViews.City(from), UnresolvedCityViews.City(to), corrections, occurrences, tenantNames);
    }
}

/// <summary>
/// Spec 2026-10-26 §4.6: el sinónimo pasa a otra ciudad y el evento <c>corrected</c> sale por el outbox en la misma transacción
/// (Shopify siembra una recorrección por huella). Bajo el candado del texto —el mismo de confirmar—, así un reporte que cruza la
/// corrección ve el sinónimo antes o después, nunca a medias. El candado del sinónimo de Shopify
/// (<c>shopify.city-synonym:{id}</c>) lo toma el consumidor al aplicar el evento: es de otro módulo y otra unidad de trabajo.
/// </summary>
public sealed class CorrectCitySynonymHandler(
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
    IValidator<CorrectCitySynonymCommand> validator)
    : ICommandHandler<CorrectCitySynonymCommand, CitySynonymDto>
{
    public async Task<CitySynonymDto> HandleAsync(CorrectCitySynonymCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        CitySynonym synonym;
        // Disponer sin commit hace rollback y suelta el candado.
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // La primera lectura sólo dice qué candado tomar (departamento y texto no cambian); con el candado se relee y se
            // vuelve a verificar: un borrado que se confirmó mientras esperábamos es 404, no 412.
            synonym = SynonymPrecheck.Live(await synonyms.FindAsync(command.Id, cancellationToken));
            await unitOfWork.LockCityTextAsync(synonym.DepartmentId, synonym.NormalizedText, cancellationToken);
            await synonyms.RefreshAsync(synonym, cancellationToken);
            SynonymPrecheck.Live(synonym);
            if (synonym.Version != command.ExpectedVersion)
            {
                throw new RequestConcurrencyException("concurrency.conflict", "The city synonym changed after it was loaded.");
            }

            var city = await SynonymPrecheck.CityOfTheDepartmentAsync(synonym, command.CityId, cities, cancellationToken);
            var now = clock.UtcNow;
            var previousCityId = synonym.ChangeCity(city.Id, executionContext.SubjectId, now);
            events.PublishSynonymCorrected(synonym, previousCityId, command.TenantId.Value, now);
            audit.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                AuditActorType.Human,
                GeographyAuditActions.SynonymCityChanged,
                GeographyAuditActions.SynonymResourceType,
                synonym.Id.Value.ToString(),
                JsonSerializer.Serialize(new
                {
                    departmentId = synonym.DepartmentId.Value,
                    normalizedText = synonym.NormalizedText,
                    previousCityId = previousCityId.Value,
                    cityId = synonym.CityId.Value,
                }),
                now);

            // Version es token de concurrencia: un cambio entre la lectura y el guardado es 412.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await CitySynonymMapping.LoadAsync(synonym, departments, cities, insights, cancellationToken);
    }
}

/// <summary>
/// Spec 2026-10-26 §4.7 y O6: borra el sinónimo (lógico). Deja de casar desde el commit; no publica nada y no deshace lo ya
/// corregido; los trabajos <c>Pending</c> los salta el worker al ver el sinónimo borrado. El próximo reporte del texto abre una
/// fila <c>Pending</c> nueva, porque la <c>Resolved</c> vieja queda fuera del índice parcial.
/// </summary>
public sealed class DeleteCitySynonymHandler(
    ICitySynonymRepository synonyms,
    IGeographyUnitOfWork unitOfWork,
    IGeographyAuditRecorder audit,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock)
    : ICommandHandler<DeleteCitySynonymCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteCitySynonymCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var synonym = SynonymPrecheck.Live(await synonyms.FindAsync(command.Id, cancellationToken));
        await unitOfWork.LockCityTextAsync(synonym.DepartmentId, synonym.NormalizedText, cancellationToken);
        // Con el candado se relee y se vuelve a verificar: si otro lo borró mientras esperábamos, es 404.
        await synonyms.RefreshAsync(synonym, cancellationToken);
        SynonymPrecheck.Live(synonym);

        var now = clock.UtcNow;
        synonym.Delete(executionContext.SubjectId, now);
        audit.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            AuditActorType.Human,
            GeographyAuditActions.SynonymDeleted,
            GeographyAuditActions.SynonymResourceType,
            synonym.Id.Value.ToString(),
            JsonSerializer.Serialize(new
            {
                departmentId = synonym.DepartmentId.Value,
                normalizedText = synonym.NormalizedText,
                cityId = synonym.CityId.Value,
            }),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

/// <summary>Spec 2026-10-26 D-C6: los trabajos <c>Failed</c> del sinónimo vuelven a <c>Pending</c>. La auditoría lleva cuántos
/// eran; devuelve el sinónimo con sus contadores frescos, que es lo que la pantalla sondea.</summary>
public sealed class RetryCitySynonymCorrectionsHandler(
    ICitySynonymRepository synonyms,
    ICityRepository cities,
    IDepartmentRepository departments,
    ICityCorrectionJobs jobs,
    IGeographyUnitOfWork unitOfWork,
    IGeographyAuditRecorder audit,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock)
    : ICommandHandler<RetryCitySynonymCorrectionsCommand, CitySynonymDto>
{
    public async Task<CitySynonymDto> HandleAsync(RetryCitySynonymCorrectionsCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);

        var synonym = SynonymPrecheck.Live(await synonyms.FindAsync(command.Id, cancellationToken));
        var retried = await jobs.RetryFailedAsync(synonym.Id, cancellationToken);
        audit.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            AuditActorType.Human,
            GeographyAuditActions.SynonymCorrectionsRetried,
            GeographyAuditActions.SynonymResourceType,
            synonym.Id.Value.ToString(),
            JsonSerializer.Serialize(new { retried }),
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await CitySynonymMapping.LoadAsync(synonym, departments, cities, insights, cancellationToken);
    }
}
