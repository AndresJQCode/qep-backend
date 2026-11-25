using BuildingBlocks.Application;
using BuildingBlocks.Domain.Pricing;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>El cuerpo de <c>POST /price-reviews/confirm-ipc</c>, todo anulable para que su ausencia nombre el campo.</summary>
public sealed record ConfirmIpcRequest(int? Year, IReadOnlyList<ConfirmIpcItemInput>? Items);

/// <summary>
/// <c>ExpectedPrice</c> es el precio calculado que la persona vio, aunque escriba otro; <c>NewPrice</c> nulo acepta el
/// calculado, y uno distinto exige <c>Reason</c>.
/// </summary>
public sealed record ConfirmIpcItemInput(Guid? ServiceId, long? Version, decimal? ExpectedPrice, decimal? NewPrice, string? Reason);

public sealed record ConfirmIpcReviewsCommand(Guid TenantId, int? Year, IReadOnlyList<ConfirmIpcItemInput> Items)
    : ICommand<ConfirmIpcResponseDto>
{
    public const int MaxItems = 200;

    public static ConfirmIpcReviewsCommand From(Guid tenantId, ConfirmIpcRequest request) =>
        new(tenantId, request.Year, request.Items ?? []);
}

public sealed class ConfirmIpcReviewsValidator : AbstractValidator<ConfirmIpcReviewsCommand>
{
    public ConfirmIpcReviewsValidator()
    {
        RuleFor(command => command.Year).NotNull();
        RuleFor(command => command.Items)
            .NotEmpty()
            .Must(items => items.Count <= ConfirmIpcReviewsCommand.MaxItems)
            .WithMessage($"A batch takes at most {ConfirmIpcReviewsCommand.MaxItems} contracts.")
            .Must(items => items
                .Select(item => item?.ServiceId)
                .OfType<Guid>()
                .GroupBy(id => id)
                .All(group => group.Count() == 1))
            .WithMessage("A contract cannot appear twice in the same batch.");
        RuleForEach(command => command.Items).NotNull().SetValidator(new ItemValidator());
    }

    private sealed class ItemValidator : AbstractValidator<ConfirmIpcItemInput>
    {
        public ItemValidator()
        {
            RuleFor(item => item.ServiceId).NotNull();
            RuleFor(item => item.Version).NotNull();
            RuleFor(item => item.ExpectedPrice).NotNull();
            RuleFor(item => item.NewPrice)
                .GreaterThan(0m)
                .PrecisionScale(ServiceRules.PricePrecision, ServiceRules.PriceScale, ignoreTrailingZeros: true);
            RuleFor(item => item.Reason).MaximumLength(ServicePriceChange.ReasonMaxLength);
        }
    }
}

/// <summary>
/// La confirmación en lote del IPC de un año (spec reajuste §6.7). Cada contrato va en su propia transacción, con su versión, y
/// responde su propio resultado: uno que falla no tumba a los demás ni deja nada escrito. El precio se vuelve a calcular aquí y
/// tiene que ser el que la persona vio. Confirmar crea el cambio programado en el período de revisión y, si ese período ya está
/// en la ventana o retenido, genera en la misma transacción. Esa transacción sostiene la tasa del año con un candado compartido,
/// así que nadie la cambia entre que se lee y que el cambio queda guardado.
/// </summary>
public sealed class ConfirmIpcReviewsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ITenantPriceRounding priceRounding,
    IMembershipDirectory membershipDirectory,
    IServiceContractsCustomerLookup customers,
    IServiceContractRepository services,
    IChargeRepository charges,
    IServicePriceChangeRepository priceChanges,
    IServicePriceScheduleRepository schedules,
    IIpcRateRepository ipcRates,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<ConfirmIpcReviewsCommand> validator)
    : ICommandHandler<ConfirmIpcReviewsCommand, ConfirmIpcResponseDto>
{
    private const string Confirmed = "Confirmed";

    private const string Failed = "Failed";

    private const string ConcurrencyConflict = "concurrency.conflict";

    private static readonly string[] ConfirmedFields = ["newPrice", "effectiveFrom", "reason", "ipcYear", "ipcRate", "extraPoints"];

    public async Task<ConfirmIpcResponseDto> HandleAsync(ConfirmIpcReviewsCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var year = command.Year!.Value;
        // Sólo comprueba que el año tenga tasa, para rechazar el lote entero; cada contrato la vuelve a leer.
        if (await ipcRates.FindAsync(command.TenantId, year, cancellationToken) is null)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.IpcNotSet, $"There is no IPC rate for {year}.");
        }

        var memberId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, command.TenantId, cancellationToken);
        var today = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today;
        var batch = new Batch(command.TenantId, year, memberId, today);

        var outcomes = new List<(Guid ServiceId, string? Code)>(command.Items.Count);
        foreach (var item in command.Items)
        {
            // Lo que un ítem rechazado dejó en el contexto no puede salir con el guardado del siguiente.
            unitOfWork.Clear();
            string? code;
            try
            {
                code = await unitOfWork.RunBatchItemAsync(() => ConfirmAsync(batch, item, cancellationToken), cancellationToken);
            }
            catch (ServiceContractsDomainException exception)
            {
                code = exception.Code;
            }
            catch (RequestConcurrencyException)
            {
                code = ConcurrencyConflict;
            }

            outcomes.Add((item.ServiceId!.Value, code));
        }

        unitOfWork.Clear();
        var confirmed = await ComposeConfirmedAsync(
            command.TenantId,
            outcomes.Where(outcome => outcome.Code is null).Select(outcome => new ServiceContractId(outcome.ServiceId)).ToArray(),
            cancellationToken);

        return new ConfirmIpcResponseDto(outcomes
            .Select(outcome => new ConfirmIpcResultDto(
                outcome.ServiceId,
                outcome.Code is null ? Confirmed : Failed,
                outcome.Code,
                outcome.Code is null ? confirmed.GetValueOrDefault(outcome.ServiceId) : null))
            .ToList());
    }

    /// <summary>Null si quedó confirmado; si no, el código del primer chequeo que no pasa, en el orden del plan.</summary>
    private async Task<string?> ConfirmAsync(Batch batch, ConfirmIpcItemInput item, CancellationToken cancellationToken)
    {
        // Cada rechazo sale sin confirmar: la transacción se revierte al liberarla y suelta el candado.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Antes de cualquier lectura que decida: si alguien está guardando la tasa, aquí se espera y se lee la nueva; mientras
        // este ítem no termine, quien quiera guardarla espera y después encuentra el cambio programado. Se lee por contrato: si la
        // editaron durante el lote, quien vio la anterior recibe price_changed, y el motivo y el cambio programado copian la que
        // vale ahora.
        var rate = await ipcRates.FindForShareAsync(batch.TenantId, batch.Year, cancellationToken);
        var service = await services.FindAsync(batch.TenantId, new ServiceContractId(item.ServiceId!.Value), cancellationToken);
        if (service is null)
        {
            return ServiceContractsErrorCodes.ServiceNotFound;
        }

        if (service.Version != item.Version)
        {
            return ConcurrencyConflict;
        }

        if (service.AdjustmentMode != AdjustmentMode.Ipc)
        {
            return ServiceContractsErrorCodes.PriceReviewNotIpc;
        }

        var pending = await schedules.FindPendingAsync(batch.TenantId, service.Id, cancellationToken);
        var review = PriceReview.Of(service, pending, batch.Today);
        if (review.State is not (ReviewState.Due or ReviewState.Held))
        {
            return ServiceContractsErrorCodes.PriceReviewNotPending;
        }

        // Un pendiente que no cubre la revisión (aplica en un período anterior aún sin generar): hay a lo sumo uno por
        // contrato, así que primero se aplica o se cancela.
        if (pending is not null)
        {
            return ServiceContractsErrorCodes.PriceScheduleAlreadyPending;
        }

        if (review.IpcYear != batch.Year)
        {
            return ServiceContractsErrorCodes.PriceReviewYearMismatch;
        }

        var rounding = await RoundingAsync(batch, service.Currency, cancellationToken);
        var computed = rate is null ? null : PriceReview.IpcPrice(service.Price, rate.Rate, service.IpcExtraPoints, rounding);
        if (rate is null || computed is null || computed != item.ExpectedPrice)
        {
            return ServiceContractsErrorCodes.PriceReviewPriceChanged;
        }

        var newPrice = item.NewPrice ?? computed.Value;
        var typed = newPrice != computed.Value;
        if (typed && string.IsNullOrWhiteSpace(item.Reason))
        {
            return ServiceContractsErrorCodes.PriceChangeReasonRequired;
        }

        var reason = typed ? item.Reason! : PriceReview.IpcReason(batch.Year, rate.Rate, service.IpcExtraPoints);
        var now = clock.UtcNow;
        var schedule = ServicePriceSchedule.ConfirmIpc(service, batch.Year, rate.Rate, newPrice, reason, batch.MemberId, now);
        schedules.Add(schedule);
        PriceReviewGeneration.Run(service, schedule, batch.Today, now, charges, priceChanges);

        auditPublisher.Publish(
            batch.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ServicePriceScheduled,
            ServiceContractsAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            ConfirmedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private async Task<PriceRounding> RoundingAsync(Batch batch, string currency, CancellationToken cancellationToken)
    {
        if (!batch.Roundings.TryGetValue(currency, out var rounding))
        {
            rounding = await priceRounding.GetAsync(batch.TenantId, currency, cancellationToken);
            batch.Roundings[currency] = rounding;
        }

        return rounding;
    }

    // Todos juntos y releídos al final: una sola composición para hasta 200 contratos, con lo que quedó guardado.
    private async Task<IReadOnlyDictionary<Guid, ServiceDto>> ComposeConfirmedAsync(
        Guid tenantId, IReadOnlyCollection<ServiceContractId> ids, CancellationToken cancellationToken)
    {
        var confirmed = await services.FindManyAsync(tenantId, ids, cancellationToken);
        var customerRefs = (await customers.FindManyAsync(
                tenantId, confirmed.Select(service => service.CustomerId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(customer => customer.Id, customer => new CustomerRefDto(customer.Id, customer.Name, customer.PhoneE164, customer.Email, customer.BusinessName));

        return (await composer.ComposeSummariesAsync(tenantId, confirmed, customerRefs, cancellationToken))
            .ToDictionary(service => service.Id);
    }

    /// <summary>Lo que es igual para todos los ítems de un lote.</summary>
    private sealed record Batch(Guid TenantId, int Year, Guid? MemberId, DateOnly Today)
    {
        public Dictionary<string, PriceRounding> Roundings { get; } = new(StringComparer.Ordinal);
    }
}
