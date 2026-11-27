using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests.Incidents;

/// <summary>Revisión final de T2, L5: <c>GET /incidents</c> ordena por <c>reported_at</c> y <c>number</c> descendentes dentro del tenant, y
/// sin un índice que lo cubra la bandeja ordena toda la tabla en cada página.</summary>
public sealed class IncidentListIndexTests
{
    [Fact]
    public async Task TheInboxOrderingHasAnIndexByTenantReportedAtAndNumber()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var definition = await ScalarAsync<string>(
            database,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'wms' AND tablename = 'incidents' AND indexname = 'IX_incidents_tenant_reported'");

        Assert.Contains("(tenant_id, reported_at, number)", definition, StringComparison.Ordinal);
    }
}
