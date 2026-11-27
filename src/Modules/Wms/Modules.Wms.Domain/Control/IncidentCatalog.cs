namespace Modules.Wms.Domain.Control;

/// <summary>El catálogo cerrado de novedades (spec 2026-11-26 §8.2 y §8.3, DA-42 a DA-49): qué resoluciones admite cada tipo, cuál
/// se puede rechazar, cuál pide una verificación y qué tipos puede declarar un operario. Un tipo nuevo sin fila aquí rompe
/// <c>IncidentCatalogTests.EveryTypeHasAnEntry</c>.</summary>
public static class IncidentCatalog
{
    private static readonly HashSet<IncidentResolution> AcceptOnly = [IncidentResolution.Accept];

    private static readonly HashSet<IncidentResolution> IntoStockOnly = [IncidentResolution.AcceptIntoStock];

    private static readonly HashSet<IncidentResolution> NoResolution = [];

    private static readonly HashSet<IncidentType> OnAMove = [IncidentType.ShortMove, IncidentType.LocationMismatch];

    private static readonly HashSet<IncidentType> NoType = [];

    /// <summary>Los tipos que se pueden reportar sueltos, sin una línea de tarea (DA-49).</summary>
    public static IReadOnlySet<IncidentType> DeclarableStandalone { get; } =
        new HashSet<IncidentType> { IncidentType.UnreadableLabel, IncidentType.LocationMismatch };

    /// <summary>Las resoluciones que un supervisor puede dar. <c>CountVariance</c> no tiene: la cierra su ajuste.</summary>
    public static IReadOnlySet<IncidentResolution> ResolutionsOf(IncidentType type) => type switch
    {
        IncidentType.ShortMove or IncidentType.LocationMismatch or IncidentType.UnreadableLabel => AcceptOnly,
        IncidentType.SyncConflict => IntoStockOnly,
        IncidentType.CountVariance => NoResolution,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The incident type has no catalog entry."),
    };

    /// <summary>Sólo <c>SyncConflict</c> se rechaza: no puso nada en el ledger, así que descartarlo no deja nada por deshacer.</summary>
    public static bool CanBeRejected(IncidentType type) => type switch
    {
        IncidentType.SyncConflict => true,
        IncidentType.ShortMove or IncidentType.LocationMismatch or IncidentType.UnreadableLabel or IncidentType.CountVariance => false,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The incident type has no catalog entry."),
    };

    /// <summary>Si la novedad pide un conteo ciego de verificación de la ubicación.</summary>
    public static bool RequestsVerification(IncidentType type) => type switch
    {
        IncidentType.ShortMove or IncidentType.LocationMismatch => true,
        IncidentType.UnreadableLabel or IncidentType.CountVariance or IncidentType.SyncConflict => false,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The incident type has no catalog entry."),
    };

    /// <summary>Los tipos que el operario declara al reportar un faltante en una línea. Recibe el tipo de la tarea <b>por nombre</b>:
    /// <c>Control</c> no ve <c>Tasks</c> (<c>DomainAreasOnlyDependDownwards</c>).</summary>
    public static IReadOnlySet<IncidentType> DeclarableOnShort(string taskType)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        return taskType == "Move" ? OnAMove : NoType;
    }
}
