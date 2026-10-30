using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>El cuerpo de <c>PUT /services/{id}</c>; anulable por el mismo motivo que <see cref="CreateServiceRequest"/>.</summary>
public sealed record UpdateServiceRequest(string? Name, decimal? Price, string? Currency);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record UpdateServiceCommand(
    Guid TenantId,
    Guid ServiceId,
    string Name,
    decimal Price,
    string Currency,
    long ExpectedVersion)
    : ICommand<ServiceDetailDto>
{
    public static UpdateServiceCommand From(Guid tenantId, Guid serviceId, UpdateServiceRequest request, long expectedVersion) => new(
        tenantId,
        serviceId,
        request.Name ?? string.Empty,
        request.Price ?? 0m,
        request.Currency ?? string.Empty,
        expectedVersion);
}

public sealed class UpdateServiceValidator : AbstractValidator<UpdateServiceCommand>
{
    public UpdateServiceValidator()
    {
        RuleFor(command => command.Name).MustBeAServiceName();
        RuleFor(command => command.Price).MustBeAServicePrice();
        RuleFor(command => command.Currency).NotEmpty();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Cambia nombre, precio y moneda (spec §3.2). Los cobros ya generados conservan su monto: el cambio sólo toca a los que se
/// generen después, porque cada <c>Charge</c> copió el precio al nacer.
/// </summary>
public sealed class UpdateServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IBillingServiceRepository services,
    IBillingUnitOfWork unitOfWork,
    IBillingAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<UpdateServiceCommand> validator)
    : ICommandHandler<UpdateServiceCommand, ServiceDetailDto>
{
    public async Task<ServiceDetailDto> HandleAsync(UpdateServiceCommand command, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, BillingPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Primero el servicio, acotado al tenant: nada más del módulo lleva tenant en la consulta.
        var service = await services.FindAsync(command.TenantId, new BillingServiceId(command.ServiceId), cancellationToken)
            ?? throw BillingNotFound.Service(command.ServiceId);
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

        var now = clock.UtcNow;
        service.Update(name, command.Price, currency, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            BillingAuditActions.ServiceUpdated,
            BillingAuditActions.Resources.Service,
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
    public static void EnsureCurrent(BillingService service, long expectedVersion)
    {
        if (service.Version != expectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The service changed after it was loaded.");
        }
    }
}
