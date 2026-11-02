using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>Lee un gauge observable del meter de ESTE host: varios hosts de prueba corren en el mismo proceso y cada uno crea su propio
/// <c>qep.wms.*</c>, así que se filtra por el <c>IMeterFactory</c> que lo creó.</summary>
internal static class WmsGaugeReader
{
    public static long Read(QepApiFactory factory, string instrumentName) =>
        Read(factory.Services.GetRequiredService<IMeterFactory>(), instrumentName);

    public static long Read(IMeterFactory meterFactory, string instrumentName)
    {
        long value = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == instrumentName && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => value = measurement);
        listener.Start();
        listener.RecordObservableInstruments();
        return value;
    }
}
