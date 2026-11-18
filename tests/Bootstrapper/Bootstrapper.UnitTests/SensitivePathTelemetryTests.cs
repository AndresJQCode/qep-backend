using System.Collections;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El token de una invitación tampoco sale por la telemetría: ni en el <c>url.path</c> del span que exporta OpenTelemetry ni en
/// el scope <c>RequestPath</c> que ASP.NET Core pone en cada línea de log del request (el JSON de consola incluye scopes).
/// </summary>
public sealed class SensitivePathTelemetryTests
{
    [Theory]
    [InlineData("/api/v1/wholesale-invitations/abc_DEF-1/session", "/api/v1/wholesale-invitations/***/session")]
    [InlineData("/API/V1/WHOLESALE-INVITATIONS/abc_DEF-1/accept", "/API/V1/WHOLESALE-INVITATIONS/***/accept")]
    [InlineData("/Api/v1/Wholesale-Invitations/abc_DEF-1", "/Api/v1/Wholesale-Invitations/***")]
    [InlineData("/api/v1/invitations/abc_DEF-1/accept", "/api/v1/invitations/***/accept")]
    [InlineData("/API/V1/INVITATIONS/abc_DEF-1/accept-with-password", "/API/V1/INVITATIONS/***/accept-with-password")]
    [InlineData("/api/V1/Invitations/abc_DEF-1", "/api/V1/Invitations/***")]
    public void TheServerSpanPathLosesTheToken(string path, string expected)
    {
        using var activity = SpanWith(("url.path", path), ("http.target", path), ("url.full", $"https://api.qep.test{path}?x=1"));

        Enrich(activity);

        Assert.Equal(expected, activity.GetTagItem("url.path"));
        Assert.Equal(expected, activity.GetTagItem("http.target"));
        Assert.Equal($"https://api.qep.test{expected}?x=1", activity.GetTagItem("url.full"));
    }

    [Theory]
    [InlineData("/api/v1/tenants/0193a3c5-0000-7000-8000-000000000001/invitations")]
    [InlineData("/api/v1/wholesale-invitations")]
    [InlineData("/api/v1/auth/session")]
    public void OtherSpanPathsAreUntouched(string path)
    {
        using var activity = SpanWith(("url.path", path), ("url.full", $"https://api.qep.test{path}"));

        Enrich(activity);

        Assert.Equal(path, activity.GetTagItem("url.path"));
        Assert.Equal($"https://api.qep.test{path}", activity.GetTagItem("url.full"));
        Assert.Null(activity.GetTagItem("http.target"));
    }

    [Fact]
    public void AnEnrichmentAlreadyConfiguredStillRuns()
    {
        var services = new ServiceCollection();
        services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
            options.EnrichWithHttpRequest = (activity, _) => activity.SetTag("previous", true));
        services.AddSensitivePathRedaction();
        using var activity = SpanWith(("url.path", "/api/v1/invitations/abc/accept"));

        EnrichWith(services, activity);

        Assert.Equal(true, activity.GetTagItem("previous"));
        Assert.Equal("/api/v1/invitations/***/accept", activity.GetTagItem("url.path"));
    }

    [Theory]
    [InlineData("/API/v1/Wholesale-Invitations/abc_DEF-1/session", "/API/v1/Wholesale-Invitations/***/session")]
    [InlineData("/api/v1/invitations/abc_DEF-1/accept", "/api/v1/invitations/***/accept")]
    public void TheRequestPathLogScopeLosesTheToken(string path, string expected)
    {
        var scopes = new LoggerExternalScopeProvider();
        using var _ = scopes.Push(new RequestScope(path, "0HN-1"));

        var written = Collect(SensitivePathTelemetry.MaskScopes(scopes));

        var scope = Assert.Single(written);
        var items = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(scope);
        Assert.Equal(expected, items.Single(item => item.Key == "RequestPath").Value);
        Assert.Equal("0HN-1", items.Single(item => item.Key == "RequestId").Value);
        Assert.Equal($"RequestPath:{expected} RequestId:0HN-1", scope!.ToString());
    }

    [Fact]
    public void OtherLogScopesAreTheSameObjects()
    {
        var scopes = new LoggerExternalScopeProvider();
        var request = new RequestScope("/api/v1/auth/session", "0HN-1");
        using var a = scopes.Push(request);
        using var b = scopes.Push("plain");

        var written = Collect(SensitivePathTelemetry.MaskScopes(scopes));

        Assert.Equal([request, "plain"], written);
    }

    [Fact]
    public void LoggerProvidersReceiveTheMaskingScopeProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerProvider, CapturingProvider>();
        services.AddSensitivePathRedaction();
        using var provider = services.BuildServiceProvider();
        var scopes = new LoggerExternalScopeProvider();
        using var _ = scopes.Push(new RequestScope("/api/v1/invitations/abc/accept", "0HN-1"));

        var logger = Assert.Single(provider.GetServices<ILoggerProvider>());
        ((ISupportExternalScope)logger).SetScopeProvider(scopes);

        var captured = CapturingProvider.Last!.ScopeProvider!;
        var items = (IReadOnlyList<KeyValuePair<string, object?>>)Collect(captured).Single()!;
        Assert.Equal("/api/v1/invitations/***/accept", items.Single(item => item.Key == "RequestPath").Value);
    }

    [Fact]
    public void TheJsonConsoleProviderIsStillBuilt()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddJsonConsole(options => options.IncludeScopes = true));
        services.AddSensitivePathRedaction();
        using var provider = services.BuildServiceProvider();

        var loggers = provider.GetServices<ILoggerProvider>().ToList();

        Assert.NotEmpty(loggers);
        Assert.DoesNotContain(loggers, logger => logger is ConsoleLoggerProvider);
        Assert.All(loggers, logger => Assert.IsAssignableFrom<ISupportExternalScope>(logger));
    }

    private static Activity SpanWith(params (string Key, string Value)[] tags)
    {
        var activity = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn");
        foreach (var (key, value) in tags)
        {
            activity.SetTag(key, value);
        }

        return activity;
    }

    private static void Enrich(Activity activity)
    {
        var services = new ServiceCollection();
        services.AddSensitivePathRedaction();
        EnrichWith(services, activity);
    }

    private static void EnrichWith(ServiceCollection services, Activity activity)
    {
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().Get(Options.DefaultName);
        Assert.NotNull(options.EnrichWithHttpRequest);
        options.EnrichWithHttpRequest(activity, new DefaultHttpContext().Request);
    }

    private static List<object?> Collect(IExternalScopeProvider scopes)
    {
        var written = new List<object?>();
        scopes.ForEachScope((scope, list) => list.Add(scope), written);
        return written;
    }

    /// <summary>La forma del <c>HostingLogScope</c> de ASP.NET Core, que es internal.</summary>
    private sealed class RequestScope(string path, string requestId) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _items = [new("RequestId", requestId), new("RequestPath", path)];

        public int Count => _items.Length;

        public KeyValuePair<string, object?> this[int index] => _items[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => $"RequestPath:{path} RequestId:{requestId}";
    }

    private sealed class CapturingProvider : ILoggerProvider, ISupportExternalScope
    {
        public CapturingProvider() => Last = this;

        public static CapturingProvider? Last { get; private set; }

        public IExternalScopeProvider? ScopeProvider { get; private set; }

        public ILogger CreateLogger(string categoryName) => throw new NotSupportedException();

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => ScopeProvider = scopeProvider;

        public void Dispose()
        {
        }
    }
}
