namespace Modules.Automations.Application.Engine;

/// <summary>Lo que <see cref="RunProcessor.ProcessAsync"/> escribe en <c>automation_inbox.outcome</c> (spec 2026-10-21 §5.2).
/// <see cref="Retry"/> no se escribe: la fila se reprograma.</summary>
public static class RunOutcomes
{
    /// <summary>Arrancó una corrida que quedó esperando al cliente.</summary>
    public const string Started = "started";

    /// <summary>Una corrida viva avanzó y sigue esperando.</summary>
    public const string Advanced = "advanced";

    /// <summary>El entrante no le tocaba al bot (D-M3) o no disparó (D-C6).</summary>
    public const string Skipped = "skipped";

    /// <summary>Una persona tomó la conversación antes o durante la unidad (D-M15).</summary>
    public const string Interrupted = "interrupted";

    /// <summary>La corrida terminó: completada, traspasada, resuelta o con la ventana cerrada.</summary>
    public const string Ended = "ended";

    /// <summary>La corrida terminó <c>Failed</c> y la conversación pasó a un asesor (D-M14).</summary>
    public const string Failed = "failed";

    public const string Retry = "retry";
}

/// <summary>Los nombres de <c>ConversationStatus</c> y <c>ConversationAutomationStatus</c> de Mensajería que viajan en
/// <see cref="AutomationConversationState"/> (Automations no referencia Messaging).</summary>
internal static class ConversationStates
{
    public const string Open = "Open";
    public const string Resolved = "Resolved";
}

internal static class AutomationStates
{
    public const string None = "None";
    public const string Active = "Active";
    public const string Paused = "Paused";
}

/// <summary>Los <c>failure_code</c> que pone el motor; los de un envío rechazado son los de Mensajería (P2-R21).</summary>
public static class AutomationsRunFailures
{
    /// <summary>P2-R6: la versión de la corrida viva ya no existe.</summary>
    public const string VersionMissing = "automations.run.version_missing";

    /// <summary>P2-R13/R14/R17: <c>automations.run.loop_detected</c> o <c>automations.run.target_missing</c>.</summary>
    public static string Of(string endReason) => "automations.run." + endReason;
}
