using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Encola la exportación de pedidos a Excel (spec 2026-09-12). Los filtros son los de la
/// lista, incluido <c>Source</c> (spec 2026-10-15 §6.1), el envío y la marca de facturado (spec 2026-11-10 §5).
/// Sin <c>Status</c> no salen los archivados, igual que en la tabla.</summary>
public sealed record ExportOrdersCommand(
    Guid TenantId,
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    DateOnly? ConvertedFrom,
    DateOnly? ConvertedTo,
    string? ClientCuc,
    string? OrderNumber,
    string? Source = null,
    /// <summary>La zona IANA de quien pide el archivo, igual que en <see cref="ListOrdersQuery"/>.</summary>
    string? TimeZone = null,
    string? FulfillmentStatus = null,
    string? Invoiced = null,
    /// <summary>Spec 2026-11-26 §12-D: el mismo <c>kind</c> de la lista.</summary>
    string? Kind = null) : ICommand<ExportJobAccepted>;

/// <summary>Lo que viaja serializado en el trabajo y el procesador vuelve a leer.</summary>
public sealed record OrdersExportFilters(
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    /// <summary>Nulas cuando se exporta por número de pedido o CUC sin rango: el archivo no corta por fecha.</summary>
    DateOnly? ConvertedFrom,
    DateOnly? ConvertedTo,
    string? ClientCuc,
    string? OrderNumber,
    string? Source = null,
    /// <summary>Nula en los jobs encolados antes de que existiera: cortan en el día del tenant.</summary>
    string? TimeZone = null,
    /// <summary>Nulos en los jobs encolados antes de los filtros del spec 2026-11-10: exportan sin filtrarlos.</summary>
    string? FulfillmentStatus = null,
    string? Invoiced = null,
    /// <summary>Nulo en los jobs encolados antes de los pedidos de proyecto: exportan los dos tipos.</summary>
    string? Kind = null);

public sealed class ExportOrdersValidator : AbstractValidator<ExportOrdersCommand>
{
    public ExportOrdersValidator()
    {
        // El rango es obligatorio sólo cuando nada más acota el archivo. Con número de pedido o CUC del cliente la
        // pantalla busca algo puntual y no sabe en qué período cayó: pedirle fechas la obliga a adivinar.
        RuleFor(command => command.ConvertedFrom)
            .NotNull()
            .When(command => !NarrowsWithoutDates(command))
            .WithMessage("convertedFrom is required.");
        RuleFor(command => command.ConvertedTo)
            .NotNull()
            .When(command => !NarrowsWithoutDates(command))
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

    private static bool NarrowsWithoutDates(ExportOrdersCommand command) =>
        !string.IsNullOrWhiteSpace(command.OrderNumber) || !string.IsNullOrWhiteSpace(command.ClientCuc);
}

public sealed class ExportOrdersHandler(
    IOrderRepository repository,
    IQuotationCustomerLookup customerLookup,
    IExportJobQueue queue,
    IQuotationsUnitOfWork unitOfWork,
    IValidator<ExportOrdersCommand> validator,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : ICommandHandler<ExportOrdersCommand, ExportJobAccepted>
{
    public async Task<ExportJobAccepted> HandleAsync(
        ExportOrdersCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderRead);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Un estado u origen inválido se rechaza acá, antes de encolar: en el procesador sería un
        // trabajo que nace muerto.
        var status = OrderListing.ParseStatus(command.Status);
        var paymentStatus = OrderListing.ParsePaymentStatus(command.PaymentStatus);
        var fulfillmentStatus = OrderListing.ParseFulfillmentStatus(command.FulfillmentStatus);
        var invoiced = OrderListing.ParseInvoiced(command.Invoiced);
        var source = OrderListing.ParseSource(command.Source);
        var kind = OrderListing.ParseKind(command.Kind);
        var advisorId = command.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, command.TenantId, command.ClientCuc, cancellationToken);

        // 3: al menos un pedido. El rango corta por la fecha del pedido, que ya es un día del tenant (spec 2026-11-16 §4):
        // TimeZone se sigue aceptando y guardando en el trabajo, pero no mueve el filtro. El calendario sólo da el instante
        // de la solicitud.
        OrderListing.EnsureValidTimeZone(command.TimeZone);
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);

        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            fulfillmentStatus,
            invoiced,
            command.ConvertedFrom,
            command.ConvertedTo,
            command.OrderNumber,
            source,
            kind,
            cancellationToken);
        if (!anyRow)
        {
            throw new QuotationsDomainException(
                "order.export.empty",
                "There are no orders matching the export filters.");
        }

        var pending = await queue.CountPendingAsync(
            command.TenantId, executionContext.SubjectId, cancellationToken);
        if (pending >= ExportJobLimits.PendingPerRequester)
        {
            throw new QuotationsDomainException(
                "order.export.pending_limit",
                $"There are already {ExportJobLimits.PendingPerRequester} exports in progress for this user.");
        }

        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(),
            command.TenantId,
            executionContext.SubjectId,
            ExportJobKind.Orders,
            ExportJobFilters.Serialize(new OrdersExportFilters(
                command.ClientId,
                command.AdvisorId,
                command.Status,
                command.PaymentStatus,
                command.ConvertedFrom,
                command.ConvertedTo,
                command.ClientCuc,
                command.OrderNumber,
                command.Source,
                command.TimeZone,
                command.FulfillmentStatus,
                command.Invoiced,
                command.Kind)),
            calendar.UtcNow);
        queue.Add(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportJobAccepted(job.Id, job.RequestedAt);
    }
}
