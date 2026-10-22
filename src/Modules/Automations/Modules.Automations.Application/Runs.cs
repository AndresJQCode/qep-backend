using BuildingBlocks.Application;
using Modules.Automations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

/// <summary>Spec 2026-10-21 §4.4: una corrida del historial del chatbot. <see cref="Status"/> es el nombre del <c>RunStatus</c>;
/// <see cref="DurationSeconds"/> es <c>EndedAt − StartedAt</c> en segundos enteros, y <c>null</c> mientras la corrida sigue viva.</summary>
public sealed record RunDto(
    Guid Id,
    Guid ConversationId,
    string Status,
    string? CurrentNodeId,
    int StepNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    string? FailureCode);

/// <summary>Sin <paramref name="From"/> y <paramref name="To"/> rige «los últimos 7 días». El repositorio topa en 500.</summary>
public sealed record ListRunsQuery(Guid TenantId, Guid ChatbotId, DateTimeOffset? From, DateTimeOffset? To) : IQuery<IReadOnlyList<RunDto>>;

/// <summary>§4.4: historial de corridas de un chatbot, de la más nueva a la más vieja, con <c>automations.chatbot.read</c>. El chatbot
/// no se exige existente: las corridas de uno borrado siguen siendo del tenant y el filtro por tenant ya las acota.</summary>
public sealed class ListRunsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IConversationRunRepository runs, IClock clock)
    : IQueryHandler<ListRunsQuery, IReadOnlyList<RunDto>>
{
    public const int DefaultRangeDays = 7;

    public async Task<IReadOnlyList<RunDto>> HandleAsync(ListRunsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotRead, cancellationToken);
        var now = clock.UtcNow;
        var to = query.To ?? now;
        var from = query.From ?? to.AddDays(-DefaultRangeDays);
        if (from > to)
        {
            throw new AutomationsDomainException(
                AutomationsErrorCodes.RunRangeInvalid, "from must not be after to.",
                new Dictionary<string, string[]> { ["from"] = ["from must not be after to."] });
        }

        var found = await runs.ListAsync(query.TenantId, query.ChatbotId, from, to, cancellationToken);
        return found.Select(ToDto).ToList();
    }

    private static RunDto ToDto(ConversationRun run) => new(
        run.Id, run.ConversationId, run.Status.ToString(), run.CurrentNodeId, run.StepNumber, run.StartedAt, run.EndedAt,
        run.EndedAt is { } ended ? (int)(ended - run.StartedAt).TotalSeconds : null,
        run.FailureCode);
}
