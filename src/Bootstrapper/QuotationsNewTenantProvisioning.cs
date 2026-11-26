using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Tenancy → Quotations (decisión del owner, 2026-11-23): el tenant recién nacido, por signup o por invitación de un mayorista,
/// arranca con compra mínima de <see cref="QuotationSettings.NewTenantMinimumUnits"/> unidad y sin montos mínimos, en vez del
/// <see cref="QuotationSettings.Default"/> de 6 unidades o 500.000 COP. Un tenant que ya existía sin fila no se toca: sigue con
/// el por defecto de antes. Se llama sólo cuando el alta crea el tenant, así que nunca pisa lo que el owner ya editó.
/// </summary>
internal sealed class QuotationsNewTenantProvisioning(IQuotationSettingsStore settings, IQuotationsUnitOfWork unitOfWork)
    : INewTenantProvisioning
{
    public async Task ProvisionAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        // SaveAsync exige la transacción: bloquea la fila del tenant hasta que termina.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await settings.SaveAsync(
            tenantId.Value,
            new QuotationSettings(QuotationSettings.NewTenantMinimumUnits, new Dictionary<string, decimal>()),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
