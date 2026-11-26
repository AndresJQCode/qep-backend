namespace Modules.Wms.Domain.Inventory;

/// <summary>Un lote de una variante de un dueño. El código es único por <c>(tenant, dueño, variante, código)</c>.</summary>
public sealed class Lot
{
    public const int CodeMaxLength = 64;

    private Lot()
    {
    }

    public LotId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid VariantId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public DateOnly? ExpirationDate { get; private set; }

    public DateOnly? ManufacturedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Lot Create(
        Guid tenantId,
        Guid ownerId,
        Guid variantId,
        string code,
        DateOnly? expirationDate,
        DateOnly? manufacturedAt,
        bool expiryRequired,
        DateTimeOffset now)
    {
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > CodeMaxLength)
        {
            throw new WmsDomainException(WmsErrorCodes.LotCodeInvalid, $"El código del lote tiene que tener entre 1 y {CodeMaxLength} caracteres.");
        }

        if (expiryRequired && expirationDate is null)
        {
            throw new WmsDomainException(WmsErrorCodes.LotExpiryRequired, "La variante controla vencimiento: el lote necesita fecha de vencimiento.");
        }

        return new Lot
        {
            Id = LotId.New(),
            TenantId = tenantId,
            OwnerId = ownerId,
            VariantId = variantId,
            Code = trimmed,
            ExpirationDate = expirationDate,
            ManufacturedAt = manufacturedAt,
            CreatedAt = now,
        };
    }
}
