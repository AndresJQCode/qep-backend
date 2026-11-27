using System.Net;
using System.Net.Http.Json;
using Modules.Wms.Domain.Control;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Incidents;

/// <summary>Otro tenant nunca ve ni toca las novedades: 403 en toda ruta, nunca 404 (un 404 confirmaría que el id existe).</summary>
public sealed class IncidentTenantIsolationApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnotherTenantGets403OnEveryIncidentRoute()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]));
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.SyncConflict));
        using var stranger = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);
        var url = $"{WmsUrl()}/incidents";
        var id = incident.Id;
        (HttpMethod Method, string Path, object? Body)[] routes =
        [
            (HttpMethod.Get, $"{url}?warehouseId={s.WarehouseId}", null),
            (HttpMethod.Get, $"{url}/{id}", null),
            (HttpMethod.Post, url, new
            {
                warehouseId = s.WarehouseId, type = "UnreadableLabel", locationId = s.BinA, note = "x",
                occurredAt = DateTimeOffset.UtcNow, deviceId = "zebra-01",
            }),
            (HttpMethod.Post, $"{url}/{id}/review", new { version = 1 }),
            (HttpMethod.Post, $"{url}/{id}/resolve", new { version = 1, resolution = "Accept" }),
            (HttpMethod.Post, $"{url}/{id}/reject", new { version = 1, note = "x" }),
            (HttpMethod.Post, $"{TasksUrl()}/{task.Id}/lines/{task.Lines[0].Id}/short", new
            {
                leaseToken = Guid.NewGuid(), quantityFound = 1, incidentType = "ShortMove", occurredAt = DateTimeOffset.UtcNow,
                deviceId = "zebra-01",
            }),
        ];
        var failures = new List<string>();

        foreach (var (method, path, body) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            // Las IK sin la clave responderían 428 antes de mirar el tenant.
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await stranger.SendAsync(request, Ct);
            var code = response.StatusCode == HttpStatusCode.Forbidden ? await CodeOfAsync(response) : null;
            if (response.StatusCode != HttpStatusCode.Forbidden || code != "authorization.denied")
            {
                failures.Add($"{method} {path} → {(int)response.StatusCode} {code}");
            }
        }

        Assert.Empty(failures);
    }
}
