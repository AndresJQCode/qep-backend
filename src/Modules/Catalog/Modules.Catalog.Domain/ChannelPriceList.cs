namespace Modules.Catalog.Domain;

/// <summary>
/// Spec 2026-11-22 (D-4): qué lista precia un canal del tenant. Sin fila, el canal vende al precio base. Una fila por tenant y
/// canal; cambiar la lista reemplaza la fila.
/// </summary>
public sealed class ChannelPriceList
{
    // EF Core materializa por acá.
    private ChannelPriceList()
    {
    }

    public Guid TenantId { get; private set; }

    public PriceChannel Channel { get; private set; }

    public PriceListId PriceListId { get; private set; }

    public static ChannelPriceList Assign(Guid tenantId, PriceChannel channel, PriceListId priceListId) =>
        Enum.IsDefined(channel)
            ? new ChannelPriceList { TenantId = tenantId, Channel = channel, PriceListId = priceListId }
            : throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown price channel.");

    /// <summary>Cambiar la lista es actualizar la fila, no borrarla y crear otra con la misma llave.</summary>
    public void Reassign(PriceListId priceListId) => PriceListId = priceListId;
}
