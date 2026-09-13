using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Pide el listado de ventas en un <c>.xlsx</c> por correo (spec 2026-09-12). Mismo modelo que
/// <see cref="ExportQuotationsCommand"/>: los filtros de <see cref="ListSalesQuery"/> sin
/// paginación, con el rango de conversión obligatorio y de a lo sumo un año.
/// </summary>
public sealed record ExportSalesCommand(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    DateOnly? ConvertedFrom,
    DateOnly? ConvertedTo,
    string? ClientCuc,
    string? SaleNumber) : ICommand<ExportJobAccepted>;

/// <summary>Los filtros tal como quedan en <c>export_jobs.filters</c>.</summary>
public sealed record SalesExportFilters(
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    DateOnly ConvertedFrom,
    DateOnly ConvertedTo,
    string? ClientCuc,
    string? SaleNumber);

/// <summary>El mismo rango que <see cref="ExportQuotationsValidator"/>, sobre la fecha de la venta
/// (D3). Validador para que el 422 lleve el mapa <c>errors</c>.</summary>
public sealed class ExportSalesValidator : AbstractValidator<ExportSalesCommand>
{
    public ExportSalesValidator()
    {
        RuleFor(command => command.ConvertedFrom)
            .NotNull()
            .WithMessage("convertedFrom is required.");
        RuleFor(command => command.ConvertedTo)
            .NotNull()
            .WithMessage("convertedTo is required.");
        RuleFor(command => command.ConvertedTo)
            .GreaterThanOrEqualTo(command => command.ConvertedFrom!.Value)
            .When(command => command.ConvertedFrom is not null && command.ConvertedTo is not null)
            .WithMessage("convertedTo must be on or after convertedFrom.");
        RuleFor(command => command.ConvertedTo)
            .LessThanOrEqualTo(command => command.ConvertedFrom!.Value.AddYears(1))
            .When(command => command.ConvertedFrom is not null && command.ConvertedTo is not null)
            .WithMessage("The range from convertedFrom to convertedTo cannot exceed one year.");
    }
}

public sealed class ExportSalesHandler(
    ISaleRepository repository,
    IQuotationCustomerLookup customerLookup,
    IExportJobQueue queue,
    IQuotationsUnitOfWork unitOfWork,
    IValidator<ExportSalesCommand> validator,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportSalesCommand, ExportJobAccepted>
{
    public async Task<ExportJobAccepted> HandleAsync(
        ExportSalesCommand command,
        CancellationToken cancellationToken)
    {
        // D4, mismo orden que cotizaciones. 1: tenant y permiso de ventas.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SalesPermissions.SaleRead);

        // 2: filtros y rango.
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var status = SaleListing.ParseStatus(command.Status);
        var paymentStatus = SaleListing.ParsePaymentStatus(command.PaymentStatus);
        var advisorId = command.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await SaleListing.ResolveClientIdsByCucAsync(
            customerLookup, command.TenantId, command.ClientCuc, cancellationToken);

        // 3: al menos una venta.
        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            command.ConvertedFrom,
            command.ConvertedTo,
            command.SaleNumber,
            cancellationToken);
        if (!anyRow)
        {
            throw new QuotationsDomainException(
                "sale.export.empty",
                "There are no sales matching the export filters.");
        }

        // 4: el mismo cupo que cotizaciones, contando los dos tipos. Es de mejor esfuerzo —cuenta
        // y después inserta, sin bloqueo—: ver ExportJobLimits.PendingPerRequester.
        var pending = await queue.CountPendingAsync(
            command.TenantId, executionContext.SubjectId, cancellationToken);
        if (pending >= ExportJobLimits.PendingPerRequester)
        {
            throw new QuotationsDomainException(
                "sale.export.pending_limit",
                $"There are already {ExportJobLimits.PendingPerRequester} exports in progress for this user.");
        }

        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(),
            command.TenantId,
            executionContext.SubjectId,
            ExportJobKind.Sales,
            ExportJobFilters.Serialize(new SalesExportFilters(
                command.ClientId,
                command.AdvisorId,
                command.Status,
                command.PaymentStatus,
                command.ConvertedFrom!.Value,
                command.ConvertedTo!.Value,
                command.ClientCuc,
                command.SaleNumber)),
            clock.UtcNow);
        queue.Add(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportJobAccepted(job.Id, job.RequestedAt);
    }
}
