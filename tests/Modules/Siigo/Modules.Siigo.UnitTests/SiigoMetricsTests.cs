using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Api;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// <c>qep_siigo_call_failures_total{operation,reason}</c>: las series de alerta nacen en cero (<c>increase()</c> toma la primera muestra
/// como base) y nada lleva tenant, conexión ni ruta.
/// </summary>
public sealed class SiigoMetricsTests : IDisposable
{
    private static readonly string[] FixedReasons =
        ["timeout", "network", "credentials_rejected", "invalid_response", "partner_id_missing", "auth_without_token"];

    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public void TheSeriesStartAtZeroForEveryOperationAndFixedReason()
    {
        var metrics = new SiigoMetrics(_services.GetRequiredService<IMeterFactory>());

        var series = Observe(_services);

        GC.KeepAlive(metrics);
        Assert.Equal(Enum.GetValues<SiigoCallOperation>().Length * FixedReasons.Length, series.Count);
        Assert.All(series.Values, value => Assert.Equal(0, value));
        foreach (var operation in Enum.GetValues<SiigoCallOperation>())
        {
            foreach (var reason in FixedReasons)
            {
                Assert.True(series.ContainsKey((operation.ToString(), reason)), $"{operation}/{reason}");
            }
        }
    }

    [Fact]
    public void AFailureAddsOneToItsSeries()
    {
        var metrics = new SiigoMetrics(_services.GetRequiredService<IMeterFactory>());

        metrics.RecordFailure(SiigoCallOperation.InvoiceCreate, "http_400");

        var series = Observe(_services);
        Assert.Equal(1, series[("InvoiceCreate", "http_400")]);
        Assert.Equal(0, series.Where(s => s.Key != ("InvoiceCreate", "http_400")).Sum(s => s.Value));
    }

    [Theory]
    [InlineData("http_")]
    [InlineData(" ")]
    [InlineData("")]
    public void ABlankReasonCountsAsUnknown(string reason)
    {
        var metrics = new SiigoMetrics(_services.GetRequiredService<IMeterFactory>());

        metrics.RecordFailure(SiigoCallOperation.Auth, reason);

        var series = Observe(_services);
        Assert.Equal(1, series[("Auth", "unknown")]);
        Assert.DoesNotContain(series.Keys, key => key.Reason is "http_" or "" or " ");
    }

    [Fact]
    public void ConcurrentFailuresAddUpExactly()
    {
        var metrics = new SiigoMetrics(_services.GetRequiredService<IMeterFactory>());

        Parallel.For(0, 1000, _ => metrics.RecordFailure(SiigoCallOperation.InvoiceCreate, "timeout"));

        Assert.Equal(1000, Observe(_services)[("InvoiceCreate", "timeout")]);
    }

    /// <summary>Lo que un exportador leería ahora: el valor de cada serie <c>(operation, reason)</c> del meter de Siigo.</summary>
    internal static Dictionary<(string Operation, string Reason), long> Observe(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IMeterFactory>();
        var series = new Dictionary<(string, string), long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "qep.siigo.call_failures")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? operation = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation")
                {
                    operation = tag.Value as string;
                }
                else if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            series[(operation!, reason!)] = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return series;
    }
}

/// <summary>Quién suma qué: <c>SiigoApiClient.RecordAsync</c> es el único punto por el que pasa toda falla.</summary>
public sealed class SiigoApiClientMetricsTests : IDisposable
{
    private static readonly SiigoCredentials Credentials = new(Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"), "api@empresa.test", "siigo-access-key-SENTINEL-77");

    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly SiigoHttpStub _api = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SiigoMetrics _metrics;

    public SiigoApiClientMetricsTests() => _metrics = new SiigoMetrics(_services.GetRequiredService<IMeterFactory>());

    public void Dispose()
    {
        _api.Dispose();
        _cache.Dispose();
        _services.Dispose();
    }

    [Fact]
    public async Task ADoubleUnauthorizedOnAnInvoiceCountsTheCallersOperation()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.Unauthorized, """{"Errors":[{"Code":"unauthorized","Message":"no"}]}""");

        await NewClient().CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        AssertOnly(("InvoiceCreate", "credentials_rejected"));
    }

    [Fact]
    public async Task ARejectedAuthCountsAsAuth()
    {
        _api.On("/auth", HttpStatusCode.Unauthorized, """{"Errors":[{"Code":"unauthorized","Message":"bad key"}]}""");

        await NewClient().CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        AssertOnly(("Auth", "credentials_rejected"));
    }

    [Fact]
    public async Task ATimeoutCountsItsReason()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.Throw("/v1/taxes", new TaskCanceledException("timed out", new TimeoutException()));

        await Assert.ThrowsAnyAsync<Exception>(() => NewClient().ListTaxesAsync(Credentials, TestContext.Current.CancellationToken));

        AssertOnly(("ListRead", "timeout"));
    }

    [Fact]
    public async Task AnInvoiceBadRequestCountsHttp400()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_total_payments","Message":"Cliente Fulano"}]}""");

        await NewClient().CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        AssertOnly(("InvoiceCreate", "http_400"));
    }

    [Fact]
    public async Task AMissingPartnerIdCountsEvenWithoutAFailureLog()
    {
        var client = NewClient(partnerId: "", failures: null);

        await Assert.ThrowsAnyAsync<Exception>(() => client.ListTaxesAsync(Credentials, TestContext.Current.CancellationToken));

        AssertOnly(("ListRead", "partner_id_missing"));
    }

    [Fact]
    public async Task ASuccessfulCallCountsNothing()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/taxes", HttpStatusCode.OK, Fixture.Read("taxes.json"));

        await NewClient().ListTaxesAsync(Credentials, TestContext.Current.CancellationToken);

        AssertOnly();
    }

    private SiigoApiClient NewClient(string partnerId = "qepTest", ISiigoCallFailureLog? failures = null) =>
        new(
            new HttpClient(_api, disposeHandler: false),
            Options.Create(new SiigoOptions { BaseUrl = "https://siigo.test", PartnerId = partnerId }),
            new SiigoTokenCache(TimeProvider.System),
            _cache,
            failures,
            _metrics);

    /// <summary>Solo esas series valen 1; las demás siguen en cero.</summary>
    private void AssertOnly(params (string Operation, string Reason)[] expected)
    {
        var nonZero = SiigoMetricsTests.Observe(_services).Where(s => s.Value != 0).ToDictionary(s => s.Key, s => s.Value);

        Assert.Equal(expected.Order(), nonZero.Keys.Order());
        Assert.All(nonZero.Values, value => Assert.Equal(1, value));
    }
}
