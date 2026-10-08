namespace Modules.Tenancy.Domain;

/// <summary>Spec 2026-10-08 §3 (O3): activo / inactivo. <c>read_only</c> vendrá con el cobro: un valor
/// más acá y un CHECK más ancho. Sin 0 a propósito, para que el centinela de EF nunca sea un estado.</summary>
public enum TenantModuleStatus
{
    Active = 1,
    Inactive = 2,
}

/// <summary>Motivo cerrado de cada cambio (O4). Qué motivo vale en qué dirección es D2.</summary>
public enum ChangeReason
{
    Contract = 1,
    Courtesy = 2,
    Nonpayment = 3,
    Cancellation = 4,
    Correction = 5,
}

/// <summary>Un solo historial para módulos y estado del tenant (D12).</summary>
public enum TenantChangeKind
{
    Module = 1,
    TenantStatus = 2,
}

/// <summary>
/// El texto de base y del JSON de cada enum, en minúscula como los CHECK, y los motivos que valen en
/// cada dirección (D2). La base y la API son los únicos lugares donde estos valores llegan como texto.
/// </summary>
public static class TenantChangeVocabulary
{
    public static readonly IReadOnlySet<ChangeReason> ModuleActivationReasons =
        new HashSet<ChangeReason> { ChangeReason.Contract, ChangeReason.Courtesy, ChangeReason.Correction };

    public static readonly IReadOnlySet<ChangeReason> ModuleDeactivationReasons =
        new HashSet<ChangeReason> { ChangeReason.Nonpayment, ChangeReason.Cancellation, ChangeReason.Correction };

    /// <summary>§4: inactivar un tenant es por falta de pago o porque ya no continúa; no hay "corrección".</summary>
    public static readonly IReadOnlySet<ChangeReason> SuspensionReasons =
        new HashSet<ChangeReason> { ChangeReason.Nonpayment, ChangeReason.Cancellation };

    public static readonly IReadOnlySet<ChangeReason> ReactivationReasons = ModuleActivationReasons;

    public static string ToText(TenantModuleStatus status) => status switch
    {
        TenantModuleStatus.Active => "active",
        TenantModuleStatus.Inactive => "inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string ToText(ChangeReason reason) => reason switch
    {
        ChangeReason.Contract => "contract",
        ChangeReason.Courtesy => "courtesy",
        ChangeReason.Nonpayment => "nonpayment",
        ChangeReason.Cancellation => "cancellation",
        ChangeReason.Correction => "correction",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static string ToText(TenantChangeKind kind) => kind switch
    {
        TenantChangeKind.Module => "module",
        TenantChangeKind.TenantStatus => "tenant_status",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static bool TryParseModuleStatus(string? text, out TenantModuleStatus status) =>
        TryParse(text, Enum.GetValues<TenantModuleStatus>(), ToText, out status);

    public static bool TryParseReason(string? text, out ChangeReason reason) =>
        TryParse(text, Enum.GetValues<ChangeReason>(), ToText, out reason);

    public static TenantModuleStatus ParseModuleStatus(string text) =>
        TryParseModuleStatus(text, out var status) ? status : throw Unknown(text, nameof(TenantModuleStatus));

    public static ChangeReason ParseReason(string text) =>
        TryParseReason(text, out var reason) ? reason : throw Unknown(text, nameof(ChangeReason));

    public static TenantChangeKind ParseKind(string text) =>
        TryParse(text, Enum.GetValues<TenantChangeKind>(), ToText, out var kind) ? kind : throw Unknown(text, nameof(TenantChangeKind));

    private static bool TryParse<T>(string? text, T[] values, Func<T, string> toText, out T value)
        where T : struct, Enum
    {
        foreach (var candidate in values)
        {
            if (string.Equals(toText(candidate), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static ArgumentException Unknown(string text, string type) =>
        new($"'{text}' is not a known {type}.", nameof(text));
}
