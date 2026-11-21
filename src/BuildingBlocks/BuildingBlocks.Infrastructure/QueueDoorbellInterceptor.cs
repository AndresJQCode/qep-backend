using System.Collections.Frozen;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildingBlocks.Infrastructure;

/// <summary>
/// Quién toca los timbres de las colas (spec 2026-11-13, D-7; plan P10). Los productores son decenas (el outbox lo escriben
/// ~20 <c>DbContext</c>) y todos insertan por <c>SaveChanges</c> o por <c>Database.ExecuteSql*</c>: este interceptor
/// reconoce en el texto del comando un <c>INSERT INTO &lt;esquema&gt;.&lt;tabla&gt;</c> de una tabla de cola y timbra su
/// cola. Un productor nuevo queda cubierto sin acordarse.
///
/// Se timbra <b>después del commit</b>: sin transacción, al instante; con una, la cola queda pendiente para ese
/// <c>DbContext</c> y suena en <c>TransactionCommitted</c>; un rollback la descarta. Un <c>INSERT</c> que no insertó nada
/// (<c>ON CONFLICT DO NOTHING</c> con 0 filas) no timbra; uno por lector (el batch de <c>SaveChanges</c>) timbra siempre,
/// porque el lector no dice cuántas filas entraron. Lo que confirma una transacción ajena (<c>UseTransaction</c> sobre la
/// conexión de otro <c>DbContext</c>) no timbra: lo cubre la red de seguridad del worker. Nunca lanza.
/// </summary>
public sealed partial class QueueDoorbellInterceptor(IQueueDoorbell doorbell) : DbCommandInterceptor, IDbTransactionInterceptor
{
    private static readonly FrozenDictionary<string, string> Tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["platform.outbox_messages"] = DoorbellQueues.Outbox,
        ["messaging.automation_inbox"] = DoorbellQueues.Automation,
        ["messaging.webhook_deliveries"] = DoorbellQueues.Webhook,
        ["shopify.webhook_deliveries"] = DoorbellQueues.ShopifyWebhook,
        ["messaging.message_media"] = DoorbellQueues.Media,
        ["messaging.scheduled_messages"] = DoorbellQueues.Scheduled,
        ["quotations.export_jobs"] = DoorbellQueues.Export,
        ["support.ticket_surveys"] = DoorbellQueues.SupportSurvey,
        ["shopify.product_imports"] = DoorbellQueues.ShopifyProducts,
        ["siigo.invoice_batch_items"] = DoorbellQueues.SiigoInvoice,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Las colas pendientes de cada DbContext con transacción abierta; se van con el contexto.
    private readonly ConditionalWeakTable<DbContext, PendingQueues> _pending = new();

    [GeneratedRegex("""INSERT\s+INTO\s+"?(\w+)"?\s*\.\s*"?(\w+)"?""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertInto();

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        if (result > 0)
        {
            Collect(command, eventData.Context);
        }

        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (result > 0)
        {
            Collect(command, eventData.Context);
        }

        return ValueTask.FromResult(result);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Collect(command, eventData.Context);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Collect(command, eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Flush(eventData.Context);

    public Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Discard(eventData.Context);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Discard(eventData.Context);

    public Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>Las colas cuyas tablas reciben un <c>INSERT</c> en <paramref name="commandText"/>. Internal para las pruebas.</summary>
    internal static IReadOnlyCollection<string> QueuesOf(string commandText)
    {
        // Corre en cada comando de cada DbContext: lo que no inserta sale sin pasar por la expresión.
        if (!commandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        HashSet<string>? queues = null;
        foreach (Match match in InsertInto().Matches(commandText))
        {
            if (Tables.TryGetValue($"{match.Groups[1].Value}.{match.Groups[2].Value}", out var queue))
            {
                (queues ??= new HashSet<string>(StringComparer.Ordinal)).Add(queue);
            }
        }

        return (IReadOnlyCollection<string>?)queues ?? [];
    }

    private void Collect(DbCommand command, DbContext? context)
    {
        var queues = QueuesOf(command.CommandText);
        if (queues.Count == 0)
        {
            return;
        }

        if (context?.Database.CurrentTransaction is null)
        {
            Ring(queues);
            return;
        }

        _pending.GetOrCreateValue(context).Add(queues);
    }

    private void Flush(DbContext? context)
    {
        if (context is not null && _pending.TryGetValue(context, out var pending))
        {
            Ring(pending.Drain());
        }
    }

    private void Discard(DbContext? context)
    {
        if (context is not null && _pending.TryGetValue(context, out var pending))
        {
            _ = pending.Drain();
        }
    }

    private void Ring(IEnumerable<string> queues)
    {
        foreach (var queue in queues)
        {
            try
            {
                doorbell.Ring(queue);
            }
            catch (Exception)
            {
                // Ring no lanza por contrato; si un doble lo hiciera, el commit ya pasó y el worker tiene su red de seguridad.
            }
        }
    }

    private sealed class PendingQueues
    {
        private readonly Lock _gate = new();
        private readonly HashSet<string> _queues = new(StringComparer.Ordinal);

        public void Add(IEnumerable<string> queues)
        {
            lock (_gate)
            {
                _queues.UnionWith(queues);
            }
        }

        public List<string> Drain()
        {
            lock (_gate)
            {
                var drained = _queues.ToList();
                _queues.Clear();
                return drained;
            }
        }
    }
}
