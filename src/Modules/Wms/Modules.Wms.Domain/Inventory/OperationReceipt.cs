namespace Modules.Wms.Domain.Inventory;

/// <summary>El recibo de una operación idempotente: la clave, el hash del cuerpo y la respuesta guardada, para repetirla igual.</summary>
public sealed class OperationReceipt
{
    public const int KeyMaxLength = 128;

    private OperationReceipt()
    {
    }

    public Guid TenantId { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string RequestHash { get; private set; } = string.Empty;

    public string ResponseJson { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static OperationReceipt Create(Guid tenantId, string key, string hash, string responseJson, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > KeyMaxLength)
        {
            throw new ArgumentException($"The idempotency key must have between 1 and {KeyMaxLength} characters.", nameof(key));
        }

        if (hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The request hash must be 64 hexadecimal characters.", nameof(hash));
        }

        return new OperationReceipt
        {
            TenantId = tenantId,
            IdempotencyKey = key,
            RequestHash = hash,
            ResponseJson = responseJson,
            CreatedAt = now,
        };
    }
}
