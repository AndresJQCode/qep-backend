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
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string Fit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
