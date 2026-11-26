using System.Collections;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace Bootstrapper;

/// <summary>
/// <see cref="SensitivePaths"/> aplicado a la telemetría: el token de una invitación viaja en la ruta y, sin esto, saldría en
/// claro por dos lados aunque <c>request_failures</c> ya lo tape.
/// <list type="bullet">
/// <item>El span del servidor que exporta OpenTelemetry lleva la ruta cruda en <c>url.path</c> (la instrumentación de ASP.NET Core
/// la pone al empezar y después llama a <c>EnrichWithHttpRequest</c>, que acá la reescribe). <c>http.route</c> y el nombre del
/// span usan la plantilla (<c>{token}</c>) y no hace falta tocarlos; <c>url.query</c> ya lo redacta la instrumentación.</item>
/// <item>ASP.NET Core abre en cada request un scope de log con <c>RequestPath</c>, y el JSON de consola escribe los scopes
/// (<c>AddQepLogging</c>, <c>IncludeScopes</c>) en <b>toda</b> línea del request, también las de nivel Information de la app y
/// de EF. El scope se arma con la ruta al empezar el request, así que no se puede cambiar desde un middleware: se cambia lo
/// que lee el proveedor de logs, envolviendo el <see cref="IExternalScopeProvider"/> que le da la fábrica (la de la fábrica es
/// la que agrega TraceId y SpanId, y se conserva).</item>
/// </list>
/// </summary>
public static class SensitivePathTelemetry
{
    private const string RequestPathKey = "RequestPath";

    private static readonly string[] PathTags = ["url.path", "http.target"];

    private const string FullUrlTag = "url.full";

    /// <summary>
    /// Registra las dos redacciones. Va después de <c>AddQepLogging</c>: envuelve los proveedores de log ya registrados.
    /// </summary>
    public static IServiceCollection AddSensitivePathRedaction(this IServiceCollection services)
    {
        services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            var previous = options.EnrichWithHttpRequest;
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                previous?.Invoke(activity, request);
                RedactSpan(activity);
            };
        });

        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType != typeof(ILoggerProvider)
                || descriptor.IsKeyedService
                || descriptor.ImplementationType is not { } type
                || !typeof(ISupportExternalScope).IsAssignableFrom(type))
            {
                continue;
            }

            services[index] = ServiceDescriptor.Describe(
                typeof(ILoggerProvider),
                provider => new ScopeMaskingLoggerProvider(Create(provider, type)),
                descriptor.Lifetime);
        }

        return services;
    }

    /// <summary>Los scopes de <paramref name="inner"/>, con <c>RequestPath</c> enmascarado donde lleva un token.</summary>
    public static IExternalScopeProvider MaskScopes(IExternalScopeProvider inner) => new ScopeMaskingProvider(inner);

    private static void RedactSpan(Activity activity)
    {
        foreach (var tag in PathTags)
        {
            if (activity.GetTagItem(tag) is string path && SensitivePaths.Mask(path) is var masked && masked != path)
            {
                activity.SetTag(tag, masked);
            }
        }

        if (activity.GetTagItem(FullUrlTag) is string full && Uri.TryCreate(full, UriKind.Absolute, out var uri))
        {
            var maskedPath = SensitivePaths.Mask(uri.AbsolutePath);
            if (maskedPath != uri.AbsolutePath)
            {
                activity.SetTag(FullUrlTag, uri.GetLeftPart(UriPartial.Authority) + maskedPath + uri.Query + uri.Fragment);
            }
        }
    }

    // ConsoleLoggerProvider tiene dos constructores; el de los formatters es el que usa AddJsonConsole (con sus opciones).
    private static ILoggerProvider Create(IServiceProvider provider, Type type) =>
        type == typeof(ConsoleLoggerProvider)
            ? new ConsoleLoggerProvider(
                provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>(),
                provider.GetServices<ConsoleFormatter>())
            : (ILoggerProvider)ActivatorUtilities.CreateInstance(provider, type);

    private static object? MaskScope(object? scope)
    {
        if (scope is not IReadOnlyList<KeyValuePair<string, object?>> items)
        {
            return scope;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is { Key: RequestPathKey, Value: string path } && SensitivePaths.Mask(path) is var masked && masked != path)
            {
                var copy = items.ToArray();
                copy[index] = new(RequestPathKey, masked);
                return new MaskedScope(copy, scope.ToString()?.Replace(path, masked, StringComparison.Ordinal) ?? string.Empty);
            }
        }

        return scope;
    }

    private sealed class ScopeMaskingProvider(IExternalScopeProvider inner) : IExternalScopeProvider
    {
        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            inner.ForEachScope((scope, innerState) => callback(MaskScope(scope), innerState), state);

        public IDisposable Push(object? state) => inner.Push(state);
    }

    private sealed class ScopeMaskingLoggerProvider(ILoggerProvider inner) : ILoggerProvider, ISupportExternalScope
    {
        public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
            ((ISupportExternalScope)inner).SetScopeProvider(MaskScopes(scopeProvider));

        public void Dispose() => inner.Dispose();
    }

    // Lo que el JSON de consola escribe de un scope: sus pares y su ToString() como «Message».
    private sealed class MaskedScope(KeyValuePair<string, object?>[] items, string text) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => items.Length;

        public KeyValuePair<string, object?> this[int index] => items[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, object?>>)items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => text;
    }
}
