using Bootstrapper.Observability;
using Microsoft.Extensions.Logging;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta <see cref="IQueueMetricsSource"/> (Observability) al puerto <see cref="IOperatorQueueSource"/> de Tenancy, que no lo referencia.
/// Recorre <see cref="QueueNames.All"/> <b>una cola tras otra</b>: las consultas comparten la conexión de <c>MessagingDbContext</c>, y
/// leerlas en paralelo pediría un scope por cola.
///
/// Tope total de 10 s para toda la respuesta (<c>CancelAfter</c> sobre un token enlazado al de la petición): sin él, el peor caso sería
/// 8 × 5 s = 40 s. Al vencer, la cola en curso y las que faltan van con <c>null</c>, sin consultarse. Una cola que falla por cualquier otra
/// causa va con sus tres números en <c>null</c> y se anota un <c>Warning</c> con su nombre (nunca datos de tenant); las demás siguen.
/// Si se cancela la <b>petición</b>, la cancelación se propaga: se distingue del tope mirando el token de la petición.
/// </summary>
internal sealed partial class OperatorQueueSource : IOperatorQueueSource
{
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

    private readonly IQueueMetricsSource _queues;
    private readonly ILogger<OperatorQueueSource> _logger;
    private readonly TimeSpan _budget;

    public OperatorQueueSource(IQueueMetricsSource queues, ILogger<OperatorQueueSource> logger)
        : this(queues, logger, DefaultBudget)
    {
    }

    // El tope se inyecta sólo en las pruebas, para no esperar 10 s de verdad.
    internal OperatorQueueSource(IQueueMetricsSource queues, ILogger<OperatorQueueSource> logger, TimeSpan budget)
    {
        _queues = queues;
        _logger = logger;
        _budget = budget;
    }

    public async Task<IReadOnlyList<OperatorQueueDto>> ListAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);

        var rows = new List<OperatorQueueDto>(QueueNames.All.Count);
        foreach (var queue in QueueNames.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(budget.IsCancellationRequested ? Unreadable(queue) : await ReadAsync(queue, budget.Token, cancellationToken));
        }

        return rows;
    }

    private async Task<OperatorQueueDto> ReadAsync(string queue, CancellationToken budgetToken, CancellationToken requestToken)
    {
        try
        {
            var reading = await _queues.ReadAsync(queue, budgetToken);
            return new OperatorQueueDto(reading.Queue, reading.Backlog, reading.Retrying, reading.OldestAgeSeconds);
        }
        catch (Exception exception) when (!requestToken.IsCancellationRequested)
        {
            // Tope vencido o falla propia de la cola: se anota y la pantalla ve «no se pudo leer», no un cero.
            LogQueueUnreadable(exception, queue);
            return Unreadable(queue);
        }
    }

    private static OperatorQueueDto Unreadable(string queue) => new(queue, null, null, null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The operator queues console could not read the {Queue} queue.")]
    private partial void LogQueueUnreadable(Exception exception, string queue);
}
