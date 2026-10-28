namespace Modules.Automations.Application.Engine;

/// <summary>Lo que <see cref="RunProcessor.ProcessAsync"/> escribe en <c>automation_inbox.outcome</c> (spec 2026-10-21 §5.2).
/// <see cref="Retry"/> y <see cref="Locked"/> no se escriben: la fila se reprograma.</summary>
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

    /// <summary>La corrida terminó <c>Failed</c> y la conversación pasó a un asesor (D-M14). Ruling P2-R46: no es el <c>failed</c> con que
    /// Mensajería cierra una fila que agotó sus intentos (<c>AutomationInbox.MaxAttempts</c>); con el mismo nombre no se distinguiría un
    /// flujo que falló de una unidad que nunca terminó.</summary>
    public const string RunFailed = "run_failed";

    /// <summary>Ruling P2-R24: la unidad de este entrante ya se había confirmado; no se hizo nada.</summary>
    public const string Duplicate = "duplicate";

    public const string Retry = "retry";

    /// <summary>Ruling P2-R45: otra unidad tenía el lock de la conversación; la fila se reprograma a 5 s.</summary>
    public const string Locked = "locked";
}
