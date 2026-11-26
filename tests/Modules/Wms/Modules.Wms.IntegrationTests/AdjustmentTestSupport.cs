using System.Net.Http.Json;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>Los cuerpos y llamadas de ajustes que repiten las pruebas de <c>AdjustmentApiTests</c>, <c>IdempotencyTests</c> y
/// <c>ConcurrentAdjustmentTests</c>.</summary>
internal static class AdjustmentTestSupport
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static object Line(
        Guid location, Guid variant, decimal delta, string status = "Available", string? lotCode = null, DateOnly? expirationDate = null) =>
        new
        {
            locationId = location,
            variantId = variant,
            lotCode,
            expirationDate,
            status,
            quantityDelta = delta,
        };

    public static object Body(
        LedgerSetup s,
        IEnumerable<object> lines,
        string reason = "CountVariance",
        bool autoApprove = false,
        DateTimeOffset? occurredAt = null,
        Guid? ownerId = null,
        string? note = null) =>
        new
        {
            warehouseId = s.WarehouseId,
            ownerId,
            reason,
            note,
            occurredAt,
            lines,
            autoApprove,
        };

    public static async Task<HttpResponseMessage> ProposeRawAsync(HttpClient client, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{WmsUrl()}/adjustments") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, Ct);
    }

    public static async Task<AdjustmentDto> ProposeAsync(HttpClient client, object body, string? key = null)
    {
        var response = await ProposeRawAsync(client, body, key);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdjustmentDto>(Ct);
        return dto!;
    }

    public static Task<HttpResponseMessage> PostRawAsync(HttpClient client, Guid id, long version, string? headerKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{WmsUrl()}/adjustments/{id}/post")
        {
            Content = JsonContent.Create(new { version }),
        };
        if (headerKey is not null)
        {
            request.Headers.Add("Idempotency-Key", headerKey);
        }

        return client.SendAsync(request, Ct);
    }

    public static Task<HttpResponseMessage> ActionRawAsync(HttpClient client, Guid id, string action, object body) =>
        client.PostAsJsonAsync($"{WmsUrl()}/adjustments/{id}/{action}", body, Ct);

    /// <summary>Propone con aprobación automática y devuelve el ajuste ya aprobado.</summary>
    public static async Task<AdjustmentDto> ApprovedAsync(HttpClient manager, LedgerSetup s, Guid location, decimal delta) =>
        await ProposeAsync(manager, Body(s, [Line(location, s.VariantId, delta)], autoApprove: true));

    public static async Task<AdjustmentDto> PostedAsync(HttpClient manager, LedgerSetup s, Guid location, decimal delta)
    {
        var approved = await ApprovedAsync(manager, s, location, delta);
        var response = await PostRawAsync(manager, approved.Id, approved.Version);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdjustmentDto>(Ct))!;
    }
}
