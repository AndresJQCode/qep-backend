namespace Modules.Automations.Infrastructure.Options;

/// <summary>Spec 2026-10-21 §5.2: los workers del motor, con defaults en código. Los barridos (abandono D-M13, purga del inbox P2-R7 y
/// de <c>http_calls</c> D-H6, simulaciones) los usan las tareas 7, 12 y 13.</summary>
public sealed class AutomationsWorkerOptions
{
    public const string SectionName = "Automations:Workers";

    /// <summary>El tick del inbox: 1 s, para que el bot conteste como una persona atenta.</summary>
    public int InboxPollSeconds { get; set; } = 1;

    /// <summary>Filas que reclama cada pasada; cada una se procesa en su propio scope.</summary>
    public int InboxBatchSize { get; set; } = 20;

    /// <summary>D-M13: cada cuánto se barren las corridas abandonadas y se purga el inbox (P2-R7).</summary>
    public int AbandonSweepMinutes { get; set; } = 10;

    /// <summary>Ruling P2-R7: los días que una fila ya procesada de <c>messaging.automation_inbox</c> se conserva (para diagnosticar un
    /// entrante que el bot no atendió) antes de que el barrido la borre.</summary>
    public int InboxRetentionDays { get; set; } = 7;

    /// <summary>D-H6: los días que una fila de <c>automations.http_calls</c> se conserva antes de que el barrido la borre.</summary>
    public int HttpCallRetentionDays { get; set; } = 30;

    public int SimulationSweepMinutes { get; set; } = 10;
}
