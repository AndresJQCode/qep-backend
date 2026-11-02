using System.Diagnostics.Metrics;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Infrastructure;

/// <summary>Métricas de Bodegas (RNF-11). El meter lleva el prefijo <c>Qep.</c>, que el exportador ya recoge: el módulo no referencia
/// la infraestructura de observabilidad. Sin etiqueta de tenant.</summary>
internal sealed class WmsMetrics
{
    public const string MeterName = "Qep.Wms";

    private readonly Counter<long> _movements;
    private readonly Histogram<double> _confirmDuration;
    private long _balanceDrift;
    private long _partitionMissing;

    public WmsMetrics(IMeterFactory meterFactory)
    {
        Meter = meterFactory.Create(MeterName);
        _movements = Meter.CreateCounter<long>("qep.wms.movements", description: "Stock movements written to the ledger.");
        _confirmDuration = Meter.CreateHistogram(
            "qep.wms.confirm.duration",
            unit: "ms",
            description: "Milliseconds to confirm a document against the ledger.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [10, 50, 150, 500, 1000, 2000, 5000] });
        Meter.CreateObservableGauge(
            "qep.wms.balance.drift",
            () => Volatile.Read(ref _balanceDrift),
            description: "Balance keys that differed from the ledger in the last reconciliation.");
        Meter.CreateObservableGauge(
            "qep.wms.partition.missing",
            () => Volatile.Read(ref _partitionMissing),
            description: "1 when the next stock movement partition could not be created.");
    }

    public Meter Meter { get; }

    public void RecordMovements(MovementReason reason, int count) =>
        _movements.Add(count, new KeyValuePair<string, object?>("reason", reason.ToString()));

    public void RecordConfirmDuration(double ms) => _confirmDuration.Record(ms);

    public void SetBalanceDrift(long keys) => Volatile.Write(ref _balanceDrift, keys);

    public void SetPartitionMissing(bool missing) => Volatile.Write(ref _partitionMissing, missing ? 1 : 0);
}
