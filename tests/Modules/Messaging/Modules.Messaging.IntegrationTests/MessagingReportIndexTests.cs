using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-28 «Computation»: el reporte barre por <c>(tenant_id, created_at)</c>; sin estos índices cada consulta recorre toda la tabla.</summary>
public sealed class MessagingReportIndexTests
{
    [Theory]
    [InlineData("IX_messages_tenant_created")]
    [InlineData("IX_messages_events_reopened")]
    [InlineData("IX_conversations_tenant_created")]
    public async Task TheReportIndexesExist(string indexName)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        _ = factory.Services;

        var count = await CountAsync(
            database.GetConnectionString(),
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'messaging' AND indexname = @name",
            ("name", indexName));

        Assert.Equal(1, count);
    }
}
