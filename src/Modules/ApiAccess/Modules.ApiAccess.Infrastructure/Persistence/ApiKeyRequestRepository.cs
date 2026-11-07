using Microsoft.EntityFrameworkCore;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;

namespace Modules.ApiAccess.Infrastructure.Persistence;

internal sealed class ApiKeyRequestRepository(ApiAccessDbContext dbContext) : IApiKeyRequestRepository
{
    public async Task<(IReadOnlyList<ApiKeyRequest> Items, int Total)> SearchAsync(
        ApiKeyRequestSearch search, CancellationToken cancellationToken)
    {
        // El tenant va siempre: una clave de otro tenant no devuelve nada, ni siquiera por su id.
        var query = dbContext.ApiKeyRequests
            .AsNoTracking()
            .Where(request => request.TenantId == search.TenantId);

        if (search.ApiKeyId is { } apiKeyId)
        {
            query = query.Where(request => request.ApiKeyId == apiKeyId);
        }

        if (!string.IsNullOrEmpty(search.TraceId))
        {
            query = query.Where(request => request.TraceId == search.TraceId);
        }

        if (search.OccurredFrom is { } from)
        {
            query = query.Where(request => request.OccurredAt >= from);
        }

        if (search.OccurredTo is { } to)
        {
            query = query.Where(request => request.OccurredAt <= to);
        }

        // Una llamada 201 cuyo mensaje falló después (webhook) cuenta como falla, no como éxito.
        var failed = search.FailedMessageIds.ToArray();
        query = search.Result switch
        {
            ApiKeyRequestResult.Failure => query.Where(request =>
                request.StatusCode >= 400
                || (request.MessageId != null && failed.Contains(request.MessageId.Value))),
            ApiKeyRequestResult.Success => query.Where(request =>
                request.StatusCode < 400
                && (request.MessageId == null || !failed.Contains(request.MessageId.Value))),
            _ => query
        };

        var total = await query.CountAsync(cancellationToken);
        var skip = (long)(search.Page - 1) * search.PageSize;
        var items = await query
            .OrderByDescending(request => request.OccurredAt)
            .ThenByDescending(request => request.Id)
            .Skip((int)Math.Min(skip, int.MaxValue))
            .Take(search.PageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }
}
