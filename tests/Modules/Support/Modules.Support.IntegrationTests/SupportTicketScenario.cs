using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using Modules.Tenancy.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// El arranque de las pruebas de tickets: un tenant nuevo con la membresía de quien llama (un usuario propio por prueba, así el
/// host se puede compartir), su cliente con leer, gestionar y configurar, y atajos para crear y operar tickets.
/// </summary>
internal sealed class SupportTicketScenario
{
    private SupportTicketScenario(QepApiFactory factory, string tenant, HttpClient client, Guid callerMemberId, string subjectId)
    {
        Factory = factory;
        Tenant = tenant;
        Client = client;
        CallerMemberId = callerMemberId;
        SubjectId = subjectId;
    }

    public QepApiFactory Factory { get; }

    public string Tenant { get; }

    public Guid TenantId => Guid.Parse(Tenant);

    public HttpClient Client { get; }

    public Guid CallerMemberId { get; }

    public string SubjectId { get; }

    public string Url => SupportUrl(Tenant);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SupportTicketScenario> StartAsync(
        QepApiFactory factory, IEnumerable<TenantModuleKey>? modules = null, string callerName = "Zoe Llamante")
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(factory, tenant, modules);
        var subjectId = Guid.CreateVersion7().ToString();
        var caller = await SeedCallerMembershipAsync(factory, callerName, "admin", tenant, subjectId);
        var client = CreateClient(
            factory, subjectId, tenant, SupportPermissions.Read, SupportPermissions.Manage, SupportPermissions.SettingsManage);
        return new SupportTicketScenario(factory, tenant, client, caller, subjectId);
    }

    public async Task<Guid> TypeIdAsync(string name) =>
        (await JsonAsync(await Client.GetAsync($"{Url}/ticket-types", Ct)))
        .EnumerateArray()
        .Single(type => type.GetProperty("name").GetString() == name)
        .GetProperty("id")
        .GetGuid();

    /// <summary>Un cuerpo de alta válido: «Petición general» salvo que se pida otro tipo, por teléfono, respuesta por correo y la
    /// solicitante Ana Gómez con su correo.</summary>
    public static Dictionary<string, object?> Body(
        Guid typeId,
        object? requester = null,
        Guid? customerId = null,
        string replyChannel = "Email",
        Guid? assignee = null,
        string subject = "No me llegó la factura") => new()
        {
            ["ticketTypeId"] = typeId,
            ["subject"] = subject,
            ["description"] = "Pedí la factura de octubre y no ha llegado.",
            ["manualMedium"] = "Phone",
            ["replyChannel"] = replyChannel,
            ["customerId"] = customerId,
            ["requester"] = requester ?? Ana,
            ["assigneeMemberId"] = assignee,
        };

    public static object Ana => new { firstNames = "Ana", lastNames = "Gómez", email = "ana@example.com", mobilePhone = "+573001234567" };

    public Task<HttpResponseMessage> PostAsync(object body) => Client.PostAsJsonAsync($"{Url}/tickets", body, Ct);

    /// <summary>Crea un ticket y exige el 201.</summary>
    public async Task<JsonElement> CreateAsync(object body)
    {
        var response = await PostAsync(body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonAsync(response);
    }

    public async Task<JsonElement> CreateAsync(string typeName = "Petición general", object? requester = null, Guid? customerId = null) =>
        await CreateAsync(Body(await TypeIdAsync(typeName), requester, customerId));

    public async Task<JsonElement> DetailAsync(Guid ticketId)
    {
        var response = await Client.GetAsync($"{Url}/tickets/{ticketId}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }

    /// <summary>Un <c>PUT</c> de acción sobre el ticket con <c>If-Match</c>; sin versión, sin el header.</summary>
    public Task<HttpResponseMessage> ActAsync(Guid ticketId, string action, object body, long? version) =>
        SupportApiHarness.PutAsync(Client, $"{Url}/tickets/{ticketId}/{action}", body, version);

    /// <summary>Cambia el estado con la versión vigente y exige el 200.</summary>
    public async Task<JsonElement> MoveAsync(JsonElement ticket, string status)
    {
        var response = await ActAsync(Id(ticket), "status", new { status }, ticket.GetProperty("version").GetInt64());
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }

    public static Guid Id(JsonElement ticket) => ticket.GetProperty("id").GetGuid();

    /// <summary>Fase 2: abre un ticket desde una conversación por la API y exige el 201.</summary>
    public async Task<JsonElement> CreateFromConversationAsync(
        Guid conversation, string subject = "Pedido sin llegar", Guid? assignee = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"{Url}/conversations/{conversation}/tickets",
            new
            {
                ticketTypeId = await TypeIdAsync("Petición general"),
                subject,
                description = "El pedido de la semana pasada no ha llegado.",
                assigneeMemberId = assignee,
            },
            Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonAsync(response);
    }

    /// <summary>Un <c>POST</c> de acción con <c>If-Match</c>; sin versión, sin el header.</summary>
    public async Task<HttpResponseMessage> PostActionAsync(string path, object? body, long? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}/{path}")
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await Client.SendAsync(request, Ct);
    }

    public static long Version(JsonElement ticket) => ticket.GetProperty("version").GetInt64();
}
