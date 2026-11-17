using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Hosting;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Disposes the rate limiters of the host's <c>RateLimitingMiddleware</c> when the host stops. The middleware builds a
/// <c>PartitionedRateLimiter</c> whose heartbeat timer ticks every 100 ms until the limiter is disposed, and ASP.NET Core never
/// disposes it: in production that is one limiter per process, but here every test host left one behind. Its partitioner captures the
/// middleware, so the running timer rooted the whole host (pipeline, container, EF models). A heap dump 9 minutes into the suite had
/// 84 live hosts and a 4 GB gen 2, and every test was slower than the one before. Test-only: the walk reads framework internals.
/// </summary>
internal sealed class RateLimiterRelease(IServer server) : IHostedService
{
    private const string MiddlewareTypeName = "Microsoft.AspNetCore.RateLimiting.RateLimitingMiddleware";

    // The pipeline is a chain of middleware instances and closures linked by these fields: TestServer and the hosting wrappers keep
    // the application in "_application", middlewares keep their successor in "_next" and the Use(...) closures in "next".
    private static readonly HashSet<string> ChainFields = ["_application", "_next", "next"];

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var (middlewares, walked) = FindMiddlewares(server);
        // Loud on purpose: if a framework update renames what the walk follows, the leak would come back without a trace.
        if (middlewares.Count == 0 && walked > 1)
        {
            throw new InvalidOperationException($"{MiddlewareTypeName} was not found in the started pipeline; see {nameof(RateLimiterRelease)}.");
        }

        foreach (var middleware in middlewares)
        {
            foreach (var field in middleware.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.GetValue(middleware) is IAsyncDisposable limiter)
                {
                    await limiter.DisposeAsync();
                }
            }
        }
    }

    private static (List<object> Found, int Walked) FindMiddlewares(object root)
    {
        var found = new List<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(root);
        while (pending.Count > 0 && visited.Count < 10_000)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is Delegate handler)
            {
                if (handler.Target is { } target)
                {
                    pending.Push(target);
                }

                continue;
            }

            if (current.GetType().FullName == MiddlewareTypeName)
            {
                found.Add(current);
            }

            // A closure holds whatever it captured: UseMiddleware's binder keeps the middleware instance in one.
            var closure = current.GetType().Name.Contains("<>c__DisplayClass", StringComparison.Ordinal);
            for (var type = current.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsValueType
                        && (closure || ChainFields.Contains(field.Name) || typeof(Delegate).IsAssignableFrom(field.FieldType))
                        && field.GetValue(current) is { } next)
                    {
                        pending.Push(next);
                    }
                }
            }
        }

        return (found, visited.Count);
    }
}
