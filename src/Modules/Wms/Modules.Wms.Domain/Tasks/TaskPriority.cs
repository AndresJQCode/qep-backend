namespace Modules.Wms.Domain.Tasks;

/// <summary>Spec 2026-11-26 §6.6: la prioridad efectiva de una tarea. Se calcula al reclamar y no se guarda. La selección de la
/// siguiente tarea en la base tiene que ordenar con estos mismos pesos.</summary>
public static class TaskPriority
{
    public const int UrgentBonus = 10;
    public const int AgingStepMinutes = 20;
    public const int AgingCap = 3;

    public static int Score(int priority, bool urgent, DateTimeOffset? releasedAt, DateTimeOffset? dueAt, DateTimeOffset now) =>
        priority + (urgent ? UrgentBonus : 0) + Aging(releasedAt, now) + DueTier(dueAt, now);

    /// <summary>0 sin fecha o a más de 2 h; 1 a 2 h o menos; 2 a 1 h o menos; 3 a 30 min o menos; 4 vencida.</summary>
    public static int DueTier(DateTimeOffset? dueAt, DateTimeOffset now) =>
        dueAt is not { } due ? 0
        : due <= now ? 4
        : due <= now.AddMinutes(30) ? 3
        : due <= now.AddHours(1) ? 2
        : due <= now.AddHours(2) ? 1
        : 0;

    /// <summary>Un punto por cada tramo completo en el pool, con tope: lo viejo sube, pero no le gana a un corte. Nunca resta, aunque
    /// los relojes de dos réplicas no coincidan.</summary>
    private static int Aging(DateTimeOffset? releasedAt, DateTimeOffset now) =>
        releasedAt is not { } released
            ? 0
            : (int)Math.Clamp((now - released).Ticks / TimeSpan.FromMinutes(AgingStepMinutes).Ticks, 0, AgingCap);
}
