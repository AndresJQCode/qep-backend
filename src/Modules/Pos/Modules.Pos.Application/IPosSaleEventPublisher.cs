using Modules.Pos.Domain;

namespace Modules.Pos.Application;

/// <summary>
/// <c>pos.sale.created.v1</c> y <c>pos.sale.voided.v1</c> en la proyección de outbox, en el mismo <c>SaveChanges</c> que el
/// cambio. El inventario básico del WMS los consume para descontar y devolver stock.
/// </summary>
public interface IPosSaleEventPublisher
{
    /// <summary>Se llama después de armar la venta y antes del guardado del caso de uso. Viaja con <c>version = 1</c>.</summary>
    void PublishCreated(PosSale sale, DateTimeOffset occurredAt);

    /// <summary>Se llama después de <see cref="PosSale.Void"/> y de registrar la anulación en la caja. Viaja con <c>version = 2</c>.</summary>
    void PublishVoided(PosSale sale, DateTimeOffset occurredAt);
}

/// <summary>
/// El contrato de los dos eventos. Los nombres en camelCase son el contrato con el WMS (System.Text.Json serializa los records con
/// el nombre del parámetro tal cual). El POS no tiene <c>Version</c>: <c>created</c> viaja con 1 y <c>voided</c> con 2, así que
/// el consumidor ordena la anulación después de la venta (decisión del agente, spec de inventario básico §5.1).
/// </summary>
public static class PosSaleEvents
{
    public const string CreatedEventName = "pos.sale.created.v1";

    public const string VoidedEventName = "pos.sale.voided.v1";

    public const int CreatedVersion = 1;

    public const int VoidedVersion = 2;

    public static PosSaleCreatedEvent Created(PosSale sale, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return new PosSaleCreatedEvent(
            sale.TenantId,
            sale.Id.Value,
            CreatedVersion,
            occurredAt,
            [.. sale.Lines.Select(line => new PosSaleEventLine(line.Id.Value, line.VariantId, line.Quantity))]);
    }

    public static PosSaleVoidedEvent Voided(PosSale sale, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return new PosSaleVoidedEvent(sale.TenantId, sale.Id.Value, VoidedVersion, occurredAt);
    }
}

public sealed record PosSaleEventLine(Guid lineId, Guid variantId, decimal quantity);

public sealed record PosSaleCreatedEvent(
    Guid tenantId, Guid saleId, int version, DateTimeOffset occurredAt, IReadOnlyList<PosSaleEventLine> lines);

public sealed record PosSaleVoidedEvent(Guid tenantId, Guid saleId, int version, DateTimeOffset occurredAt);
