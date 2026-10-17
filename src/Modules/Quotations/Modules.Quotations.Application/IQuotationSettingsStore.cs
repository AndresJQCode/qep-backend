using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

public interface IQuotationSettingsStore
{
    /// <summary><see cref="QuotationSettings.Default"/> when the tenant has no row.</summary>
    Task<QuotationSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Stages the rows on the scoped unit of work; <c>IQuotationsUnitOfWork.SaveChangesAsync</c>
    /// commits them together with the audit outbox row (same transaction, backend CLAUDE.md).
    /// Must run inside <c>IQuotationsUnitOfWork.BeginTransactionAsync</c>: it locks the tenant's
    /// settings row until that transaction ends, which is what serializes concurrent saves.</summary>
    Task SaveAsync(Guid tenantId, QuotationSettings settings, CancellationToken cancellationToken);

    /// <summary>La tasa de IVA del envío (spec 2026-10-17 §4.2), aparte de <see cref="GetAsync"/> a
    /// propósito (P2): así el guardado del mínimo de compra no la conoce y no la puede pisar.
    /// <c>null</c> sin fila o sin tasa.</summary>
    Task<Guid?> GetFreightTaxRateIdAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Sin fila, la crea con <see cref="QuotationSettings.Default"/>.<c>MinimumUnits</c>. Mismo
    /// contrato que <see cref="SaveAsync"/>: dentro de la transacción, que bloquea la fila.</summary>
    Task SetFreightTaxRateIdAsync(Guid tenantId, Guid? taxRateId, CancellationToken cancellationToken);

    /// <summary>Las opciones de «¿Quién paga el envío?» que el tenant tiene activas (spec 2026-10-17 §4.4),
    /// aparte de <see cref="GetAsync"/> por lo mismo que la tasa (P2). Sin fila, las tres.</summary>
    Task<IReadOnlySet<FreightChargeMode>> GetEnabledFreightChargeModesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Mismo contrato que <see cref="SetFreightTaxRateIdAsync"/>: dentro de la transacción, que
    /// bloquea la fila, y sin fila la crea con el mínimo de compra por defecto.</summary>
    Task SetEnabledFreightChargeModesAsync(
        Guid tenantId, IReadOnlySet<FreightChargeMode> enabledModes, CancellationToken cancellationToken);
}
