namespace Modules.Automations.Application.Engine;

/// <summary>Los <c>failure_code</c> que pone el motor; los de un envío rechazado son los de Mensajería (P2-R21).</summary>
public static class AutomationsRunFailures
{
    /// <summary>P2-R6: la versión de la corrida viva ya no existe.</summary>
    public const string VersionMissing = "automations.run.version_missing";

    /// <summary>P2-R13/R14/R17: <c>automations.run.loop_detected</c> o <c>automations.run.target_missing</c>.</summary>
    public static string Of(string endReason) => "automations.run." + endReason;
}
