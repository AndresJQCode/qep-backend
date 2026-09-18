using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Pide el listado de cotizaciones en un <c>.xlsx</c> por correo (spec 2026-09-12). Comando y no
/// query: encola un job. El Excel lo arma <c>QuotationsExportProcessor</c> en el worker, nunca el
/// request.
///
/// **Los mismos filtros que <see cref="ListQuotationsQuery"/>, menos la paginación**: el archivo
/// tiene que ser lo que la tabla muestra. **Rango obligatorio de a lo sumo un año** en vez de un
/// tope de filas (D3): un tope castiga a quien más vende y no dice qué filtro tocar.
/// </summary>
public sealed record ExportQuotationsCommand(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    DateOnly? CreatedFrom,
    DateOnly? CreatedTo,
    string? ClientNit,
    string? QuotationNumber) : ICommand<ExportJobAccepted>;

/// <summary>Lo que se acepta: el job y cuándo. Sin archivo ni filas —todavía no existen— ni
/// enlace: el canal de entrega es el correo (D5).</summary>
public sealed record ExportJobAccepted(Guid JobId, DateTimeOffset RequestedAt);

/// <summary>Los filtros tal como quedan en <c>export_jobs.filters</c>. Las fechas ya no son
/// opcionales: el validador las exigió antes de encolar.</summary>
public sealed record QuotationsExportFilters(
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    DateOnly CreatedFrom,
    DateOnly CreatedTo,
    string? ClientNit,
    string? QuotationNumber);

/// <summary>
/// El rango es la cota de volumen, así que es obligatorio y de a lo sumo un año. Validador y no
/// regla de dominio para que el 422 lleve el mapa <c>errors</c> y la pantalla marque la fecha.
/// "Un año" es <c>AddYears(1)</c> y no 365 días: vale igual en un bisiesto, y un año exacto pasa.
/// </summary>
public sealed class ExportQuotationsValidator : AbstractValidator<ExportQuotationsCommand>
{
    public ExportQuotationsValidator()
    {
        RuleFor(command => command.CreatedFrom)
            .NotNull()
            .WithMessage("createdFrom is required.");
        RuleFor(command => command.CreatedTo)
            .NotNull()
            .WithMessage("createdTo is required.");
        RuleFor(command => command.CreatedTo)
            .GreaterThanOrEqualTo(command => command.CreatedFrom!.Value)
            .When(command => command.CreatedFrom is not null && command.CreatedTo is not null)
            .WithMessage("createdTo must be on or after createdFrom.");
        RuleFor(command => command.CreatedTo)
            .LessThanOrEqualTo(command => command.CreatedFrom!.Value.AddYears(1))
            .When(command => command.CreatedFrom is not null && command.CreatedTo is not null)
            .WithMessage("The range from createdFrom to createdTo cannot exceed one year.");
    }
}

public sealed class ExportQuotationsHandler(
    IQuotationRepository repository,
    IQuotationCustomerLookup customerLookup,
    IExportJobQueue queue,
    IQuotationsUnitOfWork unitOfWork,
    IValidator<ExportQuotationsCommand> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : ICommandHandler<ExportQuotationsCommand, ExportJobAccepted>
{
    public async Task<ExportJobAccepted> HandleAsync(
        ExportQuotationsCommand command,
        CancellationToken cancellationToken)
    {
        // D4, en este orden y todo antes de encolar. 1: tenant y permiso.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationRead);

        // 2: filtros y rango.
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var status = QuotationListing.ParseStatus(command.Status);
        var advisorId = command.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await QuotationListing.ResolveClientIdsByNitAsync(
            customerLookup, command.TenantId, command.ClientNit, cancellationToken);

        // 3: al menos una fila. Un EXISTS es barato, y enterarse de que no había nada después de
        // esperar un correo es peor.
        // El rango se corta en el día del tenant (spec 2026-09-17, punto 3). El job guarda las fechas
        // tal como llegaron: el procesador las vuelve a cortar con el calendario de ese momento.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var created = TenantDayRange.Of(calendar, command.CreatedFrom, command.CreatedTo);
        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            created.From,
            created.Before,
            command.QuotationNumber,
            cancellationToken);
        if (!anyRow)
        {
            throw new QuotationsDomainException(
                "quotation.export.empty",
                "There are no quotations matching the export filters.");
        }

        // 4: el límite de pendientes, contando cotizaciones y pedidos. Es de mejor esfuerzo —cuenta
        // y después inserta, sin bloqueo—: ver ExportJobLimits.PendingPerRequester.
        var pending = await queue.CountPendingAsync(
            command.TenantId, executionContext.SubjectId, cancellationToken);
        if (pending >= ExportJobLimits.PendingPerRequester)
        {
            throw new QuotationsDomainException(
                "quotation.export.pending_limit",
                $"There are already {ExportJobLimits.PendingPerRequester} exports in progress for this user.");
        }

        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(),
            command.TenantId,
            executionContext.SubjectId,
            ExportJobKind.Quotations,
            ExportJobFilters.Serialize(new QuotationsExportFilters(
                command.ClientId,
                command.AdvisorId,
                command.Status,
                command.CreatedFrom!.Value,
                command.CreatedTo!.Value,
                command.ClientNit,
                command.QuotationNumber)),
            calendar.UtcNow);
        queue.Add(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportJobAccepted(job.Id, job.RequestedAt);
    }
}
