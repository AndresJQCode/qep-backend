using System.Data.Common;
using BuildingBlocks.Application;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// What is not «could not» but «not yet»: a cancellation (the worker shutting down), a concurrency conflict and a transient database
/// failure (lost connection, timeout, deadlock or serialization: <see cref="DbException.IsTransient"/>, which Npgsql sets, anywhere in
/// the chain). They go up and the engine unit is retried, instead of recording an outcome the customer never caused. The one rule
/// for the engine's CRM effect, the editor's CRM catalog and the Bootstrapper's conversation customer resolver.
/// </summary>
public static class TransientFailures
{
    public static bool MustPropagate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException or RequestConcurrencyException)
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
