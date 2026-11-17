using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Los dobles del bucle de modelo que comparten las pruebas del bucle y del copiloto (las del agente tienen los suyos).</summary>
internal sealed class LoopModelClient : IAiModelClient
{
    private readonly Queue<object> _script = new();

    public List<AiModelRequest> Requests { get; } = [];

    public void Enqueue(object step) => _script.Enqueue(step);

    public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _script.Dequeue() switch
        {
            AiModelResponse response => Task.FromResult(response),
            AiModelException failure => throw failure,
            var other => throw new InvalidOperationException($"Unexpected step {other}."),
        };
    }
}

internal sealed class LoopResolver(IAiModelClient client) : IAiModelClientResolver
{
    public List<AiProvider> Asked { get; } = [];

    public IAiModelClient For(AiProvider provider)
    {
        Asked.Add(provider);
        return client;
    }
}

internal sealed class LoopLedger : IAiRunLedger
{
    public List<AiUsageRecord> Usage { get; } = [];

    public List<AiToolCallRecord> ToolCalls { get; } = [];

    public Task RecordUsageAsync(AiUsageRecord record, CancellationToken cancellationToken)
    {
        Usage.Add(record);
        return Task.CompletedTask;
    }

    public Task RecordToolCallAsync(AiToolCallRecord record, CancellationToken cancellationToken)
    {
        ToolCalls.Add(record);
        return Task.CompletedTask;
    }
}

internal sealed class LoopCredentials : IAiProviderCredentials
{
    public List<(Guid Tenant, Guid Connection, string Code)> Marked { get; } = [];

    public Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task MarkNeedsAttentionAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken)
    {
        Marked.Add((tenantId, connectionId, failureCode));
        return Task.CompletedTask;
    }
}

internal sealed class LoopClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);
}

internal static class LoopResponses
{
    public static AiToolCall Call(string id, string name, object arguments) => new(id, name, JsonSerializer.Serialize(arguments));

    public static AiModelResponse Tools(params AiToolCall[] calls) =>
        new(null, calls, [new AiOpaqueBlock("opaque-1")], AiStopReason.ToolUse, new AiModelUsage(1000, 50, 900, 100), "claude-opus-5-5", "req_1");
}
