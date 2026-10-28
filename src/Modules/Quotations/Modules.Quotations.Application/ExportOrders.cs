using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Encola la exportación de pedidos a Excel (spec 2026-09-12). Los filtros son los de la
/// lista, incluido <c>Source</c> (spec 2026-10-15 §6.1).</summary>
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
    string? TimeZone = null) : ICommand<ExportJobAccepted>;

/// <summary>Lo que viaja serializado en el trabajo y el procesador vuelve a leer.</summary>
public sealed record OrdersExportFilters(
    Guid? ClientId,
    Guid? AdvisorId,
    string? Status,
    string? PaymentStatus,
    DateOnly ConvertedFrom,
    DateOnly ConvertedTo,
    string? ClientCuc,
    string? OrderNumber,
    string? Source = null,
    /// <summary>Nula en los jobs encolados antes de que existiera: cortan en el día del tenant.</summary>
    string? TimeZone = null);

public sealed class ExportOrdersValidator : AbstractValidator<ExportOrdersCommand>
{
    public ExportOrdersValidator()
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
        var source = OrderListing.ParseSource(command.Source);
        var advisorId = command.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, command.TenantId, command.ClientCuc, cancellationToken);

        // 3: al menos un pedido.
        // El rango se corta en el día de quien pide, o en el del tenant (spec 2026-09-17, punto 3).
        var calendar = OrderListing.CalendarFor(
            await tenantClock.GetAsync(command.TenantId, cancellationToken), command.TimeZone);
        var converted = TenantDayRange.Of(calendar, command.ConvertedFrom, command.ConvertedTo);

        var anyRow = await repository.AnyForExportAsync(
            command.TenantId,
            command.ClientId,
            clientIds,
            advisorId,
            status,
            paymentStatus,
            converted.From,
            converted.Before,
            command.OrderNumber,
            source,
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
                command.ConvertedFrom!.Value,
                command.ConvertedTo!.Value,
                command.ClientCuc,
                command.OrderNumber,
                command.Source,
                command.TimeZone)),
            calendar.UtcNow);
        queue.Add(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportJobAccepted(job.Id, job.RequestedAt);
    }
}
