using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary><c>null</c> quita la tasa (spec §4.2).</summary>
public sealed record SetFreightTaxRateCommand(Guid TenantId, Guid? TaxRateId) : ICommand<CarrierSettingsDto>;

/// <summary>
/// La tasa de IVA del envío, con una operación propia y no con el <c>PUT /quotations/settings</c>
/// del mínimo de compra (spec §4.2, P2): así ninguno de los dos formularios pisa al otro.
/// Sólo una tasa activa del tenant: una inexistente y una de otro tenant dan el mismo
/// <c>quotations.freight_tax_rate.not_found</c>, que nunca distingue.
/// </summary>
public sealed class SetFreightTaxRateHandler(
    ICarrierRepository carriers,
    IQuotationSettingsStore store,
    IQuotationTaxRateLookup taxRates,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SetFreightTaxRateCommand, CarrierSettingsDto>
{
    public const string AuditAction = "quotations.freight_tax_rate.updated";

    public async Task<CarrierSettingsDto> HandleAsync(SetFreightTaxRateCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);

        if (command.TaxRateId is { } taxRateId)
        {
            var taxRate = await taxRates.FindAsync(command.TenantId, taxRateId, cancellationToken)
                ?? throw new QuotationsDomainException(
                    "quotations.freight_tax_rate.not_found",
                    "La tasa de impuesto no existe.");
            if (!taxRate.IsActive)
            {
                throw new QuotationsDomainException(
                    "quotations.freight_tax_rate.inactive",
                    "La tasa de impuesto está inactiva.");
            }
        }

        // El store bloquea la fila del tenant hasta el commit, como el guardado del mínimo de compra.
        // La misma tasa no es un cambio: no se guarda ni se audita.
        if (await store.GetFreightTaxRateIdAsync(command.TenantId, cancellationToken) != command.TaxRateId)
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await store.SetFreightTaxRateIdAsync(command.TenantId, command.TaxRateId, cancellationToken);
            // El recurso es el tenant: la tasa es parte de su configuración.
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                AuditAction,
                command.TenantId.ToString(),
                "success",
                clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await CarrierMapping.SettingsAsync(carriers, store, taxRates, command.TenantId, cancellationToken);
    }
}
