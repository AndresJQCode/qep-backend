using System.Diagnostics.Metrics;
using FluentValidation;

namespace Api;

/// <summary>
/// Métricas de lo que mide el navegador. El meter lleva el prefijo <c>Qep.</c>, que el exportador ya recoge. Sin etiqueta
/// de tenant ni de persona: la única etiqueta con valores libres es la ruta, y está acotada.
/// </summary>
public sealed class WebTelemetryMetrics
{
    public const string MeterName = "Qep.Web";

    /// <summary>Tope de rutas distintas como etiqueta. Con rutas mal formadas o un cliente hostil, cada valor nuevo sería una
    /// serie nueva en Prometheus; pasado el tope todo cae en <see cref="OtherRoute"/>.</summary>
    public const int MaxRoutes = 256;

    public const string OtherRoute = "other";

    private readonly Histogram<double> _navigation;
    private readonly Histogram<double> _vital;
    private readonly Histogram<double> _cls;
    private readonly HashSet<string> _routes = new(StringComparer.Ordinal);
    private readonly Lock _routesLock = new();

    public WebTelemetryMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _navigation = meter.CreateHistogram(
            "qep.web.navigation.duration",
            unit: "s",
            description: "Seconds the SPA took to show a route after the user asked for it.",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 5, 10, 15],
            });
        _vital = meter.CreateHistogram(
            "qep.web.vital",
            unit: "s",
            description: "Web Vitals measured in seconds (lcp, inp, ttfb, fcp), by name.",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.05, 0.1, 0.2, 0.3, 0.5, 1, 1.5, 2.5, 4, 6, 10],
            });
        _cls = meter.CreateHistogram(
            "qep.web.cls",
            unit: null,
            description: "Cumulative Layout Shift, a unitless score.",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.01, 0.05, 0.1, 0.25, 0.5, 1],
            });
    }

    public void RecordNavigation(string route, double seconds) =>
        _navigation.Record(seconds, new KeyValuePair<string, object?>("route", BoundRoute(route)));

    public void RecordVital(string name, double seconds) =>
        _vital.Record(seconds, new KeyValuePair<string, object?>("name", name));

    public void RecordCls(double score) => _cls.Record(score);

    /// <summary>Una ruta ya vista sigue saliendo por su nombre aunque el tope se haya llenado.</summary>
    private string BoundRoute(string route)
    {
        lock (_routesLock)
        {
            if (_routes.Contains(route))
            {
                return route;
            }

            if (_routes.Count >= MaxRoutes)
            {
                return OtherRoute;
            }

            _routes.Add(route);
            return route;
        }
    }
}

public static class WebTelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddWebTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<WebTelemetryMetrics>();
        services.AddSingleton<IValidator<WebTelemetryRequest>, WebTelemetryRequestValidator>();
        return services;
    }
}
