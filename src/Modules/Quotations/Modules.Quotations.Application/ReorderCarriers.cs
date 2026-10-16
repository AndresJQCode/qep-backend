using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>El orden entero: todos los ids del tenant, cada uno una vez (spec §4.3).</summary>
public sealed record ReorderCarriersCommand(Guid TenantId, IReadOnlyList<Guid>? CarrierIds) : ICommand<CarrierSettingsDto>;

/// <summary>Asigna 0..n-1 en el orden recibido. Faltar, sobrar o repetir un id es
/// <c>quotations.carrier.order_mismatch</c>: un orden parcial dejaría dos transportadoras con el
/// mismo lugar. Devuelve la pantalla entera, que es lo que se repinta.</summary>
public sealed class ReorderCarriersHandler(
    ICarrierRepository carriers,
    IQuotationSettingsStore store,
    IQuotationTaxRateLookup taxRates,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ReorderCarriersCommand, CarrierSettingsDto>
{
    public async Task<CarrierSettingsDto> HandleAsync(ReorderCarriersCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);

        var ids = command.CarrierIds ?? [];
        var existing = await carriers.ListAsync(command.TenantId, cancellationToken);
        var byId = existing.ToDictionary(carrier => carrier.Id.Value);
        if (ids.Count != existing.Count || ids.Distinct().Count() != ids.Count || !ids.All(byId.ContainsKey))
        {
            throw new QuotationsDomainException(
                "quotations.carrier.order_mismatch",
                "El orden tiene que traer todas las transportadoras del tenant, cada una una sola vez.");
        }

        var now = clock.UtcNow;
        for (var position = 0; position < ids.Count; position++)
        {
            var carrier = byId[ids[position]];
            if (carrier.SortOrder != position)
            {
                carrier.MoveTo(position, now);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await CarrierMapping.SettingsAsync(carriers, store, taxRates, command.TenantId, cancellationToken);
    }
}
