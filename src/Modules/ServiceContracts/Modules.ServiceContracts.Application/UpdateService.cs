using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// El cuerpo de <c>PUT /services/{id}</c>; anulable por el mismo motivo que <see cref="CreateServiceRequest"/>.
/// <paramref name="PriceChangeReason"/> es obligatorio sólo cuando el precio o la moneda cambian; si no, se ignora.
/// </summary>
public sealed record UpdateServiceRequest(string? Name, decimal? Price, string? Currency, string? PriceChangeReason);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record UpdateServiceCommand(
    Guid TenantId,
    Guid ServiceId,
    string Name,
    decimal Price,
    string Currency,
    string? PriceChangeReason,
    long ExpectedVersion)
    : ICommand<ServiceDetailDto>
{
    public static UpdateServiceCommand From(Guid tenantId, Guid serviceId, UpdateServiceRequest request, long expectedVersion) => new(
        tenantId,
        serviceId,
        request.Name ?? string.Empty,
        request.Price ?? 0m,
        request.Currency ?? string.Empty,
        request.PriceChangeReason,
        expectedVersion);
}

public sealed class UpdateServiceValidator : AbstractValidator<UpdateServiceCommand>
{
    public UpdateServiceValidator()
    {
        RuleFor(command => command.Name).MustBeAServiceName();
        RuleFor(command => command.Price).MustBeAServicePrice();
        RuleFor(command => command.Currency).NotEmpty();
        // Si es obligatorio lo decide el dominio, que sabe si el precio cambió (billing.service.price_reason_required); acá
        // sólo el largo, para que el 422 nombre el campo.
        RuleFor(command => command.PriceChangeReason).MaximumLength(ServicePriceChange.ReasonMaxLength);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Cambia nombre, precio y moneda (spec §3.2). Los cobros ya generados conservan su monto: el cambio sólo toca a los que se
/// generen después, porque cada <c>Charge</c> copió el precio al nacer. Un cambio de precio o de moneda exige un motivo y deja
/// una fila en el historial del contrato, en la misma transacción; quien lo hizo es su membresía activa, si la tiene.
/// </summary>
public sealed class UpdateServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IServiceContractRepository services,
    IServicePriceChangeRepository priceChanges,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<UpdateServiceCommand> validator)
    : ICommandHandler<UpdateServiceCommand, ServiceDetailDto>
{
    public async Task<ServiceDetailDto> HandleAsync(UpdateServiceCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Primero el servicio, acotado al tenant: nada más del módulo lleva tenant en la consulta.
        var service = await services.FindAsync(command.TenantId, new ServiceContractId(command.ServiceId), cancellationToken)
            ?? throw ServiceContractsNotFound.Service(command.ServiceId);
        ServiceVersion.EnsureCurrent(service, command.ExpectedVersion);
        var currency = ServiceRules.NormalizeCurrency(command.Currency);

        var name = command.Name.Trim();
        var changedFields = new List<string>(3);
        if (!string.Equals(service.Name, name, StringComparison.Ordinal))
        {
            changedFields.Add("name");
        }

        if (service.Price != command.Price)
        {
            changedFields.Add("price");
        }

        if (!string.Equals(service.Currency, currency, StringComparison.Ordinal))
        {
            changedFields.Add("currency");
        }

        // La membresía sólo hace falta para firmar el historial: una edición del nombre no la consulta.
        var priceChanged = changedFields.Contains("price") || changedFields.Contains("currency");
        var changedBy = priceChanged
            ? await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            : null;

        var now = clock.UtcNow;
        var priceChange = service.Update(name, command.Price, currency, command.PriceChangeReason, changedBy, now);
        if (priceChange is not null)
        {
            priceChanges.Add(priceChange);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ServiceUpdated,
            ServiceContractsAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            changedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(service, cancellationToken);
    }
}

/// <summary>El <c>If-Match</c> de los mutadores del servicio: una versión vieja es 412 <c>concurrency.conflict</c>.</summary>
internal static class ServiceVersion
{
    public static void EnsureCurrent(ServiceContract service, long expectedVersion)
    {
        if (service.Version != expectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The service changed after it was loaded.");
        }
    }
}
