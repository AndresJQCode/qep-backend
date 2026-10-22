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

    /// <summary>Ruling P2-R24: la unidad de este entrante ya se había confirmado; no se hizo nada.</summary>
    public const string Duplicate = "duplicate";

    public const string Retry = "retry";
}
