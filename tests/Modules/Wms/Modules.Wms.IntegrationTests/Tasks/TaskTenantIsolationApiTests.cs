using System.Net;
using System.Net.Http.Json;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Otro tenant nunca ve ni toca las tareas: 403 en toda ruta, nunca 404 (un 404 confirmaría que el id existe).</summary>
public sealed class TaskTenantIsolationApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnotherTenantGets403OnEveryTaskRoute()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]));
        var line = task.Lines[0].Id;
        var token = Guid.NewGuid();
        using var stranger = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);
        var url = TasksUrl();
        var w = s.WarehouseId;
        (HttpMethod Method, string Path, object? Body)[] routes =
        [
            (HttpMethod.Get, $"{url}?warehouseId={w}", null),
            (HttpMethod.Get, $"{url}/summary?warehouseId={w}", null),
            (HttpMethod.Get, $"{url}/assignees?warehouseId={w}&type=Move", null),
            (HttpMethod.Get, $"{url}/{task.Id}", null),
            (HttpMethod.Post, url, Body(s, [Line(s.VariantId, s.BinA, task.Lines[0].ToLocationId!.Value)])),
            (HttpMethod.Get, $"{url}/mine?warehouseId={w}", null),
            (HttpMethod.Post, $"{url}/{task.Id}/publish", new { version = task.Version }),
            (HttpMethod.Post, $"{url}/{task.Id}/hold", new { version = task.Version }),
            (HttpMethod.Post, $"{url}/{task.Id}/assign", new { memberId = Guid.NewGuid(), version = task.Version }),
            (HttpMethod.Post, $"{url}/{task.Id}/unassign", new { version = task.Version }),
            (HttpMethod.Post, $"{url}/{task.Id}/priority", new { priority = 5, urgent = false, version = task.Version }),
            (HttpMethod.Post, $"{url}/{task.Id}/cancel", new { reason = "x", version = task.Version }),
            (HttpMethod.Post, $"{url}/{task.Id}/lines/{line}/cancel", new { reason = "x", version = task.Version }),
            (HttpMethod.Post, $"{url}/next", new { warehouseId = w, deviceId = "zebra-01" }),
            (HttpMethod.Post, $"{url}/{task.Id}/claim", new { deviceId = "zebra-01" }),
            (HttpMethod.Post, $"{url}/{task.Id}/heartbeat", new { leaseToken = token }),
            (HttpMethod.Post, $"{url}/{task.Id}/unclaim", new { leaseToken = token, reason = "ShiftEnd" }),
            (HttpMethod.Post, $"{url}/{task.Id}/lines/{line}/short", new
            {
                leaseToken = token, quantityFound = 1, incidentType = "ShortMove", occurredAt = DateTimeOffset.UtcNow,
                deviceId = "zebra-01",
            }),
            (HttpMethod.Post, $"{url}/{task.Id}/lines/{line}/confirm", new
            {
                leaseToken = token, fromLocationId = s.BinA, toLocationId = task.Lines[0].ToLocationId, quantity = 1,
                occurredAt = DateTimeOffset.UtcNow, deviceId = "zebra-01",
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

            // confirm es IK: sin la clave respondería 428 antes de mirar el tenant.
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
