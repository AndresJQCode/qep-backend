namespace Modules.Integrations.Domain;

/// <summary>
/// Spec 2026-10-09 §6.1: de qué tenant y conexión es un id externo del proveedor (el
/// <c>phone_number_id</c> de Meta), para que el webhook —que no tiene tenant— enrute. <c>AccountId</c>
/// es la WABA (D-M2): <c>account_update</c> llega por cuenta, no por número. Una fila por conexión, en la
/// misma transacción que ella; se borra en cascada.
/// </summary>
public sealed class IntegrationConnectionRoute
{
    public const int ExternalIdMaxLength = 64;

    private IntegrationConnectionRoute()
    {
        ProviderKey = string.Empty;
        ExternalId = string.Empty;
    }

    public string ProviderKey { get; private set; }

    public string ExternalId { get; private set; }

    public string? AccountId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public static IntegrationConnectionRoute Create(
        string providerKey, string externalId, string? accountId, Guid tenantId, Guid connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        if (externalId.Length > ExternalIdMaxLength || (accountId is not null && accountId.Length > ExternalIdMaxLength))
        {
            throw new ArgumentOutOfRangeException(nameof(externalId), $"A route id is at most {ExternalIdMaxLength} characters.");
        }

        return new IntegrationConnectionRoute
        {
            ProviderKey = providerKey,
            ExternalId = externalId.Trim(),
            AccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim(),
            TenantId = tenantId,
            ConnectionId = connectionId,
        };
    }
}
