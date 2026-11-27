using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Los cuerpos y las llamadas de la API de tareas que repiten sus pruebas.</summary>
internal static class TaskApiSupport
{
    public const string OperatorSubject = "01900000-0000-7000-8000-000000000003";
    public const string SecondOperatorSubject = "01900000-0000-7000-8000-000000000004";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string TasksUrl(string tenantId = TenantId) => $"{WmsUrl(tenantId)}/tasks";

    public static object Line(
        Guid variant, Guid from, Guid to, decimal quantity = 5, Guid? lotId = null, string? stockStatus = null) =>
        new { variantId = variant, lotId, stockStatus, fromLocationId = from, toLocationId = to, quantity };

    public static object Body(
        LedgerSetup s,
        IEnumerable<object> lines,
        bool? publish = null,
        Guid? assignTo = null,
        int? priority = null,
        string? note = null,
        Guid? warehouseId = null) =>
        new
        {
            warehouseId = warehouseId ?? s.WarehouseId,
            priority,
            note,
            publish,
            assignToMemberId = assignTo,
            lines,
        };

    public static async Task<HttpResponseMessage> CreateRawAsync(HttpClient client, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TasksUrl()) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, Ct);
    }

    public static async Task<TaskDto> CreateAsync(HttpClient client, object body)
    {
        var response = await CreateRawAsync(client, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<TaskDto>(response);
    }

    public static Task<HttpResponseMessage> ActionRawAsync(HttpClient client, Guid taskId, string action, object body) =>
        client.PostAsJsonAsync($"{TasksUrl()}/{taskId}/{action}", body, Ct);

    public static async Task<TaskDto> ActionAsync(HttpClient client, Guid taskId, string action, object body)
    {
        var response = await ActionRawAsync(client, taskId, action, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<TaskDto>(response);
    }

    public static async Task<TaskDto> GetAsync(HttpClient client, Guid taskId)
    {
        var response = await client.GetAsync($"{TasksUrl()}/{taskId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<TaskDto>(response);
    }

    /// <summary>La extensión <c>resumable</c> de un 409 <c>wms.task.lease_lost</c> (§6.7).</summary>
    public static async Task<bool> ResumableOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("resumable").GetBoolean();
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(Ct);
        Assert.NotNull(value);
        return value;
    }

    /// <summary>Una persona de bodega: su membresía en el tenant y un cliente que sólo ve documentos y mueve mercancía.</summary>
    public static async Task<(Guid MemberId, HttpClient Client)> SeedOperatorAsync(
        QepApiFactory factory, string subjectId = OperatorSubject, IReadOnlyCollection<string>? roles = null)
    {
        var memberId = await SeedCallerMembershipAsync(factory, subjectId: subjectId, roles: roles);
        return (memberId, CreateClient(factory, subjectId, TenantId, WmsPermissions.DocumentRead, WmsPermissions.PutawayPerform));
    }

    /// <summary>Una persona de bodega con los permisos que se indiquen: su membresía en el tenant y un cliente con ellos.</summary>
    public static async Task<(Guid MemberId, HttpClient Client)> SeedMemberAsync(
        QepApiFactory factory, string subjectId, params string[] permissions)
    {
        var memberId = await SeedCallerMembershipAsync(factory, subjectId: subjectId);
        return (memberId, CreateClient(factory, subjectId, TenantId, permissions));
    }

    /// <summary>Quien cuenta: ve documentos y tiene <c>wms.count.perform</c>, y nada más.</summary>
    public static Task<(Guid MemberId, HttpClient Client)> SeedCounterAsync(
        QepApiFactory factory, string subjectId = SecondOperatorSubject) =>
        SeedMemberAsync(factory, subjectId, WmsPermissions.DocumentRead, WmsPermissions.CountPerform);

    public static Task<HttpResponseMessage> NextRawAsync(HttpClient client, Guid warehouseId) =>
        client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId, deviceId = "zebra-01" }, Ct);

    public static async Task<HttpResponseMessage> CountRawAsync(
        HttpClient client, Guid taskId, Guid lineId, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TasksUrl()}/{taskId}/lines/{lineId}/count")
        {
            Content = JsonContent.Create(body),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>El cuerpo de <c>count</c>: lo que el operario contó en la ubicación.</summary>
    public static object CountBody(
        Guid token, Guid location, decimal counted, Guid? lotId = null, DateTimeOffset? occurredAt = null) =>
        new
        {
            leaseToken = token,
            locationId = location,
            lotId,
            quantityCounted = counted,
            occurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            deviceId = "zebra-01",
        };
}
