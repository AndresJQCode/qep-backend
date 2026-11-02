using System.ComponentModel.DataAnnotations;

namespace Modules.Wms.Application;

/// <summary>La sección <c>Wms:Workers</c> de appsettings. Se valida al arrancar (<c>ValidateOnStart</c>): un cero o un negativo
/// pararía el temporizador o lo dejaría girando.</summary>
public sealed class WmsWorkerOptions
{
    public const string SectionName = "Wms:Workers";

    /// <summary>Espera antes de la primera pasada de los workers del libro (publicador, conciliación, particiones). Misma razón que
    /// <see cref="HousekeepingStartupSeconds"/>: con un intervalo largo y un despliegue por commit, el pod se reiniciaría antes de que el
    /// temporizador cumpliera.</summary>
    [Range(0, 3600)]
    public int WorkerStartupSeconds { get; init; } = 30;

    [Range(1, 3600)]
    public int BalanceChangePublishSeconds { get; init; } = 10;

    [Range(0, 23)]
    public int ReconciliationHourUtc { get; init; } = 3;

    [Range(1, 1440)]
    public int ReconciliationSweepMinutes { get; init; } = 30;

    [Range(1, 168)]
    public int PartitionSweepHours { get; init; } = 24;

    [Range(1, 168)]
    public int HousekeepingHours { get; init; } = 24;

    /// <summary>Espera antes de la primera limpieza. Sin ella, con un intervalo de 24 h y un despliegue por día, el pod se reiniciaría
    /// antes de que el temporizador cumpliera y la limpieza no correría nunca.</summary>
    [Range(0, 3600)]
    public int HousekeepingStartupSeconds { get; init; } = 60;

    /// <summary>S13: una sesión offline dura como máximo 30 días; los 15 de margen cubren el reintento tardío. Un evento más viejo ya
    /// lo rechazó la ventana de <c>occurredAt</c> (S12).</summary>
    [Range(30, 3650)]
    public int ReceiptRetentionDays { get; init; } = 45;

    [Range(1, 1440)]
    public int PreviewRetentionMinutes { get; init; } = 30;
}
