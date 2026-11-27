using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Geography.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Application;

// Spec 2026-11-26 «Consola del operador»: las escrituras de la consola del exterior, calcadas de la colombiana. Autorización de
// operador primero; la transacción toma el candado del texto —el mismo que el reportero— y relee antes de verificar. A
// diferencia de Colombia, un sinónimo del exterior no publica eventos ni corrige pedidos ya hechos: sólo decide qué ciudad
// usa la próxima factura.

/// <summary>Confirmar un texto de la bandeja del exterior como sinónimo de una ciudad de la lista de Siigo de su país.</summary>
public sealed record ResolveUnresolvedForeignCityCommand(TenantId TenantId, UnresolvedForeignCityId Id, Guid BillingCityId)
    : ICommand<ForeignCitySynonymDto>;

/// <summary>Descartar un texto (sigue sumando ocurrencias, pero ya no aparece entre los pendientes).</summary>
public sealed record DiscardUnresolvedForeignCityCommand(TenantId TenantId, UnresolvedForeignCityId Id)
    : ICommand<UnresolvedForeignCityListItemDto>;

/// <summary>Devolver a la bandeja un texto descartado.</summary>
public sealed record RestoreUnresolvedForeignCityCommand(TenantId TenantId, UnresolvedForeignCityId Id)
    : ICommand<UnresolvedForeignCityListItemDto>;

/// <summary>Cambia la ciudad del sinónimo. <paramref name="ExpectedVersion"/> es la <c>version</c> que la pantalla tenía: si el
/// sinónimo cambió desde entonces, <c>412 concurrency.conflict</c>.</summary>
public sealed record CorrectForeignCitySynonymCommand(
    TenantId TenantId, ForeignCitySynonymId Id, Guid BillingCityId, long ExpectedVersion) : ICommand<ForeignCitySynonymDto>;

/// <summary>Borrado lógico: deja de casar. No publica nada ni toca facturas.</summary>
public sealed record DeleteForeignCitySynonymCommand(TenantId TenantId, ForeignCitySynonymId Id) : ICommand<bool>;

/// <summary><c>billingCityId</c> ausente o vacío es <c>422 validation.failed</c> con la clave <c>BillingCityId</c>.</summary>
public sealed class ResolveUnresolvedForeignCityValidator : AbstractValidator<ResolveUnresolvedForeignCityCommand>
{
    public ResolveUnresolvedForeignCityValidator()
    {
        RuleFor(command => command.BillingCityId).NotEmpty();
    }
}

/// <summary>Además de <c>BillingCityId</c>, una <c>expectedVersion</c> ausente o menor que 1 es <c>422</c> con la clave
/// <c>ExpectedVersion</c>: la versión de un sinónimo empieza en 1.</summary>
public sealed class CorrectForeignCitySynonymValidator : AbstractValidator<CorrectForeignCitySynonymCommand>
{
    public CorrectForeignCitySynonymValidator()
    {
        RuleFor(command => command.BillingCityId).NotEmpty();
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(1);
    }
}

/// <summary>Lo que comparten los handlers de la fila y del sinónimo del exterior.</summary>
internal static class ForeignCityPrecheck
{
    public static UnresolvedForeignCity Found(UnresolvedForeignCity? row) =>
        row ?? throw new ResourceNotFoundException("geography.unresolved.not_found", "The unresolved city was not found.");

    /// <summary>Un sinónimo inexistente o borrado es 404.</summary>
    public static ForeignCitySynonym Live(ForeignCitySynonym? synonym) =>
        synonym is { IsDeleted: false }
            ? synonym
            : throw new ResourceNotFoundException("geography.synonym.not_found", "The city synonym was not found.");

    /// <summary>La ciudad elegida existe y es del país del texto; si no, 422. Una ciudad inexistente cuenta igual: la
    /// pantalla no distingue una de otra.</summary>
    public static async Task<BillingCity> CityOfTheCountryAsync(
        string countryCode, Guid billingCityId, IBillingCityRepository billingCities, CancellationToken cancellationToken)
    {
        var city = await billingCities.FindAsync(new BillingCityId(billingCityId), cancellationToken);
        return city is not null && string.Equals(city.CountryCode, countryCode, StringComparison.Ordinal)
            ? city
            : throw new GeographyDomainException(
                "geography.billing_city.not_in_country", "The city does not belong to the country of the text.");
    }
}

/// <summary>
/// Confirmar: todo en una transacción y bajo el candado del texto (<see cref="IGeographyUnitOfWork.LockForeignCityTextAsync"/>),
/// el mismo que toma el reportero: un reporte que cruza la confirmación o entra antes o espera y encuentra el sinónimo
/// (<c>SynonymExists</c>). Si el texto coincide con <b>una sola</b> ciudad del país nunca haría falta el sinónimo (el nombre
/// gana) y es 422; con dos o más sí se confirma, porque nunca coincidiría solo.
/// </summary>
public sealed class ResolveUnresolvedForeignCityHandler(
    IUnresolvedForeignCityRepository unresolved,
    IForeignCitySynonymRepository synonyms,
    IBillingCityRepository billingCities,
    IGeographyUnitOfWork unitOfWork,
    IGeographyAuditRecorder audit,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock,
    IValidator<ResolveUnresolvedForeignCityCommand> validator)
    : ICommandHandler<ResolveUnresolvedForeignCityCommand, ForeignCitySynonymDto>
{
    public async Task<ForeignCitySynonymDto> HandleAsync(
        ResolveUnresolvedForeignCityCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        ForeignCitySynonym synonym;
        BillingCity city;
        // Disponer sin commit hace rollback y suelta el candado.
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // La primera lectura sólo dice qué candado tomar (país y texto no cambian); con el candado se relee.
            var row = ForeignCityPrecheck.Found(await unresolved.FindAsync(command.Id, cancellationToken));
            await unitOfWork.LockForeignCityTextAsync(row.CountryCode, row.NormalizedText, cancellationToken);
            await unresolved.RefreshAsync(row, cancellationToken);

            if (row.Status != UnresolvedCityStatus.Pending)
            {
                throw new GeographyDomainException("geography.unresolved.not_pending", "The city is not pending.");
            }

            city = await ForeignCityPrecheck.CityOfTheCountryAsync(row.CountryCode, command.BillingCityId, billingCities, cancellationToken);
            if ((await billingCities.MatchByNameAsync(row.CountryCode, row.NormalizedText, cancellationToken)).Count == 1)
            {
                throw new GeographyDomainException(
                    "geography.synonym.matches_city", "The text is already the name of a city of the country.");
            }

            if (await synonyms.FindActiveAsync(row.CountryCode, row.NormalizedText, cancellationToken) is not null)
            {
                throw new ResourceConflictException(
                    "geography.synonym.already_exists", "There is already a synonym for that text in the country.");
            }

            var now = clock.UtcNow;
            // La llave es la de la fila, sin volver a normalizar el texto visible recortado.
            synonym = ForeignCitySynonym.Create(ForeignCitySynonymId.New(), row, city.Id, executionContext.SubjectId, now);
            row.Resolve(synonym.Id, now);
            synonyms.Add(synonym);

            audit.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                AuditActorType.Human,
                GeographyAuditActions.ForeignSynonymCreated,
                GeographyAuditActions.ForeignSynonymResourceType,
                synonym.Id.Value.ToString(),
                JsonSerializer.Serialize(new
                {
                    unresolvedForeignCityId = row.Id.Value,
                    countryCode = row.CountryCode,
                    normalizedText = synonym.NormalizedText,
                    billingCityId = synonym.BillingCityId.Value,
                }),
                now);

            // Version es token de concurrencia (412) y el índice único del texto vivo da 409 (GeographyUnitOfWork).
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var emails = await insights.ReadUserEmailsAsync([synonym.CreatedBy], cancellationToken);
        return ForeignCityViews.Synonym(synonym, city, emails.GetValueOrDefault(synonym.CreatedBy));
    }
}

public sealed class DiscardUnresolvedForeignCityHandler(UnresolvedForeignCityTriage triage)
    : ICommandHandler<DiscardUnresolvedForeignCityCommand, UnresolvedForeignCityListItemDto>
{
    public Task<UnresolvedForeignCityListItemDto> HandleAsync(
        DiscardUnresolvedForeignCityCommand command, CancellationToken cancellationToken) =>
        triage.ApplyAsync(
            command.TenantId, command.Id, GeographyAuditActions.UnresolvedForeignCityDiscarded, (row, now) => row.Discard(now),
            cancellationToken);
}

public sealed class RestoreUnresolvedForeignCityHandler(UnresolvedForeignCityTriage triage)
    : ICommandHandler<RestoreUnresolvedForeignCityCommand, UnresolvedForeignCityListItemDto>
{
    public Task<UnresolvedForeignCityListItemDto> HandleAsync(
        RestoreUnresolvedForeignCityCommand command, CancellationToken cancellationToken) =>
        triage.ApplyAsync(
            command.TenantId, command.Id, GeographyAuditActions.UnresolvedForeignCityRestored, (row, now) => row.Restore(now),
            cancellationToken);
}

/// <summary>
/// Lo común a descartar y restaurar: autorización de operador primero, la transición del dominio (<c>not_pending</c> /
/// <c>not_discarded</c>, 422), la auditoría en el tenant operador y el guardado, con <c>Version</c> como token de concurrencia
/// (412 si otro la cambió entre la lectura y el guardado). Devuelve la fila como la pinta la tabla.
/// </summary>
public sealed class UnresolvedForeignCityTriage(
    IUnresolvedForeignCityRepository unresolved,
    IBillingCityRepository billingCities,
    IGeographyUnitOfWork unitOfWork,
    IGeographyAuditRecorder audit,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock)
{
    internal async Task<UnresolvedForeignCityListItemDto> ApplyAsync(
        TenantId tenantId,
        UnresolvedForeignCityId id,
        string auditAction,
        Action<UnresolvedForeignCity, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, tenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);

        var row = ForeignCityPrecheck.Found(await unresolved.FindAsync(id, cancellationToken));
        var from = row.Status;
        var now = clock.UtcNow;
        transition(row, now);
        audit.Record(
            tenantId.Value,
            executionContext.SubjectId,
            AuditActorType.Human,
            auditAction,
            GeographyAuditActions.UnresolvedForeignCityResourceType,
            row.Id.Value.ToString(),
            JsonSerializer.Serialize(new
            {
                countryCode = row.CountryCode,
                normalizedText = row.NormalizedText,
                from = from.ToString(),
                to = row.Status.ToString(),
            }),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var countryCities = await billingCities.ListByCountriesAsync([row.CountryCode], cancellationToken);
        var occurrences = await unresolved.ListOccurrencesAsync(row.Id, cancellationToken);
        return ForeignCityViews.ListItem(ForeignCityViews.Row(row, occurrences), countryCities);
    }
}

/// <summary>
/// Corregir: el sinónimo pasa a otra ciudad del país, bajo el mismo candado del texto que confirmar y que el reportero. No
/// publica nada: la factura siguiente usa la ciudad nueva.
/// </summary>
public sealed class CorrectForeignCitySynonymHandler(
    IForeignCitySynonymRepository synonyms,
    IBillingCityRepository billingCities,
    IGeographyUnitOfWork unitOfWork,
    IGeographyAuditRecorder audit,
    ICityCorrectionInsights insights,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock,
    IValidator<CorrectForeignCitySynonymCommand> validator)
    : ICommandHandler<CorrectForeignCitySynonymCommand, ForeignCitySynonymDto>
{
    public async Task<ForeignCitySynonymDto> HandleAsync(
        CorrectForeignCitySynonymCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        ForeignCitySynonym synonym;
        BillingCity city;
        // Disponer sin commit hace rollback y suelta el candado.
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // La primera lectura sólo dice qué candado tomar (país y texto no cambian); con el candado se relee y se
            // vuelve a verificar: un borrado que se confirmó mientras esperábamos es 404, no 412.
            synonym = ForeignCityPrecheck.Live(await synonyms.FindAsync(command.Id, cancellationToken));
            await unitOfWork.LockForeignCityTextAsync(synonym.CountryCode, synonym.NormalizedText, cancellationToken);
            await synonyms.RefreshAsync(synonym, cancellationToken);
            ForeignCityPrecheck.Live(synonym);
            if (synonym.Version != command.ExpectedVersion)
            {
                throw new RequestConcurrencyException("concurrency.conflict", "The city synonym changed after it was loaded.");
            }

            city = await ForeignCityPrecheck.CityOfTheCountryAsync(synonym.CountryCode, command.BillingCityId, billingCities, cancellationToken);
            var now = clock.UtcNow;
            var previousCityId = synonym.ChangeCity(city.Id, executionContext.SubjectId, now);
            audit.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                AuditActorType.Human,
                GeographyAuditActions.ForeignSynonymCityChanged,
                GeographyAuditActions.ForeignSynonymResourceType,
                synonym.Id.Value.ToString(),
                JsonSerializer.Serialize(new
                {
                    countryCode = synonym.CountryCode,
                    normalizedText = synonym.NormalizedText,
                    previousBillingCityId = previousCityId.Value,
                    billingCityId = synonym.BillingCityId.Value,
                }),
                now);

            // Version es token de concurrencia: un cambio entre la lectura y el guardado es 412.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var emails = await insights.ReadUserEmailsAsync([synonym.CreatedBy], cancellationToken);
        return ForeignCityViews.Synonym(synonym, city, emails.GetValueOrDefault(synonym.CreatedBy));
    }
}

/// <summary>Borra el sinónimo (lógico). Deja de casar desde el commit; no publica nada y no toca facturas. El próximo reporte
/// del texto abre una fila <c>Pending</c> nueva, porque la <c>Resolved</c> vieja queda fuera del índice parcial.</summary>
public sealed class DeleteForeignCitySynonymHandler(
    IForeignCitySynonymRepository synonyms,
    IGeographyUnitOfWork unitOfWork,
    IGeographyAuditRecorder audit,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock)
    : ICommandHandler<DeleteForeignCitySynonymCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteForeignCitySynonymCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, command.TenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var synonym = ForeignCityPrecheck.Live(await synonyms.FindAsync(command.Id, cancellationToken));
        await unitOfWork.LockForeignCityTextAsync(synonym.CountryCode, synonym.NormalizedText, cancellationToken);
        // Con el candado se relee y se vuelve a verificar: si otro lo borró mientras esperábamos, es 404.
        await synonyms.RefreshAsync(synonym, cancellationToken);
        ForeignCityPrecheck.Live(synonym);

        var now = clock.UtcNow;
        synonym.Delete(executionContext.SubjectId, now);
        audit.Record(
            command.TenantId.Value,
            executionContext.SubjectId,
            AuditActorType.Human,
            GeographyAuditActions.ForeignSynonymDeleted,
            GeographyAuditActions.ForeignSynonymResourceType,
            synonym.Id.Value.ToString(),
            JsonSerializer.Serialize(new
            {
                countryCode = synonym.CountryCode,
                normalizedText = synonym.NormalizedText,
                billingCityId = synonym.BillingCityId.Value,
            }),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
