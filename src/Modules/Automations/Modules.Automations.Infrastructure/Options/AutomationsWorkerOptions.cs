namespace Modules.Automations.Infrastructure.Options;

/// <summary>Spec 2026-10-21 §5.2: los workers del motor, con defaults en código. Los barridos (abandono D-M13, <c>http_calls</c>,
/// simulaciones) los usan las tareas 7, 12 y 13.</summary>
public sealed class AutomationsWorkerOptions
{
    public const string SectionName = "Automations:Workers";

    /// <summary>El tick del inbox: 1 s, para que el bot conteste como una persona atenta.</summary>
    public int InboxPollSeconds { get; set; } = 1;

    /// <summary>Filas que reclama cada pasada; cada una se procesa en su propio scope.</summary>
    public int InboxBatchSize { get; set; } = 20;

    public int AbandonSweepMinutes { get; set; } = 10;

    public int HttpCallRetentionDays { get; set; } = 30;

    public int SimulationSweepMinutes { get; set; } = 10;
}
