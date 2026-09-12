using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El listado de cotizaciones en un <c>.xlsx</c>, para el boton de exportar de esa misma pantalla.
///
/// **Los mismos filtros que <see cref="ListQuotationsQuery"/>, menos la paginacion**, porque el
/// archivo tiene que ser lo que la tabla muestra: con un filtro de mas o de menos, quien exporta
/// se lleva algo distinto de lo que estaba viendo.
///
/// **Rango de fechas obligatorio de a lo sumo un año en vez de un tope de filas.** Un tope castiga
/// a quien mas vende y no dice que filtro tocar; el rango acota el volumen y, cuando falla, ya
/// dice que corregir. Ver <see cref="ExportQuotationsValidator"/>.
/// </summary>
public sealed record ExportQuotationsQuery(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    DateOnly? CreatedFrom,
    DateOnly? CreatedTo,
    string? ClientNit,
    string? QuotationNumber) : IQuery<QuotationExportFile>;

/// <summary>
/// El rango de fechas es la cota de volumen de la exportacion, asi que es obligatorio y de a lo
/// sumo un año. Validador y no regla de dominio para que el 422 lleve el mapa <c>errors</c> y la
/// pantalla marque el control de fecha que hay que corregir.
///
/// "Un año" es <c>AddYears(1)</c> y no 365 dias: vale igual en un año bisiesto, y un rango de
/// exactamente un año se acepta.
/// </summary>
public sealed class ExportQuotationsValidator : AbstractValidator<ExportQuotationsQuery>
{
    public ExportQuotationsValidator()
    {
        RuleFor(query => query.CreatedFrom)
            .NotNull()
            .WithMessage("createdFrom is required.");
        RuleFor(query => query.CreatedTo)
            .NotNull()
            .WithMessage("createdTo is required.");
        RuleFor(query => query.CreatedTo)
            .GreaterThanOrEqualTo(query => query.CreatedFrom!.Value)
            .When(query => query.CreatedFrom is not null && query.CreatedTo is not null)
            .WithMessage("createdTo must be on or after createdFrom.");
        RuleFor(query => query.CreatedTo)
            .LessThanOrEqualTo(query => query.CreatedFrom!.Value.AddYears(1))
            .When(query => query.CreatedFrom is not null && query.CreatedTo is not null)
            .WithMessage("The range from createdFrom to createdTo cannot exceed one year.");
    }
}

public sealed class ExportQuotationsHandler(
    IQuotationRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IQuotationExportWorkbookBuilder workbookBuilder,
    IValidator<ExportQuotationsQuery> validator,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<ExportQuotationsQuery, QuotationExportFile>
{
    public async Task<QuotationExportFile> HandleAsync(
        ExportQuotationsQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, mismo criterio que CreateQuotationHandler: a quien no puede
        // exportar no se le contesta que su rango estaba mal.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, QuotationsPermissions.QuotationRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var status = QuotationListing.ParseStatus(query.Status);
        var advisorId = query.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await QuotationListing.ResolveClientIdsByNitAsync(
            customerLookup, query.TenantId, query.ClientNit, cancellationToken);

        var quotations = await repository.ListForExportAsync(
            query.TenantId,
            query.ClientId,
            clientIds,
            advisorId,
            status,
            query.CreatedFrom,
            query.CreatedTo,
            query.QuotationNumber,
            cancellationToken);

        // Un archivo con solo la cabecera es peor que decir que no habia nada: mismo criterio que
        // `reporting.export.empty` y `customers.export.empty`.
        if (quotations.Count == 0)
        {
            throw new QuotationsDomainException(
                "quotation.export.empty",
                "There are no quotations matching the export filters.");
        }

        var rows = await QuotationListing.ToListItemsAsync(
            customerLookup, advisorLookup, query.TenantId, quotations, cancellationToken);
        return workbookBuilder.Build(rows, clock.UtcNow, cancellationToken);
    }
}
