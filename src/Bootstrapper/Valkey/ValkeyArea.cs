namespace Bootstrapper.Valkey;

/// <summary>Las áreas del spec 2026-11-13; cada una tiene su interruptor en <see cref="ValkeyAreaOptions"/>.</summary>
internal enum ValkeyArea
{
    Presence,
    RateLimiting,
    Sessions,
    Permissions,
    SendBudget,
    Doorbells,
    ApiKeyTouch,
    LegalParameters,
}

internal static class ValkeyAreas
{
    /// <summary>La etiqueta <c>area</c> de <c>qep.valkey.fallbacks</c>: estable, porque la leen el dashboard y las alertas.</summary>
    public static string TagOf(ValkeyArea area) => area switch
    {
        ValkeyArea.Presence => "presence",
        ValkeyArea.RateLimiting => "rate_limiting",
        ValkeyArea.Sessions => "sessions",
        ValkeyArea.Permissions => "permissions",
        ValkeyArea.SendBudget => "send_budget",
        ValkeyArea.Doorbells => "doorbells",
        ValkeyArea.ApiKeyTouch => "api_key_touch",
        ValkeyArea.LegalParameters => "legal_parameters",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    public static bool IsOn(ValkeyAreaOptions areas, ValkeyArea area) => area switch
    {
        ValkeyArea.Presence => areas.Presence,
        ValkeyArea.RateLimiting => areas.RateLimiting,
        ValkeyArea.Sessions => areas.Sessions,
        ValkeyArea.Permissions => areas.Permissions,
        ValkeyArea.SendBudget => areas.SendBudget,
        ValkeyArea.Doorbells => areas.Doorbells,
        ValkeyArea.ApiKeyTouch => areas.ApiKeyTouch,
        ValkeyArea.LegalParameters => areas.LegalParameters,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };
}
