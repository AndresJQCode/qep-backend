using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application.Http;
using Modules.Automations.Infrastructure.Persistence;

namespace Modules.Automations.Infrastructure.Http;

/// <summary>
/// D-H6: <c>INSERT</c> en <c>automations.http_calls</c> con un <see cref="AutomationsDbContext"/> propio, de un scope nuevo: su conexión no
/// es la de la unidad del motor, así que la fila se confirma aunque la unidad se deshaga (la llamada a la API ya salió y no se deshace).
/// </summary>
internal sealed class HttpCallLog(IServiceScopeFactory scopeFactory) : IHttpCallLog
{
    private const int PathMaxLength = 512;

    private const int NodeIdMaxLength = 64;

    private const int ErrorCodeMaxLength = 64;

    public async Task RecordAsync(HttpCallEntry entry, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AutomationsDbContext>();
        context.HttpCalls.Add(new HttpCallRecord
        {
            TenantId = entry.TenantId,
            RunId = entry.RunId,
            SimulationId = entry.SimulationId,
            NodeId = Fit(entry.NodeId, NodeIdMaxLength),
            ConnectionId = entry.ConnectionId,
            Method = entry.Method,
            Path = Fit(entry.Path, PathMaxLength),
            StatusCode = entry.StatusCode,
            DurationMs = entry.DurationMs,
            Outcome = entry.Outcome,
            ErrorCode = entry.ErrorCode is null ? null : Fit(entry.ErrorCode, ErrorCodeMaxLength),
            CalledAt = entry.CalledAt,
            StepNumber = entry.StepNumber,
            ResponseBytes = entry.ResponseBytes,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    // Por la llave primaria, como la purga del inbox (AutomationInbox.PurgeProcessedBeforeAsync) y la de webhook_deliveries. Todos los
    // tenants a la vez: es la retención de la tabla, no una consulta de nadie. El índice (tenant_id, called_at) no ayuda sin tenant, y la
    // tabla sólo guarda la ventana de 30 días, así que no se agrega otro.
    public async Task<int> PurgeBeforeAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);
        // Npgsql sólo escribe un timestamptz desde un DateTimeOffset en UTC.
        cutoff = cutoff.ToUniversalTime();
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AutomationsDbContext>();
        return await context.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM automations.http_calls
             WHERE id IN (SELECT id FROM automations.http_calls WHERE called_at < {cutoff} LIMIT {batch})
            """, cancellationToken);
    }

    private static string Fit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
