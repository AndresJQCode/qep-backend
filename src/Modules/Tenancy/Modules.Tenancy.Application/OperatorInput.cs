using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §5: claves, estados y motivos llegan como texto y se validan <b>antes</b> de convertir.
/// Convertir primero haría que <c>TenantModuleKey.Parse</c> (lanza <c>ArgumentException</c>) saliera como
/// 500. Sin ignorar mayúsculas, igual que los CHECK.
/// </summary>
internal static class OperatorInput
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;

    public static bool IsModuleKey(string? value) =>
        value is not null && TenantModuleKeys.All.Any(key => string.Equals(key.Value, value, StringComparison.Ordinal));

    /// <summary><c>active</c> o <c>inactive</c>: la dirección de un cambio de módulo o de tenant.</summary>
    public static bool IsDirection(string? value) => TenantChangeVocabulary.TryParseModuleStatus(value, out _);

    public static bool IsReason(string? value) => TenantChangeVocabulary.TryParseReason(value, out _);
}
