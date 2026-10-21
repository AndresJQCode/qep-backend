namespace Modules.Tenancy.Domain;

/// <summary>
/// Las claves de proveedor que un tenant puede tener habilitadas. Hoy sólo <c>zenvia</c>: es la misma
/// clave de <c>IntegrationProviders.Zenvia.Key</c>, copiada acá porque Tenancy no referencia Integrations.
/// Ordinal y sin ignorar mayúsculas, como <see cref="TenantModuleKeys"/> y la columna de la base.
/// </summary>
public static class TenantProviderKeys
{
    public const string Zenvia = "zenvia";

    public static readonly IReadOnlyList<string> All = [Zenvia];

    public static bool IsKnown(string? key) => key is not null && All.Contains(key, StringComparer.Ordinal);
}

/// <summary>
/// Un proveedor de un tenant y su estado: la fila es el permiso de usar ese proveedor y
/// <see cref="Status"/> dice si está vigente. Mismo molde que <see cref="TenantModule"/> y mismo
/// vocabulario de estado (<see cref="TenantModuleStatus"/>): desactivar no borra, la fila queda y el
/// historial (<see cref="TenantChange"/>) guarda quién, cuándo y por qué. Un tenant sin fila tiene el
/// proveedor deshabilitado; por eso el registro no escribe nada.
/// </summary>
public sealed class TenantProvider
{
    public const int ProviderKeyMaxLength = 32;

    private TenantProvider()
    {
        ProviderKey = string.Empty;
    }

    private TenantProvider(TenantId tenantId, string providerKey, DateTimeOffset enabledAt)
    {
        TenantId = tenantId;
        ProviderKey = providerKey;
        EnabledAt = enabledAt;
        Status = TenantModuleStatus.Active;
        StatusChangedAt = enabledAt;
    }

    public TenantId TenantId { get; private set; }

    public string ProviderKey { get; private set; }

    public TenantModuleStatus Status { get; private set; }

    /// <summary>Cuándo se creó la fila por primera vez; no se mueve al reactivar.</summary>
    public DateTimeOffset EnabledAt { get; private set; }

    /// <summary>El último cambio de estado.</summary>
    public DateTimeOffset StatusChangedAt { get; private set; }

    public static TenantProvider Create(TenantId tenantId, string providerKey, DateTimeOffset now) =>
        new(tenantId, providerKey, now);

    public void Activate(DateTimeOffset occurredAt) => ChangeTo(TenantModuleStatus.Active, occurredAt);

    public void Deactivate(DateTimeOffset occurredAt) => ChangeTo(TenantModuleStatus.Inactive, occurredAt);

    // Sin cambios vacíos: quien llama ya lo filtra, pero el agregado no confía en él.
    private void ChangeTo(TenantModuleStatus next, DateTimeOffset occurredAt)
    {
        if (Status == next)
        {
            throw new TenantDomainException(
                TenantProviderChange.NoChangesCode,
                $"The '{ProviderKey}' provider is already {TenantChangeVocabulary.ToText(next)}.");
        }

        Status = next;
        StatusChangedAt = occurredAt;
    }
}

/// <summary>
/// Las reglas del cambio de un proveedor. Los motivos que valen en cada dirección son los mismos de los
/// módulos (activar = contrato, cortesía, corrección; desactivar = falta de pago, cancelación,
/// corrección): misma consola y mismo criterio comercial, así que no hay un vocabulario aparte.
/// </summary>
public static class TenantProviderChange
{
    public const string NoChangesCode = "tenancy.providers.no_changes";
    public const string ReasonNotAllowedCode = "tenancy.providers.reason_not_allowed";

    public static void EnsureReasonAllowed(TenantModuleStatus to, ChangeReason reason)
    {
        var allowed = to == TenantModuleStatus.Active
            ? TenantChangeVocabulary.ModuleActivationReasons
            : TenantChangeVocabulary.ModuleDeactivationReasons;
        if (!allowed.Contains(reason))
        {
            throw new TenantDomainException(
                ReasonNotAllowedCode,
                $"'{TenantChangeVocabulary.ToText(reason)}' is not a reason to {(to == TenantModuleStatus.Active ? "enable" : "disable")} a provider.");
        }
    }
}
