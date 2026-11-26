namespace Modules.Wms.Domain.Inventory;

/// <summary>Código de una unidad de medida: <c>Trim</c> + mayúsculas, <c>[A-Z0-9]{1,8}</c> (P-4).</summary>
public readonly record struct UomCode
{
    public const int MaxLength = 8;

    private UomCode(string value) => Value = value;

    public string Value { get; }

    public static UomCode Parse(string raw)
    {
        var normalized = (raw ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is 0 or > MaxLength || !normalized.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
        {
            throw new WmsDomainException(WmsErrorCodes.UomCodeInvalid, "The unit code must have 1 to 8 characters among A-Z and 0-9.");
        }

        return new UomCode(normalized);
    }

    public override string ToString() => Value;
}
