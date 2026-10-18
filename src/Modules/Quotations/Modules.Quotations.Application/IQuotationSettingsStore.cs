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

    /// <summary>Las opciones de «¿Quién paga el envío?» que el tenant tiene activas y su por defecto (spec
    /// 2026-10-17 §4.4 y §4.4.1), aparte de <see cref="GetAsync"/> por lo mismo que la tasa (P2). Sin fila, las
    /// tres activas y sin por defecto.</summary>
    Task<FreightChargeModeSettings> GetFreightChargeModesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Mismo contrato que <see cref="SetFreightTaxRateIdAsync"/>: dentro de la transacción, que
    /// bloquea la fila, y sin fila la crea con el mínimo de compra por defecto. Los modos y el por defecto se
    /// guardan juntos porque el uno depende de los otros: el por defecto tiene que estar activo.</summary>
    Task SetFreightChargeModesAsync(Guid tenantId, FreightChargeModeSettings settings, CancellationToken cancellationToken);
}

/// <summary>Las opciones de «¿Quién paga el envío?» activas del tenant (§4.4) y la que el formulario elige por
/// defecto (§4.4.1): <c>null</c> o una de <paramref name="Enabled"/>. El backend no la aplica a ningún documento
/// (D-F19): sólo la guarda y la devuelve.</summary>
public sealed record FreightChargeModeSettings(IReadOnlySet<FreightChargeMode> Enabled, FreightChargeMode? Default)
{
    /// <summary>Un tenant que nunca las tocó: las tres activas, ninguna por defecto.</summary>
    public static FreightChargeModeSettings Untouched { get; } =
        new(new HashSet<FreightChargeMode>(Enum.GetValues<FreightChargeMode>()), null);
}
