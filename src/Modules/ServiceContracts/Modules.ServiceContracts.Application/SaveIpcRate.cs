using System.Globalization;
using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// El cuerpo de <c>PUT /ipc-rates/{year}</c>. <c>ExpectedRate</c> es la tasa que mostraba la pantalla y tiene tres estados, por
/// eso es un <see cref="JsonElement"/>: ausente es un error, <c>null</c> es «no mostraba ninguna» y un número es esa tasa.
/// </summary>
public sealed record SaveIpcRateRequest(decimal? Rate)
{
    // Propiedad y no parámetro del constructor: con `JsonElement ExpectedRate = default` el esquema de OpenAPI
    // (JsonSchemaExporter) intenta escribir ese valor por defecto y /openapi/v1.json entero responde 500 (precedente
    // UpdateTaxRateRequest).
    public JsonElement ExpectedRate { get; init; }
}

public sealed record SaveIpcRateCommand(Guid TenantId, int Year, decimal? Rate, JsonElement ExpectedRate) : ICommand<IpcRateDto>
{
    public static SaveIpcRateCommand From(Guid tenantId, int year, SaveIpcRateRequest request) =>
        new(tenantId, year, request.Rate, request.ExpectedRate);
}

public sealed class SaveIpcRateValidator : AbstractValidator<SaveIpcRateCommand>
{
    /// <summary>Precisión de <c>billing.ipc_rates.rate</c> (<c>numeric(6,2)</c>).</summary>
    private const int RatePrecision = 6;

    private const int RateScale = 2;

    public SaveIpcRateValidator(ITenantClock tenantClock)
    {
        // El techo es el año siguiente al de la «hoy» del tenant.
        RuleFor(command => command.Year)
            .MustAsync(async (command, year, cancellationToken) =>
                year >= IpcRate.MinYear
                && year <= (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today.Year + 1)
            .WithMessage($"The year must be between {IpcRate.MinYear} and next year.");
        RuleFor(command => command.Rate)
            .NotNull()
            .InclusiveBetween(IpcRate.MinRate, IpcRate.MaxRate)
            .PrecisionScale(RatePrecision, RateScale, ignoreTrailingZeros: true);
        RuleFor(command => command.ExpectedRate)
            .Must(IsNullOrARate)
            .WithMessage("ExpectedRate is required: the rate the screen showed, with at most two decimals, or null.");
    }

    private static bool IsNullOrARate(JsonElement expected) => expected.ValueKind switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.Number => expected.TryGetDecimal(out var rate) && decimal.Round(rate, RateScale) == rate,
        _ => false,
    };
}

/// <summary>
/// Guarda la tasa de un año sin pisar nunca en silencio la de otra persona (owner, spec reajuste §6.11): la misma tasa ya
/// guardada responde sin escribir; si lo guardado no es <c>expectedRate</c>, 409; un año con cambios pendientes no se edita.
/// Todo se decide con la fila de la tasa bajo candado exclusivo: una confirmación en curso termina antes de que aquí se mire si
/// el año está en uso, y la que llegue después espera y lee la tasa nueva.
/// </summary>
public sealed class SaveIpcRateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IServiceContractsMemberDirectory members,
    IIpcRateRepository ipcRates,
    IServicePriceScheduleRepository schedules,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    IValidator<SaveIpcRateCommand> validator)
    : ICommandHandler<SaveIpcRateCommand, IpcRateDto>
{
    private static readonly string[] SavedFields = ["year", "rate"];

    public async Task<IpcRateDto> HandleAsync(SaveIpcRateCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var rate = command.Rate!.Value;
        decimal? expected = command.ExpectedRate.ValueKind == JsonValueKind.Null ? null : command.ExpectedRate.GetDecimal();
        var (saved, savedInUse) = await SaveAsync(command, rate, expected, cancellationToken);

        // Ya sin transacción: armar la respuesta no tiene por qué sostener el candado.
        return await ToDtoAsync(saved, savedInUse, cancellationToken);
    }

    /// <summary>La tasa que queda guardada y si algún cambio pendiente la usa.</summary>
    private async Task<(IpcRate Rate, bool InUse)> SaveAsync(
        SaveIpcRateCommand command, decimal rate, decimal? expected, CancellationToken cancellationToken)
    {
        // Sin confirmarla (la misma tasa o un rechazo) se revierte al liberarla.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // El candado primero: «en uso» se consulta después, así que ya cuenta lo que confirmó quien tenía la tasa.
        var stored = await ipcRates.FindForUpdateAsync(command.TenantId, command.Year, cancellationToken);
        var inUse = (await schedules.ListPendingIpcYearsAsync(command.TenantId, cancellationToken)).Contains(command.Year);
        if (stored?.Rate == rate)
        {
            return (stored, inUse);
        }

        // El conflicto antes que el uso: quien miraba una tasa vieja tiene que ver primero la nueva.
        if (stored?.Rate != expected)
        {
            throw RateChanged(null);
        }

        if (inUse)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.IpcInUse, "A pending price change uses that IPC rate.");
        }

        var memberId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, command.TenantId, cancellationToken);
        var now = clock.UtcNow;
        if (stored is null)
        {
            stored = IpcRate.Create(command.TenantId, command.Year, rate, memberId, now);
            ipcRates.Add(stored);
        }
        else
        {
            stored.Change(rate, memberId, now);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.IpcSaved,
            ServiceContractsAuditActions.Resources.IpcRate,
            command.Year.ToString(CultureInfo.InvariantCulture),
            "success",
            SavedFields,
            now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        // R-4: con la fila bajo candado nadie debería poder cambiarla en el medio; el token de versión queda de respaldo y, si
        // frena la escritura, para quien llama lo guardado ya no es lo que esperaba. La primera escritura simultánea, que no tiene
        // fila que bloquear, sale como billing.concurrent_first_write.
        catch (RequestConcurrencyException exception)
        {
            throw RateChanged(exception);
        }

        await transaction.CommitAsync(cancellationToken);
        return (stored, false);
    }

    private static ResourceConflictException RateChanged(Exception? inner) =>
        new(ServiceContractsErrorCodes.IpcRateChanged, "The stored IPC rate is not the expected one.", inner);

    private async Task<IpcRateDto> ToDtoAsync(IpcRate rate, bool inUse, CancellationToken cancellationToken)
    {
        MemberRefDto? updatedBy = null;
        if (rate.UpdatedByMemberId is { } id)
        {
            var found = await members.FindAsync(rate.TenantId, [id], cancellationToken);
            updatedBy = ServiceContractsMapping.ToMemberRefDto(id, found.Count > 0 ? found[0] : null);
        }

        return ServiceContractsMapping.ToIpcRateDto(rate, inUse, updatedBy);
    }
}
