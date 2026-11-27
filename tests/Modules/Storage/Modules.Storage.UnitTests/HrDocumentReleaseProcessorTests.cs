using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Storage.Infrastructure.HrDocuments;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-11-26 («Consumidor»): un payload mal formado se registra y se marca sin efecto (reintentarlo no lo arregla), y una falla va
/// en <c>Warning</c> con el id y el intento, y en <c>Error</c> desde el intento 3 (<c>ErrorAfterAttempts</c>).
/// </summary>
public sealed class HrDocumentReleaseProcessorTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tenantId\":\"x\",\"fileIds\":[]}")]
    [InlineData("{\"tenantId\":\"00000000-0000-0000-0000-000000000000\",\"fileIds\":[]}")]
    [InlineData("{\"tenantId\":\"01900000-0000-7000-8000-000000000001\",\"fileIds\":\"no\"}")]
    [InlineData("{\"tenantId\":\"01900000-0000-7000-8000-000000000001\",\"fileIds\":[\"no\"]}")]
    [InlineData("{\"tenantId\":\"01900000-0000-7000-8000-000000000001\"}")]
    [InlineData("no es json")]
    public void AMalformedPayloadIsRecognised(string json)
    {
        Assert.False(HrDocumentReleaseProcessor.TryReadPayload(json, out _, out _));
    }

    [Fact]
    public void AWellFormedPayloadIsRead()
    {
        var tenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        Assert.True(HrDocumentReleaseProcessor.TryReadPayload(
            $"{{\"tenantId\":\"{tenantId}\",\"fileIds\":[\"{first}\",\"{second}\"]}}", out var readTenant, out var fileIds));

        Assert.Equal(tenantId, readTenant);
        Assert.Equal([first, second], fileIds);
    }

    [Theory]
    [InlineData(1, LogLevel.Warning)]
    [InlineData(2, LogLevel.Warning)]
    [InlineData(3, LogLevel.Error)]
    [InlineData(4, LogLevel.Error)]
    public void TheLogLevelFollowsTheAttempt(int attempt, LogLevel level)
    {
        var logger = new LevelLogger();
        var processor = new HrDocumentReleaseProcessor(NoScopes(), logger);

        processor.LogFailure(new InvalidOperationException("R2 is down."), Record(), attempt);

        Assert.Equal([level], logger.Levels);
    }

    [Fact]
    public void AFailedClaimIsAWarning()
    {
        var logger = new LevelLogger();
        var processor = new HrDocumentReleaseProcessor(NoScopes(), logger);

        processor.LogFailure(new InvalidOperationException("The database is down."), Record(), null);

        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    private static IServiceScopeFactory NoScopes() =>
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static StorageOutboxMessage Record() => new()
    {
        Id = Guid.CreateVersion7(),
        EventName = "hr.employee.documents-released.v1",
        PayloadJson = "{}",
        CorrelationId = "test",
        OccurredAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Anota el nivel de cada entrada; nada más.</summary>
    private sealed class LevelLogger : ILogger<HrDocumentReleaseProcessor>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
