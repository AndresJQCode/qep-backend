namespace Modules.Wms.Domain.Warehousing;

/// <summary>
/// Códigos de las ubicaciones que nacen con la bodega y la regla de formato de P-4. El código se imprime en una etiqueta
/// Code 128 y la app lo busca por igualdad exacta, así que se normaliza una sola vez, aquí.
/// </summary>
public static class LocationCodes
{
    public const string Supplier = "SUPPLIER";
    public const string Customer = "CUSTOMER";
    public const string Adjustment = "ADJUSTMENT";
    public const string Transit = "TRANSIT";
    public const string ReceivingZone = "RECEIVING";
    public const string ReceivingDock = "REC-01";

    /// <summary>La ubicación donde cae el stock del inventario básico (bodega principal).</summary>
    public const string General = "GENERAL";

    public const int MaxLength = 32;

    /// <summary><c>Trim</c> + mayúsculas, y solo <c>[A-Z0-9._-]{1,32}</c>. <paramref name="invalidCode"/> es el código de error del
    /// llamador (bodega o ubicación): la regla es la misma, el 422 cambia.</summary>
    public static string Normalize(string code, string invalidCode)
    {
        ArgumentNullException.ThrowIfNull(invalidCode);

        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is 0 or > MaxLength || !normalized.All(IsAllowed))
        {
            throw new WmsDomainException(invalidCode, "The code must have 1 to 32 characters among A-Z, 0-9, '.', '_' and '-'.");
        }

        return normalized;
    }

    /// <summary>Los roles que solo existen como ubicación virtual: la API no los deja crear.</summary>
    public static bool IsVirtualRole(LocationRole role) =>
        role is LocationRole.Supplier or LocationRole.Customer or LocationRole.Adjustment or LocationRole.Transit;

    private static bool IsAllowed(char c) =>
        c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
}
