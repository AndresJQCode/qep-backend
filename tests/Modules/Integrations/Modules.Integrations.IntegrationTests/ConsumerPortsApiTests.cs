using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, «Pruebas»: <c>IIntegrationConnections.ResolveAsync</c> contra la base (Active sí;
/// Paused, NeedsAttention, otro tenant y módulo apagado no) e <c>IConnectionHealthReporter</c>
/// (NeedsAttention, auditoría con actor Integration, evento, idempotente).
/// </summary>
public sealed class ConsumerPortsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Aislamiento por tenant en la base (arrastrado de la Task 9): ni la lista ni el reporte cruzan tenants.
    [Fact]
    public async Task ThePortsNeverCrossTenants()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var owner = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, owner.OwnerUserId, owner.TenantId, ManagePermissions);
        var connection = await CreateConnectionAsync(client, owner.TenantId, "Activa");

        using (var scope = factory.Services.CreateScope())
        {
            var connections = scope.ServiceProvider.GetRequiredService<IIntegrationConnections>();
            Assert.Equal([connection.Id], (await connections.ListActiveAsync(owner.TenantId, "zenvia", Ct)).Select(summary => summary.Id));
            Assert.Empty(await connections.ListActiveAsync(other.TenantId, "zenvia", Ct));
            Assert.Null(await connections.ResolveAsync(other.TenantId, connection.Id, Ct));

            await scope.ServiceProvider.GetRequiredService<IConnectionHealthReporter>()
                .ReportCredentialsRejectedAsync(other.TenantId, connection.Id, "credentials_rejected", Ct);
        }

        var after = await (await SendAsync(client, HttpMethod.Get, ConnectionUrl(owner.TenantId, connection.Id)))
            .Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.Equal("Active", after!.Status);
        Assert.Equal(connection.Version, after.Version);
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'integrations.connection.needs_attention' AND resource_id = @id",
            ("id", connection.Id.ToString())));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'integrations.connection-needs-attention.v1' AND payload->>'connectionId' = @id",
            ("id", connection.Id.ToString())));
    }

    [Fact]
    public async Task ThePortsSeeOnlyActiveVisibleConnectionsOfTheirTenant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var active = await CreateConnectionAsync(client, tenant.TenantId, "Activa");
        var paused = await CreateConnectionAsync(client, tenant.TenantId, "Pausada");
        (await SendAsync(client, HttpMethod.Post, $"{ConnectionUrl(tenant.TenantId, paused.Id)}/pause", ifMatch: "\"1\""))
            .EnsureSuccessStatusCode();
        var rejected = await CreateConnectionAsync(client, tenant.TenantId, "Rechazada");

        using (var scope = factory.Services.CreateScope())
        {
            var reporter = scope.ServiceProvider.GetRequiredService<IConnectionHealthReporter>();
            await reporter.ReportCredentialsRejectedAsync(tenant.TenantId, rejected.Id, "credentials_rejected", Ct);
        }

        // Idempotente: el segundo reporte, con la conexión ya en NeedsAttention, no deja nada.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IConnectionHealthReporter>()
                .ReportCredentialsRejectedAsync(tenant.TenantId, rejected.Id, "credentials_rejected", Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var connections = scope.ServiceProvider.GetRequiredService<IIntegrationConnections>();

            var resolved = await connections.ResolveAsync(tenant.TenantId, active.Id, Ct);
            Assert.NotNull(resolved);
            Assert.Equal(SentinelApiToken, resolved.Secrets["apiToken"]);
            Assert.Equal(FromNumber, resolved.Fields["fromNumber"]);
            Assert.Null(await connections.ResolveAsync(tenant.TenantId, paused.Id, Ct));
            Assert.Null(await connections.ResolveAsync(tenant.TenantId, rejected.Id, Ct));
            Assert.Null(await connections.ResolveAsync(Guid.CreateVersion7(), active.Id, Ct));
            Assert.Equal([active.Id], (await connections.ListActiveAsync(tenant.TenantId, "zenvia", Ct)).Select(summary => summary.Id));
        }

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'integrations.connection.needs_attention' AND actor_type = 'Integration' AND resource_id = @id",
            ("id", rejected.Id.ToString())));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'integrations.connection-needs-attention.v1' AND payload->>'connectionId' = @id",
            ("id", rejected.Id.ToString())));

        await SetModuleStatusAsync(connectionString, tenant.TenantId, "quotations", "inactive");
        using (var scope = factory.Services.CreateScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IIntegrationConnections>()
                .ResolveAsync(tenant.TenantId, active.Id, Ct));
        }
    }
}
