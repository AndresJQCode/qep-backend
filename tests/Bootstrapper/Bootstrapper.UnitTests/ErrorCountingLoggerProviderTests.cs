#pragma warning disable CA1848 // Las pruebas llaman a LogError y compañía a propósito: es lo que hace el código de producción.

using System.Diagnostics.Metrics;
using BuildingBlocks.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El contador de logs <c>Error</c>/<c>Critical</c> que alimenta Grafana: cuenta por nivel, categoría y tipo de excepción, jamás por el
/// mensaje, y su serie de alerta nace en cero (<c>increase()</c> toma la primera muestra como base).
/// </summary>
public sealed class ErrorCountingLoggerProviderTests
{
    private const string ErrorsName = "qep.log.errors";
    private const string EntriesName = "qep.log.error_entries";

    [Fact]
    public void AnErrorWithAnExceptionCountsByLevelCategoryAndType()
    {
        using var harness = new Harness();

        harness.Logger.LogError(new InvalidOperationException("secreto"), "mensaje secreto");

        Assert.Equal(1, harness.Sum(ErrorsName, ("level", "Error"), ("category", "Tests.Cat"), ("exception_type", "System.InvalidOperationException")));
        Assert.Equal(1, harness.Entries("Error", "true"));
    }

    [Fact]
    public void ACriticalWithoutAnExceptionCountsAsNone()
    {
        using var harness = new Harness();

        harness.Logger.LogCritical("x");

        Assert.Equal(1, harness.Sum(ErrorsName, ("level", "Critical"), ("category", "Tests.Cat"), ("exception_type", "none")));
        Assert.Equal(1, harness.Entries("Critical", "false"));
    }

    [Fact]
    public void AWarningAndBelowCountNothing()
    {
        using var harness = new Harness();

        harness.Logger.LogWarning(new InvalidOperationException("w"), "w");
        harness.Logger.LogInformation("i");
        harness.Logger.LogDebug("d");
        harness.Logger.LogTrace("t");

        Assert.False(harness.Logger.IsEnabled(LogLevel.Warning));
        Assert.True(harness.Logger.IsEnabled(LogLevel.Error));
        Assert.DoesNotContain(harness.Measurements, m => m.Name == ErrorsName);
        Assert.Equal(
            0,
            harness.Entries("Error", "true") + harness.Entries("Error", "false") + harness.Entries("Critical", "true") + harness.Entries("Critical", "false"));
    }

    // Un choque de índice único lo traduce el UnitOfWork a un 409 de dominio, pero EF ya lo registró en Error con su excepción:
    // contarlo dispararía la alerta de excepciones no controladas con un camino normal (crear un producto con código repetido).
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Update")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    public void EntityFrameworkErrorsAreNotCounted(string category)
    {
        using var harness = new Harness();
        var logger = harness.Provider.CreateLogger(category);

        logger.LogError(new InvalidOperationException("duplicate key"), "SaveChangesFailed");

        Assert.False(logger.IsEnabled(LogLevel.Error));
        Assert.DoesNotContain(harness.Measurements, m => m.Name == ErrorsName);
        Assert.Equal(0, harness.Entries("Error", "true"));
    }

    [Fact]
    public void NoTagCarriesTheMessage()
    {
        using var harness = new Harness();

        harness.Logger.LogError(new InvalidOperationException("secreto"), "mensaje secreto {Dato}", "secreto");
        harness.Observe();

        Assert.NotEmpty(harness.Measurements);
        Assert.All(harness.Measurements, m => Assert.DoesNotContain(m.Tags.Values, v => v.Contains("secreto", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TheEntriesSeriesStartAtZero()
    {
        using var harness = new Harness();

        harness.Observe();

        var series = harness.Measurements.Where(m => m.Name == EntriesName).ToList();
        Assert.Equal(4, series.Count);
        Assert.All(series, m => Assert.Equal(0, m.Value));
        Assert.Equal(
            new[] { ("Critical", "false"), ("Critical", "true"), ("Error", "false"), ("Error", "true") },
            series.Select(m => (m.Tags["level"], m.Tags["has_exception"])).Order());
    }

    [Fact]
    public void AnErrorWithoutStateOrExceptionDoesNotThrow()
    {
        using var harness = new Harness();

        harness.Logger.Log(LogLevel.Error, new EventId(1), (object?)null, null, (_, _) => string.Empty);

        Assert.Equal(1, harness.Sum(ErrorsName, ("exception_type", "none")));
    }

    [Fact]
    public void AnOpenGenericExceptionTypeKeepsANonEmptyTag()
    {
        using var harness = new Harness();

        harness.Logger.LogError(new Holder<int>.Boom<string>(), "x");

        var tag = Assert.Single(harness.Measurements, m => m.Name == ErrorsName).Tags["exception_type"];
        Assert.False(string.IsNullOrWhiteSpace(tag));
        Assert.NotEqual("none", tag);
    }

    [Fact]
    public void TheProviderDoesNotImplementExternalScopes() =>
        Assert.False(typeof(ISupportExternalScope).IsAssignableFrom(typeof(ErrorCountingLoggerProvider)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegistrationOrderAgainstTheRedactionDoesNotChangeTheCount(bool redactionFirst)
    {
        var category = $"Tests.Order.{Guid.NewGuid():N}";
        var total = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, QepObservability.Meter) && instrument.Name == ErrorsName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "category" && Equals(tag.Value, category))
                {
                    Interlocked.Add(ref total, value);
                }
            }
        });
        listener.Start();

        var services = new ServiceCollection();
        if (redactionFirst)
        {
            services.AddSensitivePathRedaction();
        }

        services.AddLogging(logging => logging.AddQepLogging());
        if (!redactionFirst)
        {
            services.AddSensitivePathRedaction();
        }

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILoggerFactory>().CreateLogger(category).LogError(new InvalidOperationException("x"), "x");

        Assert.Equal(1, Interlocked.Read(ref total));
    }

    private static class Holder<T>
    {
        internal sealed class Boom<TInner> : Exception;
    }

    private sealed record Measured(string Name, long Value, Dictionary<string, string> Tags);

    private sealed class Harness : IDisposable
    {
        private readonly Meter _meter = new("test-" + Guid.NewGuid());
        private readonly MeterListener _listener = new();

        public Harness()
        {
            Provider = new ErrorCountingLoggerProvider(_meter);
            Logger = Provider.CreateLogger("Tests.Cat");
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, _meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var map = new Dictionary<string, string>();
                foreach (var tag in tags)
                {
                    map[tag.Key] = tag.Value?.ToString() ?? string.Empty;
                }

                Measurements.Add(new Measured(instrument.Name, value, map));
            });
            _listener.Start();
        }

        public ErrorCountingLoggerProvider Provider { get; }

        public ILogger Logger { get; }

        public List<Measured> Measurements { get; } = [];

        /// <summary>Dispara el callback de los <c>ObservableCounter</c>: lo que un exportador leería en este instante.</summary>
        public void Observe() => _listener.RecordObservableInstruments();

        public long Sum(string name, params (string Key, string Value)[] tags) =>
            Measurements.Where(m => m.Name == name && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && v == t.Value)).Sum(m => m.Value);

        /// <summary>El valor acumulado de una serie de <c>error_entries</c> tal como lo vería un exportador.</summary>
        public long Entries(string level, string hasException)
        {
            Measurements.RemoveAll(m => m.Name == EntriesName);
            Observe();
            return Measurements.Where(m => m.Name == EntriesName && m.Tags["level"] == level && m.Tags["has_exception"] == hasException).Sum(m => m.Value);
        }

        public void Dispose()
        {
            _listener.Dispose();
            Provider.Dispose();
            _meter.Dispose();
        }
    }
}
